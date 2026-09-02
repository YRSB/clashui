using ClashUI.Core;

namespace ClashUI.App.Hosting;

public interface IElevationGate
{
    bool IsElevated { get; }
    bool RelaunchElevated(string arguments = "");
    bool EnsureTunOrRelaunch(bool tunEnabled);
}

public sealed class ElevationGate : IElevationGate
{
    private readonly IElevationOps _ops;
    public ElevationGate(IElevationOps? ops = null) => _ops = ops ?? new ElevationAdapter();
    public bool IsElevated => _ops.IsElevated;
    public bool RelaunchElevated(string arguments = "") => _ops.RelaunchElevated(arguments);
    public bool EnsureTunOrRelaunch(bool tunEnabled)
    {
        if (!tunEnabled || _ops.IsElevated) return true;
        return _ops.RelaunchElevated();
    }
}

public sealed class FakeElevationGate : IElevationGate
{
    public bool IsElevatedValue { get; set; }
    public bool RelaunchResult { get; set; }
    public int RelaunchCallCount { get; private set; }
    public bool IsElevated => IsElevatedValue;
    public bool RelaunchElevated(string arguments = "") { RelaunchCallCount++; return RelaunchResult; }
    public bool EnsureTunOrRelaunch(bool tunEnabled)
    {
        if (!tunEnabled || IsElevatedValue) return true;
        RelaunchCallCount++;
        return RelaunchResult;
    }
}
