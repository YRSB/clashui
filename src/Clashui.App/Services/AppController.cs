using System.Diagnostics;
using Clashui.Core;
using Microsoft.UI.Dispatching;

namespace Clashui.App.Services;

/// 应用编排：核心生命周期、系统代理 / TUN 开关、设置持久化、托盘与窗口的状态同步。
public sealed class AppController
{
    private readonly DispatcherQueue _dispatcher;
    private readonly CoreHost _core = new();
    private MihomoApiClient? _api;
    private FileSystemWatcher? _profileWatcher;
    private CancellationTokenSource? _profileDebounce;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    public AppSettings Settings { get; private set; } = null!;

    public event Action? StateChanged;
    public event Action<string>? Notification;
    public Action? ExitRequested { get; set; }

    public AppController()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _core.StateChanged += state => _dispatcher.TryEnqueue(() =>
        {
            if (state == CoreState.Starting) _ = ProbeUntilHealthyAsync();
            if (state == CoreState.Running) OnCoreReady();
            if (state == CoreState.Stopped && Settings?.SystemProxyEnabled == true)
            {
                // 核心停止时不能让系统代理指向死端口，否则全系统断网
                SystemProxy.Clear();
            }
            RaiseStateChanged();
        });
        _core.CrashLoop += _ => _dispatcher.TryEnqueue(() =>
            Notify("核心连续异常退出，请查看数据目录 core.log（订阅 provider 拉取失败时会出现）"));
    }

    public bool IsCoreRunning => _core.State == CoreState.Running;
    public string DashboardUrl => $"http://{Settings.ControllerAddr}/ui/";

    public void Initialize()
    {
        AppPaths.Ensure();
        Settings = SettingsStore.Load();
        Settings.ActiveProfile = ConfigComposer.ResolveProfile(Settings.ActiveProfile, createDefault: true);
        SettingsStore.Save(Settings);
        StartProfileWatcher();
    }

    public async void StartOnLaunch()
    {
        if (!Settings.StartCoreOnLaunch) return;
        await StartCoreAsync();
    }

    public async Task StartCoreAsync()
    {
        try
        {
            if (Settings.TunEnabled && !Elevation.IsElevated)
            {
                Notify("TUN 模式需要管理员权限，正在以管理员身份重新启动…");
                await Task.Delay(500);
                if (Elevation.RelaunchElevated()) Exit();
                return;
            }

            var (exe, error) = ResolveCoreExe();
            if (exe is null)
            {
                Notify(error!);
                return;
            }

            var configPath = ConfigComposer.Compose(Settings);
            _api?.Dispose();
            _api = new MihomoApiClient(Settings.ControllerAddr, Settings.Secret);
            _core.Start(exe, $"-d \"{AppPaths.Root}\" -f \"{configPath}\"");
        }
        catch (Exception ex)
        {
            AppLog.Error("启动核心失败", ex);
            Notify($"启动核心失败：{ex.Message}");
        }
    }

    public async Task RestartCoreAsync()
    {
        _core.Stop();
        await StartCoreAsync();
    }

    public void ToggleTun(bool enabled)
    {
        if (Settings.TunEnabled == enabled) return;
        Settings.TunEnabled = enabled;
        SettingsStore.Save(Settings);
        if (!Elevation.IsElevated)
        {
            Notify("切换 TUN 模式需要管理员权限，正在以管理员身份重启…");
            if (Elevation.RelaunchElevated())
            {
                Exit();
                return;
            }
            // 用户取消 UAC：回滚开关，避免「托盘显示已开、实际未生效」且下次启动反复弹 UAC
            Settings.TunEnabled = !enabled;
            SettingsStore.Save(Settings);
            Notify("已取消提权，TUN 模式未更改");
            RaiseStateChanged();
            return;
        }
        // TUN 变更走完整重启，确保 wintun 设备/路由正确重建
        _ = RestartCoreAsync();
    }

    public void ToggleSystemProxy(bool enabled)
    {
        if (Settings.SystemProxyEnabled == enabled) return;
        try
        {
            if (enabled)
            {
                if (IsCoreRunning) SystemProxy.Set(Settings.MixedPort);
            }
            else
            {
                SystemProxy.Clear();
            }
            Settings.SystemProxyEnabled = enabled;
            SettingsStore.Save(Settings);
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            AppLog.Error("切换系统代理失败", ex);
            Notify($"切换系统代理失败：{ex.Message}");
        }
    }

    public void ToggleSilentStart(bool enabled)
    {
        Settings.SilentStart = enabled;
        SettingsStore.Save(Settings);
    }

    /// 开机自启开关。权限不足时不弹错误了事，而是像 TUN 开关一样自动提权重启，
    /// 重启后由 ProcessPendingOperations 补做注册/注销。
    public void ToggleAutoStart(bool enable)
    {
        // 任务不存在时取消勾选视为无操作，避免对缺失任务注销失败反而触发提权
        if (!enable && !AutoStart.IsRegistered())
        {
            RaiseStateChanged();
            return;
        }

        var ok = enable ? AutoStart.Register(Environment.ProcessPath ?? "") : AutoStart.Unregister();
        if (ok)
        {
            RaiseStateChanged();
            return;
        }

        if (!Elevation.IsElevated)
        {
            Settings.PendingAutoStart = enable;
            SettingsStore.Save(Settings);
            Notify("修改开机自启需要管理员权限，正在以管理员身份重启…");
            if (Elevation.RelaunchElevated())
            {
                Exit();
                return;
            }
            Settings.PendingAutoStart = null;
            SettingsStore.Save(Settings);
            Notify("已取消提权，开机自启未更改");
            RaiseStateChanged();
            return;
        }

        Notify("修改开机自启失败，详情见日志");
        RaiseStateChanged();
    }

    /// 提权重启后补做挂起的提权操作（当前仅开机自启），随后清空标记。
    public void ProcessPendingOperations()
    {
        var pending = Settings.PendingAutoStart;
        if (pending is null) return;
        Settings.PendingAutoStart = null;
        SettingsStore.Save(Settings);

        var ok = pending.Value
            ? AutoStart.Register(Environment.ProcessPath ?? "")
            : AutoStart.Unregister();
        Notify(ok
            ? $"开机自启已{(pending.Value ? "开启" : "关闭")}"
            : "开机自启设置失败，详情见日志");
        RaiseStateChanged();
    }

    public void OpenDataFolder() => OpenFolder(AppPaths.Root);

    public void OpenProfilesFolder()
    {
        Directory.CreateDirectory(AppPaths.ProfilesDir);
        OpenFolder(AppPaths.ProfilesDir);
    }

    private void OpenFolder(string path)
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

    /// profiles 目录下的全部 YAML 配置（完整路径，按文件名排序）。
    public IReadOnlyList<string> GetProfiles()
    {
        try
        {
            return Directory.EnumerateFiles(AppPaths.ProfilesDir)
                .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// 切换配置：写入设置 → 重新合成运行时配置 → 热重载（失败则整核重启）。
    public async Task SwitchProfileAsync(string profilePath)
    {
        if (!File.Exists(profilePath)) return;
        Settings.ActiveProfile = profilePath;
        SettingsStore.Save(Settings);
        RaiseStateChanged();

        if (!IsCoreRunning || _api is null)
        {
            Notify($"已选择 {Path.GetFileName(profilePath)}，核心未运行，下次启动生效");
            return;
        }

        await ReloadCoreConfigAsync($"已切换到 {Path.GetFileName(profilePath)}（热重载）", restartOnFailure: true);
    }

    /// 监听 profiles 目录：手动编辑激活的配置文件后自动合成 + 热重载。
    private void StartProfileWatcher()
    {
        _profileWatcher = new FileSystemWatcher(AppPaths.ProfilesDir)
        {
            // 部分编辑器保存走「写临时文件再改名」，仅听 LastWrite 会漏，故同时监听 FileName
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _profileWatcher.Changed += (_, e) => OnProfileFileMaybeChanged(e.FullPath);
        _profileWatcher.Renamed += (_, e) => OnProfileFileMaybeChanged(e.FullPath);
    }

    private void OnProfileFileMaybeChanged(string fullPath)
    {
        try
        {
            if (!fullPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                && !fullPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)) return;
            if (!string.Equals(fullPath, Settings.ActiveProfile, StringComparison.OrdinalIgnoreCase)) return;

            // 防抖：一次保存常触发多个事件（编辑器分段写入），只留最后一次
            _profileDebounce?.Cancel();
            _profileDebounce?.Dispose();
            var cts = new CancellationTokenSource();
            _profileDebounce = cts;
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
            await Task.Delay(600, ct);
            await ReloadCoreConfigAsync("检测到配置文件修改，已热重载", restartOnFailure: false);
        }
        catch (OperationCanceledException) { }
    }

    /// 合成当前激活配置并让运行中的核心热重载。
    /// restartOnFailure 区分两种触发：托盘显式切换配置，失败可整核重启兜底；
    /// 文件监听触发则绝不重启——用户可能正编辑到一半（YAML 暂时是坏的），
    /// TUN 日常使用中断网代价太大，修复保存后防抖触发自然会再试。
    private async Task ReloadCoreConfigAsync(string successMessage, bool restartOnFailure)
    {
        if (!IsCoreRunning || _api is null) return;
        await _reloadGate.WaitAsync();
        try
        {
            ConfigComposer.Compose(Settings);
            if (await _api.ReloadConfigAsync(AppPaths.RuntimeConfigFile))
            {
                Notify(successMessage);
                RaiseStateChanged();
            }
            else if (restartOnFailure)
            {
                Notify("热重载失败，正在重启核心…");
                await RestartCoreAsync();
            }
            else
            {
                Notify("配置热重载失败（配置文件可能有误），修复保存后会自动重试；当前连接不受影响");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("热重载失败", ex);
            if (restartOnFailure)
            {
                // 超时/网络异常时热重载状态未知，回退为整核重启保证一致性
                await RestartCoreAsync();
            }
            else
            {
                Notify("配置热重载失败，详情见日志；修复保存后会自动重试");
            }
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public void Exit()
    {
        _profileWatcher?.Dispose();
        _profileDebounce?.Cancel();
        if (Settings.SystemProxyEnabled) SystemProxy.Clear();
        _core.Stop();
        ExitRequested?.Invoke();
    }

    public void Notify(string message)
    {
        AppLog.Info(message);
        _dispatcher.TryEnqueue(() => Notification?.Invoke(message));
    }

    /// StateChanged 的触发源可能在线程池上（核心事件、文件监听），
    /// 订阅方都会摸 XAML 对象，必须统一经 DispatcherQueue 派发回 UI 线程。
    private void RaiseStateChanged() => _dispatcher.TryEnqueue(() => StateChanged?.Invoke());

    private void OnCoreReady()
    {
        if (Settings.SystemProxyEnabled) SystemProxy.Set(Settings.MixedPort);
    }

    private async Task ProbeUntilHealthyAsync()
    {
        var api = _api;
        if (api is null) return;
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(300);
            if (_core.State != CoreState.Starting) return;
            try
            {
                var version = await api.GetVersionAsync();
                if (version is not null)
                {
                    _core.MarkRunning();
                    Notify($"mihomo 已启动（{version}）");
                    return;
                }
            }
            catch
            {
                // 核心尚未监听，继续等待
            }
        }
        Notify("核心健康检查超时，详情见数据目录 core.log");
    }

    private (string? exe, string? error) ResolveCoreExe()
    {
        var exe = CoreLocator.Resolve(Settings.MihomoPath);
        return exe is null
            ? (null, $"未找到 mihomo：请将 mihomo.exe 放入数据目录 {AppPaths.Root}，或设置 MihomoPath，或确保其在 PATH 中")
            : (exe, null);
    }
}
