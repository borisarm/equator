using System.Globalization;
using System.Numerics;

namespace Equator.Core;

/// <summary>Exact rational number with arbitrary precision, always kept in lowest terms with a positive denominator.</summary>
public readonly struct BigRational : IEquatable<BigRational>, IComparable<BigRational>
{
    public BigInteger Num { get; }
    public BigInteger Den { get; }

    public static readonly BigRational Zero = new(0);
    public static readonly BigRational One = new(1);
    public static readonly BigRational MinusOne = new(-1);
    public static readonly BigRational Half = new(1, 2);

    public BigRational(BigInteger value)
    {
        Num = value;
        Den = BigInteger.One;
    }

    public BigRational(BigInteger num, BigInteger den)
    {
        if (den.IsZero) throw new DivideByZeroException();
        if (den.Sign < 0) { num = -num; den = -den; }
        var g = BigInteger.GreatestCommonDivisor(num, den);
        if (!g.IsOne && !g.IsZero) { num /= g; den /= g; }
        Num = num;
        Den = num.IsZero ? BigInteger.One : den;
    }

    public bool IsZero => Num.IsZero;
    public bool IsOne => Num.IsOne && Den.IsOne;
    public bool IsMinusOne => Num == BigInteger.MinusOne && Den.IsOne;
    public bool IsInteger => Den.IsOne;
    public int Sign => Num.Sign;

    public static implicit operator BigRational(int v) => new(v);
    public static implicit operator BigRational(long v) => new(v);
    public static implicit operator BigRational(BigInteger v) => new(v);

    public static BigRational operator +(BigRational a, BigRational b) =>
        a.Den == b.Den ? new(a.Num + b.Num, a.Den) : new(a.Num * b.Den + b.Num * a.Den, a.Den * b.Den);
    public static BigRational operator -(BigRational a, BigRational b) =>
        a.Den == b.Den ? new(a.Num - b.Num, a.Den) : new(a.Num * b.Den - b.Num * a.Den, a.Den * b.Den);
    public static BigRational operator *(BigRational a, BigRational b) => new(a.Num * b.Num, a.Den * b.Den);
    public static BigRational operator /(BigRational a, BigRational b) => new(a.Num * b.Den, a.Den * b.Num);
    public static BigRational operator -(BigRational a) => new(-a.Num, a.Den);
    public static bool operator ==(BigRational a, BigRational b) => a.Equals(b);
    public static bool operator !=(BigRational a, BigRational b) => !a.Equals(b);
    public static bool operator <(BigRational a, BigRational b) => a.CompareTo(b) < 0;
    public static bool operator >(BigRational a, BigRational b) => a.CompareTo(b) > 0;
    public static bool operator <=(BigRational a, BigRational b) => a.CompareTo(b) <= 0;
    public static bool operator >=(BigRational a, BigRational b) => a.CompareTo(b) >= 0;

    public BigRational Abs() => new(BigInteger.Abs(Num), Den);
    public BigRational Reciprocal() => new(Den, Num);

    public static BigRational Pow(BigRational b, int e)
    {
        if (e == 0) return One;
        if (e < 0) return Pow(b.Reciprocal(), -e);
        return new(BigInteger.Pow(b.Num, e), BigInteger.Pow(b.Den, e));
    }

    public BigInteger Floor()
    {
        var q = BigInteger.DivRem(Num, Den, out var r);
        return r.Sign < 0 ? q - 1 : q;
    }

    public BigInteger Ceiling() => -(-this).Floor();

    public double ToDouble()
    {
        var n = Num; var d = Den;
        // Scale huge operands down to keep the division in range.
        long excess = Math.Max(n.GetBitLength(), d.GetBitLength()) - 1000;
        if (excess > 0)
        {
            n >>= (int)excess; d >>= (int)excess;
            if (d.IsZero) return n.Sign * double.PositiveInfinity;
        }
        return (double)n / (double)d;
    }

    /// <summary>Continued-fraction approximation, used to recognise simple numeric results.</summary>
    public static BigRational FromDouble(double v, long maxDen = 1_000_000)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) throw new ArgumentException("not finite");
        BigInteger h0 = 0, h1 = 1, k0 = 1, k1 = 0;
        double x = v;
        for (int i = 0; i < 40; i++)
        {
            var a = new BigInteger(Math.Floor(x));
            var h2 = a * h1 + h0; var k2 = a * k1 + k0;
            if (k2 > maxDen) break;
            h0 = h1; h1 = h2; k0 = k1; k1 = k2;
            double frac = x - Math.Floor(x);
            if (frac < 1e-12) break;
            x = 1 / frac;
        }
        return new BigRational(h1, k1);
    }

    public bool Equals(BigRational other) => Num == other.Num && Den == other.Den;
    public override bool Equals(object? obj) => obj is BigRational r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(Num, Den);
    public int CompareTo(BigRational other) => (Num * other.Den).CompareTo(other.Num * Den);

    public override string ToString() =>
        Den.IsOne ? Num.ToString(CultureInfo.InvariantCulture) : $"{Num.ToString(CultureInfo.InvariantCulture)}/{Den.ToString(CultureInfo.InvariantCulture)}";
}
