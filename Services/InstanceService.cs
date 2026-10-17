using System.IO;
using CraftDeck.Models;

namespace CraftDeck.Services;

public class InstanceService
{
    private readonly AppSettings _settings;

    public InstanceService(AppSettings settings) => _settings = settings;

    public List<Instance> ListAll()
    {
        Directory.CreateDirectory(_settings.InstancesDir);
        var result = new List<Instance>();
        foreach (var dir in Directory.EnumerateDirectories(_settings.InstancesDir))
        {
            try
            {
                var file = Path.Combine(dir, "instance.json");
                if (!File.Exists(file)) continue;
                var cfg = Json.Load<InstanceConfig>(File.ReadAllText(file));
                if (cfg == null) continue;
                var legacyGame = Path.Combine(dir, "game");
                if (Directory.Exists(legacyGame) && !Directory.Exists(Path.Combine(dir, ".minecraft")))
                    Directory.Move(legacyGame, Path.Combine(dir, ".minecraft"));
                result.Add(new Instance(cfg, dir));
            }
            catch (Exception ex)
            {
                App.Log($"instance scan {Path.GetFileName(dir)}: {ex.Message}");
            }
        }
        return result.OrderByDescending(i => i.Config.LastPlayed).ToList();
    }

    public Instance Create(InstanceConfig cfg)
    {
        cfg.Name = UniqueName(string.IsNullOrWhiteSpace(cfg.Name) ? cfg.MCVersion : cfg.Name.Trim());
        var safe = string.Join("_", cfg.Name.Split(Path.GetInvalidFileNameChars()));
        var dir = Path.Combine(_settings.InstancesDir, safe);
        int n = 2;
        while (Directory.Exists(dir)) dir = Path.Combine(_settings.InstancesDir, $"{safe}-{n++}");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".minecraft"));
        Directory.CreateDirectory(Path.Combine(dir, ".minecraft", "mods"));
        Directory.CreateDirectory(Path.Combine(dir, ".minecraft", "shaderpacks"));
        Directory.CreateDirectory(Path.Combine(dir, ".minecraft", "resourcepacks"));
        Directory.CreateDirectory(Path.Combine(dir, ".minecraft", "datapacks"));
        Save(cfg, dir);
        return new Instance(cfg, dir);
    }

    private string UniqueName(string name)
    {
        var taken = new HashSet<string>(ListAll().Select(i => i.Config.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        int n = 1;
        while (taken.Contains($"{name} ({n})")) n++;
        return $"{name} ({n})";
    }

    public void Save(Instance inst) => Save(inst.Config, inst.Dir);

    private static void Save(InstanceConfig cfg, string dir) =>
        File.WriteAllText(Path.Combine(dir, "instance.json"), Json.Save(cfg));

    public void Delete(Instance inst)
    {
        var dir = inst.Dir;
        if (Path.GetFullPath(dir).StartsWith(Path.GetFullPath(_settings.InstancesDir),
                StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    public Instance Duplicate(Instance src)
    {
        var cfg = Json.Load<InstanceConfig>(Json.Save(src.Config))!;
        cfg.Id = Guid.NewGuid().ToString("N")[..12];
        cfg.Name = src.Config.Name + " (copy)";
        var fresh = Create(cfg);
        if (File.Exists(src.VersionJsonPath))
            File.Copy(src.VersionJsonPath, fresh.VersionJsonPath, true);
        foreach (var (from, to) in new[]
                 {
                     (src.ModsDir, fresh.ModsDir),
                     (src.ShaderPacksDir, fresh.ShaderPacksDir),
                     (src.ResourcePacksDir, fresh.ResourcePacksDir),
                 })
            if (Directory.Exists(from))
                foreach (var f in Directory.EnumerateFiles(from))
                    File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        return fresh;
    }
}
