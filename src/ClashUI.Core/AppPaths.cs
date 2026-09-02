namespace ClashUI.Core;

/// 应用数据目录布局：%LOCALAPPDATA%\ClashUI
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClashUI");

    public static string ProfilesDir => Path.Combine(Root, "profiles");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string UiDir => Path.Combine(Root, "ui");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string RuntimeConfigFile => Path.Combine(Root, "config.runtime.yaml");
    public static string CoreExe => Path.Combine(Root, "mihomo.exe");
    public static string CoreLogFile => Path.Combine(LogsDir, "core.log");
    /// WebView2 用户数据目录：默认在 exe 旁会随缓存膨胀（实测 69MB）且污染发布目录
    public static string WebView2DataDir => Path.Combine(Root, "webview2");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(LogsDir);
    }
}
