using Equator.Core.Numerics;
using Equator.Core.Printing;

namespace Equator.Core.Engine;

/// <summary>Builds <see cref="PlotSpec"/>s from plot command arguments.</summary>
internal static class Plots
{
    private static void CheckOnly(Expr f, IEnumerable<Sym> allowed, string fn)
    {
        var extra = f.FreeSymbols().Where(s => !allowed.Contains(s)).Select(s => s.Name).ToList();
        if (extra.Count > 0)
            throw new MathException(Messages.T("{0}: the expression contains {1}, which have no value. Assign them first (e.g. {2} := 1).", fn, string.Join(", ", extra), extra[0]));
    }

    private static Sym Var(Expr e, string fn) =>
        e is Sym s && !s.IsSpecial ? s : throw new MathException(Messages.T("{0}: expected a variable name, got {1}.", fn, e));

    private static double Dbl(Expr e, string what)
    {
        if (e.IsConstant && !e.IsUndefinedOrInfinite)
        {
            var v = NumericEval.EvalReal(e);
            if (double.IsFinite(v)) return v;
        }
        throw new MathException(Messages.T("{0} must be a finite real number, got {1}.", Messages.T(what), e));
    }

    private static (Sym X, double A, double B, int Next) Range(Expr[] a, int i, string fn)
    {
        if (a.Length > i && a[i] is ListExpr { Items.Count: 3 } l)
            return (Var(l.Items[0], fn), Dbl(l.Items[1], "Range start"), Dbl(l.Items[2], "Range end"), i + 1);
        if (a.Length < i + 3) throw new MathException(Messages.T("{0}: give a variable and range, e.g. {0}(f, x, -5, 5).", fn));
        return (Var(a[i], fn), Dbl(a[i + 1], "Range start"), Dbl(a[i + 2], "Range end"), i + 3);
    }

    public static Expr Plot2D(Expr[] a)
    {
        var fs = a[0] is ListExpr l && !LinearAlgebra2.IsPointList(l) ? l.Items.ToList() : [a[0]];
        var (x, lo, hi, next) = a.Length >= 2 ? Range(a, 1, "plot") : throw new MathException(Messages.T("plot: give a variable and range, e.g. plot(sin(x), x, -5, 5)."));
        if (lo >= hi) throw new MathException(Messages.T("plot: the range start must be less than the end."));
        var spec = new PlotSpec { XMin = lo, XMax = hi, XLabel = x.Name, YLabel = "" };
        if (a.Length >= next + 2)
        {
            spec.YMin = Dbl(a[next], "y minimum");
            spec.YMax = Dbl(a[next + 1], "y maximum");
        }
        foreach (var f0 in fs)
        {
            var f = f0 is EqExpr eq ? eq.Rhs : f0;
            CheckOnly(f, [x], "plot");
            spec.Series.Add(new PlotSeries { Label = TextFormatter.Format(f), Function = NumericEval.Compile1(f, x.Name) });
        }
        return new GraphicsExpr(spec);
    }

    public static Expr Parametric(Expr[] a)
    {
        var (t, lo, hi, _) = Range(a, 1, "parametricplot");
        if (a[0] is not ListExpr l) throw new MathException(Messages.T("parametricplot: expected [x(t), y(t)]."));
        var curves = l.Items.All(i => i is ListExpr { Items.Count: 2 }) ? l.Items.Cast<ListExpr>().ToList() : [l];
        var spec = new PlotSpec { XMin = 0, XMax = 1, EqualAspect = true, XLabel = "x", YLabel = "y" };
        foreach (var c in curves)
        {
            if (c.Items.Count != 2) throw new MathException(Messages.T("parametricplot: each curve needs two components [x(t), y(t)]."));
            CheckOnly(c, [t], "parametricplot");
            var fx = NumericEval.Compile1(c.Items[0], t.Name);
            var fy = NumericEval.Compile1(c.Items[1], t.Name);
            spec.Series.Add(new PlotSeries { Label = TextFormatter.Format(c), Parametric = v => (fx(v), fy(v)), TMin = lo, TMax = hi });
        }
        FitParametric(spec);
        return new GraphicsExpr(spec);
    }

    public static Expr Polar(Expr[] a)
    {
        var (t, lo, hi, _) = Range(a, 1, "polarplot");
        var rs = a[0] is ListExpr l ? l.Items.ToList() : [a[0]];
        var spec = new PlotSpec { EqualAspect = true, XLabel = "x", YLabel = "y" };
        foreach (var rExpr in rs)
        {
            CheckOnly(rExpr, [t], "polarplot");
            var r = NumericEval.Compile1(rExpr, t.Name);
            spec.Series.Add(new PlotSeries
            {
                Label = "r = " + TextFormatter.Format(rExpr),
                Parametric = v => { var rv = r(v); return (rv * Math.Cos(v), rv * Math.Sin(v)); },
                TMin = lo, TMax = hi,
            });
        }
        FitParametric(spec);
        return new GraphicsExpr(spec);
    }

    private static void FitParametric(PlotSpec spec)
    {
        double xmin = double.MaxValue, xmax = double.MinValue, ymin = double.MaxValue, ymax = double.MinValue;
        foreach (var s in spec.Series)
        {
            for (int i = 0; i <= 2000; i++)
            {
                var (px, py) = s.Parametric!(s.TMin + (s.TMax - s.TMin) * i / 2000.0);
                if (!double.IsFinite(px) || !double.IsFinite(py)) continue;
                xmin = Math.Min(xmin, px); xmax = Math.Max(xmax, px);
                ymin = Math.Min(ymin, py); ymax = Math.Max(ymax, py);
            }
        }
        if (xmin > xmax) { xmin = -1; xmax = 1; ymin = -1; ymax = 1; }
        double padX = Math.Max((xmax - xmin) * 0.08, 1e-6), padY = Math.Max((ymax - ymin) * 0.08, 1e-6);
        spec.XMin = xmin - padX; spec.XMax = xmax + padX;
        spec.YMin = ymin - padY; spec.YMax = ymax + padY;
    }

    public static Expr ListPlot(Expr[] a)
    {
        if (a[0] is not ListExpr l) throw new MathException(Messages.T("listplot: expected a list."));
        var pts = new List<(double, double)>();
        int i = 1;
        foreach (var item in l.Items)
        {
            if (item is ListExpr { Items.Count: 2 } p) pts.Add((Dbl(p.Items[0], "x"), Dbl(p.Items[1], "y")));
            else pts.Add((i, Dbl(item, "Value")));
            i++;
        }
        if (pts.Count == 0) throw new MathException(Messages.T("listplot: the list is empty."));
        bool join = a.Length > 1 && a[1] is Sym { Name: "True" or "joined" or "line" };
        var spec = new PlotSpec { XLabel = "x", YLabel = "y" };
        spec.Series.Add(new PlotSeries { Label = "data", Points = pts, JoinPoints = join });
        double xmin = pts.Min(p => p.Item1), xmax = pts.Max(p => p.Item1), ymin = pts.Min(p => p.Item2), ymax = pts.Max(p => p.Item2);
        double px = Math.Max((xmax - xmin) * 0.06, 0.5), py = Math.Max((ymax - ymin) * 0.08, 0.5);
        spec.XMin = xmin - px; spec.XMax = xmax + px; spec.YMin = ymin - py; spec.YMax = ymax + py;
        return new GraphicsExpr(spec);
    }

    public static Expr Plot3D(Expr[] a)
    {
        var x = Var(a[1], "plot3d");
        var y = Var(a[4], "plot3d");
        CheckOnly(a[0], [x, y], "plot3d");
        var f = NumericEval.Compile(a[0], x.Name, y.Name);
        var spec = new PlotSpec
        {
            Kind = PlotKind.Surface,
            XMin = Dbl(a[2], "x start"), XMax = Dbl(a[3], "x end"),
            Y0 = Dbl(a[5], "y start"), Y1 = Dbl(a[6], "y end"),
            Surface = (u, v) => f([u, v]),
            XLabel = x.Name, YLabel = y.Name, ZLabel = "z",
            Title = TextFormatter.Format(a[0]),
        };
        return new GraphicsExpr(spec);
    }
}

internal static class LinearAlgebra2
{
    /// <summary>[[x1, y1], [x2, y2], ...] with numeric entries (as opposed to a list of functions).</summary>
    public static bool IsPointList(ListExpr l) => l.Items.Count > 0 && l.Items.All(i => i is ListExpr { Items.Count: 2 } p && p.Items.All(v => v.IsNumber));
}
