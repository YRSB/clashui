using ClashUI.Core;
using Microsoft.UI.Xaml.Controls;
using WinUIEx;

namespace ClashUI.App.Tray;

public sealed class WinUiTrayView : ITrayView
{
    private readonly string _assetsDir;
    private readonly TrayIcon _icon;
    private readonly Action _showWindow;
    private readonly Action _toggleWindow;
    private string? _lastIconFile;
    private TrayViewModel? _latest;

    public event Action<TrayCommand>? Command;

    public WinUiTrayView(Action showWindow, Action toggleWindow, string iconPath, Action<TrayCommand> onCommand)
    {
        _showWindow = showWindow;
        _toggleWindow = toggleWindow;
        _assetsDir = Path.GetDirectoryName(iconPath) ?? AppContext.BaseDirectory;
        Command += onCommand;

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
        _icon.ContextMenu += (_, e) => e.Flyout = BuildMenu(_latest);
    }

    public void Render(TrayViewModel m)
    {
        if (!string.Equals(_lastIconFile, m.IconFile, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _icon.SetIcon(Path.Combine(_assetsDir, m.IconFile));
                _lastIconFile = m.IconFile;
            }
            catch (Exception ex)
            {
                AppLog.Error("更新托盘图标失败", ex);
            }
        }
        try
        {
            _icon.Tooltip = m.Tooltip;
        }
        catch (Exception ex)
        {
            AppLog.Error("更新托盘提示失败", ex);
        }
        _latest = m;
    }

    private MenuFlyout BuildMenu(TrayViewModel? m)
    {
        var menu = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };

        menu.Items.Add(Item("显示面板", () => Command?.Invoke(new TrayCommand(TrayCommandKind.ShowPanel))));

        var profilesItem = new MenuFlyoutSubItem { Text = "配置文件" };
        if (m is null || m.IsEmpty || m.Profiles.Count == 0)
        {
            profilesItem.Items.Add(new MenuFlyoutItem { Text = "（profiles 目录为空）", IsEnabled = false });
        }
        else
        {
            foreach (var e in m.Profiles)
            {
                var captured = e.Path;
                var toggle = new ToggleMenuFlyoutItem { Text = e.Name, IsChecked = e.IsChecked };
                toggle.Click += (_, _) => Safe(() => Command?.Invoke(new TrayCommand(TrayCommandKind.SwitchProfile, captured)));
                profilesItem.Items.Add(toggle);
            }
        }
        profilesItem.Items.Add(new MenuFlyoutSeparator());
        profilesItem.Items.Add(Item("打开 profiles 目录", () => Command?.Invoke(new TrayCommand(TrayCommandKind.OpenProfilesFolder))));
        menu.Items.Add(profilesItem);

        var panelsItem = new MenuFlyoutSubItem { Text = "面板" };
        if (m is not null)
        {
            foreach (var e in m.Panels)
            {
                var captured = e.Id;
                var toggle = new ToggleMenuFlyoutItem { Text = e.Name, IsChecked = e.IsChecked, IsEnabled = e.IsEnabled };
                toggle.Click += (_, _) => Safe(() => Command?.Invoke(new TrayCommand(TrayCommandKind.SwitchPanel, captured)));
                panelsItem.Items.Add(toggle);
            }
        }
        panelsItem.Items.Add(new MenuFlyoutSeparator());
        var retryPanelsItem = new MenuFlyoutItem { Text = "重新下载缺失面板" };
        retryPanelsItem.Click += (_, _) => Safe(() => Command?.Invoke(new TrayCommand(TrayCommandKind.RetryPanels)));
        retryPanelsItem.IsEnabled = m is not null && m.Panels.Any(p => !p.IsEnabled);
        panelsItem.Items.Add(retryPanelsItem);
        menu.Items.Add(panelsItem);

        menu.Items.Add(Toggle("系统代理", m?.SystemProxyChecked ?? false, () => Command?.Invoke(new TrayCommand(TrayCommandKind.ToggleSystemProxy))));
        menu.Items.Add(Toggle("TUN 模式", m?.TunChecked ?? false, () => Command?.Invoke(new TrayCommand(TrayCommandKind.ToggleTun))));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("重启核心", () => Command?.Invoke(new TrayCommand(TrayCommandKind.RestartCore))));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("打开数据目录", () => Command?.Invoke(new TrayCommand(TrayCommandKind.OpenDataFolder))));
        menu.Items.Add(Toggle("静默启动", m?.SilentChecked ?? false, () => Command?.Invoke(new TrayCommand(TrayCommandKind.ToggleSilentStart))));
        var autoStartItem = Toggle("开机自启（静默）", m?.AutoStartChecked ?? false, null);
        autoStartItem.Click += (_, _) => Safe(() => Command?.Invoke(new TrayCommand(TrayCommandKind.ToggleAutoStart, Flag: autoStartItem.IsChecked)));
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("退出", () => Command?.Invoke(new TrayCommand(TrayCommandKind.Exit))));

        return menu;
    }

    private ToggleMenuFlyoutItem Toggle(string text, bool isChecked, Action? onClick)
    {
        var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = isChecked };
        if (onClick is not null)
            item.Click += (_, _) => Safe(onClick);
        return item;
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
