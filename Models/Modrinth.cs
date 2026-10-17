#nullable disable
using System.Text.Json.Serialization;

namespace CraftDeck.Models.Modrinth;

public class SearchResponse
{
    public List<Hit> hits { get; set; }
    public int total_hits { get; set; }
    public int limit { get; set; }
    public int offset { get; set; }
}

public class Hit
{
    public string project_id { get; set; }
    public string slug { get; set; }
    public string title { get; set; }
    public string author { get; set; }
    public string description { get; set; }
    public string icon_url { get; set; }
    public List<string> categories { get; set; }
    public List<string> display_categories { get; set; }
    public string project_type { get; set; }
    public long downloads { get; set; }
    public float follows { get; set; }
    public string published { get; set; }
    public string updated { get; set; }
    public string date_modified { get; set; }
    public List<string> versions { get; set; }
    public string client_side { get; set; }
    public string server_side { get; set; }
    public System.Text.Json.JsonElement license { get; set; }
}

public class Project
{
    public string id { get; set; }
    public string slug { get; set; }
    public string title { get; set; }
    public string description { get; set; }
    public string body { get; set; }
    public List<string> categories { get; set; }
    public string project_type { get; set; }
    public long downloads { get; set; }
    public List<string> versions { get; set; }
}

public class ProjectVersion
{
    public string id { get; set; }
    public string project_id { get; set; }
    public string name { get; set; }
    public string version_number { get; set; }
    public List<string> loaders { get; set; }
    public List<string> game_versions { get; set; }
    public string version_type { get; set; }
    public List<ModrinthFile> files { get; set; }
    public System.Text.Json.JsonElement dependencies { get; set; }
    public DateTime date_published { get; set; }
    public long downloads { get; set; }
    public bool featured { get; set; }
}

public class ModrinthFile
{
    public string hashes_sha1 { get; set; }
    public string hashes_sha512 { get; set; }
    public string url { get; set; }
    public string filename { get; set; }
    public long size { get; set; }
    public bool primary { get; set; }

    [JsonPropertyName("hashes")]
    public Hashes HashesObj { set { if (value != null) { hashes_sha1 = value.sha1; hashes_sha512 = value.sha512; } } }

    [JsonPropertyName("env")]
    public object Env { get; set; }

    public class Hashes
    {
        public string sha1 { get; set; }
        public string sha512 { get; set; }
    }
}

public class ModpackFile
{
    public string id { get; set; }
    public string name { get; set; }
    public string version_number { get; set; }
    public List<ModrinthFile> files { get; set; }
}
