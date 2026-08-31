using Clashui.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.UI;

namespace Clashui.App;

public sealed partial class MainWindow : Window
{
    private readonly CoreOrchestrator _orch;
    private readonly DispatcherQueue _dispatcher;
    private bool _navigated;
    private bool _panelReady;

    public MainWindow(CoreOrchestrator orch)
    {
        _orch = orch;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        Title = "Clashui";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        try
        {
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        }
        catch (Exception ex)
        {
            AppLog.Error("设置 Tall 标题栏失败，回退标准高度", ex);
        }
        AppTitleBar.IconSource = new ImageIconSource
        {
            ImageSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"))),
        };
        SystemBackdrop = new MicaBackdrop();
        UpdateCaptionButtonColors();
        ((FrameworkElement)Content).ActualThemeChanged += (_, _) => UpdateCaptionButtonColors();
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(1120, 760));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Maximize();
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            HideToTray();
        };
        _orch.StateChanged += _ => _dispatcher.TryEnqueue(OnStateChanged);
        OnStateChanged();
        Panel.Loaded += async (_, _) => await TryNavigateAsync();
    }

    public void ShowAndActivate()
    {
        AppWindow.Show();
        Activate();
        SetPanelMemoryTarget(low: false);
    }

    public void HideToTray()
    {
        AppWindow.Hide();
        SetPanelMemoryTarget(low: true);
    }

    private void SetPanelMemoryTarget(bool low)
    {
        if (!_panelReady) return;
        try
        {
            Panel.CoreWebView2.MemoryUsageTargetLevel = low
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;
            AppLog.Info($"面板内存目标 → {(low ? "Low" : "Normal")}");
        }
        catch (Exception ex)
        {
            AppLog.Error("调整面板内存目标失败", ex);
        }
    }

    private void UpdateCaptionButtonColors()
    {
        var dark = ((FrameworkElement)Content).ActualTheme == ElementTheme.Dark;
        var fg = dark ? Colors.White : Colors.Black;
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonForegroundColor = fg;
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0x77, fg.R, fg.G, fg.B);
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = dark
            ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x14, 0x00, 0x00, 0x00);
        titleBar.ButtonHoverForegroundColor = fg;
        titleBar.ButtonPressedBackgroundColor = dark
            ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x28, 0x00, 0x00, 0x00);
        titleBar.ButtonPressedForegroundColor = fg;
    }

    private void OnStateChanged()
    {
        var running = _orch.IsCoreRunning;
        if (running)
        {
            Banner.IsOpen = false;
            _ = TryNavigateAsync();
            return;
        }
        var settings = _orch.Settings;
        var hasExe = CoreLocator.Resolve(settings.MihomoPath) is not null;
        Banner.IsOpen = true;
        Banner.Title = hasExe ? "核心未运行，详情见日志" : "未找到 mihomo";
        Banner.Message = hasExe ? "可从托盘菜单重启核心" : $"请将其放入数据目录：{AppPaths.Root}";
    }

    private async Task TryNavigateAsync()
    {
        if (_navigated) return;
        for (var i = 0; i < 60 && !_orch.IsCoreRunning; i++) await Task.Delay(250);
        if (!_orch.IsCoreRunning || _navigated) return;
        _navigated = true;
        try
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                EnableTrackingPrevention = false,
                AdditionalBrowserArguments = "--renderer-process-limit=1",
            };
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(
                null, AppPaths.WebView2DataDir, options);
            await Panel.EnsureCoreWebView2Async(env);
            _panelReady = true;
            var url = _orch.Composer.DashboardUrlFor(_orch.Settings);
            Panel.Source = new Uri(url);
        }
        catch (Exception ex)
        {
            _navigated = false;
            AppLog.Error("WebView2 初始化失败", ex);
            Banner.IsOpen = true;
            Banner.Title = "WebView2 初始化失败";
            Banner.Message = ex.Message;
        }
    }

    internal void ShowNotification(string message)
    {
        Banner.IsOpen = true;
        Banner.Title = message;
        Banner.Message = "";
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => _orch.OpenDataFolder();
}
