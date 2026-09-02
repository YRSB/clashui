namespace ClashUI.App.Hosting;

public interface IDispatcher
{
    bool TryEnqueue(Action action);
}

public sealed class DispatcherQueueAdapter : IDispatcher
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _queue;
    public DispatcherQueueAdapter(Microsoft.UI.Dispatching.DispatcherQueue queue) => _queue = queue;
    public bool TryEnqueue(Action action) => _queue.TryEnqueue(() => action());
}

public sealed class InlineDispatcher : IDispatcher
{
    public bool TryEnqueue(Action action) { action(); return true; }
}
