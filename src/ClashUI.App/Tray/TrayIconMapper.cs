namespace ClashUI.App.Tray;

public static class TrayIconMapper
{
    public static (string IconFile, string Tooltip) Map(TrayState s)
    {
        var iconFile = !s.IsCoreRunning ? "app-off.ico"
            : s.TunEnabled ? "app-tun.ico"
            : s.SystemProxyEnabled ? "app-proxy.ico"
            : "app.ico";
        var tooltip = !s.IsCoreRunning ? "ClashUI — 核心未运行"
            : s.TunEnabled ? "ClashUI — 核心运行中（TUN）"
            : s.SystemProxyEnabled ? "ClashUI — 核心运行中（系统代理）"
            : "ClashUI — 核心运行中";
        return (iconFile, tooltip);
    }

    public static TrayViewModel ToViewModel(TrayState s)
    {
        var (iconFile, tooltip) = Map(s);
        var profiles = s.Profiles.Select(p => new ProfileEntry(p, Path.GetFileName(p), string.Equals(p, s.ActiveProfile, StringComparison.OrdinalIgnoreCase))).ToList();
        return new TrayViewModel(s.SystemProxyEnabled, s.TunEnabled, s.SilentStart, s.AutoStartEnabled, iconFile, tooltip, profiles, profiles.Count == 0);
    }
}
