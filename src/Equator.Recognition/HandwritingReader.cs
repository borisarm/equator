namespace Equator.Recognition;

/// <summary>One recognised line of handwriting: where it is on the page and its LaTeX transcription.</summary>
public sealed record RecognizedLine(PixelRect Bounds, string Latex);

/// <summary>
/// Reads a page of handwritten mathematics: cleans the image, splits it into lines and transcribes each line
/// to LaTeX with <see cref="FormulaRecognizer"/>.
/// </summary>
public sealed class HandwritingReader(FormulaRecognizer recognizer)
{
    /// <summary>Longest side the page is reduced to before cleaning (speed; handwriting stays legible).</summary>
    public const int MaxPageSide = 2000;

    /// <param name="alreadyClean">True for rendered ink (black on white), which needs no photo clean-up.</param>
    public IReadOnlyList<RecognizedLine> Read(GrayImage page, bool alreadyClean = false, CancellationToken cancellation = default)
    {
        double scale = Math.Min(1, (double)MaxPageSide / Math.Max(page.Width, page.Height));
        if (scale < 1) page = page.Resize(Math.Max(1, (int)(page.Width * scale)), Math.Max(1, (int)(page.Height * scale)));
        var clean = alreadyClean ? page : page.Clean();
        var lines = clean.SplitLines();
        var result = new List<RecognizedLine>();
        foreach (var r in lines)
        {
            cancellation.ThrowIfCancellationRequested();
            var latex = recognizer.Recognize(clean.Crop(r), cancellation);
            var bounds = scale < 1
                ? new PixelRect((int)(r.Left / scale), (int)(r.Top / scale), (int)Math.Ceiling(r.Right / scale), (int)Math.Ceiling(r.Bottom / scale))
                : r;
            result.Add(new RecognizedLine(bounds, latex));
        }
        return result;
    }
}
