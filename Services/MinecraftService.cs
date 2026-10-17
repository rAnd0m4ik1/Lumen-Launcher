using System.IO;
using System.Text.Json;
using CraftDeck.Models;
using CraftDeck.Models.Minecraft;

namespace CraftDeck.Services;

public class MinecraftService
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private readonly AppSettings _settings;

    public MinecraftService(AppSettings settings) => _settings = settings;

    private VersionManifest? _manifest;

    public async Task<VersionManifest> GetManifestAsync()
    {
        if (_manifest != null) return _manifest;
        var cache = Path.Combine(_settings.MetaDir, "version-manifest.json");
        Directory.CreateDirectory(_settings.MetaDir);
        try
        {
            if (!File.Exists(cache) || DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) > TimeSpan.FromDays(1))
                WriteCache(cache, await Http.GetStringAsync(ManifestUrl));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        _manifest = ReadCache<VersionManifest>(cache)
            ?? throw new Exception("Mojang's version list is unreachable and no copy is cached yet");
        _manifest.versions ??= new();
        return _manifest;
    }

    public async Task<List<string>> GetVersionIdsAsync(bool includeSnapshots = false)
    {
        var m = await GetManifestAsync();
        return m.versions
            .Where(v => includeSnapshots || v.type == "release")
            .Select(v => v.id).ToList();
    }

    public async Task<VersionDetails> GetVersionDetailsAsync(string version)
    {
        Directory.CreateDirectory(_settings.MetaDir);
        var cache = Path.Combine(_settings.MetaDir, $"version-{version}.json");
        var cached = ReadCache<VersionDetails>(cache);
        if (cached?.mainClass != null && cached.libraries != null) return cached;

        var m = await GetManifestAsync();
        var entry = m.versions.FirstOrDefault(v => v.id == version)
            ?? throw new Exception($"Version {version} not found in manifest");
        var raw = await Http.GetStringAsync(entry.url);
        WriteCache(cache, raw);
        return Json.Load<VersionDetails>(raw)!;
    }

    private static T? ReadCache<T>(string path) where T : class
    {
        try { return File.Exists(path) ? Json.Load<T>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is JsonException || ex is IOException) { return null; }
    }

    private static void WriteCache(string path, string text)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, true);
        }
        catch (IOException)
        {
            try { File.Delete(tmp); } catch { }
            File.WriteAllText(path, text);
        }
    }

    public string ClientJarPath(string version) =>
        Path.Combine(_settings.VersionsDir, version, version + ".jar");

    public async Task PrepareVanillaAsync(string version, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(_settings.VersionsDir, version));
        Directory.CreateDirectory(_settings.LibrariesDir);
        var details = await GetVersionDetailsAsync(version);

        var items = new List<DownloadItem>();
        if (details.downloads?.client != null)
            items.Add(new DownloadItem(details.downloads.client.url, ClientJarPath(version),
                details.downloads.client.size, null, details.downloads.client.sha1));

        foreach (var lib in details.libraries.Where(l => l.Applies))
            items.AddRange(LibraryDownloads(lib, _settings.LibrariesDir));

        long total = Math.Max(1, items.Count);
        int done = 0;
        var d = new Downloader(_settings.DownloadThreads);
        d.ProgressChanged += _ => progress.Report(Math.Min(0.99, Interlocked.Increment(ref done) / (double)total));
        await d.RunAsync(items, ct);

        progress.Report(1);
    }

    public static IEnumerable<DownloadItem> LibraryDownloads(Library lib, string libsDir)
    {
        var parts = lib.name?.Split(':') ?? Array.Empty<string>();
        var classifier = parts.Length >= 4 ? parts[3] : null;

        if (lib.downloads?.artifact != null)
        {
            var rel = lib.MavenPath();
            yield return new DownloadItem(lib.downloads.artifact.url,
                Path.Combine(libsDir, rel.Replace('/', Path.DirectorySeparatorChar)),
                lib.downloads.artifact.size, null, lib.downloads.artifact.sha1);
        }

        if (lib.downloads?.classifiers != null)
        {
            string? key = null;
            if (lib.natives?.windows != null)
                key = lib.natives.windows.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
            else if (classifier?.StartsWith("natives-windows") == true)
                key = classifier;
            if (key != null && lib.downloads.classifiers.TryGetValue(key, out var nat))
            {
                var rel = lib.MavenPathClassifier(key);
                yield return new DownloadItem(nat.url,
                    Path.Combine(libsDir, rel.Replace('/', Path.DirectorySeparatorChar)), nat.size, null, nat.sha1);
            }
        }

        if (lib.downloads == null)
        {
            var baseUrl = lib.url != null
                ? (lib.url.EndsWith("/") ? lib.url : lib.url + "/")
                : "https://libraries.minecraft.net/";
            var rel = lib.MavenPath();
            yield return new DownloadItem(baseUrl + rel,
                Path.Combine(libsDir, rel.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    public async Task PrepareAssetsAsync(VersionDetails details, IProgress<(long done, long total)>? byteProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(_settings.AssetsDir);
        Directory.CreateDirectory(_settings.MetaDir);
        var idxUrl = details.assetsIndex?.url
            ?? throw new Exception("No assets index url");
        var cache = Path.Combine(_settings.MetaDir, $"assets-{details.assets ?? "legacy"}.json");
        var index = ReadCache<AssetsIndexRoot>(cache);
        if (index?.objects == null)
        {
            WriteCache(cache, await Http.GetStringAsync(idxUrl));
            index = ReadCache<AssetsIndexRoot>(cache) ?? new AssetsIndexRoot();
        }
        var idxDir = Path.Combine(_settings.AssetsDir, "indexes");
        Directory.CreateDirectory(idxDir);
        var gameIdx = Path.Combine(idxDir, (details.assets ?? "legacy") + ".json");
        if (!File.Exists(gameIdx) || new FileInfo(gameIdx).Length != new FileInfo(cache).Length)
        {
            try { File.Copy(cache, gameIdx, true); } catch (IOException) { }
        }
        var items = new List<DownloadItem>();
        foreach (var (name, obj) in index.objects)
        {
            var path = Path.Combine(_settings.AssetsDir, "objects", obj.hash[..2], obj.hash);
            if (!File.Exists(path) || new FileInfo(path).Length != obj.size)
                items.Add(new DownloadItem(
                    $"https://resources.download.minecraft.net/{obj.hash[..2]}/{obj.hash}", path, obj.size,
                    null, obj.hash));
        }
        var d = new Downloader(_settings.DownloadThreads);
        if (byteProgress != null)
            d.ProgressChanged += p => byteProgress.Report((p.BytesDone, Math.Max(1, p.BytesTotal)));
        await d.RunAsync(items, ct);
    }
}

public class AssetsIndexRoot
{
    public Dictionary<string, AssetObject> objects { get; set; } = new();
    public int total_size { get; set; }
}

public class AssetObject
{
    public string hash { get; set; } = "";
    public long size { get; set; }
}
