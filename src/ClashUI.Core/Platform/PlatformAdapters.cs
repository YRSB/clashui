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

#if DEBUG
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
#endif
