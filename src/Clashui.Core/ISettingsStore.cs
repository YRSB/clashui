namespace Clashui.Core;

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings s);
}

public sealed class FileSettingsStore : ISettingsStore
{
    public AppSettings Load() => SettingsStore.Load();
    public void Save(AppSettings settings) => SettingsStore.Save(settings);
}

public sealed class InMemorySettingsStore : ISettingsStore
{
    private AppSettings _settings;

    public InMemorySettingsStore(AppSettings? initial = null)
    {
        _settings = initial ?? new AppSettings
        {
            Secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
        };
    }

    public AppSettings Load() => _settings;
    public void Save(AppSettings s) => _settings = s;
}
