using ClashUI.Core;
using ClashUI.App.Hosting;

namespace ClashUI.App.Tray;

public sealed class TrayPresenter : IDisposable
{
    private readonly ITrayView _view;
    private readonly CoreOrchestrator _orch;
    private readonly IPlatformPolicy _platform;
    private readonly IDispatcher _dispatcher;
    private readonly Action _showWindow;
    private bool _disposed;

    public TrayPresenter(ITrayView view, CoreOrchestrator orch, IPlatformPolicy platform, IDispatcher dispatcher, Action showWindow)
    {
        _view = view;
        _orch = orch;
        _platform = platform;
        _dispatcher = dispatcher;
        _showWindow = showWindow;
        _view.Command += OnCommand;
        _orch.StateChanged += OnStateChanged;
        _platform.Notification += OnPlatformNotification;
    }

    public void Start() => Refresh();

    private void OnStateChanged(AppState _) => _dispatcher.TryEnqueue(Refresh);

    private void OnPlatformNotification(string msg) => App.ShowGlobalNotification(msg);

    private void Refresh()
    {
        if (_disposed) return;
        var settings = _orch.Settings;
        var state = new TrayState(
            _orch.IsCoreRunning,
            settings.TunEnabled,
            settings.SystemProxyEnabled,
            settings.SilentStart,
            _platform.IsAutoStartRegistered(),
            _orch.GetProfiles(),
            settings.ActiveProfile);
        var vm = TrayIconMapper.ToViewModel(state);
        try { _view.Render(vm); } catch (Exception ex) { AppLog.Error("托盘渲染失败", ex); }
    }

    private void OnCommand(TrayCommand cmd)
    {
        try
        {
            switch (cmd.Kind)
            {
                case TrayCommandKind.ShowPanel:
                    _showWindow();
                    break;
                case TrayCommandKind.RestartCore:
                    _ = _orch.RestartAsync();
                    break;
                case TrayCommandKind.OpenDataFolder:
                    _orch.OpenDataFolder();
                    break;
                case TrayCommandKind.OpenProfilesFolder:
                    _orch.OpenProfilesFolder();
                    break;
                case TrayCommandKind.ToggleSystemProxy:
                    HandleSystemProxyToggle();
                    break;
                case TrayCommandKind.ToggleTun:
                    HandleTunToggle();
                    break;
                case TrayCommandKind.ToggleSilentStart:
                    HandleSilentStartToggle();
                    break;
                case TrayCommandKind.ToggleAutoStart:
                    HandleAutoStartToggle();
                    break;
                case TrayCommandKind.SwitchProfile:
                    if (cmd.Payload is not null) _ = _orch.SwitchProfileAsync(cmd.Payload);
                    break;
                case TrayCommandKind.Exit:
                    Exit();
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("托盘命令失败", ex);
        }
        finally
        {
            _dispatcher.TryEnqueue(Refresh);
        }
    }

    private void HandleTunToggle()
    {
        var s = _orch.Settings;
        var desired = new DesiredState(s.SystemProxyEnabled, s.MixedPort, !s.TunEnabled, _platform.IsAutoStartRegistered(), s.SilentStart, Environment.ProcessPath ?? "");
        var r = _platform.ApplyAsync(desired).GetAwaiter().GetResult();
        if (r.Kind == PolicyResultKind.Ok)
        {
            _ = _orch.RestartAsync();
        }
        else if (r.Kind == PolicyResultKind.NeedsElevation)
        {
            App.ShowGlobalNotification("切换 TUN 模式需要管理员权限，正在以管理员身份重启…");
            Exit();
        }
        else if (r.Kind == PolicyResultKind.CancelledByUser)
        {
            App.ShowGlobalNotification("已取消提权，TUN 模式未更改");
        }
        else if (r.Kind == PolicyResultKind.Failed)
        {
            App.ShowGlobalNotification($"切换 TUN 失败：{r.Cause}");
        }
    }

    private void HandleSystemProxyToggle()
    {
        var s = _orch.Settings;
        var desired = new DesiredState(!s.SystemProxyEnabled, s.MixedPort, s.TunEnabled, _platform.IsAutoStartRegistered(), s.SilentStart, Environment.ProcessPath ?? "");
        var r = _platform.ApplyAsync(desired).GetAwaiter().GetResult();
        if (r.Kind == PolicyResultKind.Failed)
            App.ShowGlobalNotification($"切换系统代理失败：{r.Cause}");
    }

    private void HandleSilentStartToggle()
    {
        var s = _orch.Settings;
        var desired = new DesiredState(s.SystemProxyEnabled, s.MixedPort, s.TunEnabled, _platform.IsAutoStartRegistered(), !s.SilentStart, Environment.ProcessPath ?? "");
        _ = _platform.ApplyAsync(desired).GetAwaiter().GetResult();
    }

    private void HandleAutoStartToggle()
    {
        var s = _orch.Settings;
        var currently = _platform.IsAutoStartRegistered();
        var desired = new DesiredState(s.SystemProxyEnabled, s.MixedPort, s.TunEnabled, !currently, s.SilentStart, Environment.ProcessPath ?? "");
        var r = _platform.ApplyAsync(desired).GetAwaiter().GetResult();
        if (r.Kind == PolicyResultKind.NeedsElevation)
        {
            App.ShowGlobalNotification("修改开机自启需要管理员权限，正在以管理员身份重启…");
            Exit();
        }
        else if (r.Kind == PolicyResultKind.CancelledByUser)
            App.ShowGlobalNotification("已取消提权，开机自启未更改");
        else if (r.Kind == PolicyResultKind.Failed)
            App.ShowGlobalNotification("修改开机自启失败，详情见日志");
    }

    private void Exit()
    {
        try { _view.Dispose(); } catch { }
        try { _orch.Stop(); } catch { }
        try { _orch.Dispose(); } catch { }
        Environment.Exit(0);
    }

    public void Dispose()
    {
        _disposed = true;
        try { _orch.StateChanged -= OnStateChanged; } catch { }
        try { _platform.Notification -= OnPlatformNotification; } catch { }
        try { _view.Command -= OnCommand; } catch { }
        try { _view.Dispose(); } catch { }
    }
}
