namespace Clashui.Core;

public interface ICoreRuntime : IDisposable
{
    CoreState State { get; }
    event Action<CoreState> StateChanged;
    event Action<int> CrashLoop;
    void Start(string exe, string args);
    void Stop();
}

public sealed class MihomoCoreRuntime : ICoreRuntime
{
    private readonly CoreRuntime _runtime;
    private readonly string _controllerAddr;
    private readonly string _secret;

    public CoreState State => _runtime.State;

    public event Action<CoreState>? StateChanged;
    public event Action<int>? CrashLoop;

    public MihomoCoreRuntime(string controllerAddr = "127.0.0.1:9090", string secret = "", CoreRuntime? runtime = null)
    {
        _controllerAddr = controllerAddr;
        _secret = secret;
        _runtime = runtime ?? new CoreRuntime();
        _runtime.StateChanged += s => StateChanged?.Invoke(s);
        _runtime.CrashLoop += c => CrashLoop?.Invoke(c);
    }

    public void Start(string exe, string args)
    {
        var launch = new CoreLaunch(exe, args);
        var endpoint = new CoreEndpoint(_controllerAddr, _secret);
        Task.Run(() => _runtime.StartAsync(launch, endpoint)).GetAwaiter().GetResult();
    }
    public void Stop() => Task.Run(() => _runtime.StopAsync()).GetAwaiter().GetResult();
    public void Dispose() => _runtime.Dispose();
}

public sealed class FakeCoreRuntime : ICoreRuntime
{
    private CoreState _state = CoreState.Stopped;

    public CoreState State => _state;

    public event Action<CoreState>? StateChanged;
    public event Action<int>? CrashLoop;

    public void Start(string exe, string args) => SetState(CoreState.Starting);
    public void Stop() => SetState(CoreState.Stopped);

    public void SimulateCrash(int consecutiveCount = 1)
    {
        SetState(CoreState.Stopped);
        CrashLoop?.Invoke(consecutiveCount);
    }

    public void SimulateState(CoreState state) => SetState(state);

    private void SetState(CoreState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose() { }
}
