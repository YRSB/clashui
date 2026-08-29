namespace Clashui.Core;

/// 应用数据目录布局：%LOCALAPPDATA%\Clashui
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clashui");

    public static string ProfilesDir => Path.Combine(Root, "profiles");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string UiDir => Path.Combine(Root, "ui");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string RuntimeConfigFile => Path.Combine(Root, "config.runtime.yaml");
    public static string CoreExe => Path.Combine(Root, "mihomo.exe");
    public static string CoreLogFile => Path.Combine(LogsDir, "core.log");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(LogsDir);
    }
}
