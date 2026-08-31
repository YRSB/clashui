namespace Clashui.Core;

public interface IConfigComposer
{
    string Compose(AppSettings s);
    string DashboardUrlFor(AppSettings s);
}

internal static class DashboardUrlHelper
{
    internal static string Build(AppSettings s)
    {
        var addr = s.ControllerAddr;
        var sep = addr.LastIndexOf(':');
        var host = sep > 0 ? addr[..sep] : addr;
        var port = sep > 0 ? addr[(sep + 1)..] : "";
        var query = $"hostname={Uri.EscapeDataString(host)}";
        if (port.Length > 0) query += $"&port={Uri.EscapeDataString(port)}";
        query += $"&secret={Uri.EscapeDataString(s.Secret ?? "")}";
        return $"http://{addr}/ui/#/setup?{query}";
    }
}

public sealed class DefaultConfigComposer : IConfigComposer
{
    public string Compose(AppSettings s) => ConfigComposer.Compose(s);
    public string DashboardUrlFor(AppSettings s) => DashboardUrlHelper.Build(s);
}

public sealed class FakeConfigComposer : IConfigComposer
{
    public Func<AppSettings, string>? ComposeFunc;
    public Func<AppSettings, string>? DashboardUrlFunc;
    public int ComposeCallCount;

    public string Compose(AppSettings s)
    {
        ComposeCallCount++;
        return ComposeFunc?.Invoke(s) ?? AppPaths.RuntimeConfigFile;
    }

    public string DashboardUrlFor(AppSettings s)
    {
        if (DashboardUrlFunc is not null) return DashboardUrlFunc(s);
        return DashboardUrlHelper.Build(s);
    }
}
