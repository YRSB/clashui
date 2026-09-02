using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClashUI.Core;

public sealed record CoreLaunch(string ExePath, string Arguments);
public sealed record CoreEndpoint(string ControllerAddr, string Secret);
public enum CoreFailure { None, AlreadyRunning, ExeNotFound, StartFailed, ProbeTimeout, Cancelled }
public sealed record CoreOutcome(bool Ok, CoreFailure Failure, string? Version, string? Cause)
{
    public static CoreOutcome Success(string? v) => new(true, CoreFailure.None, v, null);
    public static CoreOutcome Fail(CoreFailure f, string? c = null) => new(false, f, null, c);
}

#region Ports
public interface IProbePort : IDisposable { Task<string?> GetVersionAsync(CancellationToken ct); }
public interface IProcessHandle : IDisposable
{
    bool HasExited { get; }
    int? ExitCode { get; }
    void Kill(bool entireTree = true);
    bool WaitForExit(int ms);
    event Action<string> Output;
    event Action<int?> Exited;
}
public interface IProcessPort { IProcessHandle Start(ProcessSpec spec); }
public sealed record ProcessSpec(string FileName, string Arguments, string WorkingDirectory, bool RedirectOutput = true, bool CreateNoWindow = true);
public interface IClockPort { DateTime UtcNow { get; } DateTime Now { get; } }
public interface IDelayPort { Task Delay(TimeSpan d, CancellationToken ct); }
#endregion

public sealed class CoreRuntime : IAsyncDisposable, IDisposable
{
    private readonly IProcessPort _processPort;
    private readonly IClockPort _clock;
    private readonly IDelayPort _delay;
    private readonly IProbePort? _probeOverride;
    private IProbePort? _probe;
    private IProcessHandle? _process;
    private IntPtr _job = IntPtr.Zero;
    private DateTime _startedAt;
    private int _consecutiveCrashes;
    private bool _userStop;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _probeCts;
    private CoreLaunch? _lastLaunch;
    private CoreEndpoint? _lastEndpoint;
    private bool _disposed;

    public CoreState State { get; private set; } = CoreState.Stopped;
    public event Action<CoreState>? StateChanged;
    public event Action<int>? CrashLoop;
    public event Action<string>? Output;

    public CoreRuntime(IProbePort? probeOverride = null, IClockPort? clockOverride = null, IDelayPort? delayOverride = null, IProcessPort? processOverride = null)
    {
        _probeOverride = probeOverride;
        _probe = probeOverride;
        _clock = clockOverride ?? new SystemClockAdapter();
        _delay = delayOverride ?? new SystemDelayAdapter();
        _processPort = processOverride ?? new SystemProcessAdapter();
    }

    public async Task<CoreOutcome> StartAsync(CoreLaunch launch, CoreEndpoint endpoint, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ct.IsCancellationRequested) return CoreOutcome.Fail(CoreFailure.Cancelled);
        bool acquired = false;
        try
        {
            await _gate.WaitAsync(ct);
            acquired = true;
            if (State != CoreState.Stopped) return CoreOutcome.Fail(CoreFailure.AlreadyRunning);
            if (!File.Exists(launch.ExePath)) return CoreOutcome.Fail(CoreFailure.ExeNotFound, launch.ExePath);
            _startedAt = _clock.UtcNow;
            _userStop = false;
            _lastLaunch = launch;
            _lastEndpoint = endpoint;
            if (_probeOverride is null)
            {
                try { _probe?.Dispose(); } catch { }
                _probe = new HttpProbeAdapter(endpoint.ControllerAddr, endpoint.Secret);
            }
            else
            {
                _probe = _probeOverride;
            }
            IProcessHandle handle;
            try
            {
                var spec = new ProcessSpec(launch.ExePath, launch.Arguments, AppPaths.Root);
                handle = _processPort.Start(spec);
            }
            catch (Exception ex)
            {
                SetState(CoreState.Stopped);
                return CoreOutcome.Fail(CoreFailure.StartFailed, ex.Message);
            }
            _process = handle;
            if (_job == IntPtr.Zero) _job = CoreJob.CreateKillOnCloseJob();
            try
            {
                if (handle is SystemProcessHandle sph) CoreJob.Assign(_job, sph.UnderlyingProcess);
            }
            catch (Exception ex)
            {
                AppLog.Error("核心进程挂载 Job 失败（不影响正常启停）", ex);
            }
            handle.Output += OnProcessOutput;
            handle.Exited += OnExited;
            SetState(CoreState.Starting);
            _probeCts?.Cancel();
            _probeCts?.Dispose();
            _probeCts = new CancellationTokenSource();
            var token = _probeCts.Token;
            _ = ProbeLoopAsync(token);
            return CoreOutcome.Success(null);
        }
        catch (OperationCanceledException)
        {
            return CoreOutcome.Fail(CoreFailure.Cancelled);
        }
        finally
        {
            if (acquired) _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _userStop = true;
        _consecutiveCrashes = 0;
        var h = _process;
        _process = null;
        try { _probeCts?.Cancel(); } catch { }
        if (h is not null)
        {
            try { h.Output -= OnProcessOutput; } catch { }
            try { h.Exited -= OnExited; } catch { }
            try { h.Kill(true); h.WaitForExit(5000); } catch (Exception ex) { AppLog.Error("停止 mihomo 失败", ex); }
            try { h.Dispose(); } catch { }
        }
        SetState(CoreState.Stopped);
        await Task.CompletedTask;
    }

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < 50; i++)
            {
                try { await _delay.Delay(TimeSpan.FromMilliseconds(300), ct); } catch (OperationCanceledException) { return; }
                if (State != CoreState.Starting || ct.IsCancellationRequested) return;
                var probe = _probe;
                if (probe is null) return;
                try
                {
                    var v = await probe.GetVersionAsync(CancellationToken.None);
                    if (v is not null)
                    {
                        SetState(CoreState.Running);
                        AppLog.Info($"mihomo 已启动（{v}）");
                        return;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch { }
            }
            AppLog.Error("核心健康检查超时，详情见 core.log");
        }
        catch { }
    }

    private void OnExited(int? exitCode)
    {
        var h = _process;
        _process = null;
        SetState(CoreState.Stopped);
        try { _probeCts?.Cancel(); } catch { }
        if (_clock.UtcNow - _startedAt > TimeSpan.FromSeconds(60)) _consecutiveCrashes = 0;
        else _consecutiveCrashes++;
        if (_consecutiveCrashes == 3 || (_consecutiveCrashes > 3 && _consecutiveCrashes % 10 == 0))
            CrashLoop?.Invoke(_consecutiveCrashes);
        if (_userStop || _lastLaunch is null || _lastEndpoint is null) return;
        var launch = _lastLaunch;
        var endpoint = _lastEndpoint;
        AppLog.Error($"mihomo 异常退出（ExitCode={exitCode?.ToString() ?? "未知"}），3 秒后自动重启");
        _ = _delay.Delay(TimeSpan.FromSeconds(3), CancellationToken.None).ContinueWith(async _ =>
        {
            if (_userStop || State != CoreState.Stopped) return;
            try { await StartAsync(launch, endpoint); } catch { SetState(CoreState.Stopped); }
        }, TaskScheduler.Default);
    }

    private void OnProcessOutput(string line)
    {
        try { AppLog.AppendCore($"{_clock.Now:HH:mm:ss} {line}"); } catch { }
        try { Output?.Invoke(line); } catch { }
    }

    private void SetState(CoreState state)
    {
        if (State == state) return;
        State = state;
        try { StateChanged?.Invoke(state); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        try { if (_job != IntPtr.Zero) CoreJob.Close(_job); } catch { }
        _job = IntPtr.Zero;
        try { _probeCts?.Cancel(); _probeCts?.Dispose(); } catch { }
        try { _gate.Dispose(); } catch { }
        try
        {
            if (_probe is not null && _probe != _probeOverride) _probe.Dispose();
        }
        catch { }
        try { (_processPort as IDisposable)?.Dispose(); } catch { }
        try { (_clock as IDisposable)?.Dispose(); } catch { }
        try { (_delay as IDisposable)?.Dispose(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await ValueTask.CompletedTask;
    }
}

#region Adapters
internal sealed class HttpProbeAdapter : IProbePort
{
    private readonly HttpClient _http;
    public HttpProbeAdapter(string addr, string secret)
    {
        _http = new HttpClient { BaseAddress = new Uri($"http://{addr}"), Timeout = TimeSpan.FromSeconds(2) };
        if (!string.IsNullOrEmpty(secret))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }
    public async Task<string?> GetVersionAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync("/version", ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
    public void Dispose() => _http.Dispose();
}

internal sealed class SystemProcessAdapter : IProcessPort
{
    public IProcessHandle Start(ProcessSpec spec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            Arguments = spec.Arguments,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = spec.CreateNoWindow,
        };
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var handle = new SystemProcessHandle(proc);
        if (!proc.Start())
        {
            handle.Dispose();
            throw new InvalidOperationException("无法启动 mihomo 进程");
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return handle;
    }
}

internal sealed class SystemProcessHandle : IProcessHandle
{
    private readonly Process _proc;
    public SystemProcessHandle(Process proc)
    {
        _proc = proc;
        _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); };
        _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); };
        _proc.Exited += (_, _) => Exited?.Invoke(TryExitCode(_proc));
    }
    public bool HasExited
    {
        get { try { return _proc.HasExited; } catch { return true; } }
    }
    public int? ExitCode => TryExitCode(_proc);
    public event Action<string>? Output;
    public event Action<int?>? Exited;
    public void Kill(bool entireTree = true) { try { if (!_proc.HasExited) _proc.Kill(entireTree); } catch { } }
    public bool WaitForExit(int ms) { try { return _proc.WaitForExit(ms); } catch { return false; } }
    public void Dispose() { try { _proc.Dispose(); } catch { } }
    public Process UnderlyingProcess => _proc;
    private static int? TryExitCode(Process? p) { try { return p?.ExitCode; } catch { return null; } }
}

internal sealed class SystemClockAdapter : IClockPort
{
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime Now => DateTime.Now;
}

internal sealed class SystemDelayAdapter : IDelayPort
{
    public Task Delay(TimeSpan d, CancellationToken ct) => Task.Delay(d, ct);
}

public sealed class FakeProbeAdapter : IProbePort
{
    public Queue<string?> VersionSequence { get; } = new();
    public int CallCount { get; private set; }
    public FakeProbeAdapter() { }
    public FakeProbeAdapter(IEnumerable<string?> seq) { foreach (var s in seq) VersionSequence.Enqueue(s); }
    public Task<string?> GetVersionAsync(CancellationToken ct)
    {
        CallCount++;
        if (ct.IsCancellationRequested) return Task.FromCanceled<string?>(ct);
        var v = VersionSequence.Count > 0 ? VersionSequence.Dequeue() : null;
        return Task.FromResult(v);
    }
    public void Enqueue(string? v) => VersionSequence.Enqueue(v);
    public void Dispose() { }
}

public sealed class FakeProcessAdapter : IProcessPort
{
    public ManualProcessHandle? LastHandle { get; private set; }
    public int StartCount { get; private set; }
    public bool ThrowOnStart { get; set; }
    public string? LastFileName { get; private set; }
    public string? LastArguments { get; private set; }
    public IProcessHandle Start(ProcessSpec spec)
    {
        if (ThrowOnStart) throw new InvalidOperationException("fake start failed");
        LastFileName = spec.FileName;
        LastArguments = spec.Arguments;
        StartCount++;
        LastHandle = new ManualProcessHandle();
        return LastHandle;
    }
}

public sealed class ManualProcessHandle : IProcessHandle
{
    private bool _hasExited;
    private int? _exitCode;
    public bool HasExited => _hasExited;
    public int? ExitCode => _exitCode;
    public event Action<string>? Output;
    public event Action<int?>? Exited;
    public void SimulateOutput(string line) => Output?.Invoke(line);
    public void SimulateExit(int? code)
    {
        if (_hasExited) return;
        _hasExited = true;
        _exitCode = code;
        Exited?.Invoke(code);
    }
    public void Kill(bool entireTree = true)
    {
        if (!_hasExited) SimulateExit(null);
    }
    public bool WaitForExit(int ms) => true;
    public void Dispose() { }
}

public sealed class ManualClockAdapter : IClockPort
{
    private DateTime _utcNow;
    private DateTime _now;
    public ManualClockAdapter(DateTime start) { _utcNow = start; _now = start; }
    public ManualClockAdapter(DateTime utcNow, DateTime now) { _utcNow = utcNow; _now = now; }
    public DateTime UtcNow => _utcNow;
    public DateTime Now => _now;
    public void Advance(TimeSpan d) { _utcNow += d; _now += d; }
    public void Set(DateTime utcNow, DateTime now) { _utcNow = utcNow; _now = now; }
}

public sealed class ManualDelayAdapter : IDelayPort
{
    private readonly List<Pending> _pending = [];
    private readonly Lock _gate = new();
    private sealed class Pending
    {
        public TimeSpan Duration;
        public TaskCompletionSource<bool> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Ct;
        public CancellationTokenRegistration Reg;
    }
    public Task Delay(TimeSpan d, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        var p = new Pending { Duration = d, Ct = ct };
        if (ct.CanBeCanceled) p.Reg = ct.Register(() => p.Tcs.TrySetCanceled(ct));
        lock (_gate) _pending.Add(p);
        return p.Tcs.Task;
    }
    public void Advance(TimeSpan? span = null)
    {
        List<Pending> toComplete;
        lock (_gate) { toComplete = [.. _pending]; _pending.Clear(); }
        foreach (var p in toComplete) { p.Reg.Dispose(); p.Tcs.TrySetResult(true); }
    }
    public Task AdvanceAsync(TimeSpan? span = null)
    {
        Advance(span);
        return Task.CompletedTask;
    }
    public void Advance(int ms) => Advance(TimeSpan.FromMilliseconds(ms));
    public int PendingCount { get { lock (_gate) return _pending.Count; } }
}
#endregion
