using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClashUI.Core;

public sealed class AppSettings
{
    /// 可选；留空则使用数据目录下的 mihomo.exe
    public string MihomoPath { get; set; } = "";
    public int MixedPort { get; set; } = 7890;
    public string ControllerAddr { get; set; } = "127.0.0.1:9090";
    public string Secret { get; set; } = "";
    /// mihomo external-ui-url，核心启动时自动下载面板到数据目录 ui/
    public string DashboardUrl { get; set; } = "https://github.com/Metacubex/metacubexd/archive/gh-pages.zip";
    public string ActiveProfile { get; set; } = "";
    public bool TunEnabled { get; set; } = true;
    public bool SystemProxyEnabled { get; set; }
    public bool StartCoreOnLaunch { get; set; } = true;
    /// 静默启动：启动时不显示主窗口，仅托盘运行
    public bool SilentStart { get; set; }
    /// 提权操作挂起标记：非管理员下改开机自启失败时记录目标状态，
    /// 提权重启后由 ProcessPendingOperations 补做并清空
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PendingAutoStart { get; set; }
}

public static partial class SettingsStore
{
    [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(AppSettings))]
    internal sealed partial class Ctx : JsonSerializerContext { }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var settings = JsonSerializer.Deserialize(File.ReadAllText(AppPaths.SettingsFile), Ctx.Default.AppSettings);
                if (settings is not null) return settings;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("读取 settings.json 失败，使用默认设置", ex);
        }
        return CreateDefault();
    }

    public static void Save(AppSettings settings)
    {
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, Ctx.Default.AppSettings));
    }

    private static AppSettings CreateDefault()
    {
        var settings = new AppSettings
        {
            Secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
        };
        Save(settings);
        return settings;
    }
}
