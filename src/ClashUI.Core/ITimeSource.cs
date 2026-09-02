namespace ClashUI.Core;

public interface ITimeSource
{
    Task Delay(int ms, CancellationToken ct);
    Task Delay(TimeSpan d, CancellationToken ct);
}

public sealed class SystemTimeSource : ITimeSource
{
    public Task Delay(int ms, CancellationToken ct) => Task.Delay(ms, ct);
    public Task Delay(TimeSpan d, CancellationToken ct) => Task.Delay(d, ct);
}

public sealed class ManualTimeSource : ITimeSource
{
    private readonly List<PendingDelay> _pending = [];
    private readonly Lock _gate = new();

    private sealed class PendingDelay
    {
        public TimeSpan Duration;
        public TaskCompletionSource<bool> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Ct;
        public CancellationTokenRegistration Reg;
    }

    public Task Delay(int ms, CancellationToken ct) => Delay(TimeSpan.FromMilliseconds(ms), ct);

    public Task Delay(TimeSpan d, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        var p = new PendingDelay { Duration = d, Ct = ct };
        if (ct.CanBeCanceled)
            p.Reg = ct.Register(() => p.Tcs.TrySetCanceled(ct));
        lock (_gate) _pending.Add(p);
        return p.Tcs.Task;
    }

    public void Advance(TimeSpan? _ = null)
    {
        List<PendingDelay> toComplete;
        lock (_gate)
        {
            toComplete = [.. _pending];
            _pending.Clear();
        }
        foreach (var p in toComplete)
        {
            p.Reg.Dispose();
            p.Tcs.TrySetResult(true);
        }
    }

    public void Advance(int ms) => Advance(TimeSpan.FromMilliseconds(ms));

    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }
}
