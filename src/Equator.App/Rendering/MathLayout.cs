using System.Numerics;
using Equator.Core.Printing;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

namespace Equator.App.Rendering;

/// <summary>
/// A laid-out piece of mathematics: width, ascent above the baseline and descent below it.
/// Coordinates are device-independent pixels.
/// </summary>
public abstract class LNode
{
    public double W { get; protected set; }
    public double A { get; protected set; }
    public double D { get; protected set; }
    public double H => A + D;
    public bool Italic { get; protected init; }

    public abstract void Draw(CanvasDrawingSession ds, double x, double baseline, Color color);
}

/// <summary>Converts a <see cref="Box"/> tree into positioned <see cref="LNode"/>s using real font metrics.</summary>
public sealed class MathLayoutBuilder(ICanvasResourceCreator device)
{
    public const string MathFont = "Cambria Math";
    public const string TextFont = "Segoe UI";

    public LNode Build(Box box, double size) => BuildNode(box, size, 0);

    private LNode BuildNode(Box box, double s, int depth) => box switch
    {
        RunBox r => Run(r, s),
        RowBox row => new LRow(SpaceRow(row.Items.Select(i => (i, BuildNode(i, s, depth))).ToList(), s)),
        FracBox f => new LFrac(BuildNode(f.Num, Shrink(s, depth == 0 ? 0.92 : 0.8), depth + 1), BuildNode(f.Den, Shrink(s, depth == 0 ? 0.92 : 0.8), depth + 1), s),
        ScriptBox sc => new LScript(BuildNode(sc.Base, s, depth),
            sc.Sup is null ? null : BuildNode(sc.Sup, Shrink(s, 0.7), depth + 1),
            sc.Sub is null ? null : BuildNode(sc.Sub, Shrink(s, 0.7), depth + 1), s),
        RadicalBox rad => new LRadical(BuildNode(rad.Inner, s, depth), rad.Index is null ? null : BuildNode(rad.Index, Shrink(s, 0.55), depth + 1), s),
        FenceBox fe => new LFence(BuildNode(fe.Inner, s, depth), fe.Open, fe.Close, s),
        GridBox g => new LGrid(g.Rows.Select(r => r.Select(c => BuildNode(c, s, depth)).ToList()).ToList(), s),
        BigOpBox op => BigOp(op, s, depth),
        _ => new LText(device, "?", s, false, TextFont),
    };

    private static double Shrink(double s, double factor) => Math.Max(9, s * factor);

    private LNode Run(RunBox r, double s) => r.Kind switch
    {
        RunKind.Variable => new LText(device, r.Text, s, IsLatinWord(r.Text), MathFont),
        RunKind.Number => new LText(device, r.Text, s, false, MathFont),
        RunKind.Function => new LText(device, r.Text, s, false, MathFont),
        RunKind.Text => new LText(device, r.Text, s * 0.9, false, TextFont),
        _ => new LText(device, r.Text, s, false, MathFont),
    };

    /// <summary>Latin identifiers are set in italics; Greek and multi-letter constants stay upright like TeX.</summary>
    private static bool IsLatinWord(string t) => t.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '′' or '\'') && char.IsLetter(t[0]);

    /// <summary>Inserts the spacing that TeX would put around binary operators and relations.</summary>
    private List<LNode> SpaceRow(List<(Box Box, LNode Node)> items, double s)
    {
        var list = new List<LNode>();
        foreach (var (box, node) in items)
        {
            if (box is RunBox { Kind: RunKind.Operator } op)
            {
                double gap = op.Text is "=" or "→" or "≤" or "≥" or "≠" ? 0.28 * s : 0.22 * s;
                list.Add(new LSpace(gap));
                list.Add(node);
                list.Add(new LSpace(gap));
            }
            else list.Add(node);
        }
        return list;
    }

    private LNode BigOp(BigOpBox op, double s, int depth)
    {
        if (op.Symbol == "lim")
        {
            var lim = new LText(device, "lim", s, false, MathFont);
            var under = op.Lower is null ? null : BuildNode(op.Lower, Shrink(s, 0.7), depth + 1);
            return new LUnderOver(lim, under, null, s);
        }
        var glyph = new LGlyph(device, op.Symbol, op.Symbol == "∫" ? s * 1.6 : s * 1.45, s);
        var lower = op.Lower is null ? null : BuildNode(op.Lower, Shrink(s, 0.7), depth + 1);
        var upper = op.Upper is null ? null : BuildNode(op.Upper, Shrink(s, 0.7), depth + 1);
        if (op.LimitsUnder) return new LUnderOver(glyph, lower, upper, s);
        return new LRow([new LScript(glyph, upper, lower, s, integralScripts: true), new LSpace(0.1 * s)]);
    }
}

public sealed class LSpace : LNode
{
    public LSpace(double w) { W = w; }
    public override void Draw(CanvasDrawingSession ds, double x, double baseline, Color color) { }
}

/// <summary>Text with nominal (font-size based) ascent/descent so baselines and fraction bars line up uniformly.</summary>
public sealed class LText : LNode
{
    private readonly CanvasTextLayout _layout;
    private readonly double _baseline;

    public LText(ICanvasResourceCreator device, string text, double size, bool italic, string font)
    {
        var format = new CanvasTextFormat
        {
            FontFamily = font,
            FontSize = (float)size,
            FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        _layout = new CanvasTextLayout(device, text, format, 100000, 10000);
        _baseline = _layout.LineMetrics.Length > 0 ? _layout.LineMetrics[0].Baseline : size * 0.8;
        W = _layout.LayoutBoundsIncludingTrailingWhitespace.Width + (italic ? size * 0.04 : 0);
        A = size * 0.70;
        D = size * 0.22;
        Italic = italic;
        // Respect glyphs that genuinely extend further (e.g. tall symbols).
        var ink = _layout.DrawBounds;
        if (ink.Height > 0)
        {
            A = Math.Max(A, Math.Min(_baseline - ink.Top, size * 1.0));
            D = Math.Max(D, Math.Min(ink.Bottom - _baseline, size * 0.35));
        }
    }

    public override void Draw(CanvasDrawingSession ds, double x, double baseline, Color color) =>
        ds.DrawTextLayout(_layout, (float)x, (float)(baseline - _baseline), color);
}

/// <summary>A large operator glyph (∫, ∑, ∏) sized by its ink and centred on the math axis.</summary>
public sealed class LGlyph : LNode
{
    private readonly CanvasTextLayout _layout;
    private readonly double _offset;

    public LGlyph(ICanvasResourceCreator device, string glyph, double glyphSize, double baseSize)
    {
        var format = new CanvasTextFormat { FontFamily = MathLayoutBuilder.MathFont, FontSize = (float)glyphSize, WordWrapping = CanvasWordWrapping.NoWrap };
        _layout = new CanvasTextLayout(device, glyph, format, 10000, 10000);
        var ink = _layout.DrawBounds;
        double axis = 0.25 * baseSize;
        double inkMid = (ink.Top + ink.Bottom) / 2;
        // Shift so the glyph's ink centre sits on the math axis.
        _offset = -axis - inkMid;
        A = -(ink.Top + _offset);
        D = ink.Bottom + _offset;
        W = Math.Max(_layout.LayoutBounds.Width, ink.Right) + 0.05 * baseSize;
    }

    public override void Draw(CanvasDrawingSession ds, double x, double baseline, Color color) =>
        ds.DrawTextLayout(_layout, (float)x, (float)(baseline + _offset), color);
}

public sealed class LRow : LNode
{
    private readonly List<LNode> _items;

    public LRow(List<LNode> items)
    {
        _items = items;
        W = items.Sum(i => i.W);
        A = items.Count == 0 ? 0 : items.Max(i => i.A);
        D = items.Count == 0 ? 0 : items.Max(i => i.D);
    }

    public override void Draw(CanvasDrawingSession ds, double x, double baseline, Color color)
    {
        foreach (var i in _items)
        {
            i.Draw(ds, x, baseline, color);
            x += i.W;
        }
    }
}

public sealed class LFrac : LNode
{
    private readonly LNode _num, _den;
    private readonly double _axis, _t, _gap, _pad;

    public LFrac(LNode num, LNode den, double s)
    {
        _num = num; _den = den;
        _axis = 0.25 * s;
        _t = Math.Max(1, 0.055 * s);
        _gap = 0.14 * s;
        _pad = 0.12 * s;
        W = Math.Max(num.W, den.W) + 2 * _pad;
        A = _axis + _t / 2 + _gap + num.D + num.A;
        D = den.A + den.D + _gap + _t / 2 - _axis;
    }

    public override void Draw(CanvasDrawingSession ds, double x, double baseline, Color color)
    {
        double barY = baseline - _axis;
        ds.FillRectangle((float)(x + _pad * 0.4), (float)(barY - _t / 2), (float)(W - _pad * 0.8), (float)_t, color);
        _num.Draw(ds, x + (W - _num.W) / 2, barY - _t / 2 - _gap - _num.D, color);
        _den.Draw(ds, x + (W - _den.W) / 2, barY + _t / 2 + _gap + _den.A, color);
    }
}

public sealed class LScript : LNode
{
    private readonly LNode _base;
    private readonly LNode? _sup, _sub;
    private readonly double _supShift, _subShift, _kern;

    public LScript(LNode b, LNode? sup, LNode? sub, double s, bool integralScripts = false)
    {
        _base = b; _sup = sup; _sub = sub;
        _kern = b.Italic ? 0.06 * s : 0.03 * s;
        if (integralScripts)
        {
            _supShift = sup is null ? 0 : b.A - sup.A * 0.6;
            _subShift = sub is null ? 0 : b.D - sub.D * 0.2;
        }
        else
        {
            _supShift = sup is null ? 0 : Math.Max(0.42 * s, b.A - 0.45 * sup.A);
            _subShift = sub is null ? 0 : Math.Max(0.22 * s, b.D + 0.25 * sub.A - 0.1 * s);
        }
        var scriptW = Math.Max(sup?.W ?? 0, sub?.W ?? 0);
        W = b.W + _kern + scriptW + 0.04 * s;
        A = Math.Max(b.A, sup is null ? 0 : _supShift + sup.A);
        D = Math.Max(b.D, sub is null ? 0 : _subShift + sub.D);
    }

    public override void Draw(CanvasDrawingSession ds, double x, double baseline, Color color)
    {
        _base.Draw(ds, x, baseline, color);
        var sx = x + _base.W + _kern;
        _sup?.Draw(ds, sx, baseline - _supShift, color);
        _sub?.Draw(ds, sx - (_base.Italic ? _kern : 0), baseline + _subShift, color);
    }
}

public sealed class LUnderOver : LNode
{
    private readonly LNode _core;
    private readonly LNode? _under, _over;
    private readonly double _gap;

    public LUnderOver(LNode core, LNode? under, LNode? over, double s)
    {
        _core = core; _under = under; _over = over;
        _gap = 0.12 * s;
        W = Math.Max(core.W, Math.Max(under?.W ?? 0, over?.W ?? 0)) + 0.15 * s;
        A = core.A + (over is null ? 0 : _gap + over.H);
        D = core.D + (under is null ? 0 : _gap + under.H);
    }

    public override void Draw(CanvasDrawingSession ds, double x, double baseline, Color color)
    {
        _core.Draw(ds, x + (W - _core.W) / 2, baseline, color);
        _over?.Draw(ds, x + (W - _over.W) / 2, baseline - _core.A - _gap - _over.D, color);
        _under?.Draw(ds, x + (W - _under.W) / 2, baseline + _core.D + _gap + _under.A, color);
    }
}

public sealed class LRadical : LNode
{
    private readonly LNode _inner;
    private readonly LNode? _index;
    private readonly double _t, _gap, _surd, _indexShift, _s;

    public LRadical(LNode inner, LNode? index, double s)
    {
        _inner = inner; _index = index; _s = s;
        _t = Math.Max(1, 0.055 * s);
        _gap = 0.13 * s;
        double h = inner.H + _gap + _t;
        _surd = 0.5 * s + 0.06 * h;
        _indexShift = index is null ? 0 : Math.Max(0, index.W - 0.32 * _surd);
        W = _indexShift + _surd + inner.W + 0.08 * s;
        A = inner.A + _gap + _t + 0.02 * s;
        D = inner.D + 0.03 * s;
        if (index is not null) A = Math.Max(A, 0.55 * (inner.H + _gap) - inner.D + index.H + 0.1 * s);
    }

    public override void Draw(CanvasDrawingSession ds, double x, double baseline, Color color)
    {
        x += _indexShift;
        double top = baseline - _inner.A - _gap - _t / 2;
        double bottom = baseline + _inner.D;
        double h = bottom - top;
        using var pb = new CanvasPathBuilder(ds);
        var p0 = new Vector2((float)(x + 0.06 * _s), (float)(bottom - 0.42 * h));
        var p1 = new Vector2((float)(x + 0.2 * _surd), (float)(bottom - 0.5 * h));
        var p2 = new Vector2((float)(x + 0.5 * _surd), (float)bottom);
        var p3 = new Vector2((float)(x + _surd), (float)top);
        var p4 = new Vector2((float)(x + W - _indexShift - 0.02 * _s), (float)top);
        pb.BeginFigure(p0);
        pb.AddLine(p1);
        pb.AddLine(p2);
        pb.AddLine(p3);
        pb.AddLine(p4);
        pb.EndFigure(CanvasFigureLoop.Open);
        using var geom = CanvasGeometry.CreatePath(pb);
        using var style = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round, StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Flat };
        ds.DrawGeometry(geom, color, (float)_t, style);
        // A heavier down-stroke, as in typeset radicals.
        ds.DrawLine(p1, p2, color, (float)(_t * 1.8), style);
        _inner.Draw(ds, x + _surd + 0.04 * _s, baseline, color);
        _index?.Draw(ds, x - _indexShift + 0.02 * _s, bottom - 0.55 * h - _index.D - 0.05 * _s, color);
    }
}

/// <summary>Stretchy delimiters drawn as paths so they grow with their content.</summary>
public sealed class LFence : LNode
{
    private readonly LNode _inner;
    private readonly char _open, _close;
    private readonly double _fw, _t, _s, _top, _bottom;

    public LFence(LNode inner, char open, char close, double s)
    {
        _inner = inner; _open = open; _close = close; _s = s;
        double axis = 0.25 * s;
        // Delimiters are symmetric about the math axis.
        double half = Math.Max(Math.Max(inner.A - axis, inner.D + axis), 0.5 * s) + 0.1 * s;
        _top = axis + half;
        _bottom = half - axis;
        double h = _top + _bottom;
        _fw = Math.Min(0.24 * s + 0.07 * h, 0.6 * s);
        _t = Math.Max(1, 0.06 * s + 0.01 * h);
        W = 2 * _fw + inner.W + 0.12 * s;
        A = _top;
        D = _bottom;
    }

    public override void Draw(CanvasDrawingSession ds, double x, double baseline, Color color)
    {
        double top = baseline - _top, bottom = baseline + _bottom;
        DrawDelimiter(ds, _open, x, top, bottom, true, color);
        _inner.Draw(ds, x + _fw + 0.06 * _s, baseline, color);
        DrawDelimiter(ds, _close, x + _fw + 0.12 * _s + _inner.W, top, bottom, false, color);
    }

    private void DrawDelimiter(CanvasDrawingSession ds, char c, double x, double top, double bottom, bool left, Color color)
    {
        float t = (float)_t;
        double mid = (top + bottom) / 2;
        double inset = 0.18 * _fw, outer = _fw - 0.18 * _fw;
        double near = left ? x + outer : x + inset;   // side touching the content
        double far = left ? x + inset : x + outer;
        using var style = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
        switch (c)
        {
            case '(':
            case ')':
                {
                    using var pb = new CanvasPathBuilder(ds);
                    pb.BeginFigure((float)near, (float)top);
                    pb.AddCubicBezier(new Vector2((float)far, (float)(top + (bottom - top) * 0.2)), new Vector2((float)far, (float)(bottom - (bottom - top) * 0.2)), new Vector2((float)near, (float)bottom));
                    pb.EndFigure(CanvasFigureLoop.Open);
                    using var g = CanvasGeometry.CreatePath(pb);
                    ds.DrawGeometry(g, color, t, style);
                    break;
                }
            case '[':
            case ']':
                {
                    ds.DrawLine((float)far, (float)top, (float)far, (float)bottom, color, t, style);
                    ds.DrawLine((float)far, (float)top, (float)near, (float)top, color, t, style);
                    ds.DrawLine((float)far, (float)bottom, (float)near, (float)bottom, color, t, style);
                    break;
                }
            case '|':
                {
                    double cx = (near + far) / 2;
                    ds.DrawLine((float)cx, (float)top, (float)cx, (float)bottom, color, t, style);
                    break;
                }
            case '{':
            case '}':
                {
                    double cx = (near + far) / 2;
                    double q = (bottom - top) / 4;
                    using var pb = new CanvasPathBuilder(ds);
                    pb.BeginFigure((float)near, (float)top);
                    pb.AddCubicBezier(new Vector2((float)cx, (float)top), new Vector2((float)cx, (float)(top + q * 0.4)), new Vector2((float)cx, (float)(top + q)));
                    pb.AddLine((float)cx, (float)(mid - q * 0.6));
                    pb.AddCubicBezier(new Vector2((float)cx, (float)(mid - q * 0.15)), new Vector2((float)far, (float)mid), new Vector2((float)far, (float)mid));
                    pb.AddCubicBezier(new Vector2((float)far, (float)mid), new Vector2((float)cx, (float)(mid + q * 0.15)), new Vector2((float)cx, (float)(mid + q * 0.6)));
                    pb.AddLine((float)cx, (float)(bottom - q));
                    pb.AddCubicBezier(new Vector2((float)cx, (float)(bottom - q * 0.4)), new Vector2((float)cx, (float)bottom), new Vector2((float)near, (float)bottom));
                    pb.EndFigure(CanvasFigureLoop.Open);
                    using var g = CanvasGeometry.CreatePath(pb);
                    ds.DrawGeometry(g, color, t, style);
                    break;
                }
        }
    }
}

public sealed class LGrid : LNode
{
    private readonly List<List<LNode>> _rows;
    private readonly double[] _colW, _rowA, _rowD;
    private readonly double _rowGap, _colGap, _axis;

    public LGrid(List<List<LNode>> rows, double s)
    {
        _rows = rows;
        _rowGap = 0.35 * s;
        _colGap = 0.9 * s;
        _axis = 0.25 * s;
        int cols = rows.Max(r => r.Count);
        _colW = new double[cols];
        _rowA = new double[rows.Count];
        _rowD = new double[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            for (int j = 0; j < rows[i].Count; j++)
            {
                _colW[j] = Math.Max(_colW[j], rows[i][j].W);
                _rowA[i] = Math.Max(_rowA[i], rows[i][j].A);
                _rowD[i] = Math.Max(_rowD[i], rows[i][j].D);
            }
        }
        W = _colW.Sum() + _colGap * (cols - 1);
        double h = _rowA.Sum() + _rowD.Sum() + _rowGap * (rows.Count - 1);
        A = h / 2 + _axis;
        D = h / 2 - _axis;
    }

    public override void Draw(CanvasDrawingSession ds, double x, double baseline, Color color)
    {
        double y = baseline - A;
        for (int i = 0; i < _rows.Count; i++)
        {
            double rowBaseline = y + _rowA[i];
            double cx = x;
            for (int j = 0; j < _colW.Length; j++)
            {
                if (j < _rows[i].Count)
                {
                    var cell = _rows[i][j];
                    cell.Draw(ds, cx + (_colW[j] - cell.W) / 2, rowBaseline, color);
                }
                cx += _colW[j] + _colGap;
            }
            y += _rowA[i] + _rowD[i] + _rowGap;
        }
    }
}
