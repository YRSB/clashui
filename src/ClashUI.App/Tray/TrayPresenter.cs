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
    private readonly Action _shutdown;
    private bool _disposed;
    private readonly Action<string>? _notify;
    private bool? _autoStartCache;

    public TrayPresenter(ITrayView view, CoreOrchestrator orch, IPlatformPolicy platform, IDispatcher dispatcher, Action showWindow, Action? shutdown = null, Action<string>? notify = null)
    {
        _view = view;
        _orch = orch;
        _platform = platform;
        _dispatcher = dispatcher;
        _showWindow = showWindow;
        _shutdown = shutdown ?? App.Shutdown;
        _notify = notify;
        _view.Command += OnCommand;
        _orch.StateChanged += OnStateChanged;
    }

    public void Start() => Refresh();

    private void OnStateChanged(AppState _) => _dispatcher.TryEnqueue(Refresh);

    public void RefreshNow() => Refresh();

    private void Notify(string message)
    {
        if (_notify is not null)
            _notify(message);
        else
            _dispatcher.TryEnqueue(() => App.ShowGlobalNotification(message));
    }

    private DesiredState CreateDesiredState(bool? systemProxy = null, bool? tun = null, bool? autoStart = null, bool? silentStart = null)
    {
        var s = _orch.Settings;
        return new DesiredState(
            systemProxy ?? s.SystemProxyEnabled,
            s.MixedPort,
            tun ?? s.TunEnabled,
            autoStart,
            silentStart ?? s.SilentStart,
            Environment.ProcessPath ?? "");
    }

    private void Refresh()
    {
        if (_disposed) return;
        var settings = _orch.Settings;
        var state = new TrayState(
            _orch.IsCoreRunning,
            settings.TunEnabled,
            settings.SystemProxyEnabled,
            settings.SilentStart,
            GetAutoStartRegistered(),
            _orch.GetProfiles(),
            settings.ActiveProfile,
            settings.ActivePanel,
            ClashUI.Core.PanelProvisioner.GetAvailable());
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
                    _ = Task.Run(HandleSystemProxyToggleAsync);
                    break;
                case TrayCommandKind.ToggleTun:
                    _ = Task.Run(HandleTunToggleAsync);
                    break;
                case TrayCommandKind.ToggleSilentStart:
                    ToggleSilentStart();
                    break;
                case TrayCommandKind.ToggleAutoStart:
                    _ = Task.Run(() => HandleAutoStartToggleAsync(cmd.Flag));
                    break;
                case TrayCommandKind.SwitchProfile:
                    if (cmd.Payload is not null) _ = _orch.SwitchProfileAsync(cmd.Payload);
                    break;
                case TrayCommandKind.SwitchPanel:
                    if (cmd.Payload is not null) _ = _orch.SwitchPanelAsync(cmd.Payload);
                    break;
                case TrayCommandKind.RetryPanels:
                    RetryPanels();
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
    }

    private async Task HandleTunToggleAsync()
    {
        var desired = CreateDesiredState(tun: !_orch.Settings.TunEnabled);
        var r = await _platform.ApplyAsync(desired).ConfigureAwait(false);
        if (r.Kind == PolicyResultKind.Ok)
        {
            _ = _orch.RestartAsync();
        }
        else if (r.Kind == PolicyResultKind.NeedsElevation)
        {
            Notify("切换 TUN 模式需要管理员权限，正在以管理员身份重启…");
            Exit();
        }
        else if (r.Kind == PolicyResultKind.CancelledByUser)
        {
            Notify("已取消提权，TUN 模式未更改");
        }
        else if (r.Kind == PolicyResultKind.Failed)
        {
            Notify($"切换 TUN 失败：{r.Cause}");
        }
        _dispatcher.TryEnqueue(Refresh);
    }

    private async Task HandleSystemProxyToggleAsync()
    {
        var desired = CreateDesiredState(systemProxy: !_orch.Settings.SystemProxyEnabled);
        var r = await _platform.ApplyAsync(desired).ConfigureAwait(false);
        if (r.Kind == PolicyResultKind.Failed)
            Notify($"切换系统代理失败：{r.Cause}");
        _dispatcher.TryEnqueue(Refresh);
    }

    public void ToggleSilentStart()
    {
        var desired = CreateDesiredState(silentStart: !_orch.Settings.SilentStart);
        _ = _platform.ApplyAsync(desired);
        Refresh();
    }

    private bool GetAutoStartRegistered()
    {
        if (_autoStartCache.HasValue) return _autoStartCache.Value;
        _ = Task.Run(() =>
        {
            try
            {
                var v = _platform.IsAutoStartRegistered();
                _autoStartCache = v;
                _dispatcher.TryEnqueue(Refresh);
            }
            catch { }
        });
        return false;
    }

    private async Task HandleAutoStartToggleAsync(bool? wantEnable)
    {
        try
        {
            var enable = wantEnable ?? !GetAutoStartRegistered();
            var desired = CreateDesiredState(autoStart: enable);
            var r = await _platform.ApplyAsync(desired).ConfigureAwait(false);
            if (r.Kind == PolicyResultKind.Ok)
            {
                _autoStartCache = enable;
            }
            else
            {
                _autoStartCache = null;
            }
            if (r.Kind == PolicyResultKind.NeedsElevation)
            {
                Notify("修改开机自启需要管理员权限，正在以管理员身份重启…");
                Exit();
            }
            else if (r.Kind == PolicyResultKind.CancelledByUser)
                Notify("已取消提权，开机自启未更改");
            else if (r.Kind == PolicyResultKind.Failed)
                Notify("修改开机自启失败，详情见日志");
        }
        catch (Exception ex)
        {
            _autoStartCache = null;
            AppLog.Error("切换开机自启失败", ex);
        }
        _dispatcher.TryEnqueue(Refresh);
    }

    private void RetryPanels()
    {
        var orch = _orch;
        _ = Task.Run(async () =>
        {
            try
            {
                await ClashUI.Core.PanelProvisioner.EnsureAllAsync(
                    msg => _dispatcher.TryEnqueue(() => App.ShowPanelNotification(msg))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ClashUI.Core.AppLog.Error("面板手动重试失败", ex);
            }
            _dispatcher.TryEnqueue(() =>
            {
                try { orch.NotifyPanelsChanged(); } catch { }
                Refresh();
            });
        });
    }

    private void Exit()
    {
        try { _view.Dispose(); } catch { }
        _shutdown();
    }

    public void Dispose()
    {
        _disposed = true;
        try { _orch.StateChanged -= OnStateChanged; } catch { }
        try { _view.Command -= OnCommand; } catch { }
        try { _view.Dispose(); } catch { }
    }
}
