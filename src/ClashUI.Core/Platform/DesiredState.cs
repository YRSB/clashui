namespace ClashUI.Core;

public sealed record DesiredState(
    bool SystemProxyEnabled,
    int MixedPort,
    bool TunEnabled,
    bool AutoStartEnabled,
    bool SilentStart,
    string ExePath);
