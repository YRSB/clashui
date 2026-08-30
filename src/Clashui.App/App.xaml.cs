using System.Threading;
using Clashui.App.Services;
using Clashui.Core;
using Microsoft.UI.Xaml;

namespace Clashui.App;

public partial class App : Application
{
    private static Mutex? _singleInstance;
    private static TrayController? _tray;
    private static MainWindow? _mainWindow;

    public static AppController Controller { get; private set; } = null!;

    /// 静默启动：--silent / -s 参数，或 settings.json 里的 SilentStart
    public static bool StartSilent { get; private set; }

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

            var hasSilentArg = Environment.GetCommandLineArgs().Any(a => a is "--silent" or "-s");
            StartSilent = hasSilentArg || Controller.Settings.SilentStart;

            _tray = new TrayController(
                Controller,
                ShowMainWindow,
                ToggleMainWindow,
                Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

            // 静默启动时不创建主窗口（连 HWND 都不存在，杜绝窗口闪烁），首次点托盘再创建
            if (!StartSilent) ShowMainWindow();
            // 提权重启场景：补做上一次实例挂起的提权操作（如开机自启注册）
            Controller.ProcessPendingOperations();
            Controller.StartOnLaunch();
        }
        catch (Exception ex)
        {
            AppLog.Error("初始化失败", ex);
            ExitProcess();
        }
    }

    public static void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow(Controller);
            _mainWindow.Activate();
            return;
        }
        _mainWindow.ShowAndActivate();
    }

    /// 托盘左键：窗口可见则隐藏到托盘（进低内存模式），否则显示并激活。
    public static void ToggleMainWindow()
    {
        if (_mainWindow is null || !_mainWindow.AppWindow.IsVisible)
        {
            ShowMainWindow();
            return;
        }
        _mainWindow.HideToTray();
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
