using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using CraftDeck.Models;
using CraftDeck.Models.CurseForge;
using CraftDeck.Models.Modrinth;

namespace CraftDeck.Services;

public enum ContentType { Mod, Shader, ResourcePack, DataPack, Modpack }

public static class ModrinthService
{
    private const string Base = "https://api.modrinth.com/v2";

    public static async Task<(List<Hit> Hits, int Total)> SearchAsync(string query, ContentType type,
        string? gameVersion = null, string? loader = null, int limit = 20, int offset = 0,
        string? shaderFormat = null)
    {
        var facets = new List<List<string>> { new() { "project_type:" + TypeSlug(type) } };
        if (!string.IsNullOrEmpty(gameVersion))
            facets.Add(McVersions.Line(gameVersion).Select(v => "versions:" + v).ToList());
        if (!string.IsNullOrEmpty(loader) && loader is not ("Vanilla" or "vanilla") && type == ContentType.Mod)
            facets.Add(new List<string> { "categories:" + loader.ToLower() });
        if (type == ContentType.Shader && !string.IsNullOrEmpty(shaderFormat))
            facets.Add(new List<string> { "categories:" + shaderFormat });
        var url = $"{Base}/search?query={Uri.EscapeDataString(query)}&limit={limit}&offset={offset}" +
                  $"&index={(string.IsNullOrEmpty(query) ? "downloads" : "relevance")}" +
                  $"&facets={WebUtility.UrlEncode(JsonSerializer.Serialize(facets))}";
        var resp = await Http.GetJsonAsync<SearchResponse>(url);
        return (resp.hits ?? new(), resp.total_hits);
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

    public static async Task<ProjectVersion?> GetVersionAsync(string projectId, string versionId) =>
        (await GetVersionsAsync(projectId)).FirstOrDefault(v => v.id == versionId);

    public static async Task<ProjectVersion?> GetBestFileAsync(string projectId, string mcVersion,
        string loader, ContentType type, string? shaderFormat = null)
    {
        var versions = await GetVersionsAsync(projectId);
        var fits = versions
            .Where(v => McVersions.Matches(v.game_versions, mcVersion))
            .Where(v => type is ContentType.Shader or ContentType.ResourcePack or ContentType.DataPack
                        || v.loaders?.Contains(loader.ToLower()) == true
                        || (loader == "NeoForge" && v.loaders?.Contains("neoforge") == true))
            .Where(v => v.files?.Count > 0)
            .ToList();
        return PreferFormat(fits, shaderFormat)
            .OrderByDescending(v => v.featured)
            .ThenByDescending(v => v.date_published)
            .FirstOrDefault();
    }

    public static List<ProjectVersion> PreferFormat(List<ProjectVersion> versions, string? shaderFormat)
    {
        if (shaderFormat == null) return versions;
        var shaped = versions.Where(v => v.loaders?.Contains(shaderFormat) == true).ToList();
        return shaped.Count > 0 ? shaped : versions;
    }

    public static async Task<string> DownloadFileAsync(ProjectVersion pv, string dir,
        string? shaderFormat = null, CancellationToken ct = default)
    {
        var file = PickFile(pv, shaderFormat);
        var target = Path.Combine(dir, Sanitize(file.filename));
        Directory.CreateDirectory(dir);
        var d = new Downloader(2);
        await d.RunAsync(new[] { new DownloadItem(file.url, target, file.size) }, ct);
        return target;
    }

    private static ModrinthFile PickFile(ProjectVersion pv, string? shaderFormat)
    {
        var files = pv.files ?? new List<ModrinthFile>();
        if (shaderFormat != null)
        {
            var named = files.FirstOrDefault(f =>
                f.filename.Contains(shaderFormat, StringComparison.OrdinalIgnoreCase));
            if (named != null) return named;
        }
        return files.FirstOrDefault(f => f.primary) ?? files[0];
    }

    public static async Task InstallAsync(Instance inst, Hit hit, string mcVersion, string loader,
        ContentType type, string? pinnedVersion = null, CancellationToken ct = default)
    {
        var format = type == ContentType.Shader ? ShaderFormat.For(inst) : null;
        var pv = pinnedVersion != null
            ? await GetVersionAsync(hit.project_id, pinnedVersion)
            : await GetBestFileAsync(hit.project_id, mcVersion, loader, type, format);
        if (pv == null)
            throw new Exception(pinnedVersion != null
                ? $"“{hit.title}” no longer has that file."
                : $"No compatible file for {hit.title} on {mcVersion} ({loader})");
        var dir = DirFor(inst, type);
        var target = await DownloadFileAsync(pv, dir, format, ct);
        ContentMatch.ReplaceSame(dir, hit.title, hit.slug, target);
    }

    public static async Task<string> DownloadModpackAsync(string projectId, string title,
        IProgress<string>? status = null, string? versionId = null, CancellationToken ct = default)
    {
        var versions = await GetVersionsAsync(projectId);
        bool IsPack(ModrinthFile f) =>
            f.filename.EndsWith(".mr", StringComparison.OrdinalIgnoreCase) ||
            f.filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase);
        var pv = versions
            .Where(v => versionId == null || v.id == versionId)
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

    public record PackInfo(string? McVersion, string? Loader, string? LoaderVersion);

    public static PackInfo PeekPack(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        if (zip.GetEntry("modrinth.index.json") is { } mr)
        {
            var index = JsonSerializer.Deserialize<ModpackIndex>(
                new StreamReader(mr.Open()).ReadToEnd(), Json.Options)!;
            var deps = index.dependencies;
            var loaderKey = deps.Keys.FirstOrDefault(k => !k.Equals("minecraft", StringComparison.OrdinalIgnoreCase));
            var loader = loaderKey == null ? null
                : loaderKey.EndsWith("-loader", StringComparison.OrdinalIgnoreCase)
                    ? loaderKey[..^"-loader".Length] : loaderKey;
            string? loaderVer = null;
            if (loaderKey != null && deps.TryGetValue(loaderKey, out var lv) && lv.Length > 0) loaderVer = lv;
            string? mcVer = null;
            if (deps.TryGetValue("minecraft", out var mc) && mc.Length > 0) mcVer = mc;
            return new PackInfo(mcVer, loader?.ToLowerInvariant(), loaderVer);
        }
        if (zip.GetEntry("manifest.json") is { } cf)
        {
            var manifest = JsonSerializer.Deserialize<CFManifest>(
                new StreamReader(cf.Open()).ReadToEnd(), Json.Options)!;
            var id = (manifest.minecraft?.modLoaders?.FirstOrDefault(l => l.primary)
                      ?? manifest.minecraft?.modLoaders?.FirstOrDefault())?.id;
            var dash = id?.LastIndexOf('-') ?? -1;
            return new PackInfo(
                manifest.minecraft?.version,
                dash > 0 ? id![..dash].ToLowerInvariant() : id?.ToLowerInvariant(),
                dash > 0 ? id![(dash + 1)..] : null);
        }
        return new PackInfo(null, null, null);
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
        public Dictionary<string, string> dependencies { get; set; } = new();
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
