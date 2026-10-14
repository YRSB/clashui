namespace ClashUI.Core;

public sealed record ControllerEndpoint(string ConnectHost, int Port, string DisplayAddr, Uri BaseUri);

public static class ControllerEndpointParser
{
    public static ControllerEndpoint Parse(string? rawAddr, int defaultPort = 9090)
    {
        var raw = (rawAddr ?? "").Trim();
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            raw = raw["http://".Length..];
        else if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            raw = raw["https://".Length..];

        string hostPart;
        int port = defaultPort;

        if (string.IsNullOrEmpty(raw))
        {
            hostPart = "127.0.0.1";
        }
        else if (raw.StartsWith('['))
        {
            var closeIdx = raw.IndexOf(']');
            if (closeIdx > 0)
            {
                hostPart = raw[1..closeIdx];
                var colonIdx = raw.IndexOf(':', closeIdx);
                if (colonIdx >= 0 && int.TryParse(raw[(colonIdx + 1)..], out var p))
                    port = p;
            }
            else
            {
                hostPart = raw.Trim('[', ']');
            }
        }
        else
        {
            var colonIdx = raw.LastIndexOf(':');
            if (colonIdx >= 0)
            {
                hostPart = raw[..colonIdx];
                if (int.TryParse(raw[(colonIdx + 1)..], out var p))
                    port = p;
            }
            else
            {
                hostPart = raw;
            }
        }

        hostPart = hostPart.Trim();
        if (string.IsNullOrEmpty(hostPart) || hostPart == "0.0.0.0")
            hostPart = "127.0.0.1";
        else if (hostPart == "::" || hostPart == "[::]")
            hostPart = "127.0.0.1";

        var connectHost = hostPart.Trim('[', ']');
        var uriHost = connectHost.Contains(':') ? $"[{connectHost}]" : connectHost;
        var baseUri = new Uri($"http://{uriHost}:{port}");
        return new ControllerEndpoint(connectHost, port, $"{connectHost}:{port}", baseUri);
    }
}
