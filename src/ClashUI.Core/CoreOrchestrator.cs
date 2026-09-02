using System.Diagnostics;

namespace ClashUI.Core;

public sealed class CoreOrchestrator : IAsyncDisposable, IDisposable
{
    private readonly ISettingsStore _settingsStore;
    private readonly IConfigComposer _composer;
    private readonly CoreRuntime? _runtime;
    private readonly ICoreRuntime? _legacyRuntime;
    private readonly IProfileWatcher _watcher;
    private readonly ITimeSource _time;
    private readonly Func<string, string, IMihomoApiClient> _apiFactory;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private CancellationTokenSource? _debounceCts;
    private IMihomoApiClient? _api;
    private bool _disposed;
    private IReadOnlyList<string>? _profilesCache;
    private readonly Lock _profilesGate = new();

    private CoreState CurrentState => _runtime?.State ?? _legacyRuntime?.State ?? CoreState.Stopped;

    public AppSettings Settings { get; private set; }
    public IConfigComposer Composer => _composer;
    public AppState State => new(
        CurrentState,
        Settings.ActiveProfile,
        Settings.SystemProxyEnabled,
        Settings.TunEnabled,
        Settings.ControllerAddr,
        GetProfiles());

    public bool IsCoreRunning => CurrentState == CoreState.Running;

    public event Action<AppState>? StateChanged;
    public event Action<string>? Notification;
    public event Action<int>? CrashLoop;

    public CoreOrchestrator(
        ISettingsStore settingsStore,
        IConfigComposer composer,
        CoreRuntime runtime,
        IProfileWatcher watcher,
        ITimeSource time,
        Func<string, string, IMihomoApiClient>? apiFactory = null)
    {
        _settingsStore = settingsStore;
        _composer = composer;
        _runtime = runtime;
        _watcher = watcher;
        _time = time;
        _apiFactory = apiFactory ?? ((addr, secret) => new MihomoApiClient(addr, secret));
        Settings = _settingsStore.Load();

        _runtime.StateChanged += OnRuntimeStateChanged;
        _runtime.CrashLoop += c =>
        {
            CrashLoop?.Invoke(c);
            Notify("核心连续异常退出，请查看数据目录 core.log（订阅 provider 拉取失败时会出现）");
        };
        _watcher.Changed += OnProfileFileMaybeChanged;
    }

    public CoreOrchestrator(
        ISettingsStore settingsStore,
        IConfigComposer composer,
        ICoreRuntime legacyRuntime,
        IProfileWatcher watcher,
        ITimeSource time,
        Func<string, string, IMihomoApiClient>? apiFactory = null)
    {
        _settingsStore = settingsStore;
        _composer = composer;
        _legacyRuntime = legacyRuntime;
        _watcher = watcher;
        _time = time;
        _apiFactory = apiFactory ?? ((addr, secret) => new MihomoApiClient(addr, secret));
        Settings = _settingsStore.Load();

        _legacyRuntime.StateChanged += OnLegacyStateChanged;
        _legacyRuntime.CrashLoop += c =>
        {
            CrashLoop?.Invoke(c);
            Notify("核心连续异常退出，请查看数据目录 core.log（订阅 provider 拉取失败时会出现）");
        };
        _watcher.Changed += OnProfileFileMaybeChanged;
    }

    public void Initialize()
    {
        AppPaths.Ensure();
        Settings = _settingsStore.Load();
        Settings.ActiveProfile = ConfigComposer.ResolveProfile(Settings.ActiveProfile, createDefault: true);
        _settingsStore.Save(Settings);
        if (Settings.SystemProxyEnabled && SystemProxy.IsSetTo(Settings.MixedPort)) SystemProxy.Clear();
        StartWatcher();
    }

    public async void StartOnLaunch()
    {
        if (!Settings.StartCoreOnLaunch) return;
        await StartAsync();
    }

    private void StartWatcher()
    {
        _watcher.Start(AppPaths.ProfilesDir);
    }

    private void OnRuntimeStateChanged(CoreState state)
    {
        if (state == CoreState.Running) OnCoreReady();
        if (state == CoreState.Stopped && Settings.SystemProxyEnabled)
            SystemProxy.Clear();
        RaiseStateChanged();
    }

    private void OnLegacyStateChanged(CoreState state)
    {
        if (state == CoreState.Running) OnCoreReady();
        if (state == CoreState.Stopped && Settings.SystemProxyEnabled)
            SystemProxy.Clear();
        RaiseStateChanged();
    }

    private void OnCoreReady()
    {
        if (Settings.SystemProxyEnabled) SystemProxy.Set(Settings.MixedPort);
    }

    public async Task<OrchestratorResult> StartAsync()
    {
        try
        {
            if (Settings.TunEnabled && !Elevation.IsElevated)
            {
                Notify("TUN 模式需要管理员权限，正在以管理员身份重新启动…");
                await _time.Delay(500, CancellationToken.None);
                if (Elevation.RelaunchElevated()) return OrchestratorResult.Fail("NeedsElevation");
                return OrchestratorResult.Fail("TunNeedsElevation");
            }

            var (exe, error) = ResolveCoreExe();
            if (exe is null)
            {
                if (error is not null) Notify(error);
                return OrchestratorResult.Fail(error);
            }

            var configPath = _composer.Compose(Settings);
            _api?.Dispose();
            _api = _apiFactory(Settings.ControllerAddr, Settings.Secret);
            if (_runtime is not null)
            {
                var launch = new CoreLaunch(exe, $"-d \"{AppPaths.Root}\" -f \"{configPath}\"");
                var endpoint = new CoreEndpoint(Settings.ControllerAddr, Settings.Secret);
                var outcome = await _runtime.StartAsync(launch, endpoint);
                if (!outcome.Ok)
                {
                    if (outcome.Failure == CoreFailure.AlreadyRunning) return OrchestratorResult.Fail("AlreadyRunning");
                    if (outcome.Failure == CoreFailure.ExeNotFound)
                    {
                        var msg = outcome.Cause ?? error ?? "exe not found";
                        Notify(msg);
                        return OrchestratorResult.Fail(msg);
                    }
                    if (outcome.Failure == CoreFailure.StartFailed)
                    {
                        var msg = outcome.Cause ?? "StartFailed";
                        Notify($"启动核心失败：{msg}");
                        return OrchestratorResult.Fail(msg);
                    }
                    var fallback = outcome.Cause ?? outcome.Failure.ToString();
                    if (outcome.Failure == CoreFailure.ProbeTimeout) Notify("核心健康检查超时，详情见数据目录 core.log");
                    else if (outcome.Failure == CoreFailure.Cancelled) Notify("启动已取消");
                    else Notify(fallback);
                    return OrchestratorResult.Fail(fallback);
                }
                return OrchestratorResult.Success;
            }
            else
            {
                _legacyRuntime!.Start(exe, $"-d \"{AppPaths.Root}\" -f \"{configPath}\"");
                return OrchestratorResult.Success;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("启动核心失败", ex);
            Notify($"启动核心失败：{ex.Message}");
            return OrchestratorResult.Fail(ex.Message);
        }
    }

    public async Task<OrchestratorResult> RestartAsync()
    {
        if (_runtime is not null) await _runtime.StopAsync();
        else _legacyRuntime!.Stop();
        return await StartAsync();
    }

    public async Task<OrchestratorResult> SwitchProfileAsync(string profilePath)
    {
        if (!File.Exists(profilePath)) return OrchestratorResult.Fail("ProfileNotFound");
        Settings.ActiveProfile = profilePath;
        _settingsStore.Save(Settings);
        RaiseStateChanged();

        if (!IsCoreRunning || _api is null)
        {
            Notify($"已选择 {Path.GetFileName(profilePath)}，核心未运行，下次启动生效");
            return OrchestratorResult.Success;
        }

        return await ReloadCoreConfigAsync($"已切换到 {Path.GetFileName(profilePath)}（热重载）", restartOnFailure: true);
    }

    public IReadOnlyList<string> GetProfiles()
    {
        lock (_profilesGate)
        {
            if (_profilesCache is not null) return _profilesCache;
            try
            {
                var list = Directory.EnumerateFiles(AppPaths.ProfilesDir)
                    .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                                || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _profilesCache = list;
                return list;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    private void InvalidateProfilesCache()
    {
        lock (_profilesGate) _profilesCache = null;
    }

    private void OnProfileFileMaybeChanged(string fullPath)
    {
        try
        {
            InvalidateProfilesCache();
            if (!fullPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                && !fullPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)) return;
            if (!string.Equals(fullPath, Settings.ActiveProfile, StringComparison.OrdinalIgnoreCase)) return;

            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            var cts = new CancellationTokenSource();
            _debounceCts = cts;
            _ = DebouncedReloadAsync(cts.Token);
        }
        catch (Exception ex)
        {
            AppLog.Error("处理配置文件变更失败", ex);
        }
    }

    private async Task DebouncedReloadAsync(CancellationToken ct)
    {
        try
        {
            await _time.Delay(600, ct);
            await ReloadCoreConfigAsync("检测到配置文件修改，已热重载", restartOnFailure: false);
        }
        catch (OperationCanceledException) { }
    }

    private async Task<OrchestratorResult> ReloadCoreConfigAsync(string successMessage, bool restartOnFailure)
    {
        if (!IsCoreRunning || _api is null) return OrchestratorResult.Fail("CoreNotRunning");
        await _reloadGate.WaitAsync();
        try
        {
            _composer.Compose(Settings);
            if (await _api.ReloadConfigAsync(AppPaths.RuntimeConfigFile))
            {
                Notify(successMessage);
                RaiseStateChanged();
                return OrchestratorResult.Success;
            }
            else if (restartOnFailure)
            {
                Notify("热重载失败，正在重启核心…");
                return await RestartAsync();
            }
            else
            {
                Notify("配置热重载失败（配置文件可能有误），修复保存后会自动重试；当前连接不受影响");
                return OrchestratorResult.Fail("ReloadFailed");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("热重载失败", ex);
            if (restartOnFailure)
            {
                return await RestartAsync();
            }
            else
            {
                Notify("配置热重载失败，详情见日志；修复保存后会自动重试");
                return OrchestratorResult.Fail(ex.Message);
            }
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private (string? exe, string? error) ResolveCoreExe()
    {
        var exe = CoreLocator.Resolve(Settings.MihomoPath);
        return exe is null
            ? (null, $"未找到 mihomo：请将 mihomo.exe 放入数据目录 {AppPaths.Root}，或设置 MihomoPath，或确保其在 PATH 中")
            : (exe, null);
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(State);

    private void Notify(string message)
    {
        AppLog.Info(message);
        Notification?.Invoke(message);
    }

    public void OpenDataFolder() => OpenFolder(AppPaths.Root);

    public void OpenProfilesFolder()
    {
        Directory.CreateDirectory(AppPaths.ProfilesDir);
        OpenFolder(AppPaths.ProfilesDir);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("打开目录失败", ex);
        }
    }

    public void Stop()
    {
        _watcher.Dispose();
        _debounceCts?.Cancel();
        if (Settings.SystemProxyEnabled) SystemProxy.Clear();
        if (_runtime is not null)
        {
            try { _runtime.StopAsync().GetAwaiter().GetResult(); } catch { }
        }
        else
        {
            try { _legacyRuntime!.Stop(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _watcher.Dispose();
        _api?.Dispose();
        if (_runtime is not null) _runtime.Dispose();
        else _legacyRuntime?.Dispose();
        _reloadGate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
