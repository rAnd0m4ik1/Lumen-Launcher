namespace CraftDeck.Services;

public sealed class ThemePreset
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required bool Light { get; init; }

    public required string BgTop { get; init; }
    public required string BgMid { get; init; }
    public required string BgBottom { get; init; }
    public required string SidebarTop { get; init; }
    public required string SidebarBottom { get; init; }
    public required string Panel { get; init; }
    public required string Bg2 { get; init; }
    public required string CardTop { get; init; }
    public required string CardBottom { get; init; }
    public required string CardHover { get; init; }
    public required string Border { get; init; }
    public required string Thumb { get; init; }
    public required string Text { get; init; }
    public required string Subtle { get; init; }

    public string Danger => Light ? "#FFC42B1C" : "#FFE5484D";
    public string Hairline => Light ? "#1F000000" : "#26FFFFFF";
    public string HairlineSoft => Light ? "#12000000" : "#14FFFFFF";
    public string Chip => Light ? "#0F000000" : "#14FFFFFF";
}

public static class ThemeCatalog
{
    public static readonly IReadOnlyList<ThemePreset> All = new List<ThemePreset>
    {
        new()
        {
            Id = "midnight", Label = "Midnight", Light = false,
            BgTop = "#FF0F1218", BgMid = "#FF141824", BgBottom = "#FF171C2A",
            SidebarTop = "#FF141822", SidebarBottom = "#FF11141D",
            Panel = "#FF13161F", Bg2 = "#FF161A24",
            CardTop = "#FF1F2431", CardBottom = "#FF1B202B", CardHover = "#FF283041",
            Border = "#FF303A4C", Thumb = "#FF4A5266",
            Text = "#FFEDEEF2", Subtle = "#FF98A0B0",
        },
        new()
        {
            Id = "paper", Label = "Paper", Light = true,
            BgTop = "#FFFFFFFF", BgMid = "#FFF7F8FA", BgBottom = "#FFF3F5F8",
            SidebarTop = "#FFF1F3F7", SidebarBottom = "#FFEAEFF5",
            Panel = "#FFF2F4F8", Bg2 = "#FFFFFFFF",
            CardTop = "#FFFFFFFF", CardBottom = "#FFF8FAFC", CardHover = "#FFF0F3F8",
            Border = "#FFD8DEE8", Thumb = "#FFC2CAD8",
            Text = "#FF1B212B", Subtle = "#FF606D7C",
        },
    };

    public static ThemePreset? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static ThemePreset DefaultFor(bool light) => Find(light ? "paper" : "midnight")!;
}

public enum BackgroundStyle { Static, Gradient }

public sealed class BackgroundPreset
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required bool Light { get; init; }

    public required string Solid { get; init; }
    public required string Top { get; init; }
    public required string Mid { get; init; }
    public required string Bottom { get; init; }

    public string[] RampFor(BackgroundStyle style) =>
        style == BackgroundStyle.Static ? new[] { Solid } : new[] { Top, Mid, Bottom };

    public ThemePreset Palette(BackgroundStyle style)
    {
        var ramp = RampFor(style);
        var (h, s, l) = Shade.ToHsl(ramp.Length > 1 ? Mid : Solid);
        var sat = Math.Min(s, 0.30);

        const double dim = 0.05;
        var drop = Light ? dim : 0.0;
        string At(double target, double mul = 1) => Shade.FromHsl(h, sat * mul, target - drop);
        string Soft(string hex)
        {
            var (hh, ss, ll) = Shade.ToHsl(hex);
            return Shade.FromHsl(hh, ss, ll - drop);
        }
        string Ink(double target, double mul) => Shade.FromHsl(h, sat * mul, target);

        return new ThemePreset
        {
            Id = Id, Label = Label, Light = Light,
            BgTop = Soft(ramp[0]),
            BgMid = ramp.Length > 1 ? Soft(ramp[1]) : Soft(ramp[0]),
            BgBottom = ramp.Length > 1 ? Soft(ramp[2]) : Soft(ramp[0]),
            SidebarTop = At(Light ? l - 0.028 : l - 0.014),
            SidebarBottom = At(Light ? l - 0.052 : l - 0.030),
            Panel = At(Light ? l - 0.008 : l + 0.008),
            Bg2 = At(Light ? Math.Min(l + 0.05, 0.995) : l + 0.028),
            CardTop = At(Light ? Math.Min(l + 0.05, 0.995) : l + 0.070),
            CardBottom = At(Light ? Math.Min(l + 0.022, 0.99) : l + 0.048),
            CardHover = At(Light ? Math.Min(l + 0.082, 1) : l + 0.115),
            Border = At(Light ? l - 0.115 : l + 0.155, 1.15),
            Thumb = At(Light ? l - 0.17 : l + 0.24),
            Text = Light ? Ink(0.21, 0.5) : Ink(0.93, 0.4),
            Subtle = Light ? Ink(0.45, 0.45) : Ink(0.62, 0.5),
        };
    }
}

public static class BackgroundCatalog
{
    private static BackgroundPreset Bg(string id, string label, bool light, string solid,
        string top, string mid, string bottom) =>
        new() { Id = id, Label = label, Light = light, Solid = solid, Top = top, Mid = mid, Bottom = bottom };

    public static readonly IReadOnlyList<BackgroundPreset> All = new List<BackgroundPreset>
    {
        Bg("midnight", "Midnight", false, "#FF141824", "#FF0F1218", "#FF141824", "#FF171C2A"),
        Bg("graphite", "Graphite", false, "#FF151517", "#FF101012", "#FF151517", "#FF1A1A1D"),
        Bg("deepsea", "Deep Sea", false, "#FF0E1C2B", "#FF0A1420", "#FF0E1C2B", "#FF122436"),
        Bg("forest", "Forest", false, "#FF101C17", "#FF0B1512", "#FF101C17", "#FF14251D"),
        Bg("plum", "Plum", false, "#FF18122A", "#FF140F1E", "#FF18122A", "#FF1D1634"),
        Bg("night", "Night", false, "#FF0D1430", "#FF090D1E", "#FF0D1430", "#FF131C44"),
        Bg("wine", "Wine", false, "#FF221219", "#FF1A0E14", "#FF221219", "#FF2B1621"),
        Bg("ember", "Ember", false, "#FF231709", "#FF1A1208", "#FF231709", "#FF2C1D0C"),
        Bg("teal", "Teal", false, "#FF0B2126", "#FF07171A", "#FF0B2126", "#FF102B31"),
        Bg("slate", "Slate", false, "#FF1C2026", "#FF16191D", "#FF1C2026", "#FF232830"),
        Bg("ink", "Ink", false, "#FF12141B", "#FF0D0F15", "#FF12141B", "#FF171A23"),
        Bg("coal", "Coal", false, "#FF131313", "#FF0D0D0D", "#FF131313", "#FF191919"),
        Bg("olive", "Olive", false, "#FF191C14", "#FF13160F", "#FF191C14", "#FF1F2419"),

        Bg("paper", "Paper", true, "#FFF7F8FA", "#FFFFFFFF", "#FFF7F8FA", "#FFF3F5F8"),
        Bg("mist", "Mist", true, "#FFEEF4FA", "#FFF4F8FC", "#FFEEF4FA", "#FFE7EFF8"),
        Bg("sand", "Sand", true, "#FFFAF6EF", "#FFFCFAF5", "#FFFAF6EF", "#FFF6F1E7"),
        Bg("lilac", "Lilac", true, "#FFF7F2FA", "#FFFBF8FD", "#FFF7F2FA", "#FFF2EBF7"),
        Bg("cloud", "Cloud", true, "#FFF1F4F8", "#FFF8FAFC", "#FFF1F4F8", "#FFE9EDF3"),
        Bg("mint", "Mint", true, "#FFECF6F0", "#FFF5FBF7", "#FFECF6F0", "#FFE2F1E8"),
        Bg("peach", "Peach", true, "#FFFFF1E7", "#FFFFF9F4", "#FFFFF1E7", "#FFFDE8D8"),
        Bg("sky", "Sky", true, "#FFEAF3FD", "#FFF3F9FF", "#FFEAF3FD", "#FFE0EDFA"),
        Bg("rose", "Rose", true, "#FFFBEFF1", "#FFFDF6F7", "#FFFBEFF1", "#FFF8E4E8"),
        Bg("snow", "Snow", true, "#FFFFFFFF", "#FFFFFFFF", "#FFFDFCF9", "#FFF8F6F1"),
        Bg("linen", "Linen", true, "#FFFAF7F2", "#FFFFFDFA", "#FFFAF7F2", "#FFF4EEE3"),
        Bg("haze", "Haze", true, "#FFF2F3F5", "#FFF7F8FA", "#FFF2F3F5", "#FFE9EBEF"),
    };

    public static BackgroundPreset? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static BackgroundPreset DefaultFor(bool light) => Find(light ? "paper" : "midnight")!;

    public static BackgroundStyle ParseStyle(string? id) =>
        string.Equals(id, "static", StringComparison.OrdinalIgnoreCase)
            ? BackgroundStyle.Static
            : BackgroundStyle.Gradient;

    public static BackgroundPreset Resolve(string? id, bool light)
    {
        var p = Find(id);
        return p != null && p.Light == light ? p : DefaultFor(light);
    }
}

public static class FxColorCatalog
{
    public const string Default = "#FFFFFFFF";

    public static readonly IReadOnlyList<(string Label, string Hex)> All = new List<(string Label, string Hex)>
    {
        ("White", "#FFFFFFFF"),
        ("Green", "#FF1FDB62"),
        ("Lime", "#FFB6E31F"),
        ("Cyan", "#FF22D3EE"),
        ("Blue", "#FF3D9AFF"),
        ("Violet", "#FF9B5CFF"),
        ("Pink", "#FFFF3D9A"),
        ("Red", "#FFFF3B30"),
        ("Ember", "#FFFF6A13"),
        ("Amber", "#FFFFB020"),
    };

    public static string Resolve(string? hex) =>
        All.Any(x => string.Equals(x.Hex, hex, StringComparison.OrdinalIgnoreCase)) ? hex! : Default;
}

internal static class Shade
{
    public static (double H, double S, double L) ToHsl(string argb)
    {
        var hex = argb.StartsWith('#') ? argb[1..] : argb;
        if (hex.Length == 8) hex = hex[2..];
        int Channel(int i) => Convert.ToInt32(hex.Substring(i * 2, 2), 16);
        return FromRgb(Channel(0) / 255.0, Channel(1) / 255.0, Channel(2) / 255.0);
    }

    public static (double H, double S, double L) FromRgb(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        var l = (max + min) / 2;
        if (d == 0) return (0, 0, l);
        double h;
        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, d / (1 - Math.Abs(2 * l - 1)), l);
    }

    public static string FromHsl(double h, double s, double l)
    {
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        byte Q(double v) => (byte)Math.Round((v + m) * 255);
        return "#FF" + Q(r).ToString("X2") + Q(g).ToString("X2") + Q(b).ToString("X2");
    }
}
