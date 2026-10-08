using Equator.App.Rendering;
using Equator.Core.Printing;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Streams;
using Windows.UI;

namespace Equator.App.Controls;

/// <summary>Displays a typeset formula. Renders once into a CanvasImageSource (no swap chain per output).</summary>
public sealed class MathView : Grid
{
    private const double Pad = 4;
    private readonly Image _image = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left };
    private LNode? _node;
    private Box? _box;
    private double _fontSize = 19;
    private double _renderedScale;

    public MathView()
    {
        Children.Add(_image);
        HorizontalAlignment = HorizontalAlignment.Left;
        Loaded += (_, _) =>
        {
            Render();
            if (XamlRoot is not null) XamlRoot.Changed += OnXamlRootChanged;
        };
        Unloaded += (_, _) =>
        {
            if (XamlRoot is not null) XamlRoot.Changed -= OnXamlRootChanged;
        };
        ActualThemeChanged += (_, _) => Render();
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (Math.Abs(sender.RasterizationScale - _renderedScale) > 0.01) Render();
    }

    public Box? Box
    {
        get => _box;
        set { _box = value; Relayout(); }
    }

    public double MathFontSize
    {
        get => _fontSize;
        set { _fontSize = value; Relayout(); }
    }

    private void Relayout()
    {
        if (_box is null) { _node = null; _image.Source = null; return; }
        _node = new MathLayoutBuilder(CanvasDevice.GetSharedDevice()).Build(_box, _fontSize);
        _image.Width = Math.Ceiling(_node.W + 2 * Pad);
        _image.Height = Math.Ceiling(_node.H + 2 * Pad);
        Render();
    }

    public static Color ForegroundFor(ElementTheme theme) =>
        theme == ElementTheme.Dark ? Color.FromArgb(255, 238, 238, 238) : Color.FromArgb(255, 26, 26, 26);

    private void Render()
    {
        if (_node is null || XamlRoot is null) return;
        _renderedScale = XamlRoot.RasterizationScale;
        var device = CanvasDevice.GetSharedDevice();
        var source = new CanvasImageSource(device, (float)_image.Width, (float)_image.Height, (float)(96 * _renderedScale), CanvasAlphaMode.Premultiplied);
        using (var ds = source.CreateDrawingSession(MathColors.Transparent))
        {
            _node.Draw(ds, Pad, Pad + _node.A, ForegroundFor(ActualTheme));
        }
        _image.Source = source;
    }

    /// <summary>PNG of the formula on an opaque background, for the clipboard.</summary>
    public async Task<IRandomAccessStream?> RenderPngAsync()
    {
        if (_node is null) return null;
        var device = CanvasDevice.GetSharedDevice();
        float scale = 2;
        using var target = new CanvasRenderTarget(device, (float)_image.Width, (float)_image.Height, 96 * scale);
        bool dark = ActualTheme == ElementTheme.Dark;
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(dark ? Color.FromArgb(255, 32, 32, 32) : MathColors.White);
            _node.Draw(ds, Pad, Pad + _node.A, ForegroundFor(ActualTheme));
        }
        var stream = new InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        return stream;
    }
}

internal static class MathColors
{
    public static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);
    public static readonly Color White = Color.FromArgb(255, 255, 255, 255);
}
