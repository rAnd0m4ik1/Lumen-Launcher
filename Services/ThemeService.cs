using System.Windows;
using System.Windows.Media;

namespace CraftDeck.Services;

public static class ThemeService
{
    public static event Action? ThemeChanged;

    public static ThemePreset Current { get; private set; } = ThemeCatalog.DefaultFor(false);

    public static BackgroundPreset CurrentBackground { get; private set; } = BackgroundCatalog.DefaultFor(false);

    public static BackgroundStyle CurrentStyle { get; private set; } = BackgroundStyle.Gradient;

    public static void ApplyTheme(string? mode, string? presetId, string? styleId, string? accentHex)
    {
        var light = string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase);
        ApplyBackground(BackgroundCatalog.Resolve(presetId, light), BackgroundCatalog.ParseStyle(styleId));
        Apply(accentHex);
    }

    public static void ApplyBackground(BackgroundPreset b, BackgroundStyle style)
    {
        CurrentBackground = b;
        CurrentStyle = style;
        ApplyPalette(b.Palette(style));
    }

    public static void ApplyPalette(ThemePreset p)
    {
        Current = p;
        var res = Application.Current.Resources;

        Set(res, "Bg", C(p.BgTop));
        Set(res, "Bg2", C(p.Bg2));
        Set(res, "Card", C(p.CardTop));
        Set(res, "CardHover", C(p.CardHover));
        Set(res, "Text", C(p.Text));
        Set(res, "Subtle", C(p.Subtle));
        Set(res, "Danger", C(p.Danger));

        res["BorderBrush"] = new SolidColorBrush(C(p.Border));
        res["SidebarEdgeBrush"] = new SolidColorBrush(C(p.SidebarBottom));
        res["HairlineBrush"] = new SolidColorBrush(C(p.Hairline));
        res["HairlineDarkBrush"] = new SolidColorBrush(C(p.HairlineSoft));
        res["ChipBrush"] = new SolidColorBrush(C(p.Chip));
        res["ThumbBrush"] = new SolidColorBrush(C(p.Thumb));
        res["PanelBrush"] = new SolidColorBrush(C(p.Panel));

        res["BgGradBrush"] = Vertical(p.BgTop, p.BgMid, p.BgBottom);
        res["SidebarGradBrush"] = Vertical(p.SidebarTop, p.SidebarBottom);
        res["TitleGradBrush"] = Horizontal(p.BgMid, p.BgTop);
        res["CardBrush"] = Diagonal(p.CardTop, p.CardBottom);
        res["TileGradBrush"] = Diagonal(p.CardHover, p.CardBottom);
        ThemeChanged?.Invoke();
    }

    private static LinearGradientBrush Vertical(params string[] hexes) => Grad(0, 0, 0, 1, hexes);
    private static LinearGradientBrush Horizontal(params string[] hexes) => Grad(0, 0, 1, 0, hexes);
    private static LinearGradientBrush Diagonal(params string[] hexes) => Grad(0, 0, 1, 1, hexes);

    private static LinearGradientBrush Grad(double x1, double y1, double x2, double y2, string[] hexes)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2) };
        for (var i = 0; i < hexes.Length; i++)
            b.GradientStops.Add(new GradientStop(C(hexes[i]), hexes.Length == 1 ? 0 : (double)i / (hexes.Length - 1)));
        return b;
    }

    private static Color C(string hex) => TryParse(hex, out var c) ? c : Color.FromRgb(0x12, 0x14, 0x1B);

    public static void Apply(string? argbHex)
    {
        var res = Application.Current.Resources;
        if (!TryParse(argbHex, out var accent))
            accent = Color.FromRgb(0x1F, 0xDB, 0x62);
        Set(res, "Accent", accent);
        Set(res, "AccentDim", Darken(accent, 0.82));
        Set(res, "AccentFg", Luminance(accent) > 0.55
            ? Color.FromRgb(0x06, 0x13, 0x0A)
            : Color.FromRgb(0xFF, 0xFF, 0xFF));
        ThemeChanged?.Invoke();
    }

    public static bool TryParse(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex.Trim());
            return true;
        }
        catch { return false; }
    }

    public static Color ContrastForeground(string? hex)
    {
        if (!TryParse(hex, out var c)) c = Color.FromRgb(0x1F, 0xDB, 0x62);
        return Luminance(c) > 0.55 ? Color.FromRgb(0x06, 0x13, 0x0A) : Colors.White;
    }

    private static void Set(ResourceDictionary res, string keyBase, Color color)
    {
        res[keyBase + "Color"] = color;
        res[keyBase + "Brush"] = new SolidColorBrush(color);
    }

    private static Color Darken(Color c, double factor) =>
        Color.FromArgb(c.A, (byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

    private static double Luminance(Color c) =>
        (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
}
