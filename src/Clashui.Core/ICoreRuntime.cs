namespace Clashui.Core;

public interface ICoreRuntime : IDisposable
{
    CoreState State { get; }
    event Action<CoreState> StateChanged;
    event Action<int> CrashLoop;
    void Start(string exe, string args);
    void Stop();
    void MarkRunning();
}

public sealed class MihomoCoreRuntime : ICoreRuntime
{
    private readonly CoreHost _host = new();

    public CoreState State => _host.State;

    public event Action<CoreState>? StateChanged;
    public event Action<int>? CrashLoop;

    public MihomoCoreRuntime()
    {
        _host.StateChanged += s => StateChanged?.Invoke(s);
        _host.CrashLoop += c => CrashLoop?.Invoke(c);
    }

    public void Start(string exe, string args) => _host.Start(exe, args);
    public void Stop() => _host.Stop();
    public void MarkRunning() => _host.MarkRunning();
    public void Dispose() => _host.Dispose();
}

public sealed class FakeCoreRuntime : ICoreRuntime
{
    private CoreState _state = CoreState.Stopped;

    public CoreState State => _state;

    public event Action<CoreState>? StateChanged;
    public event Action<int>? CrashLoop;

    public void Start(string exe, string args) => SetState(CoreState.Starting);
    public void Stop() => SetState(CoreState.Stopped);
    public void MarkRunning() => SetState(CoreState.Running);

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
