using System.Globalization;

namespace DotnetCel.Values;

/// <summary>
/// Nanosecond-precision duration matching CEL's <c>google.protobuf.Duration</c>. Stored as a
/// signed 64-bit nanosecond count, so the representable range is roughly ±292 years — wider than
/// <see cref="TimeSpan"/> in practice (TimeSpan tops out at ±10675 days but with 100 ns ticks).
/// </summary>
/// <remarks>
/// We do not lean on <see cref="TimeSpan"/> internally because it has 100 ns granularity, which
/// silently truncates spec-correct cases like <c>"0.000000001s"</c>.
/// </remarks>
public readonly record struct CelDuration(long Nanos)
{
    public const long NanosPerSecond = 1_000_000_000L;
    public const long NanosPerMillisecond = 1_000_000L;
    public const long NanosPerMicrosecond = 1_000L;

    public static readonly CelDuration Zero;

    public static CelDuration FromTimeSpan(TimeSpan ts) => new(ts.Ticks * 100L);

    public TimeSpan ToTimeSpan() => TimeSpan.FromTicks(Nanos / 100L);

    public static CelDuration FromSeconds(long seconds) => new(checked(seconds * NanosPerSecond));

    public override string ToString()
    {
        if (Nanos == 0)
        {
            return "0s";
        }

        var negative = Nanos < 0;
        var abs = negative ? -Nanos : Nanos;
        var seconds = abs / NanosPerSecond;
        var rem = abs % NanosPerSecond;

        var sign = negative ? "-" : "";
        if (rem == 0)
        {
            return $"{sign}{seconds.ToString(CultureInfo.InvariantCulture)}s";
        }

        var fraction = rem.ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0');
        return $"{sign}{seconds.ToString(CultureInfo.InvariantCulture)}.{fraction}s";
    }
}
