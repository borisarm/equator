using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Equator.Core;
using Equator.Core.Checking;
using Equator.Core.Engine;
using Equator.Core.Parsing;
using Equator.Recognition;

namespace Equator.Recognition.Tests;

/// <summary>Image processing (always) and end-to-end recognition (when the model is installed locally).</summary>
public class RecognitionTests
{
    // ---- helpers ---------------------------------------------------------------------------------

    private static GrayImage FromBitmap(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[data.Stride * bmp.Height];
        Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(data);
        var tight = new byte[bmp.Width * bmp.Height * 4];
        for (int y = 0; y < bmp.Height; y++) Array.Copy(bytes, y * data.Stride, tight, y * bmp.Width * 4, bmp.Width * 4);
        return GrayImage.FromBgra(tight, bmp.Width, bmp.Height);
    }

    private static GrayImage LoadSample(string name)
    {
        using var bmp = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Samples", name));
        using var argb = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(argb)) { g.Clear(Color.White); g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height); }
        return FromBitmap(argb);
    }

    /// <summary>
    /// Renders lines of math in a handwriting-style font, with ^ exponents raised and smaller, optionally with
    /// a lighting gradient like a photo taken under a lamp.
    /// </summary>
    private static GrayImage Handwritten(string[] lines, bool shaded = false)
    {
        const int lineHeight = 110;
        using var bmp = new Bitmap(1100, 60 + lineHeight * lines.Length, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            if (shaded)
            {
                using var gradient = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, bmp.Width, bmp.Height),
                    Color.FromArgb(255, 250, 247, 240), Color.FromArgb(255, 150, 145, 135), 35f);
                g.FillRectangle(gradient, 0, 0, bmp.Width, bmp.Height);
            }
            else g.Clear(Color.White);
            using var font = new Font("Ink Free", 40, FontStyle.Regular, GraphicsUnit.Pixel);
            using var small = new Font("Ink Free", 26, FontStyle.Regular, GraphicsUnit.Pixel);
            var ink = new SolidBrush(Color.FromArgb(255, 25, 30, 70));
            for (int i = 0; i < lines.Length; i++)
            {
                float x = 40, y = 30 + i * lineHeight;
                var text = lines[i];
                for (int j = 0; j < text.Length; j++)
                {
                    if (text[j] == '^' && j + 1 < text.Length)
                    {
                        var exp = text[j + 1].ToString();
                        g.DrawString(exp, small, ink, x - 4, y - 12);
                        x += g.MeasureString(exp, small).Width - 6;
                        j++;
                        continue;
                    }
                    var ch = text[j].ToString();
                    g.DrawString(ch, font, ink, x, y);
                    x += ch == " " ? 14 : g.MeasureString(ch, font).Width - 10;
                }
            }
        }
        return FromBitmap(bmp);
    }

    private static string? ModelDir => ModelStore.Find();

    private static readonly Lazy<FormulaRecognizer?> Recognizer = new(() => ModelDir is { } d ? new FormulaRecognizer(d) : null);

    /// <summary>The model is large and not in the repository; recognition tests run only where it is installed.</summary>
    private static bool TryRecognizer(out FormulaRecognizer recognizer)
    {
        recognizer = Recognizer.Value!;
        return recognizer is not null;
    }

    private static bool SameValue(string got, string expected)
    {
        var a = new Kernel().EvalText(got);
        var b = new Kernel().EvalText(expected);
        return a == b || Core.Algebra.Expansion.Expand(a - b).IsZero;
    }

    // ---- image processing --------------------------------------------------------------------------

    [Fact]
    public void LinesAreSplitButFractionsAndExponentsStayTogether()
    {
        using var bmp = new Bitmap(600, 500, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            var pen = new Pen(Color.Black, 4);
            void Glyph(int x, int y) => g.DrawEllipse(pen, x, y, 22, 30);
            // Line 1: a fraction: numerator glyphs, a bar, denominator glyphs, stacked closely.
            Glyph(60, 20); Glyph(95, 20); g.DrawLine(pen, 50, 62, 200, 62); Glyph(70, 70); Glyph(110, 70);
            // Line 2: glyphs with a small raised exponent.
            for (int x = 50; x < 300; x += 40) Glyph(x, 220);
            g.DrawEllipse(pen, 310, 200, 12, 16);
            // Line 3.
            for (int x = 50; x < 250; x += 40) Glyph(x, 380);
        }
        var lines = FromBitmap(bmp).SplitLines();
        Assert.Equal(3, lines.Count);
        Assert.True(lines[0].Top <= 20 && lines[0].Bottom >= 100, "the fraction must be one line");
    }

    [Fact]
    public void CleaningRemovesShadowsAndKeepsInk()
    {
        var shaded = Handwritten(["2x + 3 = 7"], shaded: true);
        var clean = shaded.Clean();
        var info = $"size {clean.Width}x{clean.Height}, bounds {clean.InkBounds()}, corner {clean[clean.Width - 5, clean.Height - 5]}";
        // The dark corner of the gradient is paper, not ink.
        Assert.False(clean.IsInk(clean.Width - 5, clean.Height - 5), info);
        Assert.False(clean.IsInk(5, 5), info);
        var bounds = clean.InkBounds();
        Assert.False(bounds.IsEmpty, info);
        Assert.True(bounds.Left >= 30 && bounds.Top >= 20 && bounds.Bottom < clean.Height - 10, $"ink bounds {bounds}");
    }

    [Fact]
    public void ResizeAndPadKeepContent()
    {
        var img = Handwritten(["x = 2"]);
        var small = img.Clean().Resize(img.Width / 2, img.Height / 2);
        Assert.Equal(img.Width / 2, small.Width);
        Assert.False(small.InkBounds().IsEmpty);
        var padded = small.Pad(5, 6, 7, 8);
        Assert.Equal(small.Width + 12, padded.Width);
        Assert.Equal(small.Height + 14, padded.Height);
    }

    [Fact]
    public void TensorHasTheModelShapeAndRange()
    {
        var t = FormulaRecognizer.Preprocess(Handwritten(["x = 2"]));
        Assert.Equal([1, 3, 384, 384], t.Dimensions.ToArray());
        Assert.InRange(t.Min(), -1f, 1f);
        Assert.InRange(t.Max(), -1f, 1f);
        Assert.True(t.Min() < -0.5f, "ink must be dark");
    }

    // ---- recognition (needs the model) --------------------------------------------------------------

    [Theory]
    [InlineData("math-formula-42.png", "limit((1 - 4^(x - 1/4))/(1 - 4x), x, 1/4)")]
    [InlineData("hw-formula2.png", null)]
    public void RecognisesSampleFormulas(string file, string? expected)
    {
        if (!TryRecognizer(out var rec)) return;
        var latex = rec.Recognize(LoadSample(file).Clean());
        Assert.False(string.IsNullOrWhiteSpace(latex));
        if (expected is null) return;
        var lines = LatexConverter.Convert(latex);
        Assert.Single(lines);
        Assert.True(SameValue(lines[0], expected), $"{file}: {latex} → {lines[0]}");
    }

    [Fact]
    public void RecognisesAStackedSystem()
    {
        if (!TryRecognizer(out var rec)) return;
        var lines = new HandwritingReader(rec).Read(LoadSample("hw-formula3.png"));
        var converted = lines.SelectMany(l => LatexConverter.Convert(l.Latex)).ToList();
        Assert.Contains(converted, c => c.Replace(" ", "") == "y'+y=3");
        Assert.Contains(converted, c => c.Replace(" ", "").StartsWith("y''+3"));
    }

    [Fact]
    public void ReadsAndChecksACorrectHandwrittenSolution()
    {
        if (!TryRecognizer(out var rec)) return;
        var page = Handwritten(["2x + 3 = 11", "2x = 8", "x = 4"], shaded: true);
        var lines = new HandwritingReader(rec).Read(page);
        Assert.Equal(3, lines.Count);
        var steps = lines.SelectMany(l => LatexConverter.Convert(l.Latex)).ToList();
        var report = WorkChecker.Check(steps);
        Assert.True(report.Overall == Verdict.Correct, string.Join(" | ", lines.Select(l => l.Latex)) + "\n" + report.ToText());
    }

    [Fact]
    public void ReadsAndFlagsAWrongHandwrittenStep()
    {
        if (!TryRecognizer(out var rec)) return;
        // A classic slip: forgetting the negative root.
        var page = Handwritten(["x^2 - 4 = 0", "x^2 = 4", "x = 2"]);
        var lines = new HandwritingReader(rec).Read(page);
        Assert.Equal(3, lines.Count);
        var steps = lines.SelectMany(l => LatexConverter.Convert(l.Latex)).ToList();
        var report = WorkChecker.Check(steps);
        var text = string.Join(" | ", lines.Select(l => l.Latex)) + "\n" + report.ToText();
        Assert.True(report.Overall == Verdict.Incorrect, text);
        Assert.True(report.Steps[^1].Verdict == Verdict.Incorrect, text);
        Assert.Contains("-2", report.Steps[^1].Message + report.Summary);
    }
}
