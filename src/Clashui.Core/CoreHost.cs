using System.Diagnostics;

namespace Clashui.Core;

public enum CoreState { Stopped, Starting, Running }

/// 托管 mihomo 子进程：启动 / 停止 / 崩溃自动重启，stdout/stderr 落盘并转发事件。
[Obsolete("Use CoreRuntime", true)]
internal sealed class CoreHost : IDisposable
{
    private Process? _process;
    private bool _userStop;
    private string? _lastExe;
    private string? _lastArgs;
    private bool _disposed;
    private IntPtr _job = IntPtr.Zero;
    private DateTime _startedAt;
    private int _consecutiveCrashes;

    public CoreState State { get; private set; } = CoreState.Stopped;

    public event Action<CoreState>? StateChanged;
    public event Action<string>? Output;
    /// 核心快速连续崩溃（非用户停止）时触发，参数为连续次数。
    public event Action<int>? CrashLoop;

    public void Start(string exePath, string arguments)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State != CoreState.Stopped) return;
        _userStop = false;
        _lastExe = exePath;
        _lastArgs = arguments;
        StartCoreProcess(exePath, arguments);
    }

    public void MarkRunning() => SetState(CoreState.Running);

    public void Stop()
    {
        _userStop = true;
        _consecutiveCrashes = 0;
        var proc = _process;
        _process = null;
        if (proc is not null && !proc.HasExited)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                AppLog.Error("停止 mihomo 失败", ex);
            }
        }
        SetState(CoreState.Stopped);
    }

    private void StartCoreProcess(string exePath, string arguments)
    {
        SetState(CoreState.Starting);
        _startedAt = DateTime.UtcNow;
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            WorkingDirectory = AppPaths.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) OnOutput(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) OnOutput(e.Data); };
        proc.Exited += (_, _) => OnExited();
        if (!proc.Start())
        {
            SetState(CoreState.Stopped);
            throw new InvalidOperationException("无法启动 mihomo 进程");
        }
        _process = proc;
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // 挂到 kill-on-close Job：应用被强杀/崩溃时核心随之终止，避免孤儿进程
        if (_job == IntPtr.Zero) _job = CoreJob.CreateKillOnCloseJob();
        CoreJob.Assign(_job, proc);
    }

    private void OnExited()
    {
        // 先捕获再置空（同 Stop()），退出码只能从局部变量读——字段已空
        var proc = _process;
        _process = null;
        SetState(CoreState.Stopped);

        // 存活超过 60 秒视为稳定运行，重置连续崩溃计数
        if (DateTime.UtcNow - _startedAt > TimeSpan.FromSeconds(60)) _consecutiveCrashes = 0;
        else _consecutiveCrashes++;
        if (_consecutiveCrashes == 3 || (_consecutiveCrashes > 3 && _consecutiveCrashes % 10 == 0))
            CrashLoop?.Invoke(_consecutiveCrashes);

        if (_userStop || _lastExe is null || _lastArgs is null) return;

        var exe = _lastExe;
        var args = _lastArgs;
        AppLog.Error($"mihomo 异常退出（ExitCode={TryExitCode(proc)?.ToString() ?? "未知"}），3 秒后自动重启");
        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            if (_userStop || State != CoreState.Stopped) return;
            try { StartCoreProcess(exe, args); }
            catch (Exception ex)
            {
                AppLog.Error("重启 mihomo 失败", ex);
                SetState(CoreState.Stopped);
            }
        }, TaskScheduler.Default);
    }

    private static int? TryExitCode(Process? proc)
    {
        try { return proc?.ExitCode; }
        catch { return null; }
    }

    private void OnOutput(string line)
    {
        try { File.AppendAllText(AppPaths.CoreLogFile, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}"); }
        catch { }
        Output?.Invoke(line);
    }

    private void SetState(CoreState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        CoreJob.Close(_job);
        _job = IntPtr.Zero;
    }
}
