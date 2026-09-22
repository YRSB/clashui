using System.IO.Compression;

namespace ClashUI.Core;

public static class PanelProvisioner
{
    public static bool IsAvailable(string id)
        => File.Exists(Path.Combine(AppPaths.PanelDir(PanelCatalog.Normalize(id)), "index.html"));

    public static IReadOnlyList<string> GetAvailable()
    {
        var list = new List<string>();
        foreach (var p in PanelCatalog.All)
        {
            try
            {
                if (File.Exists(Path.Combine(AppPaths.PanelDir(p.Id), "index.html")))
                    list.Add(p.Id);
            }
            catch
            {
            }
        }
        return list;
    }

    public static async Task<IReadOnlyList<string>> EnsureAllAsync(Action<string>? notify = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppPaths.UiDir);
        foreach (var panel in PanelCatalog.All)
        {
            ct.ThrowIfCancellationRequested();
            var dest = AppPaths.PanelDir(panel.Id);
            if (File.Exists(Path.Combine(dest, "index.html")))
                continue;
            try
            {
                await EnsureOneAsync(panel, dest, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error($"面板 {panel.Id} 准备失败", ex);
                try { notify?.Invoke($"面板 {panel.DisplayName} 下载失败：{ex.Message}"); } catch { }
            }
        }
        return GetAvailable();
    }

    private static async Task EnsureOneAsync(PanelInfo panel, string dest, CancellationToken ct)
    {
        var tmpZip = Path.Combine(Path.GetTempPath(), $"clashui-{panel.Id}-{Guid.NewGuid():N}.zip");
        var tmpDir = Path.Combine(Path.GetTempPath(), $"clashui-{panel.Id}-{Guid.NewGuid():N}");
        try
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ClashUI");
                client.Timeout = TimeSpan.FromMinutes(5);
                using var resp = await client.GetAsync(panel.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var fs = new FileStream(tmpZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            Directory.CreateDirectory(tmpDir);
            ZipFile.ExtractToDirectory(tmpZip, tmpDir);
            var src = ResolveRoot(tmpDir);
            if (Directory.Exists(dest))
                Directory.Delete(dest, recursive: true);
            Directory.CreateDirectory(AppPaths.UiDir);
            Directory.Move(src, dest);
            if (!string.Equals(src, tmpDir, StringComparison.OrdinalIgnoreCase) && Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    private static string ResolveRoot(string tmpDir)
    {
        var entries = Directory.GetFileSystemEntries(tmpDir);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
            return entries[0];
        return tmpDir;
    }
}
