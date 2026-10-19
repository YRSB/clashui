using System.Buffers;
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

    private sealed record RequestInfo(string Method, string Target, string? IfNoneMatch, DateTimeOffset? IfModifiedSince);

    private static void HandleClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 5000;
                var req = ReadRequest(stream);
                if (req is null)
                    return;
                if (!string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(req.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    WriteStatus(stream, "GET", 405, "Method Not Allowed", "text/plain; charset=utf-8", "Method Not Allowed"u8.ToArray());
                    return;
                }
                var path = req.Target.Split('?')[0].Split('#')[0];
                string decoded;
                try
                {
                    decoded = Uri.UnescapeDataString(path);
                }
                catch
                {
                    WriteStatus(stream, req.Method, 400, "Bad Request", "text/plain; charset=utf-8", "Bad Request"u8.ToArray());
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
                    WriteStatus(stream, req.Method, 404, "Not Found", "text/plain; charset=utf-8", "Not Found"u8.ToArray());
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
                    WriteStatus(stream, req.Method, 403, "Forbidden", "text/plain; charset=utf-8", "Forbidden"u8.ToArray());
                    return;
                }
                string file = full;
                if (!File.Exists(file))
                {
                    var ext = Path.GetExtension(relative);
                    var fallback = Path.Combine(rootFull, "index.html");
                    if (string.IsNullOrEmpty(ext) && File.Exists(fallback))
                        file = fallback;
                    else
                    {
                        WriteStatus(stream, req.Method, 404, "Not Found", "text/plain; charset=utf-8", "Not Found"u8.ToArray());
                        return;
                    }
                }
                ServeFile(stream, req, file);
            }
            catch (Exception ex)
            {
                AppLog.Error("静态面板请求失败", ex);
            }
        }
    }

    private static void ServeFile(NetworkStream stream, RequestInfo req, string file)
    {
        try
        {
            var fileInfo = new FileInfo(file);
            if (!fileInfo.Exists)
            {
                WriteStatus(stream, req.Method, 404, "Not Found", "text/plain; charset=utf-8", "Not Found"u8.ToArray());
                return;
            }

            var isHtml = file.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                         || file.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

            var lastModified = fileInfo.LastWriteTimeUtc;
            var etag = $"\"{lastModified.Ticks:x}-{fileInfo.Length:x}\"";

            if (!isHtml)
            {
                bool matchesEtag = req.IfNoneMatch is not null && string.Equals(req.IfNoneMatch, etag, StringComparison.Ordinal);
                bool matchesDate = req.IfModifiedSince.HasValue && req.IfModifiedSince.Value.UtcDateTime >= lastModified.AddSeconds(-1);

                if (matchesEtag || matchesDate)
                {
                    var notModifiedHeader = $"HTTP/1.1 304 Not Modified\r\nETag: {etag}\r\nConnection: close\r\nCache-Control: public, max-age=3600\r\n\r\n";
                    var nmb = Encoding.ASCII.GetBytes(notModifiedHeader);
                    stream.Write(nmb, 0, nmb.Length);
                    return;
                }
            }

            using var fs = fileInfo.OpenRead();
            var cacheControl = isHtml ? "no-cache" : "public, max-age=3600";
            var header = $"HTTP/1.1 200 OK\r\nContent-Type: {MimeFor(file)}\r\nContent-Length: {fs.Length}\r\nETag: {etag}\r\nConnection: close\r\nCache-Control: {cacheControl}\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase) && fs.Length > 0)
                fs.CopyTo(stream);
        }
        catch (Exception ex)
        {
            AppLog.Error($"静态面板读取失败 {file}", ex);
            WriteStatus(stream, req.Method, 404, "Not Found", "text/plain; charset=utf-8", "Not Found"u8.ToArray());
        }
    }

    private static RequestInfo? ReadRequest(NetworkStream stream)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int total = 0;
            while (total < 4096)
            {
                int read;
                try
                {
                    read = stream.Read(buffer, total, buffer.Length - total);
                }
                catch
                {
                    return null;
                }
                if (read <= 0) break;
                total += read;
                var span = buffer.AsSpan(0, total);
                int endIdx = span.IndexOf("\r\n\r\n"u8);
                if (endIdx >= 0)
                {
                    return ParseRequest(span[..endIdx]);
                }
            }
            if (total > 0)
                return ParseRequest(buffer.AsSpan(0, total));
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static RequestInfo? ParseRequest(ReadOnlySpan<byte> headerSpan)
    {
        int lineEnd = headerSpan.IndexOf("\r\n"u8);
        var requestLineSpan = lineEnd >= 0 ? headerSpan[..lineEnd] : headerSpan;
        var requestLine = Encoding.ASCII.GetString(requestLineSpan);
        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return null;

        string method = parts[0];
        string target = parts[1];
        string? ifNoneMatch = null;
        DateTimeOffset? ifModifiedSince = null;

        if (lineEnd >= 0)
        {
            var rest = headerSpan[(lineEnd + 2)..];
            while (!rest.IsEmpty)
            {
                int nextEnd = rest.IndexOf("\r\n"u8);
                ReadOnlySpan<byte> line = nextEnd >= 0 ? rest[..nextEnd] : rest;
                if (line.IsEmpty) break;

                if (StartsWithIgnoreCase(line, "If-None-Match:"u8))
                {
                    var valSpan = line["If-None-Match:"u8.Length..].Trim((byte)' ');
                    if (!valSpan.IsEmpty)
                        ifNoneMatch = Encoding.ASCII.GetString(valSpan);
                }
                else if (StartsWithIgnoreCase(line, "If-Modified-Since:"u8))
                {
                    var valSpan = line["If-Modified-Since:"u8.Length..].Trim((byte)' ');
                    if (!valSpan.IsEmpty)
                    {
                        var rawDate = Encoding.ASCII.GetString(valSpan);
                        if (DateTimeOffset.TryParse(rawDate, out var dt))
                            ifModifiedSince = dt;
                    }
                }

                if (nextEnd < 0) break;
                rest = rest[(nextEnd + 2)..];
            }
        }

        return new RequestInfo(method, target, ifNoneMatch, ifModifiedSince);
    }

    private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> span, ReadOnlySpan<byte> prefix)
    {
        if (span.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            byte b1 = span[i];
            byte b2 = prefix[i];
            if (b1 != b2 && (b1 | 0x20) != (b2 | 0x20)) return false;
        }
        return true;
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
