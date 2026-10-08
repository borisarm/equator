namespace Equator.Core.Engine;

public enum OutputKind { Expression, Plot, Text, Error }

/// <summary>One displayable result of evaluating a statement.</summary>
public sealed record CellOutput(OutputKind Kind, string Text, Expr? Value = null, PlotSpec? Plot = null, string? Latex = null);

public sealed record EvaluationResult(int Index, IReadOnlyList<CellOutput> Outputs, TimeSpan Elapsed);
