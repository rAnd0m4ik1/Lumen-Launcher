#nullable disable
using System.Text.Json.Serialization;

namespace CraftDeck.Models.Minecraft;

public class VersionManifest
{
    public Latest latest { get; set; }
    public List<ManifestVersion> versions { get; set; }
}

public class Latest
{
    public string release { get; set; }
    public string snapshot { get; set; }
}

public class ManifestVersion
{
    public string id { get; set; }
    public string type { get; set; }
    public string url { get; set; }
    public string releaseTime { get; set; }
}

public class VersionDetails
{
    public string id { get; set; }
    public string type { get; set; }
    public string mainClass { get; set; }
    public string assets { get; set; }
    public string minecraftArguments { get; set; }
    public System.Text.Json.JsonElement arguments { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("assetIndex")]
    public AssetsIndex assetsIndex { get; set; }
    public DownloadInfo downloads { get; set; }
    public List<Library> libraries { get; set; }
    public JavaVersionInfo javaVersion { get; set; }
    public int minimumLauncherVersion { get; set; }
    public string releaseTime { get; set; }
    public string time { get; set; }
    public string inheritanceType { get; set; }
    public string jar { get; set; }
    public List<string> jarMods { get; set; }
}

public class AssetsIndex
{
    public string id { get; set; }
    public string url { get; set; }
    public int totalSize { get; set; }
}

public class DownloadInfo
{
    public DownloadEntry client { get; set; }
    public DownloadEntry server { get; set; }
    public DownloadEntry client_mappings { get; set; }
}

public class DownloadEntry
{
    public string url { get; set; }
    public string sha1 { get; set; }
    public long size { get; set; }
}

public class LibraryDownloads
{
    public DownloadEntry artifact { get; set; }
    public Dictionary<string, DownloadEntry> classifiers { get; set; }
}

public class Library
{
    public string name { get; set; }
    public LibraryDownloads downloads { get; set; }
    public LibraryNativeMap natives { get; set; }
    public List<Rule> rules { get; set; }
    public string url { get; set; }

    [JsonIgnore]
    public bool Applies => rules == null || rules.Any(r => r.Allows());

    public string MavenPath()
    {
        var p = name.Split(':');
        var group = p[0].Replace('.', '/');
        var art = p[1];
        var ver = p[2];
        var cls = p.Length >= 4 ? "-" + p[3] : "";
        return $"{group}/{art}/{ver}/{art}-{ver}{cls}.jar";
    }

    public string MavenPathClassifier(string classifier)
    {
        var p = name.Split(':');
        var group = p[0].Replace('.', '/');
        var art = p[1];
        var ver = p[2];
        var cls = p.Length >= 4 && p[3] == classifier ? "" : "-" + classifier;
        return $"{group}/{art}/{ver}/{art}-{ver}{cls}.jar";
    }
}

public class LibraryNativeMap
{
    public string windows { get; set; }
    public string linux { get; set; }
    public string osx { get; set; }
}

public class JavaVersionInfo
{
    public string name { get; set; }
    public int majorVersion { get; set; }
}

public class Rule
{
    public string action { get; set; }
    public RuleOs os { get; set; }
    public Dictionary<string, bool> features { get; set; }

    public bool Allows(Dictionary<string, bool> features = null)
    {
        if (os != null)
        {
            if (os.name != null && os.name != "windows") return action == "disallow";
            if (os.version != null && !System.Text.RegularExpressions.Regex.IsMatch(
                    Environment.OSVersion.VersionString, os.version)) return action == "disallow";
        }
        if (features != null && this.features != null)
        {
            foreach (var kv in this.features)
            {
                if (!features.TryGetValue(kv.Key, out var v) || v != kv.Value)
                    return action == "disallow";
            }
        }
        return action != "disallow";
    }
}

public class RuleOs
{
    public string name { get; set; }
    public string version { get; set; }
    public string arch { get; set; }
}
