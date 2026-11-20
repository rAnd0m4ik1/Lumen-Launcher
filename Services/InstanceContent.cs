using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CraftDeck.Services;

public record ContentItem(string Title, string Desc, string Meta, string Letter, ImageSource? Icon, string Path)
{
    public Visibility IconVis => Icon != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LetterVis => Icon != null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DescVis => string.IsNullOrEmpty(Desc) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MetaVis => string.IsNullOrEmpty(Meta) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SwitchVis =>
        Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
        Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
}

public static class InstanceContent
{
    public static List<ContentItem> Mods(string dir) => Files(dir, f =>
    {
        var meta = ReadModMeta(f);
        var name = string.IsNullOrWhiteSpace(meta.Name) ? Path.GetFileName(f) : meta.Name;
        return new ContentItem(name, IdentityLine(f, meta.Version, meta.Loader, meta.Mc), "",
            LetterOf(name), IconOf(f, ModIconPath(f)), f);
    });

    public static List<ContentItem> ResourcePacks(string dir) => Files(dir, f =>
    {
        var (_, icon) = ReadPackMeta(f);
        var name = Stem(f);
        return new ContentItem(name, IdentityLine(f, null, null, null), "", LetterOf(name), IconOf(f, icon), f);
    });

    public static List<ContentItem> Shaders(string dir) => Files(dir, f =>
    {
        var name = Stem(f);
        return new ContentItem(name, IdentityLine(f, null, null, null), "", LetterOf(name), null, f);
    });

    public static List<ContentItem> Worlds(string savesDir)
    {
        var items = new List<ContentItem>();
        if (!Directory.Exists(savesDir)) return items;
        foreach (var dir in Directory.EnumerateDirectories(savesDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var level = ReadLevelDat(Path.Combine(dir, "level.dat"));
            var name = level?[0] as string ?? Path.GetFileName(dir);
            var meta = level != null
                ? GameTypeName(level[1]) + (level[2] is long ms && ms > 0 ? $" · played {DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime:yyyy-MM-dd HH:mm}" : "")
                : DateOf(dir);
            items.Add(new ContentItem(name, "", meta, LetterOf(name),
                IconOfFile(Path.Combine(dir, "icon.png")), dir));
        }
        return items;
    }

    public static string LatestWorld(string savesDir)
    {
        var name = "";
        var best = -1L;
        if (!Directory.Exists(savesDir)) return name;
        foreach (var dir in Directory.EnumerateDirectories(savesDir))
        {
            var level = ReadLevelDat(Path.Combine(dir, "level.dat"));
            if (level?[0] is not string world) continue;
            var played = level[2] is long ms ? ms : 0;
            if (played < best) continue;
            best = played;
            name = world;
        }
        return name;
    }

    private static List<ContentItem> Files(string dir, Func<string, ContentItem> build)
    {
        var items = new List<ContentItem>();
        if (!Directory.Exists(dir)) return items;
        foreach (var f in Directory.EnumerateFileSystemEntries(dir)
                     .Where(f => !Path.GetFileName(f).StartsWith('.'))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try { items.Add(build(f)); }
            catch { items.Add(new ContentItem(Path.GetFileName(f), "", "", "?", null, f)); }
        }
        return items;
    }

    private record ModMeta(string Name, string? Version, string? Loader, string? Mc);

    private static ModMeta ReadModMeta(string file)
    {
        var fallback = new ModMeta(Path.GetFileName(file), null, null, null);
        if (!IsZip(file)) return ReadNameFromDir(file) ?? fallback;
        try
        {
            using var zip = ZipFile.OpenRead(file);
            ModMeta? Try(string entry, string? loader, Func<string, (string Name, string? Version, string? Mc)> parse)
            {
                var text = ReadEntry(zip, entry);
                if (text == null) return null;
                try
                {
                    var found = parse(text);
                    return string.IsNullOrWhiteSpace(found.Name)
                        ? null
                        : new ModMeta(found.Name.Trim(), found.Version?.Trim(), loader, found.Mc);
                }
                catch { return null; }
            }
            return Try("fabric.mod.json", "Fabric", FabricJson)
                   ?? Try("quilt.mod.json", "Quilt", QuiltJson)
                   ?? Try("META-INF/neoforge.mods.toml", "NeoForge", TomlMeta)
                   ?? Try("META-INF/mods.toml", "Forge", TomlMeta)
                   ?? Try("mcmod.info", null, McModInfo)
                   ?? Try("plugin.yml", null, YamlMeta)
                   ?? fallback;
        }
        catch { return fallback; }
    }

    private static ModMeta? ReadNameFromDir(string dir)
    {
        var json = Path.Combine(dir, "fabric.mod.json");
        if (!File.Exists(json)) return null;
        try
        {
            var found = FabricJson(File.ReadAllText(json));
            return string.IsNullOrWhiteSpace(found.Name)
                ? null
                : new ModMeta(found.Name.Trim(), found.Version?.Trim(), "Fabric", found.Mc);
        }
        catch { return null; }
    }

    private static string? Str(JsonElement el, params string[] keys) => keys
        .Select(k => el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
        .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

    private static (string Name, string? Version, string? Mc) FabricJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        string? mc = null;
        if (root.TryGetProperty("depends", out var depends) && depends.ValueKind == JsonValueKind.Object)
            mc = VersionIn(Str(depends, "minecraft", "gameVersion"));
        return (Str(root, "name", "modid") ?? "", Str(root, "version"), mc);
    }

    private static (string Name, string? Version, string? Mc) QuiltJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var meta = doc.RootElement.TryGetProperty("metadata", out var m) ? m : doc.RootElement;
        return (Str(meta, "name", "id") ?? "", Str(meta, "version"), null);
    }

    private static (string Name, string? Version, string? Mc) McModInfo(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
            ? doc.RootElement[0] : doc.RootElement;
        return (Str(root, "name", "modid") ?? "", Str(root, "version"), VersionIn(Str(root, "mcversion")));
    }

    private static (string Name, string? Version, string? Mc) TomlMeta(string text)
    {
        string Field(params string[] keys) => keys
            .Select(k => Regex.Match(text, $@"(?mi)^\s*{k}\s*=\s*""([^""]*)""").Groups[1].Value)
            .FirstOrDefault(s => s.Length > 0) ?? "";
        var name = Field("displayName");
        var version = Field("version");
        return (name, version.Length == 0 ? null : version, VersionIn(Field("mcVersion", "minecraft")));
    }

    private static (string Name, string? Version, string? Mc) YamlMeta(string text)
    {
        var name = Regex.Match(text, "(?m)^name:\\s*(.+)$").Groups[1].Value.Trim();
        var version = Regex.Match(text, "(?m)^version:\\s*(.+)$").Groups[1].Value.Trim();
        return (name, version.Length == 0 ? null : version, null);
    }

    private static string? VersionIn(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return null;
        var m = Regex.Match(range, @"\d+\.\d+(?:\.\d+)?");
        return m.Success ? m.Value : null;
    }

    private static List<string> NumericTokens(string stem) =>
        Regex.Matches(stem, @"\d+(?:\.\d+)+(?:[.\-]?(?:alpha|beta|rc)[.\-]?\d+)?")
            .Select(m => m.Value).ToList();

    private static bool LooksLikeMc(string v) =>
        Regex.IsMatch(v, @"^(1\.\d{1,2}(?:\.\d+)?|2\d(?:\.\d+){1,2})$");

    private static (string? Mc, string? Loader, string? Version) FromFileName(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var loader = Regex.Match(stem, "(?i)(?<![a-z])(neoforge|fabric|quilt|forge)(?![a-z])").Groups[1].Value;
        var mc = Regex.Match(stem, @"(?i)mc[ _.\-]?(\d+\.\d+(?:\.\d+)?)").Groups[1].Value;
        var rest = NumericTokens(stem).Where(t => t != mc).ToList();
        return (mc.Length == 0 ? null : mc,
            loader.Length == 0 ? null : char.ToUpperInvariant(loader[0]) + loader[1..],
            rest.Count == 0 ? null : rest[0]);
    }

    private static string? OnlyMc(string stem, string? version) =>
        NumericTokens(stem).FirstOrDefault(t =>
            LooksLikeMc(t) && (version == null || !version.Contains(t, StringComparison.Ordinal)));

    private static string IdentityLine(string file, string? version, string? loader, string? mc)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var (fileMc, fileLoader, fileVersion) = FromFileName(file);
        var v = string.IsNullOrWhiteSpace(version) ? fileVersion : version!.Trim();
        string? suffixMc = null;
        var cut = Regex.Match(v ?? "", @"^(.+?)\+mc?(\d+\.\d+(?:\.\d+)?)$");
        if (cut.Success)
        {
            v = cut.Groups[1].Value;
            suffixMc = cut.Groups[2].Value;
        }
        var m = string.IsNullOrWhiteSpace(mc) ? fileMc ?? suffixMc : mc!.Trim();
        m ??= OnlyMc(stem, v);
        var l = string.IsNullOrWhiteSpace(loader) ? fileLoader : loader;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(v)) parts.Add(v!);
        if (!string.IsNullOrWhiteSpace(m)) parts.Add($"MC {m}");
        if (!string.IsNullOrWhiteSpace(l)) parts.Add(l!);
        return string.Join(" · ", parts);
    }

    private static (string Desc, string? Icon) ReadPackMeta(string file)
    {
        if (IsZip(file))
        {
            try
            {
                using var zip = ZipFile.OpenRead(file);
                var meta = ReadEntry(zip, "pack.mcmeta");
                if (meta != null)
                {
                    using var doc = JsonDocument.Parse(meta);
                    var pack = doc.RootElement.TryGetProperty("pack", out var p) ? p : doc.RootElement;
                    return (DescOf(pack), zip.Entries.Any(e => e.FullName.Equals("pack.png", StringComparison.OrdinalIgnoreCase)) ? "pack.png" : null);
                }
            }
            catch { }
            return ("", null);
        }
        var json = Path.Combine(file, "pack.mcmeta");
        if (!File.Exists(json)) return ("", null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            var pack = doc.RootElement.TryGetProperty("pack", out var p) ? p : doc.RootElement;
            return (DescOf(pack), File.Exists(Path.Combine(file, "pack.png")) ? "pack.png" : null);
        }
        catch { return ("", null); }
    }

    private static string DescOf(JsonElement pack)
    {
        if (!pack.TryGetProperty("description", out var d)) return "";
        if (d.ValueKind == JsonValueKind.String) return StripComponents(d.GetString() ?? "");
        if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            return t.GetString() ?? "";
        return "";
    }

    private static string StripComponents(string desc)
    {
        if (desc.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(desc);
                if (doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    return t.GetString() ?? "";
            }
            catch { }
            return "";
        }
        return desc;
    }

    private static string? ModIconPath(string file)
    {
        if (!IsZip(file)) return null;
        try
        {
            using var zip = ZipFile.OpenRead(file);
            var meta = ReadEntry(zip, "fabric.mod.json") ?? ReadEntry(zip, "quilt.mod.json");
            if (meta == null) return null;
            using var doc = JsonDocument.Parse(meta);
            var root = doc.RootElement.TryGetProperty("metadata", out var m) ? m : doc.RootElement;
            if (!root.TryGetProperty("icon", out var icon)) return null;
            if (icon.ValueKind == JsonValueKind.String) return icon.GetString();
            if (icon.ValueKind == JsonValueKind.Object)
                return icon.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => n.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        return null;
    }

    private static ImageSource? IconOf(string file, string? entryName)
    {
        if (entryName == null) return null;
        if (!IsZip(file)) return IconOfFile(Path.Combine(file, entryName));
        try
        {
            using var zip = ZipFile.OpenRead(file);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').TrimStart('/')
                    .Equals(entryName.Replace('\\', '/').TrimStart('/'), StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return Decode(ms.ToArray());
        }
        catch { return null; }
    }

    private static ImageSource? IconOfFile(string path)
    {
        try { return File.Exists(path) ? Decode(File.ReadAllBytes(path)) : null; }
        catch { return null; }
    }

    private static ImageSource? Decode(byte[] png)
    {
        if (png.Length == 0) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(png);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    private static object?[]? ReadLevelDat(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var r = new BinaryReader(gz);
            if (r.ReadByte() != 10) return null;
            ReadString(r);
            var root = ReadCompound(r, 0);
            if (root == null) return null;
            // Minecraft 26.x keeps the fields at the root and leaves Data empty; older versions nest them in Data.
            Dictionary<string, object?> d;
            if (root.TryGetValue("Data", out var data) && data is Dictionary<string, object?> nested && nested.Count > 0)
                d = nested;
            else
                d = root;
            return new object?[]
            {
                d.TryGetValue("LevelName", out var n) ? n as string : null,
                d.TryGetValue("GameType", out var g) ? g : null,
                d.TryGetValue("LastPlayed", out var lp) ? lp : null,
            };
        }
        catch { return null; }
    }

    private static Dictionary<string, object?>? ReadCompound(BinaryReader r, int depth)
    {
        var d = new Dictionary<string, object?>();
        if (depth > 64) return d;
        while (true)
        {
            var type = r.ReadByte();
            if (type == 0) return d;
            if (type > 13) return null;
            var name = ReadString(r);
            d[name] = ReadPayload(r, type, depth);
        }
    }

    private static object? ReadPayload(BinaryReader r, byte type, int depth)
    {
        switch (type)
        {
            case 1: return r.ReadByte();
            case 2: return (short)ReadInt(r, 2);
            case 3: return (int)ReadInt(r, 4);
            case 4: return ReadInt(r, 8);
            case 5: return BitConverter.ToSingle(BitConverter.GetBytes((uint)ReadInt(r, 4)), 0);
            case 6: return BitConverter.Int64BitsToDouble(ReadInt(r, 8));
            case 7: r.ReadBytes((int)ReadInt(r, 4)); return null;
            case 8: return ReadString(r);
            case 9:
            {
                var elem = r.ReadByte();
                var count = (int)ReadInt(r, 4);
                for (var i = 0; i < count && elem != 0; i++) ReadPayload(r, elem, depth + 1);
                return null;
            }
            case 10: return ReadCompound(r, depth + 1);
            case 11: r.ReadBytes((int)ReadInt(r, 4) * 4); return null;
            case 12: r.ReadBytes((int)ReadInt(r, 4) * 8); return null;
            case 13: r.ReadBytes((int)ReadInt(r, 4) * 8); return null;
            default: return null;
        }
    }

    private static long ReadInt(BinaryReader r, int bytes)
    {
        var b = r.ReadBytes(bytes);
        if (b.Length < bytes) throw new EndOfStreamException();
        Array.Reverse(b);
        return bytes switch { 2 => BitConverter.ToInt16(b, 0), 4 => BitConverter.ToInt32(b, 0), _ => BitConverter.ToInt64(b, 0) };
    }

    private static string ReadString(BinaryReader r)
    {
        var len = (int)ReadInt(r, 2);
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    private static string? ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        using var s = entry.Open();
        using var sr = new StreamReader(s, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    private static bool IsZip(string f)
    {
        var e = Path.GetExtension(f);
        return (e.Equals(".jar", StringComparison.OrdinalIgnoreCase) || e.Equals(".zip", StringComparison.OrdinalIgnoreCase))
               && File.Exists(f);
    }

    private static string Stem(string f) => Path.GetFileNameWithoutExtension(f);

    private static string LetterOf(string name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : char.ToUpperInvariant(name.TrimStart()[0]).ToString();

    private static string DateOf(string f)
    {
        var t = File.Exists(f) ? File.GetLastWriteTime(f)
            : Directory.Exists(f) ? Directory.GetLastWriteTime(f) : DateTime.MinValue;
        return t == DateTime.MinValue ? "" : t.ToString("yyyy-MM-dd");
    }

    private static string GameTypeName(object? gameType) => gameType switch
    {
        0 => "survival",
        1 => "creative",
        2 => "adventure",
        3 => "spectator",
        _ => "world",
    };
}
