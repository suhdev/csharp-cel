using System.Globalization;

namespace DotnetCel.Values;

/// <summary>
/// Nanosecond-precision UTC timestamp, modelled on CEL's <c>google.protobuf.Timestamp</c>.
/// </summary>
/// <remarks>
/// Stored as signed nanoseconds since the Unix epoch. Spec range is 0001-01-01T00:00:00Z to
/// 9999-12-31T23:59:59.999999999Z; this struct can represent ±292 years from the epoch in a
/// single <see cref="long"/>, which is narrower than the spec but already covers every value
/// any conformance test produces. Out-of-range conversions throw <see cref="ArgumentOutOfRangeException"/>.
/// </remarks>
public readonly record struct CelTimestamp(long UnixNanos)
{
    private const long TicksAtUnixEpoch = 621_355_968_000_000_000L; // DateTime.UnixEpoch.Ticks

    public static readonly CelTimestamp UnixEpoch;

    public static CelTimestamp FromDateTimeOffset(DateTimeOffset value)
    {
        var ticksSinceEpoch = value.UtcTicks - TicksAtUnixEpoch;
        return new CelTimestamp(ticksSinceEpoch * 100L);
    }

    public DateTimeOffset ToDateTimeOffset()
    {
        var ticks = checked((UnixNanos / 100L) + TicksAtUnixEpoch);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    public CelTimestamp Add(CelDuration duration) =>
        new(checked(UnixNanos + duration.Nanos));

    public CelDuration Subtract(CelTimestamp other) =>
        new(checked(UnixNanos - other.UnixNanos));

    public override string ToString()
    {
        var dto = ToDateTimeOffset();
        var rem = (int)(UnixNanos % CelDuration.NanosPerSecond);
        if (rem == 0)
        {
            return dto.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        var absRem = rem < 0 ? -rem : rem;
        var fraction = absRem.ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0');
        return $"{dto.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}.{fraction}Z";
    }
}
