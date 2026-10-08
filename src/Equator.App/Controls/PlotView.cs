using System.Globalization;
using System.Numerics;
using Equator.Core.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;

namespace Equator.App.Controls;

/// <summary>Interactive 2D plot: drag to pan, wheel to zoom (Shift/Ctrl for a single axis), double-click to reset.</summary>
public sealed class PlotView : Grid
{
    public static readonly Color[] Palette =
    [
        Color.FromArgb(255, 0x2F, 0x6F, 0xDB), // blue
        Color.FromArgb(255, 0xE0, 0x6C, 0x1F), // orange
        Color.FromArgb(255, 0x2E, 0x9E, 0x5B), // green
        Color.FromArgb(255, 0xC8, 0x3A, 0x4E), // red
        Color.FromArgb(255, 0x8A, 0x52, 0xC9), // purple
        Color.FromArgb(255, 0x1F, 0xA3, 0xB3), // teal
    ];

    private readonly PlotSpec _spec;
    private CanvasControl? _canvas;
    private double _x0, _x1, _y0, _y1;
    private readonly (double, double, double, double) _home;
    private Point? _dragStart;
    private (double, double, double, double) _dragView;
    private Point? _hover;

    private const float Left = 58, Bottom = 34, Top = 14, Right = 16;

    public PlotView(PlotSpec spec)
    {
        _spec = spec;
        _x0 = spec.XMin; _x1 = spec.XMax;
        (_y0, _y1) = spec.YMin is { } ymin && spec.YMax is { } ymax ? (ymin, ymax) : AutoYRange();
        if (spec.EqualAspect) MatchAspectLater = true;
        _home = (_x0, _x1, _y0, _y1);
        Height = 340;
        MaxWidth = 780;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        CornerRadius = new CornerRadius(6);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private bool MatchAspectLater { get; set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_canvas is not null) return;
        _canvas = new CanvasControl { ClearColor = Color.FromArgb(0, 0, 0, 0) };
        _canvas.Draw += OnDraw;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerExited += (_, _) => { _hover = null; _canvas?.Invalidate(); };
        _canvas.PointerWheelChanged += OnWheel;
        _canvas.DoubleTapped += (_, _) => { (_x0, _x1, _y0, _y1) = _home; _canvas?.Invalidate(); };
        _canvas.SizeChanged += (_, _) => { if (MatchAspectLater) FitAspect(); _canvas?.Invalidate(); };
        Children.Add(_canvas);
        ActualThemeChanged += (_, _) => _canvas?.Invalidate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Win2D guidance: release the swap chain when the control leaves the tree.
        if (_canvas is null) return;
        _canvas.RemoveFromVisualTree();
        Children.Remove(_canvas);
        _canvas = null;
    }

    /// <summary>For parametric/polar plots: make one unit equal in x and y.</summary>
    private void FitAspect()
    {
        if (_canvas is null || _canvas.ActualWidth < 50) return;
        MatchAspectLater = false;
        double pw = _canvas.ActualWidth - Left - Right, ph = _canvas.ActualHeight - Top - Bottom;
        double cx = (_x0 + _x1) / 2, cy = (_y0 + _y1) / 2;
        double unit = Math.Max((_x1 - _x0) / pw, (_y1 - _y0) / ph);
        _x0 = cx - unit * pw / 2; _x1 = cx + unit * pw / 2;
        _y0 = cy - unit * ph / 2; _y1 = cy + unit * ph / 2;
    }

    private (double, double) AutoYRange()
    {
        var ys = new List<double>();
        foreach (var s in _spec.Series)
        {
            if (s.Function is { } f)
            {
                for (int i = 0; i <= 600; i++)
                {
                    var v = f(_x0 + (_x1 - _x0) * i / 600.0);
                    if (double.IsFinite(v)) ys.Add(v);
                }
            }
        }
        if (ys.Count == 0) return (-1, 1);
        ys.Sort();
        double min = ys[0], max = ys[^1];
        double q02 = ys[(int)(ys.Count * 0.02)], q98 = ys[(int)Math.Min(ys.Count - 1, ys.Count * 0.98)];
        if (max - min > 6 * (q98 - q02) && q98 > q02)
        {
            // Asymptotes: focus on the bulk of the curve.
            double pad = (q98 - q02) * 0.4;
            return (q02 - pad, q98 + pad);
        }
        if (max - min < 1e-12) return (min - 1, max + 1);
        double p = (max - min) * 0.06;
        return (min - p, max + p);
    }

    // ---- interaction --------------------------------------------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragStart = e.GetCurrentPoint(_canvas).Position;
        _dragView = (_x0, _x1, _y0, _y1);
        _canvas!.CapturePointer(e.Pointer);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas).Position;
        _hover = p;
        if (_dragStart is { } start && _canvas is not null)
        {
            double pw = _canvas.ActualWidth - Left - Right, ph = _canvas.ActualHeight - Top - Bottom;
            var (ax0, ax1, ay0, ay1) = _dragView;
            double dx = (p.X - start.X) / pw * (ax1 - ax0);
            double dy = (p.Y - start.Y) / ph * (ay1 - ay0);
            (_x0, _x1, _y0, _y1) = (ax0 - dx, ax1 - dx, ay0 + dy, ay1 + dy);
        }
        _canvas?.Invalidate();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragStart = null;
        _canvas?.ReleasePointerCapture(e.Pointer);
        ProtectedCursor = null;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_canvas is null) return;
        var pt = e.GetCurrentPoint(_canvas);
        double factor = Math.Pow(1.0015, -pt.Properties.MouseWheelDelta);
        var (px, py) = ToWorld(pt.Position);
        var mods = e.KeyModifiers;
        bool onlyX = mods.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
        bool onlyY = mods.HasFlag(Windows.System.VirtualKeyModifiers.Control);
        if (!onlyY) { _x0 = px + (_x0 - px) * factor; _x1 = px + (_x1 - px) * factor; }
        if (!onlyX) { _y0 = py + (_y0 - py) * factor; _y1 = py + (_y1 - py) * factor; }
        _canvas.Invalidate();
        e.Handled = true;
    }

    private (double, double) ToWorld(Point p)
    {
        double pw = _canvas!.ActualWidth - Left - Right, ph = _canvas.ActualHeight - Top - Bottom;
        return (_x0 + (p.X - Left) / pw * (_x1 - _x0), _y1 - (p.Y - Top) / ph * (_y1 - _y0));
    }

    // ---- drawing ------------------------------------------------------------------------------------

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        Draw(args.DrawingSession, (float)sender.ActualWidth, (float)sender.ActualHeight, ActualTheme == ElementTheme.Dark, _hover);
    }

    public void Draw(CanvasDrawingSession ds, float w, float h, bool dark, Point? hover)
    {
        var fg = dark ? Color.FromArgb(255, 230, 230, 230) : Color.FromArgb(255, 40, 40, 40);
        var grid = dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0);
        var axis = dark ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0);
        var panel = dark ? Color.FromArgb(255, 36, 36, 38) : Color.FromArgb(255, 252, 252, 252);
        var frame = dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(55, 0, 0, 0);

        ds.FillRoundedRectangle(0, 0, w, h, 6, 6, panel);
        var plot = new Rect(Left, Top, Math.Max(10, w - Left - Right), Math.Max(10, h - Top - Bottom));
        float X(double v) => (float)(plot.X + (v - _x0) / (_x1 - _x0) * plot.Width);
        float Y(double v) => (float)(plot.Y + (_y1 - v) / (_y1 - _y0) * plot.Height);

        using var labelFormat = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 11, WordWrapping = CanvasWordWrapping.NoWrap };

        // Grid and tick labels.
        var xt = Ticks(_x0, _x1, Math.Max(2, (int)(plot.Width / 85)));
        var yt = Ticks(_y0, _y1, Math.Max(2, (int)(plot.Height / 50)));
        foreach (var t in xt.Values)
        {
            float px = X(t);
            ds.DrawLine(px, (float)plot.Top, px, (float)plot.Bottom, grid, 1);
            labelFormat.HorizontalAlignment = CanvasHorizontalAlignment.Center;
            ds.DrawText(Format(t, xt.Step), px, (float)plot.Bottom + 5, fg, labelFormat);
        }
        foreach (var t in yt.Values)
        {
            float py = Y(t);
            ds.DrawLine((float)plot.Left, py, (float)plot.Right, py, grid, 1);
            labelFormat.HorizontalAlignment = CanvasHorizontalAlignment.Right;
            ds.DrawText(Format(t, yt.Step), (float)plot.Left - 6, py - 8, fg, labelFormat);
        }

        using (ds.CreateLayer(1f, plot))
        {
            if (_x0 < 0 && _x1 > 0) ds.DrawLine(X(0), (float)plot.Top, X(0), (float)plot.Bottom, axis, 1.2f);
            if (_y0 < 0 && _y1 > 0) ds.DrawLine((float)plot.Left, Y(0), (float)plot.Right, Y(0), axis, 1.2f);

            for (int i = 0; i < _spec.Series.Count; i++)
                DrawSeries(ds, _spec.Series[i], Palette[i % Palette.Length], plot, X, Y);
        }
        ds.DrawRectangle(plot, frame, 1);

        // Axis captions.
        labelFormat.HorizontalAlignment = CanvasHorizontalAlignment.Right;
        if (!string.IsNullOrEmpty(_spec.XLabel))
        {
            using var italic = new CanvasTextFormat { FontFamily = "Cambria Math", FontSize = 13, FontStyle = Windows.UI.Text.FontStyle.Italic, HorizontalAlignment = CanvasHorizontalAlignment.Right };
            ds.DrawText(_spec.XLabel, (float)plot.Right - 4, (float)plot.Bottom - 20, fg, italic);
        }

        if (_spec.Series.Count > 1 || _spec.Series.Any(s => s.Parametric is not null || s.Points is not null)) DrawLegend(ds, plot, fg, panel, frame);

        if (hover is { } hp && plot.Contains(hp))
        {
            var (wx, wy) = (_x0 + (hp.X - plot.X) / plot.Width * (_x1 - _x0), _y1 - (hp.Y - plot.Y) / plot.Height * (_y1 - _y0));
            var text = $"({Format(wx, (_x1 - _x0) / 1000)}, {Format(wy, (_y1 - _y0) / 1000)})";
            using var f = new CanvasTextFormat { FontFamily = "Cascadia Mono, Consolas", FontSize = 11, HorizontalAlignment = CanvasHorizontalAlignment.Right };
            ds.DrawText(text, (float)plot.Right - 6, (float)plot.Top + 4, fg, f);
            ds.DrawLine((float)hp.X, (float)plot.Top, (float)hp.X, (float)plot.Bottom, grid, 1);
            // Mark the function values under the cursor.
            for (int i = 0; i < _spec.Series.Count; i++)
            {
                if (_spec.Series[i].Function is not { } fn) continue;
                var v = fn(wx);
                if (double.IsFinite(v) && v > _y0 && v < _y1)
                    ds.FillCircle(X(wx), Y(v), 3.5f, Palette[i % Palette.Length]);
            }
        }
    }

    private void DrawSeries(CanvasDrawingSession ds, PlotSeries s, Color color, Rect plot, Func<double, float> X, Func<double, float> Y)
    {
        using var style = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round, StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        if (s.Points is { } pts)
        {
            if (s.JoinPoints && pts.Count > 1)
            {
                for (int i = 1; i < pts.Count; i++)
                    ds.DrawLine(X(pts[i - 1].X), Y(pts[i - 1].Y), X(pts[i].X), Y(pts[i].Y), color, 1.8f, style);
            }
            foreach (var (px, py) in pts) ds.FillCircle(X(px), Y(py), 3.5f, color);
            return;
        }

        var segments = new List<List<Vector2>>();
        var current = new List<Vector2>();
        double viewH = _y1 - _y0;

        void Flush()
        {
            if (current.Count > 1) segments.Add(current);
            current = [];
        }

        if (s.Function is { } f)
        {
            int n = (int)Math.Max(400, plot.Width * 2);
            double prevY = double.NaN;
            for (int i = 0; i <= n; i++)
            {
                double x = _x0 + (_x1 - _x0) * i / n;
                double y = f(x);
                if (!double.IsFinite(y)) { Flush(); prevY = double.NaN; continue; }
                // Break at vertical asymptotes: a huge jump between neighbouring samples.
                if (!double.IsNaN(prevY) && Math.Abs(y - prevY) > viewH * 4 && Math.Sign(y) != Math.Sign(prevY)) Flush();
                current.Add(new Vector2(X(x), Clamp(Y(y), plot)));
                prevY = y;
            }
        }
        else if (s.Parametric is { } p)
        {
            int n = 3000;
            for (int i = 0; i <= n; i++)
            {
                var (x, y) = p(s.TMin + (s.TMax - s.TMin) * i / n);
                if (!double.IsFinite(x) || !double.IsFinite(y)) { Flush(); continue; }
                current.Add(new Vector2(Clamp(X(x), plot, true), Clamp(Y(y), plot)));
            }
        }
        Flush();

        foreach (var seg in segments)
        {
            using var pb = new CanvasPathBuilder(ds);
            pb.BeginFigure(seg[0]);
            for (int i = 1; i < seg.Count; i++) pb.AddLine(seg[i]);
            pb.EndFigure(CanvasFigureLoop.Open);
            using var geom = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(geom, color, 2.2f, style);
        }
    }

    /// <summary>Keeps far off-screen points finite so Direct2D draws steep segments correctly.</summary>
    private static float Clamp(float v, Rect plot, bool horizontal = false)
    {
        double lo = horizontal ? plot.Left : plot.Top, hi = horizontal ? plot.Right : plot.Bottom;
        double span = hi - lo;
        return (float)Math.Clamp(v, lo - 10 * span, hi + 10 * span);
    }

    private void DrawLegend(CanvasDrawingSession ds, Rect plot, Color fg, Color panel, Color frame)
    {
        using var format = new CanvasTextFormat { FontFamily = "Cascadia Mono, Consolas", FontSize = 11.5f, WordWrapping = CanvasWordWrapping.NoWrap };
        var labels = _spec.Series.Select(s => s.Label.Length > 40 ? s.Label[..39] + "…" : s.Label).ToList();
        float lineH = 17, width = 0;
        foreach (var l in labels)
        {
            using var layout = new CanvasTextLayout(ds, l, format, 1000, 100);
            width = Math.Max(width, (float)layout.LayoutBounds.Width);
        }
        float boxW = width + 34, boxH = labels.Count * lineH + 8;
        float x = (float)plot.Left + 10, y = (float)plot.Top + 8;
        ds.FillRoundedRectangle(x, y, boxW, boxH, 4, 4, Color.FromArgb(220, panel.R, panel.G, panel.B));
        ds.DrawRoundedRectangle(x, y, boxW, boxH, 4, 4, frame, 1);
        for (int i = 0; i < labels.Count; i++)
        {
            float ly = y + 4 + i * lineH + lineH / 2;
            ds.DrawLine(x + 6, ly, x + 22, ly, Palette[i % Palette.Length], 2.5f);
            ds.DrawText(labels[i], x + 28, ly - 8, fg, format);
        }
    }

    public static (List<double> Values, double Step) Ticks(double lo, double hi, int count)
    {
        double range = hi - lo;
        if (!(range > 0) || !double.IsFinite(range)) return ([], 1);
        double raw = range / count;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
        var values = new List<double>();
        for (double v = Math.Ceiling(lo / step) * step; v <= hi + step * 1e-9; v += step)
            values.Add(Math.Abs(v) < step * 1e-9 ? 0 : v);
        return (values, step);
    }

    public static string Format(double v, double step)
    {
        if (v == 0) return "0";
        double a = Math.Abs(v);
        if (a >= 1e6 || a < 1e-4) return v.ToString("0.##E+0", CultureInfo.InvariantCulture);
        int decimals = Math.Max(0, (int)Math.Ceiling(-Math.Log10(Math.Abs(step)) + 0.0001));
        return Math.Round(v, Math.Min(decimals, 10)).ToString("F" + Math.Min(decimals, 10), CultureInfo.InvariantCulture);
    }

    public async Task<IRandomAccessStream> RenderPngAsync(bool dark)
    {
        var device = CanvasDevice.GetSharedDevice();
        float w = (float)Math.Max(ActualWidth, 400), h = (float)Math.Max(ActualHeight, 300);
        using var target = new CanvasRenderTarget(device, w, h, 192);
        using (var ds = target.CreateDrawingSession()) Draw(ds, w, h, dark, null);
        var stream = new InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        return stream;
    }
}
