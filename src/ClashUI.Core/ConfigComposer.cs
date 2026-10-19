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

    private static readonly YamlScalarNode KeyMixedPort = new("mixed-port");
    private static readonly YamlScalarNode KeyLogLevel = new("log-level");
    private static readonly YamlScalarNode KeyExternalController = new("external-controller");
    private static readonly YamlScalarNode KeySecret = new("secret");
    private static readonly YamlScalarNode KeyExternalUi = new("external-ui");
    private static readonly YamlScalarNode KeyTun = new("tun");
    private static readonly YamlScalarNode KeyEnable = new("enable");
    private static readonly YamlScalarNode KeyStack = new("stack");
    private static readonly YamlScalarNode KeyAutoRoute = new("auto-route");
    private static readonly YamlScalarNode KeyAutoDetectInterface = new("auto-detect-interface");
    private static readonly YamlScalarNode KeyStrictRoute = new("strict-route");
    private static readonly YamlScalarNode KeyDnsHijack = new("dns-hijack");
    private static readonly YamlScalarNode KeyDns = new("dns");
    private static readonly YamlScalarNode KeyEnhancedMode = new("enhanced-mode");
    private static readonly YamlScalarNode KeyFakeIpRange = new("fake-ip-range");
    private static readonly YamlScalarNode KeyNameserver = new("nameserver");
    private static readonly YamlScalarNode KeyRuleProviders = new("rule-providers");
    private static readonly YamlScalarNode KeyProxyProviders = new("proxy-providers");
    private static readonly YamlScalarNode KeyScript = new("script");
    private static readonly YamlScalarNode KeyShortcut = new("shortcut");
    private static readonly YamlScalarNode KeyPath = new("path");

    public static string ResolveProfile(string activeProfile, bool createDefault)
    {
        if (!string.IsNullOrWhiteSpace(activeProfile) && File.Exists(activeProfile)) return activeProfile;

        var fallback = Path.Combine(AppPaths.ProfilesDir, "default.yaml");
        if (!File.Exists(fallback) && createDefault) File.WriteAllText(fallback, DefaultProfile);
        return fallback;
    }

    private static readonly Lock _cacheGate = new();
    private static (string ProfilePath, DateTime LastWriteUtc, long FileLength, int SettingsHash)? _lastComposition;

    public static string Compose(AppSettings settings)
    {
        lock (_cacheGate)
        {
            var profilePath = ResolveProfile(settings.ActiveProfile, createDefault: true);
            var fi = new FileInfo(profilePath);
            var lastWriteUtc = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
            var fileLength = fi.Exists ? fi.Length : 0;
            int settingsHash = HashCode.Combine(settings.MixedPort, settings.ControllerAddr, settings.Secret, settings.TunEnabled);

            if (_lastComposition is { } last &&
                string.Equals(last.ProfilePath, profilePath, StringComparison.OrdinalIgnoreCase) &&
                last.LastWriteUtc == lastWriteUtc &&
                last.FileLength == fileLength &&
                last.SettingsHash == settingsHash &&
                File.Exists(AppPaths.RuntimeConfigFile))
            {
                return AppPaths.RuntimeConfigFile;
            }

            var root = LoadRoot(profilePath);

            Set(root, KeyMixedPort, settings.MixedPort.ToString());
            SetIfMissing(root, KeyLogLevel, new YamlScalarNode("warning"));
            Set(root, KeyExternalController, settings.ControllerAddr);
            Set(root, KeySecret, settings.Secret);
            Set(root, KeyExternalUi, "ui");
            ApplyTun(root, settings.TunEnabled);
            if (settings.TunEnabled)
                EnsureTunDns(root);
            RelocateAssets(root, profilePath);

            var stream = new YamlStream();
            stream.Documents.Add(new YamlDocument(root));
            WriteAtomic(stream);

            _lastComposition = (profilePath, lastWriteUtc, fileLength, settingsHash);

            AppLog.Info($"已合成运行时配置：{Path.GetFileName(profilePath)} -> {AppPaths.RuntimeConfigFile}");
            return AppPaths.RuntimeConfigFile;
        }
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

    private static void Set(YamlMappingNode root, YamlScalarNode lookupKey, string value)
    {
        var scalar = new YamlScalarNode(value);
        if (root.Children.ContainsKey(lookupKey))
            root.Children[lookupKey] = scalar;
        else
            root.Children.Add(new YamlScalarNode(lookupKey.Value), scalar);
    }

    private static void Set(YamlMappingNode root, string key, string value)
        => Set(root, new YamlScalarNode(key), value);

    private static void SetIfMissing(YamlMappingNode root, YamlScalarNode lookupKey, YamlNode value)
    {
        if (!root.Children.ContainsKey(lookupKey))
            root.Children.Add(new YamlScalarNode(lookupKey.Value), value);
    }

    private static void SetIfMissing(YamlMappingNode root, string key, YamlNode value)
        => SetIfMissing(root, new YamlScalarNode(key), value);

    private static YamlMappingNode? GetMapping(YamlMappingNode root, YamlScalarNode lookupKey)
        => root.Children.TryGetValue(lookupKey, out var node) ? node as YamlMappingNode : null;

    private static YamlMappingNode? GetMapping(YamlMappingNode root, string key)
        => GetMapping(root, new YamlScalarNode(key));

    private static YamlMappingNode GetOrAddMapping(YamlMappingNode root, YamlScalarNode lookupKey)
    {
        if (GetMapping(root, lookupKey) is { } existing) return existing;
        if (root.Children.TryGetValue(lookupKey, out var bad))
            throw new ConfigCompositionException($"配置项 {lookupKey.Value} 不是键值映射（实际为 {bad.NodeType}），请修正订阅文件");

        var created = new YamlMappingNode();
        root.Children.Add(new YamlScalarNode(lookupKey.Value), created);
        return created;
    }

    private static YamlMappingNode GetOrAddMapping(YamlMappingNode root, string key)
        => GetOrAddMapping(root, new YamlScalarNode(key));

    /// <summary>TUN 只强制 enable，其余字段订阅优先，缺失时才填应用默认值。</summary>
    private static void ApplyTun(YamlMappingNode root, bool enable)
    {
        var tun = GetOrAddMapping(root, KeyTun);
        Set(tun, KeyEnable, enable ? "true" : "false");
        SetIfMissing(tun, KeyStack, new YamlScalarNode("mixed"));
        SetIfMissing(tun, KeyAutoRoute, new YamlScalarNode("true"));
        SetIfMissing(tun, KeyAutoDetectInterface, new YamlScalarNode("true"));
        SetIfMissing(tun, KeyStrictRoute, new YamlScalarNode("false"));
        SetIfMissing(tun, KeyDnsHijack, new YamlSequenceNode(new YamlScalarNode("any:53")));
    }

    /// <summary>
    /// 运行时配置固定落在数据目录、核心以 -d &lt;数据目录&gt; 运行，而订阅文件在 profiles\ 下；
    /// 订阅里的相对路径必须改基准，否则规则集 / 节点 / 脚本资源在核心眼中全部失踪。
    /// </summary>
    private static void RelocateAssets(YamlMappingNode root, string profilePath)
    {
        RelocateProviderPaths(root, KeyRuleProviders, profilePath);
        RelocateProviderPaths(root, KeyProxyProviders, profilePath);

        if (GetMapping(root, KeyScript) is { } script) RelocatePath(script, KeyShortcut, profilePath);
    }

    private static void RelocateProviderPaths(YamlMappingNode root, YamlScalarNode section, string profilePath)
    {
        if (GetMapping(root, section) is not { } providers) return;
        foreach (var entry in providers.Children)
            if (entry.Value is YamlMappingNode provider) RelocatePath(provider, KeyPath, profilePath);
    }

    private static void RelocatePath(YamlMappingNode owner, YamlScalarNode key, string profilePath)
    {
        if (!owner.Children.TryGetValue(key, out var node)) return;
        if (node is not YamlScalarNode { Value: { } raw }) return;
        if (Relocate(raw, profilePath) is not { } relocated || relocated == raw) return;

        AppLog.Info($"重定位 {key.Value}：{raw} -> {relocated}");
        owner.Children[key] = new YamlScalarNode(relocated);
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
        var dns = GetOrAddMapping(root, KeyDns);
        Set(dns, KeyEnable, "true");
        SetIfMissing(dns, KeyEnhancedMode, new YamlScalarNode("fake-ip"));
        SetIfMissing(dns, KeyFakeIpRange, new YamlScalarNode("198.18.0.1/16"));
        SetIfMissing(dns, KeyNameserver, new YamlSequenceNode(
            new YamlScalarNode("223.5.5.5"), new YamlScalarNode("119.29.29.29")));
    }
}

public sealed class ConfigCompositionException(string message, Exception? inner = null)
    : Exception(message, inner);