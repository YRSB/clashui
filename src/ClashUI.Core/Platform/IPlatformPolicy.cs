namespace ClashUI.Core;

public interface IPlatformPolicy
{
    Task<PolicyResult> ApplyAsync(DesiredState desired);
    void ReconcileOnStartup(string exePath);
    void OnCoreStateChanged(CoreState state);
    bool IsAutoStartRegistered();
    void BindSettings(AppSettings shared);
    event Action<string>? Notification;
}
