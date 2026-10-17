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
}

public static class InstanceContent
{
    public static List<ContentItem> Mods(string dir) => Files(dir, f =>
    {
        var (name, desc) = ReadModMeta(f);
        return new ContentItem(name, desc, $"{SizeOf(f)} · {DateOf(f)}", LetterOf(name), IconOf(f, ModIconPath(f)), f);
    });

    public static List<ContentItem> ResourcePacks(string dir) => Files(dir, f =>
    {
        var (desc, icon) = ReadPackMeta(f);
        return new ContentItem(Stem(f), desc, $"{SizeOf(f)} · {DateOf(f)}", LetterOf(Stem(f)), IconOf(f, icon), f);
    });

    public static List<ContentItem> Shaders(string dir) => Files(dir, f =>
    {
        var name = Stem(f);
        return new ContentItem(name, "", $"{SizeOf(f)} · {DateOf(f)}", LetterOf(name), null, f);
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

    private static (string Name, string Desc) ReadModMeta(string file)
    {
        var fallback = Path.GetFileName(file);
        if (!IsZip(file)) return (fallback, "");
        try
        {
            using var zip = ZipFile.OpenRead(file);
            (string Name, string Desc)? Try(string entry, Func<string, (string Name, string Desc)?> parse)
            {
                var text = ReadEntry(zip, entry);
                if (text == null) return null;
                try
                {
                    var found = parse(text);
                    return found is { } f && !string.IsNullOrWhiteSpace(f.Name)
                        ? (f.Name.Trim(), f.Desc?.Trim() ?? "")
                        : null;
                }
                catch { return null; }
            }
            return Try("fabric.mod.json", JsonNameDesc)
                   ?? Try("quilt.mod.json", q => JsonNameDesc(JsonDocument.Parse(q).RootElement
                        .TryGetProperty("metadata", out var m) ? m.ToString() : q))
                   ?? Try("META-INF/mods.toml", TomlNameDesc)
                   ?? Try("META-INF/neoforge.mods.toml", TomlNameDesc)
                   ?? Try("mcmod.info", JsonNameDesc)
                   ?? Try("plugin.yml", YamlNameDesc)
                   ?? (fallback, "");
        }
        catch { return (fallback, ""); }
    }

    private static (string Name, string Desc)? JsonNameDesc(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
            ? doc.RootElement[0] : doc.RootElement;
        string? Get(params string[] keys) => keys
            .Select(k => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        var name = Get("name", "modid", "id", "displayName");
        return name == null ? null : (name, Get("description", "summary") ?? "");
    }

    private static (string Name, string Desc)? TomlNameDesc(string text)
    {
        var name = Regex.Match(text, "(?m)^\\s*displayName\\s*=\\s*\"([^\"]*)\"").Groups[1].Value;
        var desc = Regex.Match(text, "(?m)^\\s*description\\s*=\\s*(?:\"\"\"\\s*)?\"?([^\"\r\n]*)").Groups[1].Value;
        return string.IsNullOrWhiteSpace(name) ? null : (name, desc.Trim());
    }

    private static (string Name, string Desc)? YamlNameDesc(string text)
    {
        var name = Regex.Match(text, "(?m)^name:\\s*(.+)$").Groups[1].Value.Trim();
        var desc = Regex.Match(text, "(?m)^description:\\s*(.+)$").Groups[1].Value.Trim();
        return name.Length == 0 ? null : (name, desc);
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
            if (root == null || !root.TryGetValue("Data", out var data) || data is not Dictionary<string, object?> d)
                return null;
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
            case 11: r.ReadBytes((int)ReadInt(r, 4)); return null;
            case 12: r.ReadBytes((int)ReadInt(r, 4) * 4); return null;
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

    private static string SizeOf(string f)
    {
        long bytes;
        if (File.Exists(f)) bytes = new FileInfo(f).Length;
        else if (Directory.Exists(f))
        {
            try
            {
                bytes = new DirectoryInfo(f).EnumerateFiles("*", SearchOption.AllDirectories).Sum(e => e.Length);
            }
            catch { return ""; }
        }
        else return "";
        return bytes switch
        {
            >= 1048576 => $"{bytes / 1048576.0:0.#} MB",
            >= 1024 => $"{bytes / 1024} KB",
            _ => $"{bytes} B",
        };
    }

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
