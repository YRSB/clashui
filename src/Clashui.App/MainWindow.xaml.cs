using Clashui.App.Services;
using Clashui.Core;
using Microsoft.UI;
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
    private readonly AppController _controller;
    private bool _navigated;
    private bool _panelReady;

    public MainWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        Title = "Clashui";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

        // 官方 TitleBar 控件（WinAppSDK 1.7+）：ExtendsContentIntoTitleBar + SetTitleBar 两步标准流程
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // 触屏友好：标题栏与系统按钮改用 48px 高度（须在 ExtendsContentIntoTitleBar 之后设置）
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
        // Window 没有 ActualTheme，从根元素取主题
        ((FrameworkElement)Content).ActualThemeChanged += (_, _) => UpdateCaptionButtonColors();

        // 还原（取消最大化）时的默认尺寸；启动即最大化
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(1120, 760));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Maximize();

        // 关闭 = 隐藏到托盘并让面板进入低内存模式；真正退出走托盘菜单
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
            SetPanelMemoryTarget(low: true);
        };

        _controller.StateChanged += OnStateChanged;
        OnStateChanged();
        // WinUI 的 Window 没有 Loaded 事件，挂到 WebView2 元素上
        Panel.Loaded += async (_, _) => await TryNavigateAsync();
    }

    public void ShowAndActivate()
    {
        AppWindow.Show();
        Activate();
        SetPanelMemoryTarget(low: false);
    }

    /// 隐藏到托盘时把 WebView2 切到低内存目标（引擎收缩缓存、必要时换出内存，
    /// 脚本与 WebSocket 继续运行）；显示时恢复正常。官方文档要求二选一，
    /// 不要与 TrySuspendAsync 混用。
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
            // 旧版 WebView2 Runtime 可能没有该 API，降级为仅不生效
            AppLog.Error("调整面板内存目标失败", ex);
        }
    }

    /// 标题按钮随主题着色（延伸进标题栏后系统默认底色会露馅）
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
        var running = _controller.IsCoreRunning;
        if (running)
        {
            Banner.IsOpen = false;
            _ = TryNavigateAsync();
            return;
        }

        var hasExe = CoreLocator.Resolve(_controller.Settings.MihomoPath) is not null;
        Banner.IsOpen = true;
        Banner.Title = hasExe ? "核心未运行，详情见日志" : "未找到 mihomo";
        Banner.Message = hasExe ? "可从托盘菜单重启核心" : $"请将其放入数据目录：{AppPaths.Root}";
    }

    private async Task TryNavigateAsync()
    {
        if (_navigated) return;
        // 等核心就绪（首次启动要等 mihomo 下载 external-ui 面板，稍慢）
        for (var i = 0; i < 60 && !_controller.IsCoreRunning; i++) await Task.Delay(250);
        if (!_controller.IsCoreRunning || _navigated) return;
        _navigated = true;
        try
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                // 面板用不上跟踪防护，关闭可省内存与 CPU
                EnableTrackingPrevention = false,
                AdditionalBrowserArguments = "--renderer-process-limit=1",
            };
            // WinUI3 投影只有 CreateWithOptionsAsync（folder 传 null 用默认值）
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, null, options);
            await Panel.EnsureCoreWebView2Async(env);
            _panelReady = true;
            Panel.Source = new Uri(_controller.DashboardUrl);
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

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => _controller.OpenDataFolder();
}
