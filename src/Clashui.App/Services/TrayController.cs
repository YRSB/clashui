using Clashui.Core;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Clashui.App.Services;

/// 托盘图标与右键菜单；左键点击打开面板窗口。
public sealed class TrayController : IDisposable
{
    private readonly AppController _controller;
    private readonly Action _showWindow;
    private readonly TaskbarIcon _icon;
    private readonly MenuFlyoutSubItem _profilesItem = new() { Text = "配置文件" };
    private readonly ToggleMenuFlyoutItem _sysProxyItem;
    private readonly ToggleMenuFlyoutItem _tunItem;
    private readonly ToggleMenuFlyoutItem _autoStartItem;
    private readonly MenuFlyoutItem _elevateItem;

    public TrayController(AppController controller, Action showWindow, string iconPath)
    {
        _controller = controller;
        _showWindow = showWindow;

        var showItem = Item("显示面板", () => _showWindow());
        var restartItem = Item("重启核心", () => _ = _controller.RestartCoreAsync());
        var dataItem = Item("打开数据目录", _controller.OpenDataFolder);
        var exitItem = Item("退出", _controller.Exit);

        _sysProxyItem = new ToggleMenuFlyoutItem { Text = "系统代理" };
        _sysProxyItem.Click += (_, _) => Safe(() => _controller.ToggleSystemProxy(_sysProxyItem.IsChecked));

        _tunItem = new ToggleMenuFlyoutItem { Text = "TUN 模式" };
        _tunItem.Click += (_, _) => Safe(() => _controller.ToggleTun(_tunItem.IsChecked));

        _elevateItem = Item("以管理员身份重启", () =>
        {
            if (Elevation.RelaunchElevated()) _controller.Exit();
        });

        _autoStartItem = new ToggleMenuFlyoutItem { Text = "开机自启（管理员）" };
        _autoStartItem.Click += (_, _) => Safe(() =>
        {
            var ok = _autoStartItem.IsChecked
                ? AutoStart.Register(Environment.ProcessPath ?? "")
                : AutoStart.Unregister();
            if (!ok)
            {
                _autoStartItem.IsChecked = !_autoStartItem.IsChecked;
                _controller.Notify("修改开机自启失败，注册计划任务需要管理员权限");
            }
            Refresh();
        });

        var menu = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
        menu.Opening += (_, _) => RebuildProfiles();
        menu.Items.Add(showItem);
        menu.Items.Add(_profilesItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_sysProxyItem);
        menu.Items.Add(_tunItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(restartItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(dataItem);
        menu.Items.Add(_elevateItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exitItem);

        var leftClickCommand = new XamlUICommand { Description = "显示面板" };
        leftClickCommand.ExecuteRequested += (_, _) => _showWindow();

        _icon = new TaskbarIcon
        {
            ToolTipText = "Clashui",
            IconSource = new BitmapImage(new Uri(iconPath)),
            ContextFlyout = menu,
            ContextMenuMode = ContextMenuMode.SecondWindow,
            LeftClickCommand = leftClickCommand,
            NoLeftClickDelay = true,
        };
        _icon.ForceCreate();

        _controller.StateChanged += Refresh;
        _controller.Notification += ShowBalloon;
        Refresh();
    }

    public void Refresh()
    {
        var settings = _controller.Settings;
        _sysProxyItem.IsChecked = settings.SystemProxyEnabled;
        _tunItem.IsChecked = settings.TunEnabled;
        try { _autoStartItem.IsChecked = AutoStart.IsRegistered(); } catch { }
        _elevateItem.Visibility = Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        _icon.ToolTipText = _controller.IsCoreRunning ? "Clashui — 核心运行中" : "Clashui — 核心未运行";
        RebuildProfiles();
    }

    /// 重建「配置文件」子菜单：列出 profiles 目录下的 YAML，勾选当前激活项。
    private void RebuildProfiles()
    {
        _profilesItem.Items.Clear();
        var active = _controller.Settings.ActiveProfile;
        var profiles = _controller.GetProfiles();

        if (profiles.Count == 0)
        {
            _profilesItem.Items.Add(new MenuFlyoutItem
            {
                Text = "（profiles 目录为空）",
                IsEnabled = false,
            });
        }

        foreach (var path in profiles)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = Path.GetFileName(path),
                IsChecked = string.Equals(path, active, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => _ = _controller.SwitchProfileAsync(path);
            _profilesItem.Items.Add(item);
        }

        _profilesItem.Items.Add(new MenuFlyoutSeparator());
        var openItem = Item("打开 profiles 目录", _controller.OpenProfilesFolder);
        _profilesItem.Items.Add(openItem);
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            // 托盘点击链路上的未处理异常会让 WinUI 进程直接退出，必须兜底
            AppLog.Error("托盘操作失败", ex);
        }
    }

    private static MenuFlyoutItem Item(string text, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => Safe(onClick);
        return item;
    }

    private void ShowBalloon(string message)
    {
        try { _icon.ShowNotification("Clashui", message); }
        catch { /* 通知失败不影响主流程 */ }
    }

    public void Dispose() => _icon.Dispose();
}
