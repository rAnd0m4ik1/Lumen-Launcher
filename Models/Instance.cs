using System.IO;
using System.Text.Json.Serialization;

namespace CraftDeck.Models;

public enum LoaderKind { Vanilla, Fabric, Quilt, Forge, NeoForge }

public class InstanceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string MCVersion { get; set; } = "";
    public LoaderKind Loader { get; set; } = LoaderKind.Vanilla;
    public string LoaderVersion { get; set; } = "";
    public string? ModrinthPackId { get; set; }
    public string? ModrinthPackVersion { get; set; }
    public string? JavaPath { get; set; }
    public int? MaxMemoryMb { get; set; }
    public string ExtraJvmArgs { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime LastPlayed { get; set; }
    public long PlaytimeSeconds { get; set; }
}

public class Instance
{
    public InstanceConfig Config { get; }
    public string Dir { get; }

    public Instance(InstanceConfig config, string dir)
    {
        Config = config;
        Dir = dir;
    }

    [JsonIgnore]
    public string GameDir => Path.Combine(Dir, ".minecraft");
    [JsonIgnore]
    public string ModsDir => Path.Combine(GameDir, "mods");
    [JsonIgnore]
    public string ShaderPacksDir => Path.Combine(GameDir, "shaderpacks");
    [JsonIgnore]
    public string ResourcePacksDir => Path.Combine(GameDir, "resourcepacks");
    [JsonIgnore]
    public string DataPacksDir => Path.Combine(GameDir, "datapacks");
    [JsonIgnore]
    public string ConfigDir => Path.Combine(GameDir, "config");
    [JsonIgnore]
    public string NativesDir => Path.Combine(Dir, "natives");
    [JsonIgnore]
    public string VersionJsonPath => Path.Combine(Dir, "version.json");
    [JsonIgnore]
    public string ExtraLibsDir => Path.Combine(Dir, "libraries");
    [JsonIgnore]
    public bool Installed => File.Exists(VersionJsonPath);

    public string DisplayName => Config.Name;
}
