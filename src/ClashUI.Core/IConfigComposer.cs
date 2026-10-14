namespace ClashUI.Core;

public interface IConfigComposer
{
    string Compose(AppSettings s);
    string DashboardUrlFor(AppSettings s);
}

internal static class DashboardUrlHelper
{
    internal static string Build(AppSettings s)
    {
        var endpoint = ControllerEndpointParser.Parse(s.ControllerAddr);
        var query = $"hostname={Uri.EscapeDataString(endpoint.ConnectHost)}&port={endpoint.Port}&secret={Uri.EscapeDataString(s.Secret ?? "")}";
        return $"http://{endpoint.DisplayAddr}/ui/#/setup?{query}";
    }
}

public sealed class DefaultConfigComposer : IConfigComposer
{
    public int PanelPort { get; set; }
    public string Compose(AppSettings s) => ConfigComposer.Compose(s);
    public string DashboardUrlFor(AppSettings s)
        => PanelPort > 0 ? PanelUrlBuilder.Build(s.ActivePanel, s.ControllerAddr, s.Secret, PanelPort) : DashboardUrlHelper.Build(s);
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
