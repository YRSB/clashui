using System.Net;
using System.Net.Sockets;
using System.Text;
using ClashUI.Core;

namespace ClashUI.App.Services;

public sealed class PanelStaticServer : IDisposable
{
    public const int StartPort = 11233;
    public const int EndPort = 11243;

    public int ActualPort { get; private set; }

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public bool Start()
    {
        for (var port = StartPort; port <= EndPort; port++)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
            }
            catch
            {
                try { listener.Stop(); } catch { }
                continue;
            }
            _listener = listener;
            ActualPort = port;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return true;
        }
        return false;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null)
            return;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested)
                    break;
                continue;
            }
            _ = Task.Run(() => HandleClient(client), ct);
        }
    }

    private static void HandleClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 5000;
                var header = ReadHeader(stream);
                if (header is null)
                    return;
                var lineEnd = header.IndexOf("\r\n", StringComparison.Ordinal);
                var requestLine = lineEnd >= 0 ? header[..lineEnd] : header;
                var parts = requestLine.Split(' ');
                if (parts.Length < 2)
                {
                    WriteStatus(stream, "GET", 400, "Bad Request", "text/plain; charset=utf-8", "Bad Request"u8.ToArray());
                    return;
                }
                var method = parts[0];
                var rawTarget = parts[1];
                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    WriteStatus(stream, "GET", 405, "Method Not Allowed", "text/plain; charset=utf-8", "Method Not Allowed"u8.ToArray());
                    return;
                }
                var path = rawTarget.Split('?')[0].Split('#')[0];
                string decoded;
                try
                {
                    decoded = Uri.UnescapeDataString(path);
                }
                catch
                {
                    WriteStatus(stream, method, 400, "Bad Request", "text/plain; charset=utf-8", "Bad Request"u8.ToArray());
                    return;
                }
                var trimmed = decoded.TrimStart('/');
                var slash = trimmed.IndexOf('/');
                string panelId;
                string relative;
                if (slash < 0)
                {
                    panelId = trimmed;
                    relative = "";
                }
                else
                {
                    panelId = trimmed[..slash];
                    relative = trimmed[(slash + 1)..];
                }
                if (panelId.Length == 0)
                {
                    panelId = PanelCatalog.Default;
                }
                else if (!PanelCatalog.IsValid(panelId))
                {
                    WriteStatus(stream, method, 404, "Not Found", "text/plain; charset=utf-8", "Not Found"u8.ToArray());
                    return;
                }
                else
                {
                    panelId = PanelCatalog.Normalize(panelId);
                }
                var panelRoot = AppPaths.PanelDir(panelId);
                if (relative.Length == 0 || relative.EndsWith('/'))
                    relative += "index.html";
                relative = relative.Replace('/', Path.DirectorySeparatorChar);
                var full = Path.GetFullPath(Path.Combine(panelRoot, relative));
                var rootFull = Path.GetFullPath(panelRoot);
                if (!full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
                    && !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    WriteStatus(stream, method, 403, "Forbidden", "text/plain; charset=utf-8", "Forbidden"u8.ToArray());
                    return;
                }
                string file = full;
                if (!File.Exists(file))
                {
                    var fallback = Path.Combine(rootFull, "index.html");
                    if (File.Exists(fallback))
                        file = fallback;
                    else
                    {
                        WriteStatus(stream, method, 404, "Not Found", "text/plain; charset=utf-8", "Not Found"u8.ToArray());
                        return;
                    }
                }
                byte[] body;
                try
                {
                    body = File.ReadAllBytes(file);
                }
                catch (Exception ex)
                {
                    AppLog.Error($"静态面板读取失败 {file}", ex);
                    WriteStatus(stream, method, 404, "Not Found", "text/plain; charset=utf-8", "Not Found"u8.ToArray());
                    return;
                }
                WriteStatus(stream, method, 200, "OK", MimeFor(file), body);
            }
            catch (Exception ex)
            {
                AppLog.Error("静态面板请求失败", ex);
            }
        }
    }

    private static string? ReadHeader(NetworkStream stream)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        int total = 0;
        while (total < 16384)
        {
            int read;
            try
            {
                read = stream.Read(buffer, 0, buffer.Length);
            }
            catch
            {
                return null;
            }
            if (read <= 0)
                break;
            ms.Write(buffer, 0, read);
            total += read;
            var text = Encoding.ASCII.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                return text;
            if (!stream.DataAvailable)
                break;
        }
        if (ms.Length == 0)
            return null;
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private static void WriteStatus(NetworkStream stream, string method, int code, string reason, string contentType, byte[] body)
    {
        var header = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && body.Length > 0)
            stream.Write(body, 0, body.Length);
    }

    private static string MimeFor(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".json" or ".map" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".ttf" => "font/ttf",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream",
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _cts?.Dispose(); } catch { }
    }
}
