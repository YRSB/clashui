using System.IO.Compression;

namespace ClashUI.Core;

public static class PanelProvisioner
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static volatile IReadOnlyList<string>? _availableCache;

    public static bool IsAvailable(string id)
    {
        var norm = PanelCatalog.Normalize(id);
        var cached = _availableCache;
        if (cached is not null) return cached.Contains(norm);
        return File.Exists(Path.Combine(AppPaths.PanelDir(norm), "index.html"));
    }

    public static IReadOnlyList<string> GetAvailable()
    {
        if (_availableCache is { } cached) return cached;
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
        _availableCache = list;
        return list;
    }

    public static async Task<IReadOnlyList<string>> EnsureAllAsync(Action<string>? notify = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(AppPaths.UiDir);
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ClashUI");

            foreach (var panel in PanelCatalog.All)
            {
                ct.ThrowIfCancellationRequested();
                var dest = AppPaths.PanelDir(panel.Id);
                if (File.Exists(Path.Combine(dest, "index.html")))
                    continue;
                try
                {
                    await EnsureOneAsync(panel, dest, client, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Error($"面板 {panel.Id} 准备失败", ex);
                    try { notify?.Invoke($"面板 {panel.DisplayName} 下载失败：{ex.Message}"); } catch { }
                }
            }
            _availableCache = null;
            return GetAvailable();
        }
        finally
        {
            try { _gate.Release(); } catch { }
        }
    }

    private static async Task EnsureOneAsync(PanelInfo panel, string dest, HttpClient client, CancellationToken ct)
    {
        var stagingDir = Path.Combine(AppPaths.UiDir, $".staging-{panel.Id}-{Guid.NewGuid():N}");
        var tmpZip = Path.Combine(stagingDir, "panel.zip");
        var tmpDir = Path.Combine(stagingDir, "extracted");
        try
        {
            Directory.CreateDirectory(stagingDir);
            using (var resp = await client.GetAsync(panel.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
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
        }
        finally
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); } catch { }
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
