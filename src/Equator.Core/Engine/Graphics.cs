namespace Equator.Core.Engine;

public enum PlotKind { Cartesian, Surface }

/// <summary>A curve to draw: y = f(x), a parametric curve (x(t), y(t)), or a list of points.</summary>
public sealed class PlotSeries
{
    public required string Label { get; init; }
    public Func<double, double>? Function { get; init; }
    public Func<double, (double X, double Y)>? Parametric { get; init; }
    public double TMin { get; init; }
    public double TMax { get; init; }
    public IReadOnlyList<(double X, double Y)>? Points { get; init; }
    public bool JoinPoints { get; init; }
}

/// <summary>Description of a plot; the UI samples the functions for the current viewport.</summary>
public sealed class PlotSpec
{
    public PlotKind Kind { get; init; } = PlotKind.Cartesian;
    public List<PlotSeries> Series { get; } = [];
    public double XMin { get; set; }
    public double XMax { get; set; }
    public double? YMin { get; set; }
    public double? YMax { get; set; }
    public string XLabel { get; init; } = "x";
    public string YLabel { get; init; } = "y";
    public bool EqualAspect { get; init; }

    // Surface plots
    public Func<double, double, double>? Surface { get; init; }
    public double Y0 { get; init; }
    public double Y1 { get; init; }
    public string ZLabel { get; init; } = "z";
    public string Title { get; init; } = "";
}

/// <summary>Expression node wrapping a plot so graphics can flow through the evaluator like any other value.</summary>
public sealed class GraphicsExpr(PlotSpec spec) : Expr
{
    public PlotSpec Spec { get; } = spec;
    internal override int Rank => 9;
    protected override int ComputeHash() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Spec);
    protected override bool StructEquals(Expr other) => ReferenceEquals(((GraphicsExpr)other).Spec, Spec);
}
