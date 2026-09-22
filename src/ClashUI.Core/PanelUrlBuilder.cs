namespace ClashUI.Core;

public static class PanelUrlBuilder
{
    public static string Build(string? panelId, string controllerAddr, string? secret, int panelPort)
    {
        var id = PanelCatalog.Normalize(panelId);
        var addr = controllerAddr ?? "";
        var sep = addr.LastIndexOf(':');
        var host = sep > 0 ? addr[..sep] : addr;
        var port = sep > 0 ? addr[(sep + 1)..] : "";
        var query = $"hostname={Uri.EscapeDataString(host)}";
        if (port.Length > 0)
            query += $"&port={Uri.EscapeDataString(port)}";
        query += $"&secret={Uri.EscapeDataString(secret ?? "")}";
        var panel = PanelCatalog.Get(id);
        if (panel.QueryMode == PanelQueryMode.QueryBeforeHash)
            return $"http://127.0.0.1:{panelPort}/{panel.DirName}/?{query}#/proxies";
        return $"http://127.0.0.1:{panelPort}/{panel.DirName}/#/setup?{query}";
    }
}
