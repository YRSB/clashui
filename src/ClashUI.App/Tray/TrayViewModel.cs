namespace ClashUI.App.Tray;

public sealed record ProfileEntry(string Path, string Name, bool IsChecked);

public sealed record TrayViewModel(
    bool SystemProxyChecked,
    bool TunChecked,
    bool SilentChecked,
    bool AutoStartChecked,
    string IconFile,
    string Tooltip,
    IReadOnlyList<ProfileEntry> Profiles,
    bool IsEmpty);

public enum TrayCommandKind
{
    ShowPanel,
    RestartCore,
    OpenDataFolder,
    OpenProfilesFolder,
    ToggleSystemProxy,
    ToggleTun,
    ToggleSilentStart,
    ToggleAutoStart,
    SwitchProfile,
    Exit
}

public sealed record TrayCommand(TrayCommandKind Kind, string? Payload = null);
