namespace Clashui.Core;

public sealed record AppState
{
    public CoreState CoreState { get; init; }
    public string ActiveProfile { get; init; } = "";
    public bool SystemProxyEnabled { get; init; }
    public bool TunEnabled { get; init; }
    public string ControllerAddr { get; init; } = "";
    public IReadOnlyList<string> Profiles { get; init; } = Array.Empty<string>();

    public AppState(CoreState coreState, string activeProfile, bool systemProxyEnabled, bool tunEnabled, string controllerAddr, IReadOnlyList<string> profiles)
    {
        CoreState = coreState;
        ActiveProfile = activeProfile;
        SystemProxyEnabled = systemProxyEnabled;
        TunEnabled = tunEnabled;
        ControllerAddr = controllerAddr;
        Profiles = profiles.ToList().AsReadOnly();
    }
}
