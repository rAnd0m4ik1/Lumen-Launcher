using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CraftDeck.Models;

namespace CraftDeck.Services;

public static class InstanceIcons
{
    public const string FileName = "icon.png";

    public static string? FileOf(Instance inst)
    {
        var icon = inst.Config.Icon;
        if (string.IsNullOrWhiteSpace(icon)) return null;
        if (Path.IsPathRooted(icon)) return File.Exists(icon) ? icon : null;
        var local = Path.Combine(inst.Dir, icon);
        return File.Exists(local) ? local : null;
    }

    public static ImageSource? Load(Instance inst) => LoadFile(FileOf(inst));

    public static ImageSource? LoadFile(string? path)
    {
        if (path == null) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    public static void Store(Instance inst, string sourceImage)
    {
        Directory.CreateDirectory(inst.Dir);
        File.Copy(sourceImage, Path.Combine(inst.Dir, FileName), true);
        inst.Config.Icon = FileName;
    }

    public static void Store(Instance inst, byte[] image)
    {
        if (image.Length == 0) return;
        Directory.CreateDirectory(inst.Dir);
        File.WriteAllBytes(Path.Combine(inst.Dir, FileName), image);
        inst.Config.Icon = FileName;
    }

    public static void Clear(Instance inst)
    {
        var icon = inst.Config.Icon;
        if (!string.IsNullOrWhiteSpace(icon) && !Path.IsPathRooted(icon))
        {
            var local = Path.Combine(inst.Dir, icon);
            if (File.Exists(local)) File.Delete(local);
        }
        inst.Config.Icon = "";
    }

    public static string? CustomGlyph(Instance inst)
    {
        if (FileOf(inst) != null) return null;
        var icon = inst.Config.Icon;
        if (string.IsNullOrWhiteSpace(icon) || LooksLikeFile(icon)) return null;
        return icon;
    }

    private static bool LooksLikeFile(string s) =>
        Path.IsPathRooted(s) || s.Contains('\\') || s.Contains('/') ||
        s.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        s.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        s.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        s.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
        s.EndsWith(".ico", StringComparison.OrdinalIgnoreCase);
}
