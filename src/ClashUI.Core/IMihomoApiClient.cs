namespace ClashUI.Core;

public interface IMihomoApiClient : IProbePort, IDisposable
{
    Task<bool> ReloadConfigAsync(string configPath, CancellationToken ct = default);
}
