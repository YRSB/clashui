namespace Clashui.Core;

public static class AppLog
{
    private static readonly Lock Gate = new();

    public static event Action<string>? Line;

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} :: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (Gate)
        {
            try { File.AppendAllText(Path.Combine(AppPaths.LogsDir, "app.log"), line + Environment.NewLine); }
            catch { /* 日志失败不向上抛 */ }
        }
        Line?.Invoke(line);
    }
}
