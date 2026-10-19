using ClashUI.App.Hosting;
using ClashUI.Core;
using Microsoft.UI.Xaml;

namespace ClashUI.App;

public partial class App : Application
{
    private static AppHost? _host;
    private static MainWindow? _mainWindow;

    public static CoreOrchestrator Orchestrator => _host!.Orchestrator;
    public static bool StartSilent => _host?.StartSilent ?? false;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var host = new AppHost();
        if (!host.AcquireSingleInstance())
        {
            if (!HasSilentArg) host.ForwardActivation();
            ExitProcess();
            return;
        }
        try
        {
            _host = host;
            var ok = host.Start(new HostStartArgs(
                ShowMainWindow,
                ToggleMainWindow,
                Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"),
                HasSilentArg));
            if (!ok)
            {
                ExitProcess();
                return;
            }
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

    public static void Shutdown()
    {
        if (_host is not null)
        {
            _host.Shutdown();
            return;
        }
        ExitProcess();
    }

    internal static void ShowGlobalNotification(string message)
    {
        AppLog.Info(message);
        if (_mainWindow is not null)
            _mainWindow.ShowNotification(message);
    }
    internal static void ShowPanelNotification(string message)
    {
        AppLog.Info(message);
        if (_mainWindow is not null)
            _mainWindow.ShowPanelNotification(message);
    }

    private static void ExitProcess() => Environment.Exit(0);

    private static bool HasSilentArg =>
        Environment.GetCommandLineArgs().Any(a => a is "--silent" or "-s");
}
