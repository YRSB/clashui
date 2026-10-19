using ClashUI.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.UI;

namespace ClashUI.App;

public sealed partial class MainWindow : Window
{
    private readonly CoreOrchestrator _orch;
    private readonly DispatcherQueue _dispatcher;
    private bool _webReady;
    private string _loadedUrl = "";
    private Task<bool>? _webViewInitTask;
    private readonly SemaphoreSlim _navGate = new(1, 1);
    private bool _isTransientNotification;
    private readonly DispatcherTimer _notificationDismissTimer;

    public MainWindow(CoreOrchestrator orch)
    {
        _orch = orch;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        Title = "ClashUI";
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

        _notificationDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _notificationDismissTimer.Tick += (_, _) =>
        {
            _notificationDismissTimer.Stop();
            _isTransientNotification = false;
            if (_orch.IsCoreRunning)
                Banner.IsOpen = false;
            else
                OnStateChanged();
        };

        _orch.StateChanged += _ => _dispatcher.TryEnqueue(OnStateChanged);
        OnStateChanged();
        Panel.Loaded += async (_, _) => await NavigatePanelAsync();
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
        if (!_webReady) return;
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
        var coreState = _orch.CoreState;
        if (coreState == CoreState.Running)
        {
            if (!_isTransientNotification)
                Banner.IsOpen = false;
            _ = NavigatePanelAsync();
            return;
        }
        if (coreState == CoreState.Starting)
        {
            if (!_isTransientNotification)
                Banner.IsOpen = false;
            return;
        }
        if (!_isTransientNotification)
        {
            var settings = _orch.Settings;
            var hasExe = CoreLocator.Resolve(settings.MihomoPath) is not null;
            Banner.IsOpen = true;
            Banner.Severity = InfoBarSeverity.Warning;
            Banner.Title = hasExe ? "核心未运行，详情见日志" : "未找到 mihomo";
            Banner.Message = hasExe ? "可从托盘菜单重启核心" : $"请将其放入数据目录：{AppPaths.Root}";
            SetBannerAction(retry: false);
        }
    }

    private Task<bool> EnsureWebViewAsync()
    {
        if (_webReady) return Task.FromResult(true);
        return _webViewInitTask ??= DoEnsureWebViewAsync();
    }

    private async Task<bool> DoEnsureWebViewAsync()
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                EnableTrackingPrevention = false,
                AdditionalBrowserArguments = "--renderer-process-limit=1 --disable-features=msSmartScreenProtection,Translate,BackForwardCache,msEdgeSidebarV2 --disable-extensions --disable-component-update",
            };
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(
                null, AppPaths.WebView2DataDir, options);
            await Panel.EnsureCoreWebView2Async(env);
            _webReady = true;
            return true;
        }
        catch (Exception ex)
        {
            _webViewInitTask = null;
            AppLog.Error("WebView2 初始化失败", ex);
            Banner.IsOpen = true;
            Banner.Severity = InfoBarSeverity.Error;
            Banner.Title = "WebView2 初始化失败";
            Banner.Message = ex.Message;
            SetBannerAction(retry: false);
            return false;
        }
    }

    private async Task NavigatePanelAsync()
    {
        await _navGate.WaitAsync();
        try
        {
            if (!_orch.IsCoreRunning) return;
            string target;
            try
            {
                var settings = _orch.Settings;
                if (_orch.Composer is DefaultConfigComposer defaults && defaults.PanelPort > 0)
                {
                    var active = PanelCatalog.Normalize(settings.ActivePanel);
                    var targetId = active;
                    if (!PanelProvisioner.IsAvailable(active))
                    {
                        var available = PanelProvisioner.GetAvailable();
                        if (available.Count == 0)
                        {
                            Banner.IsOpen = true;
                            Banner.Severity = InfoBarSeverity.Informational;
                            Banner.Title = "面板正在下载";
                            Banner.Message = "三面板均尚未就绪，下载完成后自动加载";
                            SetBannerAction(retry: true);
                            return;
                        }
                        targetId = available[0];
                    }
                    target = PanelUrlBuilder.Build(targetId, settings.ControllerAddr, settings.Secret, defaults.PanelPort);
                }
                else
                {
                    target = _orch.Composer.DashboardUrlFor(settings);
                }
            }
            catch
            {
                return;
            }
            if (target == _loadedUrl) return;
            if (!await EnsureWebViewAsync()) return;
            try
            {
                Panel.Source = new Uri(target);
                _loadedUrl = target;
            }
            catch (Exception ex)
            {
                AppLog.Error("面板导航失败", ex);
            }
        }
        finally
        {
            _navGate.Release();
        }
    }

    internal void ShowNotification(string message)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => ShowNotification(message));
            return;
        }
        _isTransientNotification = true;
        Banner.IsOpen = true;
        Banner.Severity = InfoBarSeverity.Informational;
        Banner.Title = message;
        Banner.Message = "";
        BannerAction.Visibility = Visibility.Collapsed;
        ScheduleDismissBanner();
    }

    internal void ShowPanelNotification(string message)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => ShowPanelNotification(message));
            return;
        }
        _isTransientNotification = true;
        Banner.IsOpen = true;
        Banner.Severity = InfoBarSeverity.Informational;
        Banner.Title = message;
        Banner.Message = "";
        SetBannerAction(retry: true);
        ScheduleDismissBanner();
    }

    private void ScheduleDismissBanner()
    {
        _notificationDismissTimer.Stop();
        _notificationDismissTimer.Start();
    }

    private bool _bannerRetryMode;
    private void SetBannerAction(bool retry)
    {
        _bannerRetryMode = retry;
        BannerAction.Visibility = Visibility.Visible;
        BannerAction.Content = retry ? "重试下载" : "打开数据目录";
        BannerAction.IsEnabled = true;
    }
    private void BannerAction_Click(object sender, RoutedEventArgs e)
    {
        if (!_bannerRetryMode)
        {
            _orch.OpenDataFolder();
            return;
        }
        BannerAction.IsEnabled = false;
        _ = Task.Run(async () =>
        {
            try
            {
                await PanelProvisioner.EnsureAllAsync(
                    msg => _dispatcher.TryEnqueue(() => ShowPanelNotification(msg))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error("面板手动重试失败", ex);
            }
            _dispatcher.TryEnqueue(() =>
            {
                BannerAction.IsEnabled = true;
                try { _orch.NotifyPanelsChanged(); } catch { }
            });
        });
    }
}
