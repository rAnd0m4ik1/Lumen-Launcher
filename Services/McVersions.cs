namespace CraftDeck.Services;

public static class McVersions
{
    private static List<string> _releases = new();

    public static void Seed(IEnumerable<string> releases) => _releases = releases.ToList();

    public static List<string> Line(string? mc)
    {
        if (string.IsNullOrEmpty(mc)) return new();
        var prefix = mc + ".";
        var line = _releases.Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (_releases.Contains(mc, StringComparer.OrdinalIgnoreCase)) line.Add(mc);
        return line.Count > 0 ? line : new List<string> { mc };
    }

    public static string Query(string key, string? mc) =>
        string.Concat(Line(mc).Select(v => $"&{key}={Uri.EscapeDataString(v)}"));

    public static string? Match(List<string>? values, string? mc)
    {
        if (string.IsNullOrEmpty(mc)) return mc;
        if (values == null) return null;
        var line = Line(mc);
        foreach (var v in line)
            foreach (var got in values)
                if (got.Equals(v, StringComparison.OrdinalIgnoreCase)) return got;
        return null;
    }

    public static bool Matches(List<string>? values, string? mc) =>
        string.IsNullOrEmpty(mc) || Match(values, mc) != null;
}
