using System.Text;

namespace ClashUI.Core;

public static class AppLog
{
    private static readonly Lock Gate = new();
    private static StreamWriter? _appWriter;
    private static StreamWriter? _coreWriter;

    public static event Action<string>? Line;

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} :: {ex}");

    public static void AppendCore(string line)
    {
        lock (Gate)
        {
            try
            {
                var w = EnsureCoreWriter();
                w.WriteLine(line);
            }
            catch { }
        }
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (Gate)
        {
            try
            {
                var w = EnsureAppWriter();
                w.WriteLine(line);
            }
            catch { }
        }
        Line?.Invoke(line);
    }

    private static StreamWriter EnsureAppWriter()
    {
        if (_appWriter is not null) return _appWriter;
        Directory.CreateDirectory(AppPaths.LogsDir);
        var fs = new FileStream(Path.Combine(AppPaths.LogsDir, "app.log"), FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan);
        _appWriter = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        return _appWriter;
    }

    private static StreamWriter EnsureCoreWriter()
    {
        if (_coreWriter is not null) return _coreWriter;
        Directory.CreateDirectory(AppPaths.LogsDir);
        var fs = new FileStream(AppPaths.CoreLogFile, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan);
        _coreWriter = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        return _coreWriter;
    }
}
