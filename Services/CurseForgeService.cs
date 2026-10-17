using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using CraftDeck.Models;
using CraftDeck.Models.CurseForge;

namespace CraftDeck.Services;

public static class CurseForgeService
{
    private const string Base = "https://api.curseforge.com/v1";
    private const string ApiKey = "$2a$10$6RwfR2B4TunkGqeDWxP/g.aeKQLuI7TfAmuP4eSNfdKE2Svja0W5q";

    private static async Task<string> GetAsync(string url, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("x-api-key", ApiKey);
        var resp = await Http.Client.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new Exception("CurseForge rejected the built-in API key.");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static int ClassId(ContentType t) => t switch
    {
        ContentType.Mod => 6,
        ContentType.ResourcePack => 12,
        ContentType.Shader => 6552,
        ContentType.DataPack => 6945,
        ContentType.Modpack => 4471,
        _ => 6,
    };

    public static async Task<List<CFMod>> SearchAsync(string query, ContentType type,
        string? gameVersion = null, string? loader = null, int pageSize = 30, CancellationToken ct = default)
    {
        var url = $"{Base}/mods/search?gameId=432&classId={ClassId(type)}" +
                  (string.IsNullOrEmpty(query) ? "" : $"&searchFilter={Uri.EscapeDataString(query)}") +
                  $"&pageSize={pageSize}&sortField=2";
        if (!string.IsNullOrEmpty(gameVersion)) url += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";
        var resp = Json.Load<CFList<CFMod>>(await GetAsync(url, ct));
        var mods = resp?.data ?? new();
        if (type == ContentType.Mod && !string.IsNullOrEmpty(loader) && loader is not ("Vanilla" or "vanilla"))
            mods = mods.Where(m => (m.latestFiles ?? new()).Any(f =>
                f.gameVersions?.Any(v => v.Equals(loader, StringComparison.OrdinalIgnoreCase)) == true)).ToList();
        return mods;
    }

    public static CFFile? PickFile(CFMod m, string? loader, ContentType type)
    {
        var files = (m.latestFiles ?? new List<CFFile>())
            .Where(f => f.fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                        f.fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .AsEnumerable();
        if (type != ContentType.Modpack && !string.IsNullOrEmpty(loader) && loader is not ("Vanilla" or "vanilla"))
        {
            var match = files.Where(f => f.gameVersions?.Any(v => v.Equals(loader, StringComparison.OrdinalIgnoreCase)) == true).ToList();
            if (match.Count > 0) files = match;
        }
        return files
            .OrderByDescending(f => DateTime.TryParse(f.fileDate, out var d) ? d : DateTime.MinValue)
            .FirstOrDefault();
    }

    public static string LoadersOf(CFFile f)
    {
        string[] known = { "Fabric", "Forge", "NeoForge", "Quilt", "OptiFine" };
        var found = (f.gameVersions ?? new List<string>()).Where(v => known.Contains(v)).Distinct().ToList();
        return found.Count > 0 ? string.Join(" ", found) : "";
    }

    public static List<string> VersionsOf(CFFile f) =>
        (f.gameVersions ?? new List<string>())
            .Where(v => v.Length > 3 && (char.IsDigit(v[0]) || v[0] == 'b'))
            .Take(2).ToList();

    public static async Task<string> ResolveDownloadUrlAsync(long projectId, long fileId, CancellationToken ct = default)
    {
        var raw = await GetAsync($"{Base}/mods/{projectId}/files/{fileId}/download", ct);
        var el = Json.Document(raw).RootElement.GetProperty("data");
        return el.ValueKind == JsonValueKind.String
            ? el.GetString()!
            : el.GetProperty("url").GetString()!;
    }

    public static async Task InstallAsync(Instance inst, CFMod m, string mcVersion, string loader,
        ContentType type, CancellationToken ct = default)
    {
        var f = PickFile(m, loader, type)
            ?? throw new Exception($"No compatible file for “{m.name}” on {mcVersion} ({loader}).");
        var url = string.IsNullOrEmpty(f.downloadUrl)
            ? await ResolveDownloadUrlAsync(f.modId, f.id, ct)
            : f.downloadUrl;
        var dir = ModrinthService.DirFor(inst, type);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, ModrinthService.Sanitize(f.fileName));
        var d = new Downloader(2);
        await d.RunAsync(new[] { new DownloadItem(url, target, f.fileLength) }, ct);
    }

    public static async Task<string> DownloadModpackAsync(long modId, string title,
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        var raw = await GetAsync($"{Base}/mods/{modId}/files?pageSize=50", ct);
        var files = Json.Load<CFList<CFFile>>(raw)?.data ?? new();
        var pack = files.FirstOrDefault(f => f.fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ?? throw new Exception($"“{title}” has no downloadable modpack file.");
        var url = string.IsNullOrEmpty(pack.downloadUrl)
            ? await ResolveDownloadUrlAsync(modId, pack.id, ct)
            : pack.downloadUrl;
        var dir = Path.Combine(Path.GetTempPath(), "Lumen", "packs");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, ModrinthService.Sanitize(pack.fileName));
        status?.Report($"Downloading {pack.fileName}…");
        var d = new Downloader(2);
        await d.RunAsync(new[] { new DownloadItem(url, target, pack.fileLength) }, ct);
        return target;
    }

    public static async Task ImportModpackAsync(Instance inst, string zipPath,
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        CFManifest manifest;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.GetEntry("manifest.json")
                ?? throw new Exception("Not a CurseForge modpack (no manifest.json)");
            manifest = JsonSerializer.Deserialize<CFManifest>(
                new StreamReader(entry.Open()).ReadToEnd(), Json.Options)!;

            var cfg = inst.Config;
            if (manifest.minecraft != null)
            {
                if (!string.IsNullOrEmpty(manifest.minecraft.version)) cfg.MCVersion = manifest.minecraft.version;
                var loader = manifest.minecraft.modLoaders?.FirstOrDefault(l => l.primary)
                             ?? manifest.minecraft.modLoaders?.FirstOrDefault();
                if (loader?.id != null)
                {
                    var dash = loader.id.LastIndexOf('-');
                    var name = (dash > 0 ? loader.id[..dash] : loader.id).ToLowerInvariant();
                    var ver = dash > 0 ? loader.id[(dash + 1)..] : "";
                    cfg.Loader = name switch
                    {
                        "fabric" => LoaderKind.Fabric,
                        "forge" => LoaderKind.Forge,
                        "neoforge" => LoaderKind.NeoForge,
                        "quilt" => LoaderKind.Quilt,
                        _ => LoaderKind.Vanilla,
                    };
                    cfg.LoaderVersion = ver;
                }
            }

            var items = new List<DownloadItem>();
            var files = (manifest.files ?? new()).Where(f => f.projectID > 0).ToList();
            status?.Report($"Resolving {files.Count} pack files…");
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                long fileId = f.fileID;
                try
                {
                    if (fileId == 0)
                    {
                        var raw = await GetAsync($"{Base}/mods/{f.projectID}/files?pageSize=1" +
                            $"&gameVersion={Uri.EscapeDataString(inst.Config.MCVersion)}", ct);
                        var latest = Json.Load<CFList<CFFile>>(raw)?.data?.FirstOrDefault();
                        if (latest == null) continue;
                        fileId = latest.id;
                    }
                    var url = await ResolveDownloadUrlAsync(f.projectID, fileId, ct);
                    var name = await FileNameAsync(f.projectID, fileId, url);
                    items.Add(new DownloadItem(url, Path.Combine(inst.ModsDir, ModrinthService.Sanitize(name)), 0));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    status?.Report($"Skipped project {f.projectID}: {ex.Message}");
                }
                await Task.Delay(120, ct);
            }
            var d = new Downloader(3);
            await d.RunAsync(items, ct);

            var overrides = manifest.overrides ?? "overrides";
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(overrides + "/") && !e.FullName.EndsWith("/")))
            {
                var target = Path.Combine(inst.GameDir, e.FullName[(overrides.Length + 1)..]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, true);
            }
        }
    }

    private static async Task<string> FileNameAsync(long projectId, long fileId, string cdnUrl)
    {
        try
        {
            var raw = await GetAsync($"{Base}/mods/{projectId}/files/{fileId}");
            var f = Json.Load<CFData<CFFile>>(raw)?.data;
            if (f?.fileName?.Length > 0) return f.fileName;
        }
        catch { }
        return Path.GetFileName(new Uri(cdnUrl).LocalPath);
    }
}
