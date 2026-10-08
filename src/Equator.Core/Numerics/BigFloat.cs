using System.Globalization;
using System.Numerics;
using System.Text;

namespace Equator.Core.Numerics;

/// <summary>
/// Arbitrary-precision binary floating-point number: Mantissa · 2^Exponent, rounded to Precision bits.
/// Immutable. Elementary functions live in <see cref="BigMath"/>.
/// </summary>
public readonly struct BigFloat : IEquatable<BigFloat>, IComparable<BigFloat>
{
    public BigInteger Mantissa { get; }
    public long Exponent { get; }
    public int Precision { get; }

    public BigFloat(BigInteger mantissa, long exponent, int precision)
    {
        Precision = Math.Max(precision, 16);
        if (mantissa.IsZero) { Mantissa = 0; Exponent = 0; return; }
        long bits = (long)mantissa.GetBitLength() - 1 + (mantissa.Sign < 0 ? 0 : 0);
        long excess = (long)BigInteger.Abs(mantissa).GetBitLength() - Precision;
        if (excess > 0)
        {
            // Round half away from zero on the dropped bits.
            var abs = BigInteger.Abs(mantissa);
            var half = BigInteger.One << (int)(excess - 1);
            abs = (abs + half) >> (int)excess;
            mantissa = mantissa.Sign < 0 ? -abs : abs;
            exponent += excess;
        }
        // Strip trailing zero bits so equal values have equal representations.
        if (!mantissa.IsZero)
        {
            int tz = TrailingZeros(mantissa);
            if (tz > 0) { mantissa >>= tz; exponent += tz; }
        }
        _ = bits;
        Mantissa = mantissa;
        Exponent = exponent;
    }

    private static int TrailingZeros(BigInteger v)
    {
        v = BigInteger.Abs(v);
        if (v.IsZero) return 0;
        int n = 0;
        while ((v & ulong.MaxValue).IsZero) { v >>= 64; n += 64; }
        var low = (ulong)(v & ulong.MaxValue);
        return n + BitOperations.TrailingZeroCount(low);
    }

    public static BigFloat Zero(int precision) => new(0, 0, precision);
    public static BigFloat FromInteger(BigInteger v, int precision) => new(v, 0, precision);

    public static BigFloat FromRational(BigRational q, int precision) =>
        Divide(new BigFloat(q.Num, 0, precision + 8), new BigFloat(q.Den, 0, precision + 8), precision);

    public static BigFloat FromDouble(double d, int precision)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) throw new MathException("Cannot convert a non-finite number to arbitrary precision.");
        if (d == 0) return Zero(precision);
        long bits = BitConverter.DoubleToInt64Bits(d);
        bool negative = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF);
        long frac = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp++; else frac |= 1L << 52;
        exp -= 1075;
        var m = new BigInteger(frac);
        return new BigFloat(negative ? -m : m, exp, precision);
    }

    public bool IsZero => Mantissa.IsZero;
    public int Sign => Mantissa.Sign;
    public BigFloat WithPrecision(int precision) => new(Mantissa, Exponent, precision);

    /// <summary>floor(log2 |x|) for non-zero x.</summary>
    public long Log2Magnitude => (long)BigInteger.Abs(Mantissa).GetBitLength() - 1 + Exponent;

    public static BigFloat operator -(BigFloat a) => new(-a.Mantissa, a.Exponent, a.Precision);
    public BigFloat Abs() => new(BigInteger.Abs(Mantissa), Exponent, Precision);

    public static BigFloat operator +(BigFloat a, BigFloat b) => Add(a, b, Math.Min(a.Precision, b.Precision));
    public static BigFloat operator -(BigFloat a, BigFloat b) => Add(a, -b, Math.Min(a.Precision, b.Precision));
    public static BigFloat operator *(BigFloat a, BigFloat b) => new(a.Mantissa * b.Mantissa, a.Exponent + b.Exponent, Math.Min(a.Precision, b.Precision));
    public static BigFloat operator /(BigFloat a, BigFloat b) => Divide(a, b, Math.Min(a.Precision, b.Precision));

    public static BigFloat Add(BigFloat a, BigFloat b, int precision)
    {
        if (a.IsZero) return b.WithPrecision(precision);
        if (b.IsZero) return a.WithPrecision(precision);
        if (a.Exponent < b.Exponent) (a, b) = (b, a);
        // b is the operand with the smaller exponent; if it lies entirely below a's rounding position, a wins.
        long gap = a.Exponent - b.Exponent;
        long aTop = a.Log2Magnitude, bTop = b.Log2Magnitude;
        if (aTop - bTop > precision + 4)
        {
            // Keep a sticky contribution so rounding direction stays correct.
            var m = (a.Mantissa << (precision + 8)) + (b.Sign);
            return new BigFloat(m, a.Exponent - (precision + 8), precision);
        }
        return new BigFloat((a.Mantissa << (int)gap) + b.Mantissa, b.Exponent, precision);
    }

    public static BigFloat Divide(BigFloat a, BigFloat b, int precision)
    {
        if (b.IsZero) throw new DivideByZeroException();
        if (a.IsZero) return Zero(precision);
        int shift = precision + 8 + (int)b.Mantissa.GetBitLength() - (int)a.Mantissa.GetBitLength();
        if (shift < 0) shift = 0;
        var q = BigInteger.DivRem(a.Mantissa << shift, b.Mantissa, out var rem);
        // Sticky bit for correct rounding.
        q = q * 2 + (rem.IsZero ? 0 : rem.Sign * b.Mantissa.Sign);
        return new BigFloat(q, a.Exponent - b.Exponent - shift - 1, precision);
    }

    public static BigFloat Sqrt(BigFloat a, int precision)
    {
        if (a.Sign < 0) throw new MathException("Square root of a negative number.");
        if (a.IsZero) return Zero(precision);
        // Scale so the exponent is even and the mantissa has about 2·precision bits.
        long shift = 2L * (precision + 8) - (long)a.Mantissa.GetBitLength();
        if (shift < 0) shift = 0;
        if (((a.Exponent - shift) & 1) != 0) shift++;
        var m = a.Mantissa << (int)shift;
        var r = IntSqrt(m);
        // Sticky bit.
        var sticky = r * r == m ? 0 : 1;
        return new BigFloat(r * 2 + sticky, (a.Exponent - shift) / 2 - 1, precision);
    }

    private static BigInteger IntSqrt(BigInteger n)
    {
        if (n.IsZero) return 0;
        var x = BigInteger.One << (int)((n.GetBitLength() + 1) / 2);
        while (true)
        {
            var y = (x + n / x) >> 1;
            if (y >= x) return x;
            x = y;
        }
    }

    /// <summary>a · 2^k.</summary>
    public BigFloat Scale(long k) => new(Mantissa, Exponent + k, Precision);

    public static BigFloat operator *(BigFloat a, long k) => new(a.Mantissa * k, a.Exponent, a.Precision);

    public static BigFloat operator /(BigFloat a, long k) => Divide(a, new BigFloat(k, 0, a.Precision + 64), a.Precision);

    /// <summary>The integer part, rounded towards −∞.</summary>
    public BigInteger Floor()
    {
        if (Exponent >= 0) return Mantissa << (int)Exponent;
        var q = BigInteger.DivRem(Mantissa, BigInteger.One << (int)Math.Min(-Exponent, int.MaxValue), out var r);
        return r.Sign < 0 ? q - 1 : q;
    }

    public BigInteger Round() => (this + new BigFloat(1, -1, Precision + 64)).Floor();

    public double ToDouble()
    {
        if (IsZero) return 0;
        var m = Mantissa;
        long e = Exponent;
        long extra = (long)BigInteger.Abs(m).GetBitLength() - 62;
        if (extra > 0) { m >>= (int)extra; e += extra; }
        if (e > 2000) return m.Sign * double.PositiveInfinity;
        if (e < -2200) return 0;
        return Math.ScaleB((double)(long)m, (int)e);
    }

    public BigRational ToRational() =>
        Exponent >= 0 ? new BigRational(Mantissa << (int)Exponent) : new BigRational(Mantissa, BigInteger.One << (int)-Exponent);

    public int CompareTo(BigFloat other)
    {
        var d = Add(this, -other, Math.Max(Precision, other.Precision) + 8);
        return d.Sign;
    }

    public bool Equals(BigFloat other) => Mantissa == other.Mantissa && Exponent == other.Exponent;
    public override bool Equals(object? obj) => obj is BigFloat b && Equals(b);
    public override int GetHashCode() => HashCode.Combine(Mantissa, Exponent);

    /// <summary>Decimal digits for a number of bits of precision.</summary>
    public static int BitsToDigits(int bits) => (int)Math.Floor(bits * 0.30102999566398120);
    public static int DigitsToBits(int digits) => (int)Math.Ceiling(digits * 3.3219280948873623) + 4;

    /// <summary>
    /// Decimal form with the given number of significant digits: (sign, digit string, decimal exponent of the
    /// first digit). Value = 0.d₁d₂… × 10^(exponent + 1).
    /// </summary>
    public (bool Negative, string Digits, long Exponent10) ToDecimal(int digits)
    {
        if (IsZero) return (false, new string('0', digits), 0);
        var abs = BigInteger.Abs(Mantissa);
        // Estimate the decimal exponent of the leading digit.
        long e10 = (long)Math.Floor((Log2Magnitude) * 0.30102999566398120);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            long k = digits - 1 - e10; // scale so the result has `digits` digits
            BigInteger scaled;
            // value · 10^k = abs · 2^Exponent · 10^k
            BigInteger num = abs, den = BigInteger.One;
            if (k >= 0) num *= BigInteger.Pow(10, (int)k); else den *= BigInteger.Pow(10, (int)-k);
            if (Exponent >= 0) num <<= (int)Exponent; else den <<= (int)-Exponent;
            scaled = BigInteger.DivRem(num, den, out var rem);
            if (rem * 2 >= den) scaled += 1;
            var s = scaled.ToString(CultureInfo.InvariantCulture);
            if (s.Length == digits) return (Mantissa.Sign < 0, s, e10);
            if (s.Length > digits) e10++; else e10--;
        }
        var fallback = ToDouble().ToString("E" + Math.Max(digits - 1, 0), CultureInfo.InvariantCulture);
        return (Mantissa.Sign < 0, fallback.TrimStart('-').Replace(".", "").Split('E')[0], long.Parse(fallback.Split('E')[1], CultureInfo.InvariantCulture));
    }

    /// <summary>Plain decimal text with the given significant digits (scientific notation for very large or small values).</summary>
    public string ToString(int digits)
    {
        var (neg, ds, e10) = ToDecimal(digits);
        var sb = new StringBuilder();
        if (neg) sb.Append('-');
        if (e10 >= -5 && e10 < digits)
        {
            if (e10 < 0)
            {
                sb.Append("0.").Append('0', (int)(-e10 - 1)).Append(ds);
            }
            else
            {
                sb.Append(ds, 0, (int)e10 + 1);
                if (e10 + 1 < ds.Length) sb.Append('.').Append(ds, (int)e10 + 1, ds.Length - (int)e10 - 1);
                else sb.Append(".0");
            }
            return sb.ToString();
        }
        sb.Append(ds[0]).Append('.').Append(ds.Length > 1 ? ds[1..] : "0").Append("*10^").Append(e10);
        return sb.ToString();
    }

    public override string ToString() => ToString(Math.Max(BitsToDigits(Precision), 1));

    /// <summary>Parses a decimal literal (digits, optional point, optional e±exponent) at the given precision.</summary>
    public static BigFloat Parse(string text, int precision)
    {
        text = text.Trim();
        int ePos = text.IndexOfAny(['e', 'E']);
        long exp10 = 0;
        if (ePos >= 0)
        {
            exp10 = long.Parse(text[(ePos + 1)..], CultureInfo.InvariantCulture);
            text = text[..ePos];
        }
        int dot = text.IndexOf('.');
        if (dot >= 0)
        {
            exp10 -= text.Length - dot - 1;
            text = text.Remove(dot, 1);
        }
        var m = BigInteger.Parse(text, CultureInfo.InvariantCulture);
        var q = exp10 >= 0 ? new BigRational(m * BigInteger.Pow(10, (int)exp10)) : new BigRational(m, BigInteger.Pow(10, (int)-exp10));
        return FromRational(q, precision);
    }
}
