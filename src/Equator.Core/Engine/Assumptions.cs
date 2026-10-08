using System.Numerics;

namespace Equator.Core.Engine;

/// <summary>Properties assumed for symbols (filled in by the assumptions system).</summary>
public static class Assumptions
{
    /// <summary>A numeric value for symbol <paramref name="s"/> near <paramref name="v"/> that is consistent with its assumptions.</summary>
    public static Complex SampleValue(Sym s, double v) => v;
}
