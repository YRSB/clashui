using ClashUI.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIEx;

namespace ClashUI.App.Services;

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
    private readonly Dictionary<string, ToggleMenuFlyoutItem> _profileToggles = new(StringComparer.OrdinalIgnoreCase);
    private readonly MenuFlyoutSeparator _profilesSeparator = new();
    private readonly MenuFlyoutItem _openProfilesItem;
    private MenuFlyoutItem? _emptyPlaceholder;
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

        _openProfilesItem = Item("打开 profiles 目录", () => _orch.OpenProfilesFolder());
        _profilesItem.Items.Add(_profilesSeparator);
        _profilesItem.Items.Add(_openProfilesItem);

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

        TrayIcon? icon = null;
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                icon = new TrayIcon(1, iconPath, "ClashUI");
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(120);
            }
        }
        if (icon is null)
        {
            AppLog.Error($"托盘图标加载失败 {iconPath}", last);
            var fallback = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(fallback))
            {
                try { icon = new TrayIcon(1, fallback, "ClashUI"); } catch (Exception ex2) { AppLog.Error($"回退图标也失败 {fallback}", ex2); }
            }
        }
        _icon = icon ?? throw new ArgumentException($"Failed to load icon from {iconPath}", nameof(iconPath), last);
        _icon.IsVisible = true;
        _icon.Selected += (_, _) => Safe(_toggleWindow);
        _icon.ContextMenu += (_, e) => e.Flyout = _menu;
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
        var tooltip = !isRunning ? "ClashUI — 核心未运行"
            : settings.TunEnabled ? "ClashUI — 核心运行中（TUN）"
            : settings.SystemProxyEnabled ? "ClashUI — 核心运行中（系统代理）"
            : "ClashUI — 核心运行中";
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
        var active = _orch.Settings.ActiveProfile;
        var profiles = _orch.GetProfiles();

        if (profiles.Count == 0)
        {
            foreach (var kv in _profileToggles.ToList())
            {
                _profilesItem.Items.Remove(kv.Value);
            }
            _profileToggles.Clear();
            if (_emptyPlaceholder is null)
            {
                _emptyPlaceholder = new MenuFlyoutItem { Text = "（profiles 目录为空）", IsEnabled = false };
                _profilesItem.Items.Insert(0, _emptyPlaceholder);
            }
            return;
        }

        if (_emptyPlaceholder is not null)
        {
            _profilesItem.Items.Remove(_emptyPlaceholder);
            _emptyPlaceholder = null;
        }

        var wanted = new HashSet<string>(profiles, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _profileToggles.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _profilesItem.Items.Remove(_profileToggles[key]);
            _profileToggles.Remove(key);
        }

        for (var i = 0; i < profiles.Count; i++)
        {
            var path = profiles[i];
            if (!_profileToggles.TryGetValue(path, out var item))
            {
                var captured = path;
                item = new ToggleMenuFlyoutItem { Text = Path.GetFileName(path) };
                item.Click += (_, _) => _ = _orch.SwitchProfileAsync(captured);
                _profileToggles[path] = item;
                _profilesItem.Items.Insert(i, item);
            }
            else
            {
                var currentIdx = _profilesItem.Items.IndexOf(item);
                if (currentIdx != i)
                {
                    _profilesItem.Items.RemoveAt(currentIdx);
                    _profilesItem.Items.Insert(i, item);
                }
                var name = Path.GetFileName(path);
                if (item.Text != name) item.Text = name;
            }
            item.IsChecked = string.Equals(path, active, StringComparison.OrdinalIgnoreCase);
        }
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
