using System.IO.MemoryMappedFiles;
using ClashUI.Core;

namespace ClashUI.App.Hosting;

public sealed class MmfEventActivationBridge : IActivationBridge
{
    private const string ActivateEventName = @"Local\ClashUI.ActivateSignal";
    private const string LegacyActivateEventName = @"Local\Clashui.ActivateSignal";
    private const string ActivatePidMapName = @"Local\ClashUI.ActivatePid";
    private const string LegacyActivatePidMapName = @"Local\Clashui.ActivatePid";

    private EventWaitHandle? _signal;
    private MemoryMappedFile? _pidFile;
    private EventWaitHandle? _legacySignal;
    private bool _disposed;

    public void StartWatcher(Action onActivate)
    {
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName, out _);
        _pidFile = MemoryMappedFile.CreateOrOpen(ActivatePidMapName, sizeof(int));
        using var view = _pidFile.CreateViewAccessor();
        view.Write(0, Environment.ProcessId);
        _ = Task.Run(() =>
        {
            while (!_disposed && _signal.WaitOne())
            {
                try { onActivate(); } catch { }
            }
        });
        try
        {
            _legacySignal = new EventWaitHandle(false, EventResetMode.AutoReset, LegacyActivateEventName, out var created);
            if (!created)
            {
                try { _legacySignal.Dispose(); } catch { }
                _legacySignal = null;
                return;
            }
            var ls = _legacySignal;
            _ = Task.Run(() =>
            {
                while (!_disposed && ls.WaitOne())
                {
                    try { onActivate(); } catch { }
                }
            });
        }
        catch { }
    }

    public void Forward()
    {
        var forwarded = false;
        try
        {
            using var pidFile = MemoryMappedFile.OpenExisting(ActivatePidMapName);
            using var view = pidFile.CreateViewAccessor();
            var pid = view.ReadInt32(0);
            if (pid > 0) NativeMethods.AllowSetForegroundWindow((uint)pid);
        }
        catch { }
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ActivateEventName);
            signal.Set();
            forwarded = true;
        }
        catch { }
        try
        {
            using var pidFile = MemoryMappedFile.OpenExisting(LegacyActivatePidMapName);
            using var view = pidFile.CreateViewAccessor();
            var pid = view.ReadInt32(0);
            if (pid > 0) NativeMethods.AllowSetForegroundWindow((uint)pid);
        }
        catch (Exception ex)
        {
            if (!forwarded) AppLog.Error("转授前台失败", ex);
        }
        try
        {
            using var signal = EventWaitHandle.OpenExisting(LegacyActivateEventName);
            signal.Set();
            forwarded = true;
        }
        catch (Exception ex)
        {
            if (!forwarded) AppLog.Error("激活信号发送失败", ex);
        }
        if (!forwarded) AppLog.Error("激活信号发送失败", new InvalidOperationException("no signal"));
    }

    public void Dispose()
    {
        _disposed = true;
        try { _signal?.Set(); } catch { }
        try { _legacySignal?.Set(); } catch { }
        try { _signal?.Dispose(); } catch { }
        try { _legacySignal?.Dispose(); } catch { }
        try { _pidFile?.Dispose(); } catch { }
    }
}

public sealed class InProcessActivationBridge : IActivationBridge
{
    private Action? _onActivate;
    public int ForwardCount { get; private set; }
    public void StartWatcher(Action onActivate) => _onActivate = onActivate;
    public void Trigger() => _onActivate?.Invoke();
    public void Forward() => ForwardCount++;
    public void Dispose() { }
}
