using System.IO;
using System.Text.RegularExpressions;
using CraftDeck.Models;

namespace CraftDeck.Services;

public static class ShaderFormat
{
    private static readonly Regex IrisJar = new(@"(?i)^(iris|oculus)(?![a-z])", RegexOptions.Compiled);

    public static string? For(Instance inst) => HasIris(inst.ModsDir) ? "iris" : null;

    public static bool HasIris(string modsDir)
    {
        if (!Directory.Exists(modsDir)) return false;
        try
        {
            foreach (var f in Directory.EnumerateFileSystemEntries(modsDir))
            {
                var name = Path.GetFileName(f);
                if (!IsArchive(name)) continue;
                if (IrisJar.IsMatch(name)) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool IsArchive(string name) =>
        name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
}
