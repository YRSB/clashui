namespace ClashUI.Core;

public sealed class PlatformIntegration : IPlatformPolicy
{
    private readonly ISettingsStore _store;
    private readonly ISystemProxy _proxy;
    private readonly IAutoStartOps _autoStart;
    private readonly IElevationOps _elevation;
    private readonly Func<bool>? _isCoreRunning;
    private AppSettings? _sharedSettings;

    public event Action<string>? Notification;

    public PlatformIntegration(ISettingsStore store, ISystemProxy proxy, IAutoStartOps autoStart, IElevationOps elevation, Func<bool>? isCoreRunning = null)
    {
        _store = store;
        _proxy = proxy;
        _autoStart = autoStart;
        _elevation = elevation;
        _isCoreRunning = isCoreRunning;
    }

    public void BindSettings(AppSettings shared) => _sharedSettings = shared;

    private AppSettings GetSettings()
    {
        if (_sharedSettings is not null) return _sharedSettings;
        return _store.Load();
    }

    private void SaveSettings(AppSettings s)
    {
        _store.Save(s);
        if (_sharedSettings is not null && !ReferenceEquals(_sharedSettings, s))
            _sharedSettings = s;
    }

    public Task<PolicyResult> ApplyAsync(DesiredState desired)
    {
        var s = GetSettings();
        if (s.MixedPort != desired.MixedPort)
        {
            s.MixedPort = desired.MixedPort;
            SaveSettings(s);
        }
        if (s.SilentStart != desired.SilentStart)
        {
            s.SilentStart = desired.SilentStart;
            SaveSettings(s);
        }
        var proxyResult = ApplySystemProxy(s, desired.SystemProxyEnabled, desired.MixedPort);
        if (proxyResult.Kind == PolicyResultKind.Failed) return Task.FromResult(proxyResult);
        var tunResult = ApplyTun(s, desired.TunEnabled);
        if (tunResult.Kind != PolicyResultKind.Ok) return Task.FromResult(tunResult);
        var autoResult = ApplyAutoStart(s, desired.AutoStartEnabled, desired.ExePath);
        if (autoResult.Kind != PolicyResultKind.Ok) return Task.FromResult(autoResult);
        return Task.FromResult(new PolicyResult(PolicyResultKind.Ok));
    }

    private PolicyResult ApplySystemProxy(AppSettings s, bool enabled, int port)
    {
        if (s.SystemProxyEnabled == enabled) return new PolicyResult(PolicyResultKind.Ok);
        try
        {
            if (enabled)
            {
                var shouldSet = _isCoreRunning?.Invoke() ?? true;
                if (shouldSet) _proxy.Set(port);
            }
            else
            {
                _proxy.Clear();
            }
            s.SystemProxyEnabled = enabled;
            SaveSettings(s);
            return new PolicyResult(PolicyResultKind.Ok);
        }
        catch (Exception ex)
        {
            AppLog.Error("切换系统代理失败", ex);
            return new PolicyResult(PolicyResultKind.Failed, ex.Message);
        }
    }

    private PolicyResult ApplyTun(AppSettings s, bool enabled)
    {
        if (s.TunEnabled == enabled) return new PolicyResult(PolicyResultKind.Ok);
        s.TunEnabled = enabled;
        SaveSettings(s);
        if (!_elevation.IsElevated)
        {
            if (_elevation.RelaunchElevated())
                return new PolicyResult(PolicyResultKind.NeedsElevation);
            s.TunEnabled = !enabled;
            SaveSettings(s);
            return new PolicyResult(PolicyResultKind.CancelledByUser);
        }
        return new PolicyResult(PolicyResultKind.Ok);
    }

    private PolicyResult ApplyAutoStart(AppSettings s, bool enable, string exePath)
    {
        var currently = _autoStart.IsRegistered();
        if (enable == currently && s.PendingAutoStart is null) return new PolicyResult(PolicyResultKind.Ok);
        if (!enable && !currently) return new PolicyResult(PolicyResultKind.Ok);
        var ok = enable ? _autoStart.Register(exePath) : _autoStart.Unregister();
        if (ok) return new PolicyResult(PolicyResultKind.Ok);
        if (!_elevation.IsElevated)
        {
            s.PendingAutoStart = enable;
            SaveSettings(s);
            if (_elevation.RelaunchElevated())
                return new PolicyResult(PolicyResultKind.NeedsElevation);
            s.PendingAutoStart = null;
            SaveSettings(s);
            return new PolicyResult(PolicyResultKind.CancelledByUser);
        }
        return new PolicyResult(PolicyResultKind.Failed, "修改开机自启失败");
    }

    public void ReconcileOnStartup(string exePath)
    {
        var s = GetSettings();
        if (s.SystemProxyEnabled && _proxy.IsSetTo(s.MixedPort))
        {
            try { _proxy.Clear(); } catch { }
        }
        var pending = s.PendingAutoStart;
        if (pending is null) return;
        s.PendingAutoStart = null;
        SaveSettings(s);
        var ok = pending.Value ? _autoStart.Register(exePath) : _autoStart.Unregister();
        var msg = ok ? $"开机自启已{(pending.Value ? "开启" : "关闭")}" : "开机自启设置失败，详情见日志";
        Notification?.Invoke(msg);
        AppLog.Info(msg);
    }

    public void OnCoreStateChanged(CoreState state)
    {
        var s = GetSettings();
        if (state == CoreState.Running && s.SystemProxyEnabled)
        {
            try { _proxy.Set(s.MixedPort); } catch (Exception ex) { AppLog.Error("设置系统代理失败", ex); }
        }
        else if (state == CoreState.Stopped && s.SystemProxyEnabled)
        {
            try { _proxy.Clear(); } catch { }
        }
    }

    public bool IsAutoStartRegistered() => _autoStart.IsRegistered();
}

public sealed class FakePlatformPolicy : IPlatformPolicy
{
    public DesiredState? LastDesired { get; private set; }
    public int ApplyCount { get; private set; }
    public PolicyResult NextResult { get; set; } = new(PolicyResultKind.Ok);
    public event Action<string>? Notification;
    public Task<PolicyResult> ApplyAsync(DesiredState desired) { LastDesired = desired; ApplyCount++; return Task.FromResult(NextResult); }
    public void ReconcileOnStartup(string exePath) { }
    public void OnCoreStateChanged(CoreState state) { }
    public bool IsAutoStartRegistered() => false;
    public void BindSettings(AppSettings shared) { }
    public void RaiseNotification(string msg) => Notification?.Invoke(msg);
}
