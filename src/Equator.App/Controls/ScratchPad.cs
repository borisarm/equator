using System.Numerics;
using Equator.Recognition;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;

namespace Equator.App.Controls;

/// <summary>A marker drawn beside a recognised line: its bounds (page pixels) and a verdict colour/label.</summary>
public sealed record LineMarker(PixelRect Bounds, Color Color, string Label);

/// <summary>
/// The writing surface of the check window: shows a loaded page (photo or scan) or collects pen/mouse/touch
/// ink on a paper-like background, and draws verdict markers over the recognised lines. The pen's eraser
/// end or the right mouse button erases whole strokes.
/// </summary>
public sealed class ScratchPad : Grid
{
    private const float InkWidth = 3.2f;
    private const float RenderScale = 2f; // ink is rendered at twice the DIP resolution for recognition

    private readonly CanvasControl _canvas = new();
    private readonly List<List<Vector2>> _strokes = [];
    private List<Vector2>? _current;
    private bool _erasing;
    private CanvasBitmap? _page;
    private byte[]? _pageBgra;
    private int _pageWidth, _pageHeight;
    private IReadOnlyList<LineMarker> _markers = [];

    public event EventHandler? InkChanged;

    public ScratchPad()
    {
        Children.Add(_canvas);
        _canvas.Draw += OnDraw;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCanceled += OnPointerReleased;
        _canvas.PointerCaptureLost += (_, _) => FinishStroke();
        Unloaded += (_, _) => { _canvas.RemoveFromVisualTree(); };
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    }

    public bool IsPageMode => _page is not null;
    public bool HasInk => _strokes.Count > 0;
    public bool HasContent => IsPageMode || HasInk;

    // ---- page mode -----------------------------------------------------------------------------------

    public async Task SetPageAsync(byte[] bgra, int width, int height)
    {
        _strokes.Clear();
        _pageBgra = bgra;
        _pageWidth = width;
        _pageHeight = height;
        _page = CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), bgra, width, height, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
        _markers = [];
        await Task.Yield();
        _canvas.Invalidate();
    }

    public void Clear()
    {
        _page = null;
        _pageBgra = null;
        _strokes.Clear();
        _markers = [];
        _canvas.Invalidate();
        InkChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (_strokes.Count == 0) return;
        _strokes.RemoveAt(_strokes.Count - 1);
        _markers = [];
        _canvas.Invalidate();
        InkChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetMarkers(IReadOnlyList<LineMarker> markers)
    {
        _markers = markers;
        _canvas.Invalidate();
    }

    /// <summary>The content to recognise: the page, or the ink rendered black on white.</summary>
    public (GrayImage Image, bool Clean)? Capture()
    {
        if (_pageBgra is not null) return (GrayImage.FromBgra(_pageBgra, _pageWidth, _pageHeight), false);
        if (_strokes.Count == 0) return null;
        var bounds = InkBoundsDip();
        int w = (int)Math.Ceiling((bounds.Right + 20) * RenderScale), h = (int)Math.Ceiling((bounds.Bottom + 20) * RenderScale);
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), w, h, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Colors.White);
            ds.Transform = Matrix3x2.CreateScale(RenderScale);
            foreach (var s in _strokes) DrawStroke(ds, s, Colors.Black);
        }
        var bytes = target.GetPixelBytes();
        return (GrayImage.FromBgra(bytes, w, h), true);
    }

    private Rect InkBoundsDip()
    {
        float maxX = 0, maxY = 0;
        foreach (var s in _strokes)
            foreach (var p in s) { maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
        return new Rect(0, 0, maxX, maxY);
    }

    // ---- drawing --------------------------------------------------------------------------------------

    private static readonly CanvasStrokeStyle Round = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };

    private static void DrawStroke(CanvasDrawingSession ds, List<Vector2> s, Color color)
    {
        if (s.Count == 1) { ds.FillCircle(s[0], InkWidth / 2, color); return; }
        using var builder = new CanvasPathBuilder(ds);
        builder.BeginFigure(s[0]);
        for (int i = 1; i < s.Count; i++) builder.AddLine(s[i]);
        builder.EndFigure(CanvasFigureLoop.Open);
        using var geometry = CanvasGeometry.CreatePath(builder);
        ds.DrawGeometry(geometry, color, InkWidth, Round);
    }

    /// <summary>Page pixels → canvas DIPs for the current layout.</summary>
    private (float Scale, Vector2 Offset) PageTransform()
    {
        if (_page is null) return (1 / RenderScale, Vector2.Zero);
        float w = (float)_canvas.ActualWidth, h = (float)_canvas.ActualHeight;
        float scale = Math.Min(w / _pageWidth, h / _pageHeight);
        scale = Math.Min(scale, 2);
        var offset = new Vector2((w - _pageWidth * scale) / 2, (h - _pageHeight * scale) / 2);
        return (scale, offset);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        bool dark = ActualTheme == ElementTheme.Dark;
        float w = (float)sender.ActualWidth, h = (float)sender.ActualHeight;
        // Paper: always light, so ink stays black and readable.
        ds.FillRectangle(0, 0, w, h, dark ? Color.FromArgb(255, 238, 238, 232) : Color.FromArgb(255, 253, 253, 250));
        var (scale, offset) = PageTransform();
        if (_page is not null)
        {
            ds.DrawImage(_page, new Rect(offset.X, offset.Y, _pageWidth * scale, _pageHeight * scale));
        }
        else
        {
            // Faint ruled lines as a writing guide.
            for (float y = 64; y < h; y += 64) ds.DrawLine(16, y, w - 16, y, Color.FromArgb(40, 70, 110, 180), 1);
            if (_strokes.Count == 0 && _current is null)
            {
                using var hint = new CanvasTextFormat { FontSize = 15, HorizontalAlignment = CanvasHorizontalAlignment.Center };
                ds.DrawText("Write one step per line, or open a photo of the work", new Vector2(w / 2, 24), Color.FromArgb(150, 90, 90, 90), hint);
            }
            foreach (var s in _strokes) DrawStroke(ds, s, Color.FromArgb(255, 20, 30, 60));
            if (_current is not null) DrawStroke(ds, _current, Color.FromArgb(255, 20, 30, 60));
        }

        using var label = new CanvasTextFormat { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = CanvasVerticalAlignment.Center };
        foreach (var m in _markers)
        {
            var r = new Rect(offset.X + m.Bounds.Left * scale - 6, offset.Y + m.Bounds.Top * scale - 4, m.Bounds.Width * scale + 12, m.Bounds.Height * scale + 8);
            ds.DrawRoundedRectangle(r, 6, 6, Color.FromArgb(170, m.Color.R, m.Color.G, m.Color.B), 2);
            var badge = new Rect(r.Right + 4, r.Top + r.Height / 2 - 11, 22, 22);
            ds.FillEllipse(new Vector2((float)(badge.X + 11), (float)(badge.Y + 11)), 11, 11, m.Color);
            using var center = new CanvasTextFormat { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
            ds.DrawText(m.Label, badge, Colors.White, center);
        }
    }

    // ---- input -----------------------------------------------------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_page is not null) return;
        var point = e.GetCurrentPoint(_canvas);
        _erasing = point.Properties.IsEraser || point.Properties.IsRightButtonPressed;
        _canvas.CapturePointer(e.Pointer);
        if (_erasing) { EraseAt(point.Position.ToVector2()); return; }
        _current = [point.Position.ToVector2()];
        _markers = [];
        _canvas.Invalidate();
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_page is not null) return;
        var point = e.GetCurrentPoint(_canvas);
        if (_erasing && point.IsInContact) { EraseAt(point.Position.ToVector2()); return; }
        if (_current is null) return;
        foreach (var p in e.GetIntermediatePoints(_canvas).Reverse())
        {
            var v = p.Position.ToVector2();
            if (Vector2.Distance(v, _current[^1]) >= 0.8f) _current.Add(v);
        }
        _canvas.Invalidate();
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _canvas.ReleasePointerCapture(e.Pointer);
        FinishStroke();
    }

    private void FinishStroke()
    {
        if (_erasing) { _erasing = false; return; }
        if (_current is null) return;
        _strokes.Add(_current);
        _current = null;
        _canvas.Invalidate();
        InkChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EraseAt(Vector2 p)
    {
        int removed = _strokes.RemoveAll(s => s.Any(q => Vector2.Distance(q, p) < 8));
        if (removed > 0)
        {
            _markers = [];
            _canvas.Invalidate();
            InkChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
