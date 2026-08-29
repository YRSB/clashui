using YamlDotNet.RepresentationModel;

namespace Clashui.Core;

/// <summary>
/// 把用户订阅 profile 与应用注入项（端口 / 密钥 / TUN / external-ui）合成核心实际加载的运行时配置。
/// 只走 YamlDotNet 的文档模型（无反射反序列化），对 NativeAOT 友好。
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

    /// 合成运行时配置并写入数据目录，返回配置文件路径。
    public static string Compose(AppSettings settings)
    {
        var root = LoadRoot(ResolveProfile(settings.ActiveProfile, createDefault: true));

        Set(root, "mixed-port", settings.MixedPort.ToString());
        Set(root, "log-level", "info");
        Set(root, "external-controller", settings.ControllerAddr);
        Set(root, "secret", settings.Secret);
        Set(root, "external-ui", "ui");
        Set(root, "external-ui-url", settings.DashboardUrl);
        SetNode(root, "tun", BuildTun(settings.TunEnabled));
        if (settings.TunEnabled && !root.Children.ContainsKey("dns"))
            SetNode(root, "dns", BuildDns());

        using var writer = new StringWriter();
        var stream = new YamlStream();
        stream.Documents.Add(new YamlDocument(root));
        stream.Save(writer);
        File.WriteAllText(AppPaths.RuntimeConfigFile, writer.ToString());
        return AppPaths.RuntimeConfigFile;
    }

    private static YamlMappingNode LoadRoot(string profilePath)
    {
        var stream = new YamlStream();
        using (var reader = new StreamReader(profilePath))
        {
            stream.Load(reader);
        }
        return stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode map
            ? map
            : new YamlMappingNode();
    }

    private static void Set(YamlMappingNode root, string key, string value)
        => SetNode(root, key, new YamlScalarNode(value));

    private static void SetNode(YamlMappingNode root, string key, YamlNode value)
    {
        var k = new YamlScalarNode(key);
        if (root.Children.ContainsKey(k)) root.Children.Remove(k);
        root.Children.Add(k, value);
    }

    private static YamlMappingNode BuildTun(bool enable) => new()
    {
        { "enable", enable ? "true" : "false" },
        { "stack", "mixed" },
        { "auto-route", "true" },
        { "auto-detect-interface", "true" },
        { "strict-route", "false" },
        { "dns-hijack", new YamlSequenceNode(new YamlScalarNode("any:53")) },
    };

    private static YamlMappingNode BuildDns() => new()
    {
        { "enable", "true" },
        { "enhanced-mode", "fake-ip" },
        { "fake-ip-range", "198.18.0.1/16" },
        { "nameserver", new YamlSequenceNode(
            new YamlScalarNode("223.5.5.5"), new YamlScalarNode("119.29.29.29")) },
    };
}
