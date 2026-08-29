using System.Threading;
using Clashui.App.Services;
using Clashui.Core;
using Microsoft.UI.Xaml;

namespace Clashui.App;

public partial class App : Application
{
    private static Mutex? _singleInstance;
    private static TrayController? _tray;

    public static AppController Controller { get; private set; } = null!;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!AcquireSingleInstance())
        {
            ExitProcess();
            return;
        }

        try
        {
            Controller = new AppController();
            Controller.Initialize();
            Controller.ExitRequested = Shutdown;

            var window = new MainWindow(Controller);
            _tray = new TrayController(
                Controller,
                window.ShowAndActivate,
                Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

            window.Activate();
            Controller.StartOnLaunch();
        }
        catch (Exception ex)
        {
            AppLog.Error("初始化失败", ex);
            ExitProcess();
        }
    }

    private static void Shutdown()
    {
        _tray?.Dispose();
        _tray = null;
        ExitProcess();
    }

    private static void ExitProcess() => Environment.Exit(0);

    private static bool AcquireSingleInstance()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            _singleInstance = new Mutex(initiallyOwned: true, @"Local\Clashui.SingleInstance", out var createdNew);
            if (createdNew) return true;
            _singleInstance.Dispose();
            // 提权重启场景下旧实例需要几秒才能退出，稍作等待
            Thread.Sleep(250);
        }
        return false;
    }
}
