namespace Clashui.Core;

public interface IMihomoApiClient : IDisposable
{
    Task<string?> GetVersionAsync(CancellationToken ct = default);
    Task<bool> ReloadConfigAsync(string configPath, CancellationToken ct = default);
}
