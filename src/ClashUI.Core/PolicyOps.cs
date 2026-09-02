namespace ClashUI.Core;

public interface ISystemProxy
{
    void Set(int port);
    void Clear();
    bool IsSetTo(int port);
}

public interface IAutoStartOps
{
    bool IsRegistered();
    bool Register(string exe);
    bool Unregister();
}

public interface IElevationOps
{
    bool IsElevated { get; }
    bool RelaunchElevated(string arguments = "");
}

public sealed class SystemProxyAdapter : ISystemProxy
{
    public void Set(int port) => SystemProxy.Set(port);
    public void Clear() => SystemProxy.Clear();
    public bool IsSetTo(int port) => SystemProxy.IsSetTo(port);
}

public sealed class AutoStartAdapter : IAutoStartOps
{
    public bool IsRegistered() => AutoStart.IsRegistered();
    public bool Register(string exe) => AutoStart.Register(exe);
    public bool Unregister() => AutoStart.Unregister();
}

public sealed class ElevationAdapter : IElevationOps
{
    public bool IsElevated => Elevation.IsElevated;
    public bool RelaunchElevated(string arguments = "") => Elevation.RelaunchElevated(arguments);
}

public sealed class FakeSystemProxy : ISystemProxy
{
    public int CurrentPort { get; private set; }
    public bool Enabled { get; private set; }
    public bool ThrowOnSet { get; set; }
    public bool ThrowOnClear { get; set; }
    public void Set(int port)
    {
        if (ThrowOnSet) throw new InvalidOperationException("fake set failed");
        CurrentPort = port;
        Enabled = true;
    }
    public void Clear()
    {
        if (ThrowOnClear) throw new InvalidOperationException("fake clear failed");
        Enabled = false;
    }
    public bool IsSetTo(int port) => Enabled && CurrentPort == port;
}

public sealed class FakeAutoStart : IAutoStartOps
{
    public bool Registered { get; set; }
    public bool RegisterResult { get; set; } = true;
    public bool UnregisterResult { get; set; } = true;
    public string? LastRegisterExe { get; private set; }
    public bool IsRegistered() => Registered;
    public bool Register(string exe)
    {
        LastRegisterExe = exe;
        if (!RegisterResult) return false;
        Registered = true;
        return true;
    }
    public bool Unregister()
    {
        if (!UnregisterResult) return false;
        Registered = false;
        return true;
    }
}

public sealed class FakeElevation : IElevationOps
{
    public bool Elevated { get; set; }
    public bool RelaunchResult { get; set; }
    public string? LastArgs { get; private set; }
    public bool IsElevated => Elevated;
    public bool RelaunchElevated(string arguments = "")
    {
        LastArgs = arguments;
        return RelaunchResult;
    }
}

public sealed class PolicyOps
{
    private readonly ISettingsStore _store;
    private readonly ISystemProxy _proxy;
    private readonly IAutoStartOps _autoStart;
    private readonly IElevationOps _elevation;
    private AppSettings? _sharedSettings;

    public event Action<string>? Notification;

    public PolicyOps(ISettingsStore store, ISystemProxy proxy, IAutoStartOps autoStart, IElevationOps elevation)
    {
        _store = store;
        _proxy = proxy;
        _autoStart = autoStart;
        _elevation = elevation;
    }

    public void BindSettings(AppSettings settings) => _sharedSettings = settings;

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

    public PolicyResult SetTun(bool enabled, Action? requestExit = null)
    {
        var s = GetSettings();
        if (s.TunEnabled == enabled) return new PolicyResult(PolicyResultKind.Ok);
        s.TunEnabled = enabled;
        SaveSettings(s);
        if (!_elevation.IsElevated)
        {
            if (_elevation.RelaunchElevated())
            {
                return new PolicyResult(PolicyResultKind.NeedsElevation);
            }
            s.TunEnabled = !enabled;
            SaveSettings(s);
            return new PolicyResult(PolicyResultKind.CancelledByUser);
        }
        return new PolicyResult(PolicyResultKind.Ok);
    }


    public PolicyResult SetSystemProxy(bool enabled, bool coreRunning, int port)
    {
        var s = GetSettings();
        if (s.SystemProxyEnabled == enabled) return new PolicyResult(PolicyResultKind.Ok);
        try
        {
            if (enabled)
            {
                if (coreRunning) _proxy.Set(port);
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

    public PolicyResult SetSilentStart(bool enabled)
    {
        var s = GetSettings();
        s.SilentStart = enabled;
        SaveSettings(s);
        return new PolicyResult(PolicyResultKind.Ok);
    }

    public PolicyResult SetAutoStart(bool enable, string exePath)
    {
        var s = GetSettings();
        if (!enable && !_autoStart.IsRegistered())
        {
            return new PolicyResult(PolicyResultKind.Ok);
        }
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
    public void ApplyPending(string exePath)
    {
        var s = GetSettings();
        var pending = s.PendingAutoStart;
        if (pending is null) return;
        s.PendingAutoStart = null;
        SaveSettings(s);
        var ok = pending.Value ? _autoStart.Register(exePath) : _autoStart.Unregister();
        var msg = ok ? $"开机自启已{(pending.Value ? "开启" : "关闭")}" : "开机自启设置失败，详情见日志";
        Notification?.Invoke(msg);
        AppLog.Info(msg);
    }

    public bool IsAutoStartRegistered() => _autoStart.IsRegistered();
}
