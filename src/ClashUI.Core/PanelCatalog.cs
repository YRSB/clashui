namespace ClashUI.Core;

public enum PanelQueryMode
{
    QueryBeforeHash,
    HashInner,
}

public sealed record PanelInfo(string Id, string DirName, string DisplayName, string ZipUrl, PanelQueryMode QueryMode);

public static class PanelCatalog
{
    public const string Default = "metacubexd";

    public static IReadOnlyList<PanelInfo> All { get; } =
    [
        new("yacd", "yacd", "Yacd", "https://github.com/MetaCubeX/Yacd-meta/archive/gh-pages.zip", PanelQueryMode.QueryBeforeHash),
        new("metacubexd", "metacubexd", "Metacubexd", "https://github.com/MetaCubeX/metacubexd/archive/gh-pages.zip", PanelQueryMode.HashInner),
        new("zashboard", "zashboard", "zashboard", "https://github.com/Zephyruso/zashboard/releases/latest/download/dist.zip", PanelQueryMode.HashInner),
    ];

    public static bool IsValid(string? id)
        => All.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string? id)
        => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))?.Id ?? Default;

    public static PanelInfo Get(string? id)
        => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All.First(p => p.Id == Default);
}
