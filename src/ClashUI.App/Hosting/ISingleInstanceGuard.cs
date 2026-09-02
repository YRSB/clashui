namespace ClashUI.App.Hosting;

public interface ISingleInstanceGuard : IDisposable
{
    bool Acquire();
}
