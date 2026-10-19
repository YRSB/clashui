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
        bool changed = false;
        if (s.MixedPort != desired.MixedPort)
        {
            s.MixedPort = desired.MixedPort;
            changed = true;
        }
        if (s.SilentStart != desired.SilentStart)
        {
            s.SilentStart = desired.SilentStart;
            changed = true;
        }

        var (proxyResult, proxyChanged) = ApplySystemProxy(s, desired.SystemProxyEnabled, desired.MixedPort);
        changed |= proxyChanged;
        if (proxyResult.Kind == PolicyResultKind.Failed)
        {
            if (changed) SaveSettings(s);
            return Task.FromResult(proxyResult);
        }

        var (tunResult, tunChanged) = ApplyTun(s, desired.TunEnabled);
        changed |= tunChanged;
        if (tunResult.Kind != PolicyResultKind.Ok)
        {
            if (changed) SaveSettings(s);
            return Task.FromResult(tunResult);
        }

        if (desired.AutoStartEnabled.HasValue)
        {
            var (autoResult, autoChanged) = ApplyAutoStart(s, desired.AutoStartEnabled.Value, desired.ExePath);
            changed |= autoChanged;
            if (autoResult.Kind != PolicyResultKind.Ok)
            {
                if (changed) SaveSettings(s);
                return Task.FromResult(autoResult);
            }
        }

        if (changed) SaveSettings(s);
        return Task.FromResult(new PolicyResult(PolicyResultKind.Ok));
    }

    private (PolicyResult Result, bool Changed) ApplySystemProxy(AppSettings s, bool enabled, int port)
    {
        if (s.SystemProxyEnabled == enabled) return (new PolicyResult(PolicyResultKind.Ok), false);
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
            return (new PolicyResult(PolicyResultKind.Ok), true);
        }
        catch (Exception ex)
        {
            AppLog.Error("切换系统代理失败", ex);
            return (new PolicyResult(PolicyResultKind.Failed, ex.Message), false);
        }
    }

    private (PolicyResult Result, bool Changed) ApplyTun(AppSettings s, bool enabled)
    {
        if (s.TunEnabled == enabled) return (new PolicyResult(PolicyResultKind.Ok), false);
        if (!_elevation.IsElevated)
        {
            s.TunEnabled = enabled;
            if (_elevation.RelaunchElevated())
                return (new PolicyResult(PolicyResultKind.NeedsElevation), true);
            s.TunEnabled = !enabled;
            return (new PolicyResult(PolicyResultKind.CancelledByUser), false);
        }
        s.TunEnabled = enabled;
        return (new PolicyResult(PolicyResultKind.Ok), true);
    }

    private (PolicyResult Result, bool Changed) ApplyAutoStart(AppSettings s, bool enable, string exePath)
    {
        var currently = _autoStart.IsRegistered();
        if (enable == currently && s.PendingAutoStart is null) return (new PolicyResult(PolicyResultKind.Ok), false);
        if (!enable && !currently) return (new PolicyResult(PolicyResultKind.Ok), false);
        var ok = enable ? _autoStart.Register(exePath) : _autoStart.Unregister();
        if (ok) return (new PolicyResult(PolicyResultKind.Ok), false);
        if (!_elevation.IsElevated)
        {
            s.PendingAutoStart = enable;
            if (_elevation.RelaunchElevated())
                return (new PolicyResult(PolicyResultKind.NeedsElevation), true);
            s.PendingAutoStart = null;
            return (new PolicyResult(PolicyResultKind.CancelledByUser), false);
        }
        return (new PolicyResult(PolicyResultKind.Failed, "修改开机自启失败"), false);
    }

    public void ReconcileOnStartup(string exePath)
    {
        var s = GetSettings();
        if (s.SystemProxyEnabled && _proxy.IsSetTo(s.MixedPort))
        {
            try { _proxy.Clear(); } catch { }
        }
        _ = Task.Run(() => ReconcileSlow(s, exePath));
    }

    private void ReconcileSlow(AppSettings s, string exePath)
    {
        var pending = s.PendingAutoStart;
        if (pending is null) return;
        s.PendingAutoStart = null;
        try { SaveSettings(s); } catch { }
        bool ok;
        try { ok = pending.Value ? _autoStart.Register(exePath) : _autoStart.Unregister(); }
        catch { ok = false; }
        var msg = ok ? $"开机自启已{(pending.Value ? "开启" : "关闭")}" : "开机自启设置失败，详情见日志";
        try { Notification?.Invoke(msg); } catch { }
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

#if DEBUG
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
#endif
