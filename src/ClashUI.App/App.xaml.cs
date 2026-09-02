using System.IO.MemoryMappedFiles;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using ClashUI.App.Services;
using ClashUI.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClashUI.App;

public partial class App : Application
{
    private const string MutexName = @"Local\ClashUI.SingleInstance";
    private const string LegacyMutexName = @"Local\Clashui.SingleInstance";
    private const string ActivateEventName = @"Local\ClashUI.ActivateSignal";
    private const string LegacyActivateEventName = @"Local\Clashui.ActivateSignal";
    private const string ActivatePidMapName = @"Local\ClashUI.ActivatePid";
    private const string LegacyActivatePidMapName = @"Local\Clashui.ActivatePid";

    private static Mutex? _singleInstance;
    private static EventWaitHandle? _activateSignal;
    private static MemoryMappedFile? _activatePidFile;
    private static TrayController? _tray;
    private static MainWindow? _mainWindow;

    public static CoreOrchestrator Orchestrator { get; private set; } = null!;
    public static PolicyOps Policy { get; private set; } = null!;

    public static bool StartSilent { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!AcquireSingleInstance())
        {
            if (!HasSilentArg) ForwardActivationSignal();
            ExitProcess();
            return;
        }

        var earlyStore = new FileSettingsStore();
        var earlySettings = earlyStore.Load();
        if (earlySettings.TunEnabled && !Elevation.IsElevated)
        {
            AppLog.Info("TUN 模式需要管理员权限，正在以管理员身份重新启动…");
            if (Elevation.RelaunchElevated())
            {
                ExitProcess();
                return;
            }
            earlySettings.TunEnabled = false;
            earlyStore.Save(earlySettings);
            AppLog.Info("已取消提权，TUN 已临时关闭，下次可在托盘中重新开启");
        }

        try
        {
            var store = new FileSettingsStore();
            var composer = new DefaultConfigComposer();
            var runtime = new CoreRuntime();
            var watcher = new FileProfileWatcher();
            var time = new SystemTimeSource();
            Orchestrator = new CoreOrchestrator(store, composer, runtime, watcher, time);
            Policy = new PolicyOps(store, new SystemProxyAdapter(), new AutoStartAdapter(), new ElevationAdapter());
            Orchestrator.Initialize();
            Policy.BindSettings(Orchestrator.Settings);
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            Orchestrator.Notification += msg => dispatcher.TryEnqueue(() => ShowGlobalNotification(msg));
            Orchestrator.CrashLoop += count => dispatcher.TryEnqueue(() => ShowGlobalNotification($"核心连续异常退出，请查看数据目录 core.log（订阅 provider 拉取失败时会出现）({count})"));
            Policy.Notification += msg => dispatcher.TryEnqueue(() => ShowGlobalNotification(msg));
            StartActivationWatcher();

            var hasSilentArg = HasSilentArg;
            StartSilent = hasSilentArg || Orchestrator.Settings.SilentStart;

            _tray = new TrayController(
                Orchestrator,
                Policy,
                ShowMainWindow,
                ToggleMainWindow,
                Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

            if (!StartSilent) ShowMainWindow();
            Policy.ApplyPending(Environment.ProcessPath ?? "");
            Orchestrator.StartOnLaunch();
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
            _mainWindow = new MainWindow(Orchestrator);
            _mainWindow.Activate();
            return;
        }
        _mainWindow.ShowAndActivate();
    }

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
        Orchestrator?.Stop();
        ExitProcess();
    }

    internal static void ShowGlobalNotification(string message)
    {
        AppLog.Info(message);
        if (_mainWindow is not null)
            _mainWindow.ShowNotification(message);
    }

    private static void ExitProcess() => Environment.Exit(0);

    private static bool AcquireSingleInstance()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            bool createdNew;
            try
            {
                _singleInstance = new Mutex(false, MutexName, out createdNew);
                if (!createdNew)
                {
                    try { _singleInstance.Dispose(); } catch { }
                    _singleInstance = null;
                    Thread.Sleep(250);
                    continue;
                }
                var sec = CreateWorldMutexSecurity();
                if (sec != null) try { _singleInstance.SetAccessControl(sec); } catch { }
                try { _singleInstance.WaitOne(0); } catch { }
            }
            catch
            {
                Thread.Sleep(250);
                continue;
            }
            var legacyExists = false;
            try
            {
                using var legacy = Mutex.OpenExisting(LegacyMutexName);
                legacyExists = true;
            }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (UnauthorizedAccessException) { legacyExists = true; }
            catch { }
            if (!legacyExists) return true;
            try { _singleInstance.ReleaseMutex(); } catch { }
            try { _singleInstance.Dispose(); } catch { }
            _singleInstance = null;
            return false;
        }
        return false;
    }

    private static MutexSecurity CreateWorldMutexSecurity()
    {
        try
        {
            var sec = new MutexSecurity();
            var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            sec.AddAccessRule(new MutexAccessRule(world, MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
            sec.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), MutexRights.FullControl, AccessControlType.Allow));
            return sec;
        }
        catch { return null!; }
    }


    private static bool HasSilentArg =>
        Environment.GetCommandLineArgs().Any(a => a is "--silent" or "-s");

    private static void StartActivationWatcher()
    {
        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName, out _);
        _activatePidFile = MemoryMappedFile.CreateOrOpen(ActivatePidMapName, sizeof(int));
        using var view = _activatePidFile.CreateViewAccessor();
        view.Write(0, Environment.ProcessId);

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(() =>
        {
            while (_activateSignal.WaitOne())
            {
                dispatcher.TryEnqueue(ShowMainWindow);
            }
        });
        try
        {
            using var legacySignal = new EventWaitHandle(false, EventResetMode.AutoReset, LegacyActivateEventName, out var created);
            if (!created) return;
            _ = Task.Run(() =>
            {
                while (legacySignal.WaitOne())
                {
                    dispatcher.TryEnqueue(ShowMainWindow);
                }
            });
        }
        catch { }
    }

    private static void ForwardActivationSignal()
    {
        var forwarded = false;
        try
        {
            using var pidFile = MemoryMappedFile.OpenExisting(ActivatePidMapName);
            using var view = pidFile.CreateViewAccessor();
            var pid = view.ReadInt32(0);
            if (pid > 0) NativeMethods.AllowSetForegroundWindow((uint)pid);
        }
        catch { }
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ActivateEventName);
            signal.Set();
            forwarded = true;
        }
        catch { }
        try
        {
            using var pidFile = MemoryMappedFile.OpenExisting(LegacyActivatePidMapName);
            using var view = pidFile.CreateViewAccessor();
            var pid = view.ReadInt32(0);
            if (pid > 0) NativeMethods.AllowSetForegroundWindow((uint)pid);
        }
        catch (Exception ex)
        {
            if (!forwarded) AppLog.Error("转授前台失败", ex);
        }
        try
        {
            using var signal = EventWaitHandle.OpenExisting(LegacyActivateEventName);
            signal.Set();
            forwarded = true;
        }
        catch (Exception ex)
        {
            if (!forwarded) AppLog.Error("激活信号发送失败", ex);
        }
        if (!forwarded) AppLog.Error("激活信号发送失败", new InvalidOperationException("no signal"));
    }
}
