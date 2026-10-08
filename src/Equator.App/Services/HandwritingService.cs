using Equator.Core.Parsing;
using Equator.Recognition;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Equator.App.Services;

/// <summary>One step of a scanned solution: Equator input text and the page line it came from.</summary>
public sealed record ScannedStep(string Text, int SourceLine, string Latex);

/// <summary>
/// Handwriting recognition for the app: finds or downloads the on-device model, loads it once, and runs
/// recognition off the UI thread.
/// </summary>
public static class HandwritingService
{
    private static FormulaRecognizer? _recognizer;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string? ModelDirectory => ModelStore.Find();
    public static bool ModelAvailable => ModelDirectory is not null;
    public static double DownloadMegabytes => ModelStore.DownloadSize / 1e6;

    public static Task DownloadAsync(IProgress<double> progress, CancellationToken cancellation) =>
        ModelStore.DownloadAsync(progress, cancellation);

    private static async Task<FormulaRecognizer> RecognizerAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_recognizer is not null) return _recognizer;
            var dir = ModelDirectory ?? throw new InvalidOperationException("The handwriting model is not installed.");
            _recognizer = await Task.Run(() => new FormulaRecognizer(dir));
            return _recognizer;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Reads a page and converts every recognised line to Equator input (environments may give several steps per line).</summary>
    public static async Task<(IReadOnlyList<RecognizedLine> Lines, List<ScannedStep> Steps)> ReadAsync(GrayImage page, bool alreadyClean, CancellationToken cancellation)
    {
        var recognizer = await RecognizerAsync();
        return await Task.Run(() =>
        {
            var lines = new HandwritingReader(recognizer).Read(page, alreadyClean, cancellation);
            var steps = new List<ScannedStep>();
            for (int i = 0; i < lines.Count; i++)
            {
                List<string> converted;
                try { converted = LatexConverter.Convert(lines[i].Latex); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
                {
                    converted = [lines[i].Latex];
                }
                foreach (var text in converted) steps.Add(new ScannedStep(text, i, lines[i].Latex));
            }
            return ((IReadOnlyList<RecognizedLine>)lines, steps);
        }, cancellation);
    }

    /// <summary>Decodes an image file or stream (PNG, JPEG, BMP, GIF, TIFF, HEIC where supported) to BGRA pixels.</summary>
    public static async Task<(byte[] Bgra, int Width, int Height)> DecodeAsync(IRandomAccessStream stream)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        // Very large photos are reduced while decoding.
        uint w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight;
        double scale = Math.Min(1, 2400.0 / Math.Max(w, h));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
            ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        // The scaled size refers to the stored image; a 90° EXIF orientation swaps it.
        bool swapped = decoder.OrientedPixelWidth != decoder.PixelWidth;
        int sw = (int)transform.ScaledWidth, sh = (int)transform.ScaledHeight;
        var (ow, oh) = swapped ? (sh, sw) : (sw, sh);
        return (data.DetachPixelData(), ow, oh);
    }
}
