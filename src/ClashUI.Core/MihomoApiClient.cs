using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClashUI.Core;

public sealed class MihomoApiClient : IMihomoApiClient
{
    private readonly HttpClient _http;

    public MihomoApiClient(string controllerAddr, string secret)
    {
        var endpoint = ControllerEndpointParser.Parse(controllerAddr);
        _http = new HttpClient
        {
            BaseAddress = endpoint.BaseUri,
            Timeout = TimeSpan.FromSeconds(15),
        };
        if (!string.IsNullOrEmpty(secret))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var resp = await _http.GetAsync("/version", cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            return doc.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    public async Task<bool> ReloadConfigAsync(string configPath, CancellationToken ct = default)
    {
        var normalized = configPath.Replace('\\', '/');
        using var content = JsonContent.Create(new ReloadBody { Path = normalized }, ApiJson.Default.ReloadBody);
        using var resp = await _http.PutAsync("/configs?force=true", content, ct);
        return resp.IsSuccessStatusCode;
    }

    public void Dispose() => _http.Dispose();
}

public sealed class ReloadBody
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";
}

[JsonSerializable(typeof(ReloadBody))]
internal sealed partial class ApiJson : JsonSerializerContext { }
