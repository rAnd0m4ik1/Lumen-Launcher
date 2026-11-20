using System.IO;

namespace CraftDeck.Services;

public static class ContentMatch
{
    private static readonly HashSet<string> Tags = new(StringComparer.Ordinal)
    {
        "fabric", "quilt", "forge", "neoforge", "optifine", "liteloader", "minecraft", "mc", "mod", "mods",
        "shader", "shaders", "resource", "resources", "resourcepack", "resourcepacks", "pack", "packs",
        "data", "datapack", "datapacks", "texture", "textures", "asset", "assets", "model", "models",
        "release", "snapshot", "beta", "alpha", "final", "latest", "stable", "client", "server",
        "for", "and", "the", "of", "x", "jar", "zip",
    };

    public record Entry(string Path, HashSet<string> Words);

    public static List<Entry> ScanDir(string dir)
    {
        if (!Directory.Exists(dir)) return new();
        try
        {
            return Directory.EnumerateFiles(dir)
                .Select(f => new Entry(f, Words(Path.GetFileNameWithoutExtension(f))))
                .Where(e => e.Words.Count > 0)
                .ToList();
        }
        catch { return new(); }
    }

    public static bool Any(string? title, string? slug, List<Entry> installed) =>
        SameAs(title, slug, installed).Count > 0;

    public static List<string> SameAs(string? title, string? slug, List<Entry> installed)
    {
        var card = new List<HashSet<string>> { Words(title), Words(slug?.Replace('-', ' ')), Slug(slug) };
        card.RemoveAll(c => c.Count == 0);
        if (card.Count == 0) return new();
        return installed.Where(i => card.Any(c => Fits(c, i.Words) || Fits(i.Words, c)))
            .Select(i => i.Path).ToList();
    }

    public static void ReplaceSame(string dir, string? title, string? slug, string keep)
    {
        var full = Path.GetFullPath(keep);
        foreach (var old in SameAs(title, slug, ScanDir(dir)))
        {
            if (string.Equals(Path.GetFullPath(old), full, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(old); } catch { }
        }
    }

    private static HashSet<string> Slug(string? slug)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(slug)) return set;
        var raw = new string(slug.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (raw.Length > 0 && !Noise(raw)) set.Add(raw);
        return set;
    }

    private static bool Fits(HashSet<string> small, HashSet<string> big)
    {
        if (small.Count == 0 || !big.IsSupersetOf(small)) return false;
        foreach (var extra in big)
            if (!small.Contains(extra) && !Tags.Contains(extra)) return false;
        return true;
    }

    private static HashSet<string> Words(string? text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return set;
        var spaced = System.Text.RegularExpressions.Regex.Replace(text, "(?<=[a-z0-9])(?=[A-Z])", " ");
        foreach (var raw in spaced.Split(new[] { ' ', '-', '_', '+', '.', ',', '(', ')', '[', ']', '~', '\t' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var w = raw.ToLowerInvariant();
            if (Noise(w)) continue;
            set.Add(w);
        }
        return set;
    }

    private static bool Noise(string w) => w.Any(char.IsDigit);
}
