using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CraftDeck.Models;
using CraftDeck.Models.Modrinth;

namespace CraftDeck.Services;

public enum ContentType { Mod, Shader, ResourcePack, DataPack, Modpack }

public static class ModrinthService
{
    private const string Base = "https://api.modrinth.com/v2";

    public static async Task<List<Hit>> SearchAsync(string query, ContentType type,
        string? gameVersion = null, string? loader = null, int limit = 40, int offset = 0)
    {
        var facets = new List<List<string>> { new() { "project_type:" + TypeSlug(type) } };
        if (!string.IsNullOrEmpty(gameVersion)) facets.Add(new List<string> { "versions:" + gameVersion });
        if (type == ContentType.Mod && !string.IsNullOrEmpty(loader) && loader is not ("Vanilla" or "vanilla"))
            facets.Add(new List<string> { "categories:" + loader.ToLower() });
        var url = $"{Base}/search?query={Uri.EscapeDataString(query)}&limit={limit}&offset={offset}" +
                  $"&index={(string.IsNullOrEmpty(query) ? "downloads" : "relevance")}" +
                  $"&facets={WebUtility.UrlEncode(JsonSerializer.Serialize(facets))}";
        var resp = await Http.GetJsonAsync<SearchResponse>(url);
        return resp.hits ?? new();
    }

    public static string TypeSlug(ContentType t) => t switch
    {
        ContentType.Shader => "shader",
        ContentType.ResourcePack => "resourcepack",
        ContentType.DataPack => "datapack",
        ContentType.Modpack => "modpack",
        _ => "mod",
    };

    public static string DirFor(Instance inst, ContentType t) => t switch
    {
        ContentType.Shader => inst.ShaderPacksDir,
        ContentType.ResourcePack => inst.ResourcePacksDir,
        ContentType.DataPack => Path.Combine(inst.DataPacksDir, "world", "datapacks"),
        _ => inst.ModsDir,
    };

    public static async Task<List<ProjectVersion>> GetVersionsAsync(string projectId) =>
        await Http.GetJsonAsync<List<ProjectVersion>>($"{Base}/project/{projectId}/version");

    public static async Task<ProjectVersion?> GetBestFileAsync(string projectId, string mcVersion,
        string loader, ContentType type)
    {
        var versions = await GetVersionsAsync(projectId);
        return versions
            .Where(v => v.game_versions?.Contains(mcVersion) == true)
            .Where(v => type is ContentType.Shader or ContentType.ResourcePack or ContentType.DataPack
                        || v.loaders?.Contains(loader.ToLower()) == true
                        || (loader == "NeoForge" && v.loaders?.Contains("neoforge") == true))
            .OrderByDescending(v => v.featured)
            .ThenByDescending(v => v.date_published)
            .FirstOrDefault(v => v.files?.Count > 0);
    }

    public static async Task<string> DownloadFileAsync(ProjectVersion pv, string dir, CancellationToken ct = default)
    {
        var file = pv.files.FirstOrDefault(f => f.primary) ?? pv.files[0];
        var target = Path.Combine(dir, Sanitize(file.filename));
        Directory.CreateDirectory(dir);
        var d = new Downloader(2);
        await d.RunAsync(new[] { new DownloadItem(file.url, target, file.size) }, ct);
        return target;
    }

    public static async Task InstallAsync(Instance inst, Hit hit, string mcVersion, string loader,
        ContentType type, CancellationToken ct = default)
    {
        var pv = await GetBestFileAsync(hit.project_id, mcVersion, loader, type)
            ?? throw new Exception($"No compatible file for {hit.title} on {mcVersion} ({loader})");
        await DownloadFileAsync(pv, DirFor(inst, type), ct);
    }

    public static async Task<string> DownloadModpackAsync(string projectId, string title,
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        var versions = await GetVersionsAsync(projectId);
        bool IsPack(ModrinthFile f) =>
            f.filename.EndsWith(".mr", StringComparison.OrdinalIgnoreCase) ||
            f.filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase);
        var pv = versions
            .OrderByDescending(v => v.featured)
            .ThenByDescending(v => v.date_published)
            .FirstOrDefault(v => v.files?.Any(IsPack) == true)
            ?? throw new Exception($"“{title}” has no downloadable modpack file.");
        var file = pv.files.FirstOrDefault(f => f.primary && IsPack(f)) ?? pv.files.First(IsPack);
        var dir = Path.Combine(Path.GetTempPath(), "Lumen", "packs");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Sanitize(file.filename));
        status?.Report($"Downloading {file.filename}…");
        var d = new Downloader(2);
        await d.RunAsync(new[] { new DownloadItem(file.url, target, file.size) }, ct);
        return target;
    }

    public static async Task InstallModpackAsync(Instance inst, string mrFilePath,
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        using (var probe = System.IO.Compression.ZipFile.OpenRead(mrFilePath))
        {
            bool modrinth = probe.GetEntry("modrinth.index.json") != null;
            bool curse = !modrinth && probe.GetEntry("manifest.json") != null;
            if (!modrinth && !curse)
                throw new Exception("Not a modpack (no modrinth.index.json / manifest.json inside)");
            if (curse)
            {
                await CurseForgeService.ImportModpackAsync(inst, mrFilePath, status, ct);
                return;
            }
        }

        using var zip = System.IO.Compression.ZipFile.OpenRead(mrFilePath);
        var entry = zip.GetEntry("modrinth.index.json")!;
        var index = JsonSerializer.Deserialize<ModpackIndex>(
            new StreamReader(entry.Open()).ReadToEnd(), Json.Options)!;

        var cfg = inst.Config;
        if (index.gameVersions.TryGetValue("minecraft", out var mc) && !string.IsNullOrEmpty(mc))
            cfg.MCVersion = mc;
        var loaderKey = index.gameVersions.Keys.FirstOrDefault(k => k != "minecraft");
        if (loaderKey != null)
        {
            cfg.Loader = loaderKey switch
            {
                "fabric" => LoaderKind.Fabric,
                "forge" => LoaderKind.Forge,
                "neoforge" => LoaderKind.NeoForge,
                "quilt" => LoaderKind.Quilt,
                _ => cfg.Loader,
            };
            cfg.LoaderVersion = index.gameVersions[loaderKey];
        }

        var items = new List<DownloadItem>();
        foreach (var f in index.files)
        {
            var env = f.env;
            if (env?.client == "unsupported") continue;
            var rel = f.path.Replace('\\', '/');
            string target;
            if (rel.StartsWith("mods/")) target = Path.Combine(inst.ModsDir, rel[5..]);
            else if (rel.StartsWith("resourcepacks/")) target = Path.Combine(inst.ResourcePacksDir, rel[14..]);
            else if (rel.StartsWith("shaderpacks/")) target = Path.Combine(inst.ShaderPacksDir, rel[13..]);
            else continue;
            var url = f.downloads.FirstOrDefault();
            if (url != null) items.Add(new DownloadItem(url, target, f.fileSize));
        }
        status?.Report($"Downloading {items.Count} pack files…");
        var d = new Downloader(8);
        await d.RunAsync(items, ct);

        var overrides = zip.Entries.Where(e => e.FullName.StartsWith("overrides/") && !e.FullName.EndsWith("/"));
        foreach (var e in overrides)
        {
            var target = Path.Combine(inst.GameDir, e.FullName["overrides/".Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, true);
        }
        var clientOverrides = zip.Entries.Where(e => e.FullName.StartsWith("client-overrides/") && !e.FullName.EndsWith("/"));
        foreach (var e in clientOverrides)
        {
            var target = Path.Combine(inst.GameDir, e.FullName["client-overrides/".Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, true);
        }
    }

    public static string Sanitize(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

    public class ModpackIndex
    {
        public Dictionary<string, string> gameVersions { get; set; } = new();
        public List<ModpackFileEntry> files { get; set; } = new();
    }

    public class ModpackFileEntry
    {
        public string path { get; set; } = "";
        public List<string> downloads { get; set; } = new();
        public long fileSize { get; set; }
        public ModpackEnv? env { get; set; }
    }

    public class ModpackEnv
    {
        public string? world { get; set; }
        public string? client { get; set; }
        public string? server { get; set; }
    }
}
