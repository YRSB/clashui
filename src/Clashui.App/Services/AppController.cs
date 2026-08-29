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
            StateChanged?.Invoke();
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
            if (Elevation.RelaunchElevated()) Exit();
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
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error("切换系统代理失败", ex);
            Notify($"切换系统代理失败：{ex.Message}");
        }
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
        StateChanged?.Invoke();

        if (!IsCoreRunning || _api is null)
        {
            Notify($"已选择 {Path.GetFileName(profilePath)}，核心未运行，下次启动生效");
            return;
        }

        try
        {
            ConfigComposer.Compose(Settings);
            if (await _api.ReloadConfigAsync(AppPaths.RuntimeConfigFile))
            {
                Notify($"已切换到 {Path.GetFileName(profilePath)}（热重载）");
                StateChanged?.Invoke();
            }
            else
            {
                Notify("热重载失败，正在重启核心…");
                await RestartCoreAsync();
            }
        }
        catch (Exception ex)
        {
            // 超时/网络异常时热重载状态未知，回退为整核重启保证一致性
            AppLog.Error("热重载失败，回退为重启核心", ex);
            await RestartCoreAsync();
        }
    }

    public void Exit()
    {
        if (Settings.SystemProxyEnabled) SystemProxy.Clear();
        _core.Stop();
        ExitRequested?.Invoke();
    }

    public void Notify(string message)
    {
        AppLog.Info(message);
        _dispatcher.TryEnqueue(() => Notification?.Invoke(message));
    }

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
