using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CraftDeck.Models;
using CraftDeck.Models.Minecraft;

namespace CraftDeck.Services;

public record LoaderVersion(string Loader, string GameVersion, string Intermediate);

public static class LoaderService
{

    public static async Task<List<string>> GetFabricLoadersAsync()
    {
        var list = await Http.GetJsonAsync<List<FabricLoaderEntry>>("https://meta.fabricmc.net/v2/versions/loader");
        return list.Select(e => e.version).ToList();
    }

    public static async Task<List<string>> GetQuiltLoadersAsync()
    {
        var list = await Http.GetJsonAsync<List<FabricLoaderEntry>>("https://meta.quiltmc.org/v3/versions/loader");
        return list.Select(e => e.version).ToList();
    }

    public static async Task<List<string>> GetForgeVersionsAsync(string mc)
    {
        var xml = await Http.GetStringAsync("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml");
        return NewestFirst(ParseMavenVersions(xml)
            .Where(v => v.StartsWith(mc + "-"))
            .Select(v => v[(mc.Length + 1)..])
            .Where(v => !v.Contains('-')));
    }

    public static async Task<List<string>> GetNeoForgeVersionsAsync(string mc)
    {
        var xml = await Http.GetStringAsync("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml");
        var line = NeoForgeLine(mc);
        var all = ParseMavenVersions(xml).Where(v => v.StartsWith(line + ".")).ToList();
        var stable = all.Where(v => !v.Contains('-')).ToList();
        return NewestFirst(stable.Count > 0 ? stable : all);
    }

    private static string NeoForgeLine(string mc)
    {
        var parts = mc.Split('.');
        return parts.Length switch
        {
            1 => parts[0] + ".0",
            2 => parts[0] == "1" ? $"{parts[1]}.0" : $"{parts[0]}.{parts[1]}",
            _ => parts[0] == "1" ? $"{parts[1]}.{parts[2]}" : $"{parts[0]}.{parts[1]}",
        };
    }

    private static List<string> NewestFirst(IEnumerable<string> versions) =>
        versions.OrderByDescending(v =>
        {
            var core = v.Split('-')[0].Split('+')[0];
            return Version.TryParse(core, out var p) ? p : new Version(0, 0);
        }).ToList();

    private static List<string> ParseMavenVersions(string xml)
    {
        var doc = XDocument.Parse(xml);
        return doc.Descendants("versioning").Elements("versions").Elements("version")
            .Select(e => e.Value)
            .Where(v => !v.Contains("-universal") && !v.Contains("-installer") && !v.Contains("-changelog") && !v.Contains("-src"))
            .ToList();
    }

    public static async Task<VersionDetails> InstallAsync(Instance inst, VersionDetails vanilla,
        AppSettings settings, IProgress<string>? status = null, CancellationToken ct = default)
    {
        var cfg = inst.Config;
        VersionDetails loaderJson = cfg.Loader switch
        {
            LoaderKind.Fabric => await Http.GetJsonAsync<VersionDetails>(
                $"https://meta.fabricmc.net/v2/versions/loader/{cfg.MCVersion}/{cfg.LoaderVersion}/profile/json"),
            LoaderKind.Quilt => await Http.GetJsonAsync<VersionDetails>(
                $"https://meta.quiltmc.org/v3/versions/loader/{cfg.MCVersion}/{cfg.LoaderVersion}/profile/json"),
            LoaderKind.Forge => await FromInstallerAsync(
                $"https://maven.minecraftforge.net/net/minecraftforge/forge/{cfg.MCVersion}-{cfg.LoaderVersion}/forge-{cfg.MCVersion}-{cfg.LoaderVersion}-installer.jar",
                Path.Combine(inst.Dir, "forge-installer.jar"), status, ct),
            LoaderKind.NeoForge => await FromInstallerAsync(
                $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{cfg.LoaderVersion}/neoforge-{cfg.LoaderVersion}-installer.jar",
                Path.Combine(inst.Dir, "neoforge-installer.jar"), status, ct),
            _ => vanilla,
        };

        var merged = Merge(vanilla, loaderJson);
        merged.id = cfg.MCVersion;
        Directory.CreateDirectory(inst.Dir);
        File.WriteAllText(inst.VersionJsonPath, Json.Save(merged));
        return merged;
    }

    private static async Task<VersionDetails> FromInstallerAsync(string installerUrl, string installerPath,
        IProgress<string>? status, CancellationToken ct)
    {
        if (!File.Exists(installerPath))
        {
            status?.Report("Downloading installer…");
            var d = new Downloader(2);
            await d.RunAsync(new[] { new DownloadItem(installerUrl, installerPath) }, ct);
        }
        status?.Report("Extracting loader profile…");
        using var zip = ZipFile.OpenRead(installerPath);
        var entry = zip.GetEntry("version.json")
            ?? throw new Exception("Installer has no version.json — unsupported loader version");
        using var r = new StreamReader(entry.Open());
        var json = await r.ReadToEndAsync(ct);
        return Json.Load<VersionDetails>(json)
            ?? throw new Exception("Bad version.json in installer");
    }

    public static VersionDetails Merge(VersionDetails vanilla, VersionDetails loader)
    {
        var m = Json.Load<VersionDetails>(Json.Save(vanilla))!;
        if (!string.IsNullOrEmpty(loader.mainClass)) m.mainClass = loader.mainClass;
        m.libraries ??= new List<Library>();
        var added = new HashSet<string>(m.libraries.Select(l => l.name));
        foreach (var lib in loader.libraries ?? new List<Library>())
            if (added.Add(lib.name)) m.libraries.Insert(0, lib);

        if (loader.arguments.ValueKind == JsonValueKind.Object)
        {
            var vanillaArgs = m.arguments;
            var result = new JsonObject();
            var props = new List<JsonProperty>();
            if (vanillaArgs.ValueKind == JsonValueKind.Object) props.AddRange(vanillaArgs.EnumerateObject());
            props.AddRange(loader.arguments.EnumerateObject());
            foreach (var prop in props)
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    if (result.TryGetPropertyValue(prop.Name, out var existing) && existing is JsonArray exArr)
                        foreach (var el in prop.Value.EnumerateArray()) exArr.Add(CloneNode(el));
                    else result[prop.Name] = CloneNode(prop.Value);
                }
                else result[prop.Name] = CloneNode(prop.Value);
            }
            m.arguments = JsonSerializer.Deserialize<JsonElement>(result.ToJsonString());
        }
        else if (!string.IsNullOrEmpty(loader.minecraftArguments))
        {
            m.minecraftArguments = (m.minecraftArguments ?? "") + " " + loader.minecraftArguments;
        }
        return m;
    }

    private static JsonNode CloneNode(JsonElement el) => JsonNode.Parse(el.GetRawText())!;

    private class FabricLoaderEntry
    {
        public string separator { get; set; } = "";
        public int build { get; set; }
        public string version { get; set; } = "";
        public bool stable { get; set; }
    }
}
