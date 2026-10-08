using System.Numerics;
using Equator.Core.Engine;
using Microsoft.Graphics.Canvas;
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

/// <summary>Shaded 3D surface z = f(x, y) with painter's-algorithm rendering. Drag to rotate, wheel to zoom.</summary>
public sealed class SurfaceView : Grid
{
    private const int N = 46;
    private readonly PlotSpec _spec;
    private readonly double[,] _z = new double[N + 1, N + 1];
    private readonly double _zmin, _zmax;
    private CanvasControl? _canvas;
    private double _az = -0.95, _el = 0.5, _zoom = 1;
    private Point? _drag;

    public SurfaceView(PlotSpec spec)
    {
        _spec = spec;
        var values = new List<double>();
        for (int i = 0; i <= N; i++)
            for (int j = 0; j <= N; j++)
            {
                double x = spec.XMin + (spec.XMax - spec.XMin) * i / N;
                double y = spec.Y0 + (spec.Y1 - spec.Y0) * j / N;
                var v = spec.Surface!(x, y);
                _z[i, j] = v;
                if (double.IsFinite(v)) values.Add(v);
            }
        values.Sort();
        if (values.Count == 0) { _zmin = -1; _zmax = 1; }
        else
        {
            _zmin = values[(int)(values.Count * 0.01)];
            _zmax = values[(int)Math.Min(values.Count - 1, values.Count * 0.99)];
            if (_zmax - _zmin < 1e-12) { _zmin -= 1; _zmax += 1; }
        }
        Height = 400;
        MaxWidth = 780;
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            if (_canvas is null) return;
            _canvas.RemoveFromVisualTree();
            Children.Remove(_canvas);
            _canvas = null;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_canvas is not null) return;
        _canvas = new CanvasControl { ClearColor = Color.FromArgb(0, 0, 0, 0) };
        _canvas.Draw += (s, a) => Draw(a.DrawingSession, (float)s.ActualWidth, (float)s.ActualHeight, ActualTheme == ElementTheme.Dark);
        _canvas.PointerPressed += (s, a) => { _drag = a.GetCurrentPoint(_canvas).Position; _canvas.CapturePointer(a.Pointer); ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll); };
        _canvas.PointerMoved += OnMove;
        _canvas.PointerReleased += (s, a) => { _drag = null; _canvas?.ReleasePointerCapture(a.Pointer); ProtectedCursor = null; };
        _canvas.PointerWheelChanged += (s, a) =>
        {
            _zoom = Math.Clamp(_zoom * Math.Pow(1.0012, a.GetCurrentPoint(_canvas).Properties.MouseWheelDelta), 0.3, 5);
            _canvas?.Invalidate();
            a.Handled = true;
        };
        Children.Add(_canvas);
        ActualThemeChanged += (_, _) => _canvas?.Invalidate();
    }

    private void OnMove(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } start || _canvas is null) return;
        var p = e.GetCurrentPoint(_canvas).Position;
        _az += (p.X - start.X) * 0.01;
        _el = Math.Clamp(_el + (p.Y - start.Y) * 0.01, -1.45, 1.45);
        _drag = p;
        _canvas.Invalidate();
    }

    private static Color Colormap(double t)
    {
        // Perceptually ordered blue -> teal -> yellow ramp.
        t = Math.Clamp(t, 0, 1);
        (double r, double g, double b)[] stops = [(0.18, 0.20, 0.55), (0.13, 0.45, 0.70), (0.15, 0.65, 0.60), (0.55, 0.80, 0.35), (0.98, 0.88, 0.25)];
        double pos = t * (stops.Length - 1);
        int i = Math.Min((int)pos, stops.Length - 2);
        double f = pos - i;
        var (r0, g0, b0) = stops[i];
        var (r1, g1, b1) = stops[i + 1];
        return Color.FromArgb(255, (byte)(255 * (r0 + (r1 - r0) * f)), (byte)(255 * (g0 + (g1 - g0) * f)), (byte)(255 * (b0 + (b1 - b0) * f)));
    }

    public void Draw(CanvasDrawingSession ds, float w, float h, bool dark)
    {
        var panel = dark ? Color.FromArgb(255, 36, 36, 38) : Color.FromArgb(255, 252, 252, 252);
        var fg = dark ? Color.FromArgb(255, 230, 230, 230) : Color.FromArgb(255, 40, 40, 40);
        var edge = dark ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(50, 0, 0, 0);
        var boxColor = dark ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(80, 0, 0, 0);
        ds.FillRoundedRectangle(0, 0, w, h, 6, 6, panel);

        double ca = Math.Cos(_az), sa = Math.Sin(_az), ce = Math.Cos(_el), se = Math.Sin(_el);
        double scale = Math.Min(w, h) * 0.33 * _zoom;
        double cx = w / 2, cy = h / 2 + h * 0.04;

        // Model coordinates: x, y in [-1, 1], z in [-0.75, 0.75].
        (Vector2 P, double Depth) Project(double X, double Y, double Z)
        {
            double xr = X * ca - Y * sa;
            double yr = X * sa + Y * ca;
            double depth = yr * ce - Z * se;
            double sy = yr * se + Z * ce;
            return (new Vector2((float)(cx + scale * xr), (float)(cy - scale * sy)), depth);
        }
        double ZN(double z) => (Math.Clamp(z, _zmin, _zmax) - _zmin) / (_zmax - _zmin) * 1.5 - 0.75;

        // Bounding box (drawn first, so the surface covers the hidden edges).
        double[] c = [-1, 1];
        foreach (var a in c)
            foreach (var b in c)
            {
                ds.DrawLine(Project(-1, a, b * 0.75).P, Project(1, a, b * 0.75).P, boxColor, 1);
                ds.DrawLine(Project(a, -1, b * 0.75).P, Project(a, 1, b * 0.75).P, boxColor, 1);
                ds.DrawLine(Project(a, b, -0.75).P, Project(a, b, 0.75).P, boxColor, 1);
            }

        var quads = new List<(Vector2[] Pts, double Depth, Color Fill)>();
        var light = Vector3.Normalize(new Vector3(-0.4f, -0.5f, 1f));
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                double z00 = _z[i, j], z10 = _z[i + 1, j], z11 = _z[i + 1, j + 1], z01 = _z[i, j + 1];
                if (!double.IsFinite(z00) || !double.IsFinite(z10) || !double.IsFinite(z11) || !double.IsFinite(z01)) continue;
                double x0 = -1 + 2.0 * i / N, x1 = -1 + 2.0 * (i + 1) / N, y0 = -1 + 2.0 * j / N, y1 = -1 + 2.0 * (j + 1) / N;
                var p0 = Project(x0, y0, ZN(z00)); var p1 = Project(x1, y0, ZN(z10));
                var p2 = Project(x1, y1, ZN(z11)); var p3 = Project(x0, y1, ZN(z01));
                // Lambert shading from the surface normal in model space.
                var n = Vector3.Normalize(Vector3.Cross(
                    new Vector3((float)(x1 - x0), (float)(y1 - y0), (float)(ZN(z11) - ZN(z00))),
                    new Vector3((float)(x0 - x1), (float)(y1 - y0), (float)(ZN(z01) - ZN(z10)))));
                double shade = 0.55 + 0.45 * Math.Abs(Vector3.Dot(n, light));
                var baseColor = Colormap((((z00 + z10 + z11 + z01) / 4) - _zmin) / (_zmax - _zmin));
                var fill = Color.FromArgb(255, (byte)(baseColor.R * shade), (byte)(baseColor.G * shade), (byte)(baseColor.B * shade));
                quads.Add(([p0.P, p1.P, p2.P, p3.P], (p0.Depth + p1.Depth + p2.Depth + p3.Depth) / 4, fill));
            }
        foreach (var q in quads.OrderByDescending(q => q.Depth))
        {
            using var geom = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePolygon(ds, q.Pts);
            ds.FillGeometry(geom, q.Fill);
            ds.DrawGeometry(geom, edge, 0.6f);
        }

        using var format = new CanvasTextFormat { FontFamily = "Cambria Math", FontSize = 14, FontStyle = Windows.UI.Text.FontStyle.Italic };
        ds.DrawText(_spec.XLabel, Project(1.18, -1, -0.75).P, fg, format);
        ds.DrawText(_spec.YLabel, Project(-1, 1.18, -0.75).P, fg, format);
        ds.DrawText(_spec.ZLabel, Project(-1, -1, 0.92).P, fg, format);

        using var small = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 11 };
        ds.DrawText($"z ∈ [{PlotView.Format(_zmin, (_zmax - _zmin) / 100)}, {PlotView.Format(_zmax, (_zmax - _zmin) / 100)}]   x ∈ [{PlotView.Format(_spec.XMin, 0.01)}, {PlotView.Format(_spec.XMax, 0.01)}]   y ∈ [{PlotView.Format(_spec.Y0, 0.01)}, {PlotView.Format(_spec.Y1, 0.01)}]", 10, h - 22, fg, small);
        using var title = new CanvasTextFormat { FontFamily = "Cascadia Mono, Consolas", FontSize = 12 };
        ds.DrawText("z = " + _spec.Title, 10, 8, fg, title);
    }

    public async Task<IRandomAccessStream> RenderPngAsync(bool dark)
    {
        var device = CanvasDevice.GetSharedDevice();
        float w = (float)Math.Max(ActualWidth, 400), h = (float)Math.Max(ActualHeight, 300);
        using var target = new CanvasRenderTarget(device, w, h, 192);
        using (var ds = target.CreateDrawingSession()) Draw(ds, w, h, dark);
        var stream = new InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        return stream;
    }
}
