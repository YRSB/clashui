using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clashui.Core;

/// mihomo RESTful API（external-controller）的轻量客户端。
public sealed class MihomoApiClient : IDisposable
{
    private readonly HttpClient _http;

    public MihomoApiClient(string controllerAddr, string secret)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://{controllerAddr}"),
            // 热重载在面板 WebSocket 流较多时可能偏慢，留足余量
            Timeout = TimeSpan.FromSeconds(15),
        };
        if (!string.IsNullOrEmpty(secret))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/version", ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
    }

    /// 热重载指定配置文件（force=true 时即使路径相同也重载）。路径统一用正斜杠，
    /// mihomo 对请求体里的反斜杠路径会报 "Body invalid"。
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
