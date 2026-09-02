namespace ClashUI.Core;

/// mihomo 可执行文件的解析顺序：settings.MihomoPath → 数据目录 → PATH 环境变量。
public static class CoreLocator
{
    public static string? Resolve(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;
        if (File.Exists(AppPaths.CoreExe))
            return AppPaths.CoreExe;
        return FindOnPath("mihomo.exe");
    }

    public static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // PATH 中的非法路径段直接跳过
            }
        }
        return null;
    }
}
