namespace ClashUI.App.Hosting;

public interface IActivationBridge : IDisposable
{
    void StartWatcher(Action onActivate);
    void Forward();
}
