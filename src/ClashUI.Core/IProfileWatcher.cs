namespace ClashUI.Core;

public interface IProfileWatcher : IDisposable
{
    event Action<string> Changed;
    void Start(string dir);
}

public sealed class FileProfileWatcher : IProfileWatcher
{
    private FileSystemWatcher? _watcher;

    public event Action<string>? Changed;

    public void Start(string dir)
    {
        Directory.CreateDirectory(dir);
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(dir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, e) => Changed?.Invoke(e.FullPath);
        _watcher.Renamed += (_, e) => Changed?.Invoke(e.FullPath);
    }

    public void Dispose() => _watcher?.Dispose();
}

public sealed class ManualProfileWatcher : IProfileWatcher
{
    public event Action<string>? Changed;

    public void Start(string dir) { }

    public void Trigger(string path) => Changed?.Invoke(path);

    public void Dispose() { }
}
