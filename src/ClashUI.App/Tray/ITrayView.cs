namespace ClashUI.App.Tray;

public interface ITrayView : IDisposable
{
    void Render(TrayViewModel model);
    event Action<TrayCommand> Command;
}
