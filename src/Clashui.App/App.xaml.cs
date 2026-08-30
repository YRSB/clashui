using System.IO.MemoryMappedFiles;
using System.Threading;
using Clashui.App.Services;
using Clashui.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Clashui.App;

public partial class App : Application
{
    private const string MutexName = @"Local\Clashui.SingleInstance";
    private const string ActivateEventName = @"Local\Clashui.ActivateSignal";
    private const string ActivatePidMapName = @"Local\Clashui.ActivatePid";

    private static Mutex? _singleInstance;
    private static EventWaitHandle? _activateSignal;
    private static MemoryMappedFile? _activatePidFile;
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
            // 显式 --silent 的再启动（如计划任务重复触发）不弹窗，仅退出
            if (!HasSilentArg) ForwardActivationSignal();
            ExitProcess();
            return;
        }

        try
        {
            Controller = new AppController();
            Controller.Initialize();
            Controller.ExitRequested = Shutdown;
            StartActivationWatcher();

            var hasSilentArg = HasSilentArg;
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
            _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (createdNew) return true;
            _singleInstance.Dispose();
            // 提权重启场景下旧实例需要几秒才能退出，稍作等待
            Thread.Sleep(250);
        }
        return false;
    }

    private static bool HasSilentArg =>
        Environment.GetCommandLineArgs().Any(a => a is "--silent" or "-s");

    /// 再启动激活转发：本实例监听命名信号，收到即弹出主窗口——双击 exe 等价于托盘左键。
    private static void StartActivationWatcher()
    {
        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName, out _);
        // 第二实例要读 PID 来转授前台权，MMF 必须与实例同生命周期，不能 using
        _activatePidFile = MemoryMappedFile.CreateOrOpen(ActivatePidMapName, sizeof(int));
        using var view = _activatePidFile.CreateViewAccessor();
        view.Write(0, Environment.ProcessId);

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(() =>
        {
            // 常驻等待再启动信号；进程退出时随线程一并终止
            while (_activateSignal.WaitOne())
            {
                dispatcher.TryEnqueue(ShowMainWindow);
            }
        });
    }

    /// 抢锁失败的实例：退出前把前台设置权转授给第一实例并发激活信号。
    private static void ForwardActivationSignal()
    {
        try
        {
            using var pidFile = MemoryMappedFile.OpenExisting(ActivatePidMapName);
            using var view = pidFile.CreateViewAccessor();
            var pid = view.ReadInt32(0);
            if (pid > 0) NativeMethods.AllowSetForegroundWindow((uint)pid);
        }
        catch { /* 第一实例是旧版本或跨完整性级别打开失败，仅失去前台转授 */ }
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ActivateEventName);
            signal.Set();
        }
        catch { /* 第一实例不存在（如旧版本运行中），维持静默退出 */ }
    }
}
