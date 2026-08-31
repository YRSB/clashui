using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clashui.Core;

public sealed class MihomoApiClient : IMihomoApiClient
{
    private readonly HttpClient _http;

    public MihomoApiClient(string controllerAddr, string secret)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://{controllerAddr}"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        if (!string.IsNullOrEmpty(secret))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/version", ct);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
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
