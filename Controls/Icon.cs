using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace CraftDeck.Controls;

public class Icon : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(Icon),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(Icon),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(Icon),
            new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WeightProperty =
        DependencyProperty.Register(nameof(Weight), typeof(double), typeof(Icon),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double Weight
    {
        get => (double)GetValue(WeightProperty);
        set => SetValue(WeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        var (stroke, fill, weight) = IconLibrary.Get(Glyph);
        if (stroke == null && fill == null) return;

        var brush = Foreground;
        if (brush == null || brush == Brushes.Black && Glyph.Length == 0) return;

        var scale = Size / IconLibrary.Grid;
        var width = double.IsNaN(Weight) ? weight : Weight;

        dc.PushTransform(new ScaleTransform(scale, scale));
        if (fill != null) dc.DrawGeometry(brush, null, IconLibrary.Geometry(fill));
        if (stroke != null)
        {
            var pen = new Pen(brush, width)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, IconLibrary.Geometry(stroke));
        }
        dc.Pop();
    }
}

public static class IconLibrary
{
    public const double Grid = 24;

    private const double DefaultWeight = 1.7;

    private static readonly Dictionary<string, (string? Stroke, string? Fill, double Weight)> Icons = new()
    {
        ["Plus"] = ("M12,5.6 V18.4 M5.6,12 H18.4", null, DefaultWeight),
        ["Check"] = ("M4.9,12.6 L9.6,17.2 L19.1,7.1", null, 2.0),
        ["Download"] = ("M12,4.4 V14.6 M7.6,10.4 L12,14.9 L16.4,10.4 M5.2,19.4 H18.8", null, DefaultWeight),
        ["Trash"] = ("M4.6,7.2 H19.4 M9.4,4.4 H14.6 M6.6,7.2 L7.7,19.6 H16.3 L17.4,7.2 " +
                     "M10.3,10.6 V16.2 M13.7,10.6 V16.2", null, DefaultWeight),
        ["Copy"] = ("M8.6,8.6 H17.4 V17.4 H8.6 Z M5.6,15.4 H4.6 V4.6 H15.4 V5.6", null, DefaultWeight),
        ["Folder"] = ("M3.6,6.4 H9.3 L11.3,8.8 H20.4 V19.4 H3.6 Z", null, DefaultWeight),
        ["Layers"] = ("M12,3.8 L20.2,8.2 L12,12.6 L3.8,8.2 Z M3.8,12.4 L12,16.8 L20.2,12.4 M3.8,16.4 L12,20.8 L20.2,16.4", null, DefaultWeight),
        ["More"] = (null, "M6.6,12 m-1.7,0 a1.7,1.7 0 1,0 3.4,0 a1.7,1.7 0 1,0 -3.4,0 " +
                          "M12,12 m-1.7,0 a1.7,1.7 0 1,0 3.4,0 a1.7,1.7 0 1,0 -3.4,0 " +
                          "M17.4,12 m-1.7,0 a1.7,1.7 0 1,0 3.4,0 a1.7,1.7 0 1,0 -3.4,0", DefaultWeight),
        ["Play"] = (null, "M8.4,5.2 L18.9,12 L8.4,18.8 Z", DefaultWeight),
        ["Stop"] = (null, "M8.2,8.2 H15.8 V15.8 H8.2 Z", DefaultWeight),
        ["Import"] = ("M4.4,13.4 V18.4 A1.2,1.2 0 0 0 5.6,19.6 H18.4 A1.2,1.2 0 0 0 19.6,18.4 V13.4 " +
                      "M12,4 V14.3 M8.4,10.7 L12,14.5 L15.6,10.7", null, DefaultWeight),
        ["User"] = ("M12,4.6 a3.7,3.7 0 1,0 0,7.4 a3.7,3.7 0 1,0 0,-7.4 " +
                    "M5.2,19.6 C5.2,15.9 8.2,14.1 12,14.1 C15.8,14.1 18.8,15.9 18.8,19.6", null, DefaultWeight),
        ["Sliders"] = ("M4,8.4 H20 M4,15.6 H20",
                       "M9.4,8.4 m-2.7,0 a2.7,2.7 0 1,0 5.4,0 a2.7,2.7 0 1,0 -5.4,0 " +
                       "M14.6,15.6 m-2.7,0 a2.7,2.7 0 1,0 5.4,0 a2.7,2.7 0 1,0 -5.4,0", DefaultWeight),
        ["Compass"] = ("M12,3.8 a8.2,8.2 0 1,0 0,16.4 a8.2,8.2 0 1,0 0,-16.4", "M15.9,8.1 L10.6,10.6 L8.1,15.9 L13.4,13.4 Z", DefaultWeight),
        ["Cube"] = ("M12,3.6 L20,8.1 V15.9 L12,20.4 L4,15.9 V8.1 Z M4,8.1 L12,12.6 L20,8.1 M12,12.6 V20.4", null, DefaultWeight),
        ["Tiles"] = (null, "M4.2,4.2 H10.7 V10.7 H4.2 Z M13.3,4.2 H19.8 V10.7 H13.3 Z " +
                          "M4.2,13.3 H10.7 V19.8 H4.2 Z M13.3,13.3 H19.8 V19.8 H13.3 Z", DefaultWeight),
        ["ChevronRight"] = ("M9.6,5.4 L16.2,12 L9.6,18.6", null, DefaultWeight),
        ["ChevronDown"] = ("M5.4,9.4 L12,16 L18.6,9.4", null, DefaultWeight),
        ["CaptionMin"] = ("M5,12.6 H19", null, 2.1),
        ["CaptionMax"] = ("M5.8,5.8 H18.2 V18.2 H5.8 Z", null, 2.1),
        ["CaptionRestore"] = ("M8.2,8.2 H18.6 V18.6 H8.2 Z M5.4,15.8 H5 V5 H15.8 V5.4", null, 2.1),
        ["Close"] = ("M6.2,6.2 L17.8,17.8 M17.8,6.2 L6.2,17.8", null, 2.1),
        ["Heart"] = (null, "M12,19.9 C12,19.9 3.9,14.7 3.9,9.3 C3.9,6.4 6.2,4.6 8.5,4.6 C10.1,4.6 11.3,5.5 12,6.6 " +
                           "C12.7,5.5 13.9,4.6 15.5,4.6 C17.8,4.6 20.1,6.4 20.1,9.3 C20.1,14.7 12,19.9 12,19.9 Z", DefaultWeight),
        ["Clock"] = ("M12,3.8 a8.2,8.2 0 1,0 0,16.4 a8.2,8.2 0 1,0 0,-16.4 M12,7.3 V12.4 L15.6,14.4", null, DefaultWeight),
        ["Contrast"] = ("M12,3.9 a8.1,8.1 0 1,0 0,16.2 a8.1,8.1 0 1,0 0,-16.2",
                        "M12,3.9 A8.1,8.1 0 0 1 12,20.1 Z", DefaultWeight),
        ["Cup"] = ("M5.2,7.6 H16.4 V13.4 C16.4,16.6 14,18.9 10.8,18.9 C7.6,18.9 5.2,16.6 5.2,13.4 Z " +
                   "M16.4,9.8 H17.7 C19.2,9.8 20.4,11.1 20.4,12.6 C20.4,14.1 19.2,15.4 17.7,15.4 H16.4 " +
                   "M3.4,21 H19", null, DefaultWeight),
        ["Gauge"] = ("M4.4,17.4 A8.8,8.8 0 1 1 19.6,17.4 M12,12.6 L16.3,8.7",
                     "M12,12.7 m-1.7,0 a1.7,1.7 0 1,0 3.4,0 a1.7,1.7 0 1,0 -3.4,0", DefaultWeight),
        ["Window"] = ("M3.8,6.4 A1.6,1.6 0 0 1 5.4,4.8 H18.6 A1.6,1.6 0 0 1 20.2,6.4 V17.6 A1.6,1.6 0 0 1 18.6,19.2 " +
                      "H5.4 A1.6,1.6 0 0 1 3.8,17.6 Z M3.8,8.8 H20.2",
                      "M6.9,6.8 m-0.9,0 a0.9,0.9 0 1,0 1.8,0 a0.9,0.9 0 1,0 -1.8,0 " +
                      "M9.7,6.8 m-0.9,0 a0.9,0.9 0 1,0 1.8,0 a0.9,0.9 0 1,0 -1.8,0", DefaultWeight),
        ["Wand"] = ("M4.0,20 L14.4,9.6",
                    "M17.8,2.2 L19.1,5.1 L22,6.4 L19.1,7.7 L17.8,10.6 L16.5,7.7 L13.6,6.4 L16.5,5.1 Z " +
                    "M12.4,1.4 L13.1,3.1 L14.8,3.8 L13.1,4.5 L12.4,6.2 L11.7,4.5 L10,3.8 L11.7,3.1 Z", 2.0),
    };

    private static readonly Dictionary<string, Geometry> Cache = new();

    public static (string? Stroke, string? Fill, double Weight) Get(string? name) =>
        name != null && Icons.TryGetValue(name, out var entry) ? entry : (null, null, DefaultWeight);

    public static Geometry Geometry(string data)
    {
        if (!Cache.TryGetValue(data, out var geometry))
        {
            geometry = System.Windows.Media.Geometry.Parse(data);
            geometry.Freeze();
            Cache[data] = geometry;
        }
        return geometry;
    }
}
