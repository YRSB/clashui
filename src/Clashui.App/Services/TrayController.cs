using Clashui.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIEx;

namespace Clashui.App.Services;

public sealed class TrayController : IDisposable
{
    private readonly CoreOrchestrator _orch;
    private readonly PolicyOps _policy;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _showWindow;
    private readonly Action _toggleWindow;
    private readonly string _assetsDir;
    private readonly TrayIcon _icon;
    private readonly MenuFlyout _menu;
    private readonly ToggleMenuFlyoutItem _sysProxyItem;
    private readonly ToggleMenuFlyoutItem _tunItem;
    private readonly ToggleMenuFlyoutItem _silentStartItem;
    private readonly ToggleMenuFlyoutItem _autoStartItem;
    private readonly MenuFlyoutSubItem _profilesItem = new() { Text = "配置文件" };

    public TrayController(CoreOrchestrator orch, PolicyOps policy, Action showWindow, Action toggleWindow, string iconPath)
    {
        _orch = orch;
        _policy = policy;
        _showWindow = showWindow;
        _toggleWindow = toggleWindow;
        _assetsDir = Path.GetDirectoryName(iconPath) ?? AppContext.BaseDirectory;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        var showItem = Item("显示面板", () => _showWindow());
        var restartItem = Item("重启核心", () => _ = _orch.RestartAsync());
        var dataItem = Item("打开数据目录", () => _orch.OpenDataFolder());
        var exitItem = Item("退出", () => ExitOrchestrator());

        _sysProxyItem = new ToggleMenuFlyoutItem { Text = "系统代理" };
        _sysProxyItem.Click += (_, _) => Safe(() => HandleSystemProxyToggle(_sysProxyItem.IsChecked));

        _tunItem = new ToggleMenuFlyoutItem { Text = "TUN 模式" };
        _tunItem.Click += (_, _) => Safe(() => HandleTunToggle(_tunItem.IsChecked));

        _silentStartItem = new ToggleMenuFlyoutItem { Text = "静默启动" };
        _silentStartItem.Click += (_, _) => Safe(() => { _policy.SetSilentStart(_silentStartItem.IsChecked); _dispatcher.TryEnqueue(Refresh); });

        _autoStartItem = new ToggleMenuFlyoutItem { Text = "开机自启（静默）" };
        _autoStartItem.Click += (_, _) => Safe(() => HandleAutoStartToggle(_autoStartItem.IsChecked));

        _menu = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
        _menu.Opening += (_, _) => RebuildProfiles();
        _menu.Items.Add(showItem);
        _menu.Items.Add(_profilesItem);
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(_sysProxyItem);
        _menu.Items.Add(_tunItem);
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(restartItem);
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(dataItem);
        _menu.Items.Add(_silentStartItem);
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(exitItem);

        _icon = new TrayIcon(1, iconPath, "Clashui");
        _icon.IsVisible = true;
        _icon.Selected += (_, _) => Safe(_toggleWindow);
        _icon.ContextMenu += (_, e) => e.Flyout = _menu;

        _orch.StateChanged += _ => _dispatcher.TryEnqueue(Refresh);
        Refresh();
    }

    private void ExitOrchestrator()
    {
        _icon.Dispose();
        _orch.Stop();
        _orch.Dispose();
        Environment.Exit(0);
    }

    private void Notify(string message) => App.ShowGlobalNotification(message);

    private void HandleTunToggle(bool enabled)
    {
        var r = _policy.SetTun(enabled);
        if (r.Kind == PolicyResultKind.Ok)
        {
            _ = _orch.RestartAsync();
        }
        else if (r.Kind == PolicyResultKind.NeedsElevation)
        {
            Notify("切换 TUN 模式需要管理员权限，正在以管理员身份重启…");
            ExitOrchestrator();
            return;
        }
        else if (r.Kind == PolicyResultKind.CancelledByUser)
        {
            Notify("已取消提权，TUN 模式未更改");
        }
        else if (r.Kind == PolicyResultKind.Failed)
        {
            Notify($"切换 TUN 失败：{r.Cause}");
        }
        _dispatcher.TryEnqueue(Refresh);
    }

    private void HandleSystemProxyToggle(bool enabled)
    {
        var s = _orch.Settings;
        var r = _policy.SetSystemProxy(enabled, _orch.IsCoreRunning, s.MixedPort);
        if (r.Kind == PolicyResultKind.Failed)
            Notify($"切换系统代理失败：{r.Cause}");
        _dispatcher.TryEnqueue(Refresh);
    }

    private void HandleAutoStartToggle(bool enable)
    {
        var r = _policy.SetAutoStart(enable, Environment.ProcessPath ?? "");
        if (r.Kind == PolicyResultKind.NeedsElevation)
        {
            Notify("修改开机自启需要管理员权限，正在以管理员身份重启…");
            ExitOrchestrator();
            return;
        }
        else if (r.Kind == PolicyResultKind.CancelledByUser)
            Notify("已取消提权，开机自启未更改");
        else if (r.Kind == PolicyResultKind.Failed)
            Notify("修改开机自启失败，详情见日志");
        _dispatcher.TryEnqueue(Refresh);
    }

    public void Refresh()
    {
        var settings = _orch.Settings;
        _sysProxyItem.IsChecked = settings.SystemProxyEnabled;
        _tunItem.IsChecked = settings.TunEnabled;
        _silentStartItem.IsChecked = settings.SilentStart;
        try { _autoStartItem.IsChecked = _policy.IsAutoStartRegistered(); } catch (Exception ex) { AppLog.Error("读取开机自启状态失败", ex); }

        var isRunning = _orch.IsCoreRunning;
        var iconFile = !isRunning ? "app-off.ico"
            : settings.TunEnabled ? "app-tun.ico"
            : settings.SystemProxyEnabled ? "app-proxy.ico"
            : "app.ico";
        var tooltip = !isRunning ? "Clashui — 核心未运行"
            : settings.TunEnabled ? "Clashui — 核心运行中（TUN）"
            : settings.SystemProxyEnabled ? "Clashui — 核心运行中（系统代理）"
            : "Clashui — 核心运行中";
        try
        {
            _icon.SetIcon(Path.Combine(_assetsDir, iconFile));
            _icon.Tooltip = tooltip;
        }
        catch (Exception ex)
        {
            AppLog.Error("更新托盘图标失败", ex);
        }
        RebuildProfiles();
    }

    private void RebuildProfiles()
    {
        _profilesItem.Items.Clear();
        var active = _orch.Settings.ActiveProfile;
        var profiles = _orch.GetProfiles();

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
            item.Click += (_, _) => _ = _orch.SwitchProfileAsync(path);
            _profilesItem.Items.Add(item);
        }

        _profilesItem.Items.Add(new MenuFlyoutSeparator());
        _profilesItem.Items.Add(Item("打开 profiles 目录", () => _orch.OpenProfilesFolder()));
    }

    private static void Safe(Action action)
    {
        try { action(); } catch (Exception ex) { AppLog.Error("托盘操作失败", ex); }
    }

    private static MenuFlyoutItem Item(string text, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => Safe(onClick);
        return item;
    }

    public void Dispose() => _icon.Dispose();
}
