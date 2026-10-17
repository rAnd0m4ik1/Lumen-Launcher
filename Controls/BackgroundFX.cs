using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using CraftDeck.Services;

namespace CraftDeck.Controls;

public enum FxMode
{
    Off,
    Triangles,
    Snow,
}

public sealed class BackgroundFX : FrameworkElement
{
    private sealed class Mote
    {
        public double X, Y, VX, VY, R, Phase;
    }

    private sealed class Flake
    {
        public double X, Y, R, Fall, Sway, Swing, Phase;
    }

    private const int MoteCount = 44;
    private const double LinkRange = 0.125;
    private const double PointerRange = 0.2;
    private const double FrameTime = 1.0 / 30;

    private static readonly int[] LayerCount = { 300, 130, 36 };
    private static readonly double[] LayerFall = { 11, 26, 58 };
    private static readonly double[] LayerR = { 0.55, 0.95, 2.3 };
    private static readonly double[] LayerSway = { 3, 7, 12 };
    private static readonly double[] LayerWind = { 8, 17, 28 };
    private const double Streak = 0.015;

    private readonly List<Flake>[] _layers = { new(), new(), new() };
    private readonly Brush[] _snow = new Brush[3];
    private double _time;

    private double _acc;

    private readonly Random _rng = new(0x1FDB62);
    private readonly List<Mote> _motes = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private Brush _moteBrush = Brushes.Transparent;
    private Pen _linkPen = new(Brushes.Transparent, 1);
    private Point _pointer, _eased;
    private bool _pointerActive;
    private bool _looping;
    private bool _gridDirty = true;
    private double _last;
    private FxMode _mode = FxMode.Triangles;

    public BackgroundFX()
    {
        IsHitTestVisible = false;
        for (var i = 0; i < MoteCount; i++)
        {
            _motes.Add(new Mote
            {
                X = _rng.NextDouble(),
                Y = _rng.NextDouble(),
                VX = (_rng.NextDouble() - 0.5) * 0.02,
                VY = -0.006 - _rng.NextDouble() * 0.016,
                R = 1.1 + _rng.NextDouble() * 1.9,
                Phase = _rng.NextDouble() * Math.PI * 2,
            });
        }
        ThemeService.ThemeChanged += RebuildBrushes;
        IsVisibleChanged += (_, _) => UpdateLoop();
        RebuildBrushes();
    }

    public FxMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            Visibility = value == FxMode.Off ? Visibility.Hidden : Visibility.Visible;
            _gridDirty = true;
            UpdateLoop();
            InvalidateVisual();
        }
    }

    public static FxMode ParseMode(string? id) => id?.ToLowerInvariant() switch
    {
        "off" => FxMode.Off,
        "snow" or "magnet" or "liquid" or "sparks" => FxMode.Snow,
        _ => FxMode.Triangles,
    };

    public void SetPointer(Point px, bool inside)
    {
        _pointer = px;
        _pointerActive = inside;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        _gridDirty = true;
        UpdateLoop();
    }

    private void RebuildBrushes()
    {
        var p = ThemeService.Current;
        var c = ThemeService.TryParse(AppServices.Settings.FxColor, out var fx) ? fx : Colors.White;

        _moteBrush = Frozen(new SolidColorBrush(p.Light
            ? Alpha(Mix(c, Colors.Black, 0.78), 0x8C)
            : Alpha(Mix(c, Colors.White, 0.12), 0xE0)));
        _linkPen = Frozen(new Pen(new SolidColorBrush(Alpha(c, p.Light ? (byte)0x1E : (byte)0x28)), 0.7));

        var flake = p.Light ? Mix(c, Colors.Black, 0.8) : Mix(c, Colors.White, 0.1);
        _snow[0] = Frozen(new SolidColorBrush(Alpha(flake, 0x4A)));
        _snow[1] = Frozen(new SolidColorBrush(Alpha(flake, 0x82)));
        var soft = new RadialGradientBrush(Alpha(flake, 0x9A), Alpha(flake, 0x00))
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        soft.Freeze();
        _snow[2] = soft;

        InvalidateVisual();
    }

    public void RefreshColors() => RebuildBrushes();

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static SolidColorBrush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    private void UpdateLoop()
    {
        var want = _mode != FxMode.Off && IsVisible && ActualWidth > 1 && ActualHeight > 1;
        if (want && !_looping)
        {
            _looping = true;
            _last = _clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += Tick;
        }
        else if (!want && _looping)
        {
            _looping = false;
            CompositionTarget.Rendering -= Tick;
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _last;
        _last = now;
        _acc += dt;
        if (_acc < FrameTime) return;
        var step = Math.Min(0.05, _acc);
        _acc = 0;
        if (_mode == FxMode.Snow) StepSnow(step);
        else Step(step);
        InvalidateVisual();
    }

    private void Step(double dt)
    {
        _time += dt;
        var d = Math.Min(ActualWidth, ActualHeight);
        var target = _pointerActive ? _pointer : new Point(ActualWidth * 0.5, ActualHeight * 0.42);
        var ease = 1 - Math.Pow(0.0025, dt);
        _eased.X += (target.X - _eased.X) * ease;
        _eased.Y += (target.Y - _eased.Y) * ease;
        var pull = _pointerActive ? 1.0 : 0.0;

        foreach (var m in _motes)
        {
            m.X += m.VX * dt;
            m.Y += m.VY * dt;
            if (m.X < -0.02) m.X += 1.04;
            if (m.X > 1.02) m.X -= 1.04;
            if (m.Y < -0.02) m.Y += 1.04;
            if (m.Y > 1.02) m.Y -= 1.04;
            var ox = (m.X * ActualWidth - _eased.X) / d;
            var oy = (m.Y * ActualHeight - _eased.Y) / d;
            var dist = Math.Sqrt(ox * ox + oy * oy);
            if (dist is <= 0.0001 or >= PointerRange) continue;
            var f = (1 - dist / PointerRange) * 0.09 * pull;
            m.X += ox / dist * f * dt * 8;
            m.Y += oy / dist * f * dt * 8;
        }
    }

    private void EnsureSnow()
    {
        if (!_gridDirty || ActualWidth < 1 || ActualHeight < 1) return;
        _gridDirty = false;
        for (var l = 0; l < 3; l++)
        {
            _layers[l].Clear();
            for (var i = 0; i < LayerCount[l]; i++)
            {
                _layers[l].Add(new Flake
                {
                    X = _rng.NextDouble() * ActualWidth,
                    Y = _rng.NextDouble() * ActualHeight,
                    R = LayerR[l] * (0.75 + _rng.NextDouble() * 0.75),
                    Fall = LayerFall[l] * (0.72 + _rng.NextDouble() * 0.7),
                    Sway = LayerSway[l] * (0.6 + _rng.NextDouble() * 0.9),
                    Swing = 0.25 + _rng.NextDouble() * 0.55,
                    Phase = _rng.NextDouble() * Math.PI * 2,
                });
            }
        }
    }

    private void StepSnow(double dt)
    {
        _time += dt;
        EnsureSnow();
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 1 || h < 1) return;
        var gust = Math.Sin(_time * 0.11) * 0.62 + Math.Sin(_time * 0.037 + 2.1) * 0.38;
        for (var l = 0; l < 3; l++)
        {
            var wind = LayerWind[l] * gust;
            foreach (var f in _layers[l])
            {
                f.Y += f.Fall * dt;
                f.X += (Math.Cos(_time * f.Swing + f.Phase) * f.Sway + wind) * dt;
                if (f.Y > h + f.R * 2)
                {
                    f.Y = -f.R * 2;
                    f.X = _rng.NextDouble() * w;
                }
                else if (f.X < -12) f.X += w + 24;
                else if (f.X > w + 12) f.X -= w + 24;
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (_mode == FxMode.Off || w < 1 || h < 1) return;
        if (_mode == FxMode.Snow)
        {
            foreach (var f in _layers[0])
            {
                var rx = f.R;
                var ry = rx + f.Fall * Streak;
                dc.DrawRectangle(_snow[0], null, new Rect(f.X - rx, f.Y - ry, rx * 2, ry * 2));
            }
            for (var l = 1; l < 3; l++)
            {
                var brush = _snow[l];
                foreach (var f in _layers[l])
                {
                    var ry = f.R + f.Fall * Streak;
                    dc.DrawEllipse(brush, null, new Point(f.X, f.Y), f.R, ry);
                }
            }
            return;
        }
        var d = Math.Min(w, h);

        var links = new StreamGeometry();
        using (var g = links.Open())
        {
            var maxDist = LinkRange * d;
            for (var i = 0; i < _motes.Count; i++)
            {
                var a = _motes[i];
                for (var j = i + 1; j < _motes.Count; j++)
                {
                    var b = _motes[j];
                    var dx = (a.X - b.X) * w;
                    var dy = (a.Y - b.Y) * h;
                    if (dx * dx + dy * dy > maxDist * maxDist) continue;
                    g.BeginFigure(new Point(a.X * w, a.Y * h), false, false);
                    g.LineTo(new Point(b.X * w, b.Y * h), true, false);
                }
                if (!_pointerActive) continue;
                var px = a.X * w - _eased.X;
                var py = a.Y * h - _eased.Y;
                if (px * px + py * py > (PointerRange * d) * (PointerRange * d)) continue;
                g.BeginFigure(new Point(a.X * w, a.Y * h), false, false);
                g.LineTo(_eased, true, false);
            }
        }
        links.Freeze();
        dc.DrawGeometry(null, _linkPen, links);

        foreach (var m in _motes)
        {
            var px = m.X * w;
            var py = m.Y * h;
            var near = Math.Clamp(1 - Math.Sqrt((px - _eased.X) * (px - _eased.X) + (py - _eased.Y) * (py - _eased.Y)) / (PointerRange * d), 0, 1);
            var twinkle = 0.5 + Math.Sin(_time * 1.4 + m.Phase) * 0.22 + near * 0.55;
            var r = m.R * (1 + near * 0.9);
            dc.PushOpacity(Math.Clamp(twinkle, 0.12, 1));
            dc.DrawEllipse(_moteBrush, null, new Point(px, py), r, r);
            dc.Pop();
        }
    }
}
