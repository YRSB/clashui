using System.Text;
using YamlDotNet.RepresentationModel;
namespace ClashUI.Core;

/// <summary>
/// 把用户订阅 profile 与应用注入项（端口 / 密钥 / TUN / external-ui）合成核心实际加载的运行时配置。
/// 只走 YamlDotNet 的文档模型（无反射反序列化），对 NativeAOT 友好。
/// 订阅原文只读不写；合成失败抛 <see cref="ConfigCompositionException"/>，既有运行时配置原样保留。
/// </summary>
public static class ConfigComposer
{
    private const string DefaultProfile = """
proxies: []
proxy-groups:
  - name: PROXY
    type: select
    proxies:
      - DIRECT
rules:
  - MATCH,DIRECT
""";

    public static string ResolveProfile(string activeProfile, bool createDefault)
    {
        if (!string.IsNullOrWhiteSpace(activeProfile) && File.Exists(activeProfile)) return activeProfile;

        var fallback = Path.Combine(AppPaths.ProfilesDir, "default.yaml");
        if (!File.Exists(fallback) && createDefault) File.WriteAllText(fallback, DefaultProfile);
        return fallback;
    }

    public static string Compose(AppSettings settings)
    {
        var profilePath = ResolveProfile(settings.ActiveProfile, createDefault: true);
        var root = LoadRoot(profilePath);

        Set(root, "mixed-port", settings.MixedPort.ToString());
        Set(root, "log-level", "info");
        Set(root, "external-controller", settings.ControllerAddr);
        Set(root, "secret", settings.Secret);
        Set(root, "external-ui", "ui");
        ApplyTun(root, settings.TunEnabled);
        if (settings.TunEnabled)
            EnsureTunDns(root);
        RelocateAssets(root, profilePath);

        var stream = new YamlStream();
        stream.Documents.Add(new YamlDocument(root));
        WriteAtomic(stream);
        AppLog.Info($"已合成运行时配置：{Path.GetFileName(profilePath)} -> {AppPaths.RuntimeConfigFile}");
        return AppPaths.RuntimeConfigFile;
    }

    private static YamlMappingNode LoadRoot(string profilePath)
    {
        var name = Path.GetFileName(profilePath);
        var stream = new YamlStream();
        try
        {
            using (var reader = new StreamReader(profilePath))
            {
                stream.Load(reader);
            }
        }
        catch (Exception ex)
        {
            throw new ConfigCompositionException($"配置文件 {name} 不是有效的 YAML：{ex.Message}", ex);
        }

        if (stream.Documents.Count > 1)
            AppLog.Info($"配置文件 {name} 含 {stream.Documents.Count} 个 YAML 文档，仅首个文档参与合成（与 mihomo 解析行为一致）");

        if (stream.Documents.Count == 0)
            throw new ConfigCompositionException($"配置文件 {name} 为空，未生成运行时配置");

        if (stream.Documents[0].RootNode is not YamlMappingNode map)
            throw new ConfigCompositionException(
                $"配置文件 {name} 顶层不是键值映射（常见于订阅过期返回的 HTML 页面、加密内容或非 Clash 配置），未生成运行时配置");

        return map;
    }

    private static void Set(YamlMappingNode root, string key, string value)
        => SetNode(root, key, new YamlScalarNode(value));

    private static void SetNode(YamlMappingNode root, string key, YamlNode value)
    {
        var k = new YamlScalarNode(key);
        if (root.Children.ContainsKey(k)) root.Children.Remove(k);
        root.Children.Add(k, value);
    }

    private static void SetIfMissing(YamlMappingNode root, string key, YamlNode value)
    {
        if (!root.Children.ContainsKey(new YamlScalarNode(key))) SetNode(root, key, value);
    }

    private static YamlMappingNode? GetMapping(YamlMappingNode root, string key)
        => root.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node as YamlMappingNode : null;

    private static YamlMappingNode GetOrAddMapping(YamlMappingNode root, string key)
    {
        if (GetMapping(root, key) is { } existing) return existing;
        if (root.Children.TryGetValue(new YamlScalarNode(key), out var bad))
            throw new ConfigCompositionException($"配置项 {key} 不是键值映射（实际为 {bad.NodeType}），请修正订阅文件");

        var created = new YamlMappingNode();
        SetNode(root, key, created);
        return created;
    }

    /// <summary>TUN 只强制 enable，其余字段订阅优先，缺失时才填应用默认值。</summary>
    private static void ApplyTun(YamlMappingNode root, bool enable)
    {
        var tun = GetOrAddMapping(root, "tun");
        Set(tun, "enable", enable ? "true" : "false");
        SetIfMissing(tun, "stack", new YamlScalarNode("mixed"));
        SetIfMissing(tun, "auto-route", new YamlScalarNode("true"));
        SetIfMissing(tun, "auto-detect-interface", new YamlScalarNode("true"));
        SetIfMissing(tun, "strict-route", new YamlScalarNode("false"));
        SetIfMissing(tun, "dns-hijack", new YamlSequenceNode(new YamlScalarNode("any:53")));
    }

    /// <summary>
    /// 运行时配置固定落在数据目录、核心以 -d &lt;数据目录&gt; 运行，而订阅文件在 profiles\ 下；
    /// 订阅里的相对路径必须改基准，否则规则集 / 节点 / 脚本资源在核心眼中全部失踪。
    /// </summary>
    private static void RelocateAssets(YamlMappingNode root, string profilePath)
    {
        RelocateProviderPaths(root, "rule-providers", profilePath);
        RelocateProviderPaths(root, "proxy-providers", profilePath);

        if (GetMapping(root, "script") is { } script) RelocatePath(script, "shortcut", profilePath);
    }

    private static void RelocateProviderPaths(YamlMappingNode root, string section, string profilePath)
    {
        if (GetMapping(root, section) is not { } providers) return;
        foreach (var entry in providers.Children)
            if (entry.Value is YamlMappingNode provider) RelocatePath(provider, "path", profilePath);
    }

    private static void RelocatePath(YamlMappingNode owner, string key, string profilePath)
    {
        if (!owner.Children.TryGetValue(new YamlScalarNode(key), out var node)) return;
        if (node is not YamlScalarNode { Value: { } raw }) return;
        if (Relocate(raw, profilePath) is not { } relocated || relocated == raw) return;

        AppLog.Info($"重定位 {key}：{raw} -> {relocated}");
        owner.Children[new YamlScalarNode(key)] = new YamlScalarNode(relocated);
    }

    private static string? Relocate(string raw, string profilePath)
    {
        var path = raw.Trim();
        if (path.Length == 0) return null;
        if (path.StartsWith('~') || path.Contains("://", StringComparison.Ordinal)) return null;
        if (Path.IsPathRooted(path)) return null;

        var baseDir = Path.GetDirectoryName(profilePath) ?? AppPaths.ProfilesDir;
        try
        {
            var full = Path.GetFullPath(Path.Combine(baseDir, path));
            return Path.GetRelativePath(AppPaths.Root, full).Replace('\\', '/');
        }
        catch (Exception ex)
        {
            AppLog.Error($"无法重定位相对路径 {raw}，保留原值", ex);
            return null;
        }
    }

    private static void WriteAtomic(YamlStream stream)
    {
        var target = AppPaths.RuntimeConfigFile;
        var tmp = target + ".tmp";
        try
        {
            using (var writer = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                stream.Save(writer, false);
                writer.Flush();
                if (writer.BaseStream is FileStream fs) fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    private static void EnsureTunDns(YamlMappingNode root)
    {
        var dns = GetOrAddMapping(root, "dns");
        Set(dns, "enable", "true");
        SetIfMissing(dns, "enhanced-mode", new YamlScalarNode("fake-ip"));
        SetIfMissing(dns, "fake-ip-range", new YamlScalarNode("198.18.0.1/16"));
        SetIfMissing(dns, "nameserver", new YamlSequenceNode(
            new YamlScalarNode("223.5.5.5"), new YamlScalarNode("119.29.29.29")));
    }
}

public sealed class ConfigCompositionException(string message, Exception? inner = null)
    : Exception(message, inner);