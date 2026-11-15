using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using CraftDeck.Controls;
using CraftDeck.Models;
using CraftDeck.Services;

namespace CraftDeck.Views;

public partial class SettingsView : UserControl
{
    private static readonly string[] AccentPaletteColors =
    {
        "#FF1FDB62",
        "#FFFFB900", "#FFFF8C00", "#FFF7630C", "#FFCA5010", "#FFDA3B01", "#FFEF6950", "#FFD13438", "#FFFF4343", "#FFE74856",
        "#FFE81123", "#FFEA005E", "#FFC30052", "#FFE3008C", "#FFBF0077", "#FFC239B3", "#FF9A0089", "#FF0A7DD6", "#FF0063B1",
        "#FF8E8CD8", "#FF6B69D6", "#FF8764B8", "#FF744DA9", "#FFB146C2", "#FF881798", "#FF0099BC", "#FF2D7D9A", "#FF00B7C3",
        "#FF038387", "#FF00B294", "#FF018574", "#FF00CC6A", "#FF10893E", "#FF7A7574", "#FF5D5A58", "#FF68768A", "#FF515C6B",
        "#FF567C73", "#FF486860", "#FF498205", "#FF107C10", "#FF767676", "#FF4C4A48", "#FF69797E", "#FF4A5459", "#FF647C64",
        "#FF525E54", "#FF847545", "#FF7E735F",
    };

    private readonly List<(string Hex, Icon Check, Button Ring)> _swatches = new();
    private readonly List<(string Hex, Icon Check, Button Ring)> _fxSwatches = new();
    private readonly List<(BackgroundPreset Preset, Border Preview, Button Ring, TextBlock Label)> _bgSwatches = new();

    private const double MemFloorMb = 512;
    private const double MemCapture = 0.09;
    private double _memCeilMb = 1024;
    private double _memSpan = 1;
    private double _memScaleWidth = -1;

    public SettingsView()
    {
        InitializeComponent();
        BuildSwatches(AccentPalette, AccentPaletteColors.Select(h => (h, h)), "Accent", AccentSwatch_Click, _swatches);
        BuildSwatches(FxPalette, FxColorCatalog.All.Select(c => (c.Hex, c.Label)), "Effect color", FxSwatch_Click, _fxSwatches);
        BuildBackgroundPalette();
        InitMemScale();
        Loaded += (_, _) => Refresh();
        WireAutoSave();
    }

    private bool _suppressAutoSave;

    private void WireAutoSave()
    {
        foreach (var tb in new[] { JvmBox, GameBox })
            tb.TextChanged += (_, _) => ApplyLive();
        JavaBox.TextChanged += (_, _) => { ApplyLive(); RefreshJavaStatus(); };
        MaxMemBox.TextChanged += (_, _) => MemBox_Changed();
        _memTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _memTimer.Tick += MemTimer_Tick;
        _memBoxTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _memBoxTimer.Tick += MemBox_Tick;
        CloseOnLaunch.Checked += (_, _) => ApplyLive();
        CloseOnLaunch.Unchecked += (_, _) => ApplyLive();
    }

    private void ApplyLive()
    {
        if (_suppressAutoSave) return;
        var s = AppServices.Settings;
        s.JavaPath = string.IsNullOrWhiteSpace(JavaBox.Text) ? null : JavaBox.Text.Trim();
        s.MaxMemoryMb = MbAt(MaxMemSlider.Value);
        _memSavedMb = s.MaxMemoryMb;
        s.JVMArguments = JvmBox.Text;
        s.GameArguments = GameBox.Text;
        s.CloseOnLaunch = CloseOnLaunch.IsChecked == true;
        s.Save();
    }

    private int _javaProbeSeq;

    private void RefreshJavaStatus()
    {
        var path = AppServices.Settings.JavaPath;
        var seq = ++_javaProbeSeq;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            var java = JavaLocator.Find(path);
            var text = java == null
                ? "No Java found yet — the launcher will download a portable Temurin JDK into <launcher folder>\\java automatically when you press Play."
                : $"Detected: {java} (Java {JavaLocator.JavaMajorVersion(java)})";
            Dispatcher.BeginInvoke(() =>
            {
                if (seq == _javaProbeSeq) JavaDetected.Text = text;
            });
        });
    }

    private int MbAt(double stop) =>
        (int)Math.Round(Math.Min(MemFloorMb * Math.Pow(2, Math.Clamp(stop, 0, _memSpan)), _memCeilMb));

    private double StopOf(int mb) =>
        Math.Clamp(Math.Log2(Math.Clamp((double)mb, MemFloorMb, _memCeilMb) / MemFloorMb), 0, _memSpan);

    private List<double> MemMarks()
    {
        var stops = new List<double>();
        for (var s = 0.0; s < _memSpan - 0.02; s += 1) stops.Add(s);
        stops.Add(_memSpan);
        return stops;
    }

    private static double PhysicalMb()
    {
        var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return bytes > 1L << 30 ? Math.Round(bytes / (1024.0 * 1024.0)) : 16384;
    }

    private void InitMemScale()
    {
        _memCeilMb = PhysicalMb();
        _memSpan = Math.Max(1, Math.Log2(_memCeilMb / MemFloorMb));
        MaxMemSlider.Maximum = _memSpan;
        MemScale.SizeChanged += (_, _) => LayoutMemScale();
    }

    private void MaxMem_Loaded(object sender, RoutedEventArgs e) => LayoutMemScale();

    private void MaxMem_Size(object sender, SizeChangedEventArgs e) => LayoutMemScale();

    private void LayoutMemScale()
    {
        MaxMemSlider.ApplyTemplate();
        var ticks = MaxMemSlider.Template?.FindName("TickLayer", MaxMemSlider) as Canvas;
        var track = MaxMemSlider.Template?.FindName("PART_Track", MaxMemSlider) as System.Windows.Controls.Primitives.Track;
        var width = MemScale.ActualWidth;
        if (ticks == null || track == null || width <= 0) return;

        var half = track.Thumb?.ActualWidth / 2 ?? 0;
        var usable = width - half * 2;
        if (usable <= 0) return;
        if (MemScale.Children.Count > 0 && Math.Abs(width - _memScaleWidth) < 0.5) return;
        _memScaleWidth = width;

        ticks.Children.Clear();
        MemScale.Children.Clear();
        var marks = MemMarks();
        var subtle = (Style)FindResource("Subtle");

        for (var i = 0; i < marks.Count; i++)
        {
            var x = half + marks[i] / _memSpan * usable;

            var dash = new Border
            {
                Width = 2,
                Height = 9,
                CornerRadius = new CornerRadius(1),
                Opacity = 0.45,
            };
            dash.SetResourceReference(Border.BackgroundProperty, "TextBrush");
            Canvas.SetLeft(dash, x - 1);
            Canvas.SetTop(dash, (ticks.ActualHeight - 9) / 2);
            ticks.Children.Add(dash);

            var label = new TextBlock { Style = subtle, FontSize = 11, Text = MbAt(marks[i]).ToString() };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var left = i == 0
                ? 0
                : i == marks.Count - 1
                    ? width - label.DesiredSize.Width
                    : x - label.DesiredSize.Width / 2;
            Canvas.SetLeft(label, left);
            MemScale.Children.Add(label);
        }
    }

    private System.Windows.Threading.DispatcherTimer? _memTimer;
    private double _memParked = -1;
    private double _memTravel;
    private bool _memApplying;
    private int _memShownMb = -1;
    private int _memSavedMb = -1;

    private void ShowMemMb(int mb)
    {
        if (mb == _memShownMb || MaxMemBox.IsKeyboardFocusWithin) return;
        SetMemText(mb);
    }

    private void SetMemText(int mb)
    {
        _memShownMb = mb;
        MaxMemBox.Text = mb.ToString();
    }

    private void MaxMem_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_memApplying) return;

        var raw = e.NewValue;
        if (_memParked >= 0)
        {
            _memTravel += raw - e.OldValue;
            if (Math.Abs(_memTravel) >= MemCapture)
            {
                raw = Math.Clamp(_memParked + _memTravel, 0, _memSpan);
                _memParked = -1;
                _memTravel = 0;
            }
            else
            {
                raw = _memParked;
            }
        }
        else
        {
            var nearest = Math.Round(raw);
            if (Math.Abs(raw - nearest) < MemCapture)
            {
                _memParked = Math.Clamp(nearest, 0, _memSpan);
                _memTravel = 0;
                raw = _memParked;
            }
        }

        if (Math.Abs(raw - MaxMemSlider.Value) > 1e-9)
        {
            _memApplying = true;
            MaxMemSlider.Value = raw;
            _memApplying = false;
        }

        ShowMemMb(MbAt(raw));
        if (_suppressAutoSave || _memTimer == null) return;
        _memTimer.Stop();
        _memTimer.Start();
    }

    private void MemTimer_Tick(object? sender, EventArgs e)
    {
        _memTimer!.Stop();
        _memParked = -1;
        _memTravel = 0;
        if (MbAt(MaxMemSlider.Value) != _memSavedMb) ApplyLive();
    }

    private void MaxMemBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        MaxMemBox_Commit(sender, new RoutedEventArgs());
        MaxMemBox.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
    }

    private void MaxMemBox_Commit(object sender, RoutedEventArgs e)
    {
        var mb = int.TryParse(MaxMemBox.Text.Trim(), out var typed)
            ? Math.Clamp(typed, (int)MemFloorMb, (int)_memCeilMb)
            : MbAt(MaxMemSlider.Value);
        ApplyMemMb(mb);
        SetMemText(MbAt(MaxMemSlider.Value));
    }

    private System.Windows.Threading.DispatcherTimer? _memBoxTimer;

    private void MemBox_Changed()
    {
        if (_memBoxTimer == null) return;
        _memBoxTimer.Stop();
        _memBoxTimer.Start();
    }

    private void MemBox_Tick(object? sender, EventArgs e)
    {
        _memBoxTimer!.Stop();
        if (int.TryParse(MaxMemBox.Text.Trim(), out var mb) && mb >= (int)MemFloorMb && mb != MbAt(MaxMemSlider.Value)) ApplyMemMb(mb);
    }

    private void ApplyMemMb(int mb)
    {
        _memParked = -1;
        _memTravel = 0;
        _memApplying = true;
        MaxMemSlider.Value = StopOf(mb);
        _memApplying = false;
        ShowMemMb(MbAt(MaxMemSlider.Value));
        if (!_suppressAutoSave && MbAt(MaxMemSlider.Value) != _memSavedMb) ApplyLive();
    }

    private static readonly ControlTemplate RingTemplate = CreateRingTemplate();

    private static ControlTemplate CreateRingTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetBinding(Border.BackgroundProperty, new Binding(nameof(ContentControl.Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        bd.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        bd.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Control.BorderThickness)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        return t;
    }

    private void BuildSwatches(WrapPanel host, IEnumerable<(string Hex, string Label)> items,
        string namePrefix, RoutedEventHandler onClick, List<(string Hex, Icon Check, Button Ring)> sink)
    {
        foreach (var (hex, label) in items)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var check = new Icon
            {
                Glyph = "Check",
                Size = 13,
                Weight = 2.6,
                Foreground = new SolidColorBrush(ThemeService.ContrastForeground(hex)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            var fill = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(color),
                Child = check,
            };
            var ring = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Template = RingTemplate,
                Content = fill,
                Margin = new Thickness(0, 0, 4, 4),
                ToolTip = label,
                Tag = hex,
            };
            ring.SetResourceReference(Control.BorderBrushProperty, "TextBrush");
            ring.Click += onClick;
            AttachRingFeedback(ring);
            System.Windows.Automation.AutomationProperties.SetHelpText(ring, hex);
            System.Windows.Automation.AutomationProperties.SetName(ring, namePrefix + " " + label);
            host.Children.Add(ring);
            sink.Add((hex, check, ring));
        }
    }

    private void BuildBackgroundPalette()
    {
        foreach (var p in BackgroundCatalog.All)
        {
            var preview = new Border
            {
                Height = 26,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Child = new Border
                {
                    Width = 30,
                    Height = 8,
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(4, 0, 0, 5),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Bottom,
                },
            };
            preview.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
            PaintPreview(preview, p, ThemeService.CurrentStyle);

            var label = new TextBlock
            {
                Text = p.Label,
                FontSize = 10.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");

            var content = new StackPanel();
            content.Children.Add(preview);
            content.Children.Add(label);

            var ring = new Button
            {
                Width = 74,
                Height = 52,
                Padding = new Thickness(4, 4, 4, 2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Template = RingTemplate,
                Content = content,
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = p.Label + (p.Light ? ", light" : ", dark"),
                Tag = p.Id,
            };
            ring.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
            ring.Click += BackgroundSwatch_Click;
            AttachRingFeedback(ring);
            System.Windows.Automation.AutomationProperties.SetName(ring, "Background " + p.Id);
            BackgroundPalette.Children.Add(ring);
            _bgSwatches.Add((p, preview, ring, label));
        }
    }

    private static void PaintPreview(Border preview, BackgroundPreset p, BackgroundStyle style)
    {
        var stops = p.RampFor(style);
        if (stops.Length == 1)
        {
            preview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(stops[0]));
        }
        else
        {
            var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.4, 1) };
            for (var i = 0; i < stops.Length; i++)
                grad.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(stops[i]),
                    (double)i / (stops.Length - 1)));
            preview.Background = grad;
        }
        if (preview.Child is Border card)
            card.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.Palette(style).CardTop));
    }

    private void RefreshBackgroundPreviews(BackgroundStyle style)
    {
        foreach (var (preset, preview, _, _) in _bgSwatches) PaintPreview(preview, preset, style);
    }

    private void FxMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoSave) return;
        var mode = ReferenceEquals(sender, FxOff) ? FxMode.Off
            : ReferenceEquals(sender, FxSnow) ? FxMode.Snow
            : FxMode.Triangles;
        var s = AppServices.Settings;
        s.BackgroundFx = mode.ToString().ToLowerInvariant();
        s.Save();
        MainWindow.Main?.SetBackgroundFx(mode);
    }

    private void BackgroundSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var s = AppServices.Settings;
        s.BackgroundPreset = id;
        s.Save();
        ThemeService.ApplyBackground(BackgroundCatalog.Resolve(id, ThemeService.Current.Light), ThemeService.CurrentStyle);
        UpdateBackgroundChecks();
    }

    private void BgStyle_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoSave) return;
        var style = ReferenceEquals(sender, BgStatic) ? BackgroundStyle.Static : BackgroundStyle.Gradient;
        var s = AppServices.Settings;
        s.BackgroundStyle = style.ToString().ToLowerInvariant();
        s.Save();
        ThemeService.ApplyBackground(ThemeService.CurrentBackground, style);
        RefreshBackgroundPreviews(style);
    }

    private void ThemeMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoSave) return;
        var light = ReferenceEquals(sender, ThemeLight);
        var s = AppServices.Settings;
        s.ThemeMode = light ? "light" : "dark";
        s.BackgroundPreset = BackgroundCatalog.DefaultFor(light).Id;
        s.Save();
        ThemeService.ApplyTheme(s.ThemeMode, s.BackgroundPreset, s.BackgroundStyle, s.AccentColor);
        UpdateBackgroundChecks();
    }

    private void UpdateBackgroundChecks()
    {
        var s = AppServices.Settings;
        var light = string.Equals(s.ThemeMode, "light", StringComparison.OrdinalIgnoreCase);
        foreach (var (p, _, ring, label) in _bgSwatches)
        {
            ring.Visibility = p.Light == light ? Visibility.Visible : Visibility.Collapsed;
            var selected = string.Equals(p.Id, s.BackgroundPreset, StringComparison.OrdinalIgnoreCase);
            ring.BorderThickness = new Thickness(selected ? 2 : 0);
            label.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
            label.SetResourceReference(TextBlock.ForegroundProperty, selected ? "AccentBrush" : "SubtleBrush");
        }
    }

    private static void AttachRingFeedback(Button ring)
    {
        var scale = new ScaleTransform(1, 1);
        ring.RenderTransformOrigin = new Point(0.5, 0.5);
        ring.RenderTransform = scale;
        ring.MouseEnter += (_, _) =>
        {
            Pop(scale, 1.18, 0.10);
            ring.Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 0,
                Opacity = 0.5,
                Color = ThemeService.TryParse(AppServices.Settings.AccentColor, out var c) ? c : Colors.White,
            };
        };
        ring.MouseLeave += (_, _) =>
        {
            Pop(scale, 1, 0.13);
            ring.Effect = null;
        };
        ring.PreviewMouseLeftButtonDown += (_, _) => Pop(scale, 0.8, 0.05);
        ring.MouseLeftButtonUp += (_, _) => Pop(scale, 1.18, 0.10);
    }

    private static void Pop(ScaleTransform scale, double to, double seconds)
    {
        var d = TimeSpan.FromSeconds(seconds);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, d));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, d));
    }

    private void AccentSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string hex }) return;
        var s = AppServices.Settings;
        s.AccentColor = hex;
        s.Save();
        ThemeService.Apply(hex);
        UpdateAccentChecks();
    }

    private void FxSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string hex }) return;
        var s = AppServices.Settings;
        s.FxColor = hex;
        s.Save();
        MainWindow.Main?.RefreshBackgroundFxColors();
        UpdateFxChecks();
    }

    private void UpdateAccentChecks() => UpdateSwatchChecks(_swatches, AppServices.Settings.AccentColor);

    private void UpdateFxChecks() => UpdateSwatchChecks(_fxSwatches, AppServices.Settings.FxColor);

    private static void UpdateSwatchChecks(List<(string Hex, Icon Check, Button Ring)> swatches, string current)
    {
        foreach (var (hex, check, ring) in swatches)
        {
            bool selected = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            ring.BorderThickness = new Thickness(selected ? 2 : 0);
        }
    }

    public void Refresh()
    {
        _suppressAutoSave = true;
        var s = AppServices.Settings;
        JavaBox.Text = s.JavaPath ?? "";
        _memParked = -1;
        _memTravel = 0;
        _memApplying = true;
        MaxMemSlider.Value = StopOf(s.MaxMemoryMb);
        _memApplying = false;
        SetMemText(MbAt(MaxMemSlider.Value));
        if (s.MaxMemoryMb != MbAt(MaxMemSlider.Value))
        {
            s.MaxMemoryMb = MbAt(MaxMemSlider.Value);
            s.Save();
        }
        _memSavedMb = s.MaxMemoryMb;
        JvmBox.Text = s.JVMArguments;
        GameBox.Text = s.GameArguments;
        CloseOnLaunch.IsChecked = s.CloseOnLaunch;
        var light = string.Equals(s.ThemeMode, "light", StringComparison.OrdinalIgnoreCase);
        ThemeLight.IsChecked = light;
        ThemeDark.IsChecked = !light;
        var fx = BackgroundFX.ParseMode(s.BackgroundFx);
        FxOff.IsChecked = fx == FxMode.Off;
        FxTriangles.IsChecked = fx == FxMode.Triangles;
        FxSnow.IsChecked = fx == FxMode.Snow;
        var style = BackgroundCatalog.ParseStyle(s.BackgroundStyle);
        BgStatic.IsChecked = style == BackgroundStyle.Static;
        BgGradient.IsChecked = style == BackgroundStyle.Gradient;
        RefreshBackgroundPreviews(style);
        UpdateBackgroundChecks();
        UpdateAccentChecks();
        s.FxColor = FxColorCatalog.Resolve(s.FxColor);
        UpdateFxChecks();
        RootDirText.Text = "Data directory: " + s.RootDir;
        RefreshJavaStatus();
        _suppressAutoSave = false;
    }

    private void JavaBrowse_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "javaw.exe|javaw.exe|java.exe|java.exe|All files|*.*",
            Title = "Pick javaw.exe",
        };
        if (ofd.ShowDialog() == true) JavaBox.Text = ofd.FileName;
    }

    private void JavaAuto_Click(object sender, RoutedEventArgs e) => JavaBox.Text = "";
}
