using ClashUI.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using WinUIEx;

namespace ClashUI.App.Tray;

public sealed class WinUiTrayView : ITrayView
{
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
    private readonly Action _showWindow;
    private readonly Action _toggleWindow;

    public event Action<TrayCommand>? Command;

    public WinUiTrayView(Action showWindow, Action toggleWindow, string iconPath, Action<TrayCommand> onCommand)
    {
        _showWindow = showWindow;
        _toggleWindow = toggleWindow;
        _assetsDir = Path.GetDirectoryName(iconPath) ?? AppContext.BaseDirectory;
        Command += onCommand;

        var showItem = Item("显示面板", () => Command?.Invoke(new TrayCommand(TrayCommandKind.ShowPanel)));
        var restartItem = Item("重启核心", () => Command?.Invoke(new TrayCommand(TrayCommandKind.RestartCore)));
        var dataItem = Item("打开数据目录", () => Command?.Invoke(new TrayCommand(TrayCommandKind.OpenDataFolder)));
        var exitItem = Item("退出", () => Command?.Invoke(new TrayCommand(TrayCommandKind.Exit)));

        _sysProxyItem = new ToggleMenuFlyoutItem { Text = "系统代理" };
        _sysProxyItem.Click += (_, _) => Safe(() => Command?.Invoke(new TrayCommand(TrayCommandKind.ToggleSystemProxy)));
        _tunItem = new ToggleMenuFlyoutItem { Text = "TUN 模式" };
        _tunItem.Click += (_, _) => Safe(() => Command?.Invoke(new TrayCommand(TrayCommandKind.ToggleTun)));
        _silentStartItem = new ToggleMenuFlyoutItem { Text = "静默启动" };
        _silentStartItem.Click += (_, _) => Safe(() => Command?.Invoke(new TrayCommand(TrayCommandKind.ToggleSilentStart)));
        _autoStartItem = new ToggleMenuFlyoutItem { Text = "开机自启（静默）" };
        _autoStartItem.Click += (_, _) => Safe(() => Command?.Invoke(new TrayCommand(TrayCommandKind.ToggleAutoStart)));

        _openProfilesItem = Item("打开 profiles 目录", () => Command?.Invoke(new TrayCommand(TrayCommandKind.OpenProfilesFolder)));
        _profilesItem.Items.Add(_profilesSeparator);
        _profilesItem.Items.Add(_openProfilesItem);

        _menu = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
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
    }

    public void Render(TrayViewModel m)
    {
        _sysProxyItem.IsChecked = m.SystemProxyChecked;
        _tunItem.IsChecked = m.TunChecked;
        _silentStartItem.IsChecked = m.SilentChecked;
        _autoStartItem.IsChecked = m.AutoStartChecked;
        try
        {
            _icon.SetIcon(Path.Combine(_assetsDir, m.IconFile));
            _icon.Tooltip = m.Tooltip;
        }
        catch (Exception ex)
        {
            AppLog.Error("更新托盘图标失败", ex);
        }
        RenderProfiles(m.Profiles, m.IsEmpty);
    }

    private void RenderProfiles(IReadOnlyList<ProfileEntry> profiles, bool isEmpty)
    {
        if (isEmpty)
        {
            foreach (var kv in _profileToggles.ToList())
                _profilesItem.Items.Remove(kv.Value);
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
        var wanted = new HashSet<string>(profiles.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var key in _profileToggles.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _profilesItem.Items.Remove(_profileToggles[key]);
            _profileToggles.Remove(key);
        }
        for (var i = 0; i < profiles.Count; i++)
        {
            var e = profiles[i];
            if (!_profileToggles.TryGetValue(e.Path, out var item))
            {
                var captured = e.Path;
                item = new ToggleMenuFlyoutItem { Text = e.Name };
                item.Click += (_, _) => Command?.Invoke(new TrayCommand(TrayCommandKind.SwitchProfile, captured));
                _profileToggles[e.Path] = item;
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
                if (item.Text != e.Name) item.Text = e.Name;
            }
            item.IsChecked = e.IsChecked;
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

public sealed class FakeTrayView : ITrayView
{
    public TrayViewModel? LastModel { get; private set; }
    public int RenderCount { get; private set; }
    public event Action<TrayCommand>? Command;
    public void Render(TrayViewModel model) { LastModel = model; RenderCount++; }
    public void Emit(TrayCommand cmd) => Command?.Invoke(cmd);
    public void Dispose() { }
}
