using Clashui.App.Services;
using Clashui.Core;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace Clashui.App;

public sealed partial class MainWindow : Window
{
    private readonly AppController _controller;
    private bool _navigated;

    public MainWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        Title = "Clashui";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        // 还原（取消最大化）时的默认尺寸；启动即最大化
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(1120, 760));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Maximize();

        // 关闭 = 隐藏到托盘；真正退出走托盘菜单
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
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
            // M0 使用默认用户数据目录（exe 目录下的 WebView2 文件夹）；后续版本改为指向数据目录
            await Panel.EnsureCoreWebView2Async();
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
