namespace ClashUI.App.Tray;

public sealed record TrayState(
    bool IsCoreRunning,
    bool TunEnabled,
    bool SystemProxyEnabled,
    bool SilentStart,
    bool AutoStartEnabled,
    IReadOnlyList<string> Profiles,
    string ActiveProfile);
