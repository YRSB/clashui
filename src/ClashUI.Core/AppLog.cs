using System.Text;
using System.Threading.Channels;

namespace ClashUI.Core;

public static class AppLog
{
    private static readonly Channel<string> LogChannel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private const long MaxLogFileBytes = 2 * 1024 * 1024; // 2MB
    private static readonly Task WorkerTask = Task.Run(ProcessLogQueueAsync);

    public static event Action<string>? Line;

    public static void Flush()
    {
        LogChannel.Writer.TryComplete();
        try { WorkerTask.Wait(500); } catch { }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} :: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        LogChannel.Writer.TryWrite(line);
        try { Line?.Invoke(line); } catch { }
    }

    private static async Task ProcessLogQueueAsync()
    {
        StreamWriter? writer = null;
        var logFile = Path.Combine(AppPaths.LogsDir, "app.log");
        var oldLogFile = Path.Combine(AppPaths.LogsDir, "app.log.old");
        var reader = LogChannel.Reader;

        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var line))
                {
                    try
                    {
                        if (writer is null)
                        {
                            Directory.CreateDirectory(AppPaths.LogsDir);
                            RotateIfNeeded(logFile, oldLogFile);
                            var fs = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.None);
                            writer = new StreamWriter(fs, new UTF8Encoding(false));
                        }

                        writer.WriteLine(line);

                        if (writer.BaseStream.Length > MaxLogFileBytes)
                        {
                            writer.Dispose();
                            writer = null;
                            RotateIfNeeded(logFile, oldLogFile);
                        }
                    }
                    catch { }
                }
                try { writer?.Flush(); } catch { }
            }
        }
        catch { }
        finally
        {
            try { writer?.Dispose(); } catch { }
        }
    }

    private static void RotateIfNeeded(string logFile, string oldLogFile)
    {
        try
        {
            if (File.Exists(logFile) && new FileInfo(logFile).Length > MaxLogFileBytes)
            {
                File.Move(logFile, oldLogFile, overwrite: true);
            }
        }
        catch { }
    }
}
