namespace ClashUI.Core;

public static class PanelUrlBuilder
{
    public static string Build(string? panelId, string controllerAddr, string? secret, int panelPort)
    {
        var id = PanelCatalog.Normalize(panelId);
        var endpoint = ControllerEndpointParser.Parse(controllerAddr);
        var query = $"hostname={Uri.EscapeDataString(endpoint.ConnectHost)}&port={endpoint.Port}&secret={Uri.EscapeDataString(secret ?? "")}";
        var panel = PanelCatalog.Get(id);
        if (panel.QueryMode == PanelQueryMode.QueryBeforeHash)
            return $"http://127.0.0.1:{panelPort}/{panel.DirName}/?{query}#/proxies";
        return $"http://127.0.0.1:{panelPort}/{panel.DirName}/#/setup?{query}";
    }
}
