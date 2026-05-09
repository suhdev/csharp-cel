using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cel.Diagnostics;
using Cel.Values;

namespace Cel.Runtime;

/// <summary>
/// Runtime implementations for the overload ids declared by <see cref="Cel.Stdlib"/>. The
/// registration mirrors the checker's overload table; each id binds to a delegate that consumes
/// already-evaluated arguments and produces a <see cref="CelValue"/>. Errors are returned as
/// <see cref="ErrorValue"/> so the evaluator can propagate them through short-circuiting
/// operators per CEL spec.
/// </summary>
internal static class Stdlib
{
    public static void Register(FunctionRegistry r)
    {
        Arithmetic(r);
        Negation(r);
        Logic(r);
        Ordering(r);
        Equality(r);
        Containment(r);
        Indexing(r);
        Sizing(r);
        TypeOps(r);
        Conversions(r);
        Strings(r);
        Optionals(r);
        Time(r);
    }

    // ── arithmetic ──

    private static void Arithmetic(FunctionRegistry r)
    {
        r.Bind("add_int_int_int", static a =>
            TryInt(static (x, y) => CelValue.Of(checked(x + y)), a, "integer overflow"));
        r.Bind("add_uint_uint_uint", static a =>
            TryUint(static (x, y) => CelValue.Of(checked(x + y)), a, "unsigned overflow"));
        r.Bind("add_double_double_double", static a =>
            CelValue.Of(D(a[0]) + D(a[1])));
        r.Bind("add_string_string_string", static a =>
            CelValue.Of(S(a[0]) + S(a[1])));
        r.Bind("add_bytes_bytes_bytes", static a =>
        {
            var lhs = ((BytesValue)a[0]).Value;
            var rhs = ((BytesValue)a[1]).Value;
            var combined = ImmutableArray.CreateBuilder<byte>(lhs.Length + rhs.Length);
            combined.AddRange(lhs);
            combined.AddRange(rhs);
            return new BytesValue(combined.ToImmutable());
        });
        r.Bind("add_list_A_list_A", static a =>
        {
            var lhs = ((ListValue)a[0]).Elements;
            var rhs = ((ListValue)a[1]).Elements;
            return new ListValue(lhs.AddRange(rhs));
        });
        r.Bind("add_timestamp_duration_timestamp", static a =>
            CelValue.Of(((TimestampValue)a[0]).Value.Add(((DurationValue)a[1]).Value)));
        r.Bind("add_duration_timestamp_timestamp", static a =>
            CelValue.Of(((TimestampValue)a[1]).Value.Add(((DurationValue)a[0]).Value)));
        r.Bind("add_duration_duration_duration", static a =>
        {
            try
            {
                return CelValue.Of(new CelDuration(checked(((DurationValue)a[0]).Value.Nanos + ((DurationValue)a[1]).Value.Nanos)));
            }
            catch (OverflowException)
            {
                return CelValue.Error("duration overflow");
            }
        });

        r.Bind("subtract_int_int_int", static a =>
            TryInt(static (x, y) => CelValue.Of(checked(x - y)), a, "integer overflow"));
        r.Bind("subtract_uint_uint_uint", static a =>
            TryUint(static (x, y) => CelValue.Of(checked(x - y)), a, "unsigned overflow"));
        r.Bind("subtract_double_double_double", static a =>
            CelValue.Of(D(a[0]) - D(a[1])));
        r.Bind("subtract_timestamp_timestamp_duration", static a =>
        {
            try
            {
                return CelValue.Of(((TimestampValue)a[0]).Value.Subtract(((TimestampValue)a[1]).Value));
            }
            catch (OverflowException)
            {
                return CelValue.Error("timestamp overflow");
            }
        });
        r.Bind("subtract_timestamp_duration_timestamp", static a =>
            CelValue.Of(((TimestampValue)a[0]).Value.Add(new CelDuration(-((DurationValue)a[1]).Value.Nanos))));
        r.Bind("subtract_duration_duration_duration", static a =>
        {
            try
            {
                return CelValue.Of(new CelDuration(checked(((DurationValue)a[0]).Value.Nanos - ((DurationValue)a[1]).Value.Nanos)));
            }
            catch (OverflowException)
            {
                return CelValue.Error("duration overflow");
            }
        });

        r.Bind("multiply_int_int_int", static a =>
            TryInt(static (x, y) => CelValue.Of(checked(x * y)), a, "integer overflow"));
        r.Bind("multiply_uint_uint_uint", static a =>
            TryUint(static (x, y) => CelValue.Of(checked(x * y)), a, "unsigned overflow"));
        r.Bind("multiply_double_double_double", static a =>
            CelValue.Of(D(a[0]) * D(a[1])));

        r.Bind("divide_int_int_int", static a =>
        {
            var x = I(a[0]);
            var y = I(a[1]);
            if (y == 0) { return CelValue.Error("divide by zero"); }
            if (x == long.MinValue && y == -1) { return CelValue.Error("integer overflow"); }
            return CelValue.Of(x / y);
        });
        r.Bind("divide_uint_uint_uint", static a =>
        {
            var y = U(a[1]);
            if (y == 0) { return CelValue.Error("divide by zero"); }
            return CelValue.Of(U(a[0]) / y);
        });
        r.Bind("divide_double_double_double", static a =>
            CelValue.Of(D(a[0]) / D(a[1])));

        r.Bind("modulo_int_int_int", static a =>
        {
            var x = I(a[0]);
            var y = I(a[1]);
            if (y == 0) { return CelValue.Error("modulo by zero"); }
            if (x == long.MinValue && y == -1) { return CelValue.Of(0L); }
            return CelValue.Of(x % y);
        });
        r.Bind("modulo_uint_uint_uint", static a =>
        {
            var y = U(a[1]);
            if (y == 0) { return CelValue.Error("modulo by zero"); }
            return CelValue.Of(U(a[0]) % y);
        });
    }

    private static void Negation(FunctionRegistry r)
    {
        r.Bind("negate_int_int", static a =>
        {
            var v = I(a[0]);
            if (v == long.MinValue) { return CelValue.Error("integer overflow"); }
            return CelValue.Of(-v);
        });
        r.Bind("negate_double_double", static a => CelValue.Of(-D(a[0])));
        r.Bind("negate_duration_duration", static a =>
        {
            var d = ((DurationValue)a[0]).Value;
            if (d.Nanos == long.MinValue) { return CelValue.Error("duration overflow"); }
            return CelValue.Of(new CelDuration(-d.Nanos));
        });
    }

    private static void Logic(FunctionRegistry r)
    {
        r.Bind("logical_not", static a => CelValue.Of(!((BoolValue)a[0]).Value));
        // Even though _||_ / _&&_ are short-circuited in the evaluator, we still bind them so that
        // a user-supplied @not_strictly_false or future direct dispatch works uniformly.
        r.Bind("logical_and", static a => CelValue.Of(((BoolValue)a[0]).Value && ((BoolValue)a[1]).Value));
        r.Bind("logical_or", static a => CelValue.Of(((BoolValue)a[0]).Value || ((BoolValue)a[1]).Value));
        r.Bind("not_strictly_false", static a => a[0] switch
        {
            BoolValue b => CelValue.Of(b.Value),
            ErrorValue or UnknownValue => CelValue.True,
            _ => CelValue.True,
        });
    }

    private static void Ordering(FunctionRegistry r)
    {
        BindOrdering(r, "less", (x, _) => x < 0);
        BindOrdering(r, "less_equals", (x, _) => x <= 0);
        BindOrdering(r, "greater", (x, _) => x > 0);
        BindOrdering(r, "greater_equals", (x, _) => x >= 0);

        static void BindOrdering(FunctionRegistry r, string prefix, Func<int, int, bool> pred)
        {
            // Bool ordering: false < true (boolean as 0 < 1).
            r.Bind(prefix + "_bool_bool", a => CelValue.Of(pred(((BoolValue)a[0]).Value.CompareTo(((BoolValue)a[1]).Value), 0)));

            // All numeric orderings — same-type AND cross-type — go through a single
            // promotion-aware comparator so a dyn() injection at runtime that skews the
            // operand types from what the checker selected doesn't crash with a cast error.
            OverloadFn numericCompare = a =>
            {
                if ((a[0] is DoubleValue d1 && double.IsNaN(d1.Value))
                    || (a[1] is DoubleValue d2 && double.IsNaN(d2.Value)))
                {
                    return CelValue.False;
                }
                return CelValue.Of(pred(CelEquality.Compare(a[0], a[1]), 0));
            };
            foreach (var id in new[]
            {
                "int_int", "uint_uint", "double_double",
                "int_uint", "uint_int", "int_double", "double_int", "uint_double", "double_uint",
            })
            {
                r.Bind($"{prefix}_{id}", numericCompare);
            }

            // Non-numeric orderings stay typed.
            r.Bind(prefix + "_string_string", a => CelValue.Of(pred(string.CompareOrdinal(S(a[0]), S(a[1])), 0)));
            r.Bind(prefix + "_bytes_bytes", a => CelValue.Of(pred(((BytesValue)a[0]).Value.AsSpan().SequenceCompareTo(((BytesValue)a[1]).Value.AsSpan()), 0)));
            r.Bind(prefix + "_timestamp_timestamp", a => CelValue.Of(pred(((TimestampValue)a[0]).Value.UnixNanos.CompareTo(((TimestampValue)a[1]).Value.UnixNanos), 0)));
            r.Bind(prefix + "_duration_duration", a => CelValue.Of(pred(((DurationValue)a[0]).Value.Nanos.CompareTo(((DurationValue)a[1]).Value.Nanos), 0)));
        }
    }

    private static void Equality(FunctionRegistry r)
    {
        r.Bind("equals", static a => CelValue.Of(CelEquality.Equals(a[0], a[1])));
        r.Bind("not_equals", static a => CelValue.Of(!CelEquality.Equals(a[0], a[1])));
    }

    private static void Containment(FunctionRegistry r)
    {
        r.Bind("in_list", static a =>
        {
            var elem = a[0];
            foreach (var item in ((ListValue)a[1]).Elements)
            {
                if (CelEquality.Equals(elem, item))
                {
                    return CelValue.True;
                }
            }
            return CelValue.False;
        });
        r.Bind("in_map", static a =>
        {
            var key = a[0];
            foreach (var k in ((MapValue)a[1]).Entries.Keys)
            {
                if (CelEquality.Equals(key, k))
                {
                    return CelValue.True;
                }
            }
            return CelValue.False;
        });
    }

    private static void Indexing(FunctionRegistry r)
    {
        r.Bind("index_list", static a =>
        {
            var list = (ListValue)a[0];
            if (!TryNumericIndex(a[1], out var idx))
            {
                return CelValue.Error($"invalid_argument: list index must be numeric, got {a[1].Type.Name}");
            }
            if (idx < 0 || idx >= list.Elements.Length)
            {
                return CelValue.Error($"index out of range: {idx}");
            }
            return list.Elements[(int)idx];
        });
        r.Bind("index_map", static a => MapLookup((MapValue)a[0], a[1]) ?? CelValue.Error($"no such key: {a[1]}"));

        r.Bind("optindex_list", static a =>
        {
            var list = (ListValue)a[0];
            var idx = I(a[1]);
            if (idx < 0 || idx >= list.Elements.Length)
            {
                return OptionalValue.None;
            }
            return OptionalValue.Of(list.Elements[(int)idx]);
        });
        r.Bind("optindex_map", static a =>
        {
            var v = MapLookup((MapValue)a[0], a[1]);
            return v is null ? OptionalValue.None : OptionalValue.Of(v);
        });
    }

    private static void Sizing(FunctionRegistry r)
    {
        r.Bind("size_string", static a =>
        {
            var s = S(a[0]);
            // CEL spec: size of a string is the number of unicode code points (NOT UTF-16 units).
            var count = 0;
            foreach (var _ in s.EnumerateRunes())
            {
                count++;
            }
            return CelValue.Of((long)count);
        });
        r.Bind("size_bytes", static a => CelValue.Of((long)((BytesValue)a[0]).Value.Length));
        r.Bind("size_list", static a => CelValue.Of((long)((ListValue)a[0]).Elements.Length));
        r.Bind("size_map", static a => CelValue.Of((long)((MapValue)a[0]).Entries.Count));
    }

    private static void TypeOps(FunctionRegistry r)
    {
        r.Bind("type", static a => new TypeValue(a[0].Type));
        r.Bind("to_dyn", static a => a[0]);
    }

    private static void Conversions(FunctionRegistry r)
    {
        // int(...)
        r.Bind("int_to_int", static a => a[0]);
        r.Bind("uint_to_int", static a =>
        {
            var u = U(a[0]);
            if (u > long.MaxValue) { return CelValue.Error("uint to int overflow"); }
            return CelValue.Of((long)u);
        });
        r.Bind("double_to_int", static a =>
        {
            var d = D(a[0]);
            if (double.IsNaN(d) || double.IsInfinity(d)) { return CelValue.Error("double to int: not finite"); }
            // (double)long.MaxValue rounds up to 2^63, so a double of exactly 2^63 cannot be
            // represented as long — use a strict-less-than check at the upper bound.
            if (d < -9223372036854775808.0 || d >= 9223372036854775808.0)
            {
                return CelValue.Error("double to int overflow");
            }
            return CelValue.Of((long)d);
        });
        r.Bind("string_to_int", static a =>
            long.TryParse(S(a[0]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? CelValue.Of(v)
                : CelValue.Error($"cannot parse int: {S(a[0])}"));
        r.Bind("timestamp_to_int", static a =>
            CelValue.Of(((TimestampValue)a[0]).Value.UnixNanos / CelDuration.NanosPerSecond));
        r.Bind("duration_to_int", static a =>
            CelValue.Of(((DurationValue)a[0]).Value.Nanos / CelDuration.NanosPerSecond));

        // uint(...)
        r.Bind("int_to_uint", static a =>
        {
            var i = I(a[0]);
            if (i < 0) { return CelValue.Error("int to uint underflow"); }
            return CelValue.Of((ulong)i);
        });
        r.Bind("uint_to_uint", static a => a[0]);
        r.Bind("double_to_uint", static a =>
        {
            var d = D(a[0]);
            if (double.IsNaN(d) || double.IsInfinity(d)) { return CelValue.Error("double to uint: not finite"); }
            if (d < 0 || d > ulong.MaxValue) { return CelValue.Error("double to uint overflow"); }
            return CelValue.Of((ulong)d);
        });
        r.Bind("string_to_uint", static a =>
            ulong.TryParse(S(a[0]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? CelValue.Of(v)
                : CelValue.Error($"cannot parse uint: {S(a[0])}"));

        // double(...)
        r.Bind("int_to_double", static a => CelValue.Of((double)I(a[0])));
        r.Bind("uint_to_double", static a => CelValue.Of((double)U(a[0])));
        r.Bind("double_to_double", static a => a[0]);
        r.Bind("string_to_double", static a =>
            double.TryParse(S(a[0]), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? CelValue.Of(v)
                : CelValue.Error($"cannot parse double: {S(a[0])}"));

        // string(...)
        r.Bind("string_to_string", static a => a[0]);
        r.Bind("int_to_string", static a => CelValue.Of(I(a[0]).ToString(CultureInfo.InvariantCulture)));
        r.Bind("uint_to_string", static a => CelValue.Of(U(a[0]).ToString(CultureInfo.InvariantCulture)));
        r.Bind("double_to_string", static a => CelValue.Of(D(a[0]).ToString("R", CultureInfo.InvariantCulture)));
        r.Bind("bool_to_string", static a => CelValue.Of(((BoolValue)a[0]).Value ? "true" : "false"));
        r.Bind("bytes_to_string", static a =>
        {
            try { return CelValue.Of(Encoding.UTF8.GetString(((BytesValue)a[0]).Value.AsSpan())); }
            catch (DecoderFallbackException) { return CelValue.Error("bytes are not valid UTF-8"); }
        });
        r.Bind("timestamp_to_string", static a => CelValue.Of(((TimestampValue)a[0]).Value.ToString()));
        r.Bind("duration_to_string", static a => CelValue.Of(((DurationValue)a[0]).Value.ToString()));

        // bool(...)
        r.Bind("bool_to_bool", static a => a[0]);
        r.Bind("string_to_bool", static a => S(a[0]) switch
        {
            "true" or "TRUE" or "True" or "t" or "1" => CelValue.True,
            "false" or "FALSE" or "False" or "f" or "0" => CelValue.False,
            _ => CelValue.Error($"cannot parse bool: {S(a[0])}"),
        });

        // bytes(...)
        r.Bind("bytes_to_bytes", static a => a[0]);
        r.Bind("string_to_bytes", static a => CelValue.Of(Encoding.UTF8.GetBytes(S(a[0]))));

        // timestamp(...)
        r.Bind("string_to_timestamp", static a =>
        {
            if (DateTimeOffset.TryParse(S(a[0]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            {
                return CelValue.Of(CelTimestamp.FromDateTimeOffset(dto));
            }
            return CelValue.Error($"cannot parse timestamp: {S(a[0])}");
        });
        r.Bind("int_to_timestamp", static a =>
        {
            try { return CelValue.Of(new CelTimestamp(checked(I(a[0]) * CelDuration.NanosPerSecond))); }
            catch (OverflowException) { return CelValue.Error("timestamp out of range"); }
        });

        // duration(...)
        r.Bind("string_to_duration", static a =>
        {
            var s = S(a[0]);
            var nanos = ParseDuration(s);
            if (nanos is null) { return CelValue.Error($"cannot parse duration: {s}"); }
            return CelValue.Of(new CelDuration(nanos.Value));
        });
    }

    private static void Time(FunctionRegistry r)
    {
        // Timestamp accessors: each has a no-tz variant (UTC) and a `with_tz` variant taking
        // either an IANA name ("America/Los_Angeles") or a fixed offset ("+11:00").
        BindTimestamp(r, "timestamp_to_year",          static dto => dto.Year);
        BindTimestamp(r, "timestamp_to_month",         static dto => dto.Month - 1);
        BindTimestamp(r, "timestamp_to_date",          static dto => dto.Day);
        BindTimestamp(r, "timestamp_to_day_of_month",  static dto => dto.Day - 1);
        BindTimestamp(r, "timestamp_to_day_of_week",   static dto => (long)dto.DayOfWeek);
        BindTimestamp(r, "timestamp_to_day_of_year",   static dto => dto.DayOfYear - 1);
        BindTimestamp(r, "timestamp_to_hours",         static dto => dto.Hour);
        BindTimestamp(r, "timestamp_to_minutes",       static dto => dto.Minute);
        BindTimestamp(r, "timestamp_to_seconds",       static dto => dto.Second);
        BindTimestamp(r, "timestamp_to_milliseconds",  static dto => dto.Millisecond);

        // Duration accessors return whole-units truncated toward zero, matching cel-go.
        r.Bind("duration_to_hours",        static a => CelValue.Of(((DurationValue)a[0]).Value.Nanos / (CelDuration.NanosPerSecond * 3600)));
        r.Bind("duration_to_minutes",      static a => CelValue.Of(((DurationValue)a[0]).Value.Nanos / (CelDuration.NanosPerSecond * 60)));
        r.Bind("duration_to_seconds",      static a => CelValue.Of(((DurationValue)a[0]).Value.Nanos / CelDuration.NanosPerSecond));
        r.Bind("duration_to_milliseconds", static a => CelValue.Of(((DurationValue)a[0]).Value.Nanos / CelDuration.NanosPerMillisecond));
    }

    private static void BindTimestamp(FunctionRegistry r, string idPrefix, Func<DateTimeOffset, long> get)
    {
        r.Bind(idPrefix, a =>
        {
            var ts = ((TimestampValue)a[0]).Value;
            return CelValue.Of(get(ts.ToDateTimeOffset()));
        });
        r.Bind(idPrefix + "_with_tz", a =>
        {
            var ts = ((TimestampValue)a[0]).Value;
            var tzName = ((StringValue)a[1]).Value;
            var tz = ResolveTimeZone(tzName);
            if (tz is null)
            {
                return CelValue.Error($"unknown timezone: {tzName}");
            }
            var local = TimeZoneInfo.ConvertTime(ts.ToDateTimeOffset(), tz);
            return CelValue.Of(get(local));
        });
    }

    private static TimeZoneInfo? ResolveTimeZone(string tz)
    {
        if (string.IsNullOrEmpty(tz))
        {
            return TimeZoneInfo.Utc;
        }
        // Numeric offset like "+11:00", "-02:30", or "02:00" (unsigned → positive).
        var sign = 1;
        var span = tz.AsSpan();
        if (span[0] == '+') { span = span[1..]; }
        else if (span[0] == '-') { sign = -1; span = span[1..]; }
        if (span.Length >= 4 && (char.IsAsciiDigit(span[0]) || char.IsAsciiDigit(span[1])))
        {
            if (TimeSpan.TryParseExact(span, "h\\:mm", System.Globalization.CultureInfo.InvariantCulture, out var offset)
                || TimeSpan.TryParseExact(span, "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture, out offset))
            {
                if (sign == -1)
                {
                    offset = -offset;
                }
                return TimeZoneInfo.CreateCustomTimeZone(tz, offset, tz, tz);
            }
        }
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(tz);
        }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }

    private static void Optionals(FunctionRegistry r)
    {
        // Optional select on map: `m.?key` — looks up key, returns Optional.None on miss.
        // The dyn variant (objects/POCOs) is handled inside the evaluator since it needs the
        // PocoAdapter; here we just bind a fallback for non-map cases.
        r.Bind("optselect_map_string", static a =>
        {
            var map = (MapValue)a[0];
            return MapLookup(map, a[1]) is { } v ? OptionalValue.Of(v) : OptionalValue.None;
        });
        r.Bind("optselect_dyn_string", static a => a[0] switch
        {
            MapValue m => MapLookup(m, a[1]) is { } v ? OptionalValue.Of(v) : OptionalValue.None,
            // ObjectValue is intercepted by the evaluator; if we reach here it's an unsupported type.
            _ => OptionalValue.None,
        });

        r.Bind("optional_of", static a => OptionalValue.Of(a[0]));
        r.Bind("optional_none", static _ => OptionalValue.None);
        r.Bind("optional_of_non_zero_value", static a =>
            IsZero(a[0]) ? OptionalValue.None : OptionalValue.Of(a[0]));

        r.Bind("optional_has_value", static a => CelValue.Of(((OptionalValue)a[0]).HasValue));
        r.Bind("optional_value", static a =>
        {
            var opt = (OptionalValue)a[0];
            return opt.HasValue ? opt.Inner! : CelValue.Error("optional.value() on empty optional");
        });
        r.Bind("optional_or", static a =>
        {
            var lhs = (OptionalValue)a[0];
            return lhs.HasValue ? lhs : a[1];
        });
        r.Bind("optional_or_value", static a =>
        {
            var lhs = (OptionalValue)a[0];
            return lhs.HasValue ? lhs.Inner! : a[1];
        });
    }

    /// <summary>
    /// Returns true if <paramref name="v"/> is the canonical zero value for its type — used by
    /// <c>optional.ofNonZeroValue</c> per cel-go.
    /// </summary>
    private static bool IsZero(CelValue v) => v switch
    {
        NullValue => true,
        BoolValue b => !b.Value,
        IntValue i => i.Value == 0,
        UintValue u => u.Value == 0,
        DoubleValue d => d.Value == 0.0,
        StringValue s => s.Value.Length == 0,
        BytesValue b => b.Value.IsDefaultOrEmpty,
        ListValue l => l.Elements.IsDefaultOrEmpty,
        MapValue m => m.Entries.Count == 0,
        DurationValue d => d.Value.Nanos == 0,
        TimestampValue t => t.Value.UnixNanos == 0,
        _ => false,
    };

    private static void Strings(FunctionRegistry r)
    {
        r.Bind("contains_string", static a => CelValue.Of(S(a[0]).Contains(S(a[1]), StringComparison.Ordinal)));
        r.Bind("starts_with_string", static a => CelValue.Of(S(a[0]).StartsWith(S(a[1]), StringComparison.Ordinal)));
        r.Bind("ends_with_string", static a => CelValue.Of(S(a[0]).EndsWith(S(a[1]), StringComparison.Ordinal)));
        r.Bind("matches_string", MatchesImpl);
        r.Bind("matches_string_method", MatchesImpl);
    }

    private static CelValue MatchesImpl(ReadOnlySpan<CelValue> a)
    {
        try
        {
            return CelValue.Of(Regex.IsMatch(S(a[0]), S(a[1]), RegexOptions.None, TimeSpan.FromSeconds(1)));
        }
        catch (RegexParseException ex)
        {
            return CelValue.Error("invalid regex: " + ex.Message);
        }
        catch (RegexMatchTimeoutException)
        {
            return CelValue.Error("regex match timed out");
        }
    }

    // ── helpers ──

    private static long I(CelValue v) => ((IntValue)v).Value;
    private static ulong U(CelValue v) => ((UintValue)v).Value;
    private static double D(CelValue v) => ((DoubleValue)v).Value;
    private static string S(CelValue v) => ((StringValue)v).Value;

    private static CelValue TryInt(Func<long, long, CelValue> op, ReadOnlySpan<CelValue> args, string overflowMsg)
    {
        try { return op(I(args[0]), I(args[1])); }
        catch (OverflowException) { return CelValue.Error(overflowMsg); }
    }

    private static CelValue TryUint(Func<ulong, ulong, CelValue> op, ReadOnlySpan<CelValue> args, string overflowMsg)
    {
        try { return op(U(args[0]), U(args[1])); }
        catch (OverflowException) { return CelValue.Error(overflowMsg); }
    }

    /// <summary>
    /// Coerce a CEL value to a list index. Accepts <see cref="IntValue"/>, <see cref="UintValue"/>
    /// (when in int range), and <see cref="DoubleValue"/> (when whole-numbered and finite). Returns
    /// false otherwise — caller surfaces an error.
    /// </summary>
    private static bool TryNumericIndex(CelValue v, out long idx)
    {
        switch (v)
        {
            case IntValue i:
                idx = i.Value;
                return true;
            case UintValue u when u.Value <= long.MaxValue:
                idx = (long)u.Value;
                return true;
            case DoubleValue d when double.IsFinite(d.Value)
                && d.Value >= long.MinValue && d.Value <= long.MaxValue
                && Math.Truncate(d.Value) == d.Value:
                idx = (long)d.Value;
                return true;
            default:
                idx = 0;
                return false;
        }
    }

    private static CelValue? MapLookup(MapValue map, CelValue key)
    {
        // Linear scan with CEL equality (cross-numeric semantics) so that 1 in {1u: x} works.
        foreach (var (k, v) in map.Entries)
        {
            if (CelEquality.Equals(key, k))
            {
                return v;
            }
        }
        return null;
    }

    /// <summary>
    /// Parse a CEL duration literal like <c>"1.5s"</c>, <c>"100ms"</c>, <c>"3h45m12.5s"</c>.
    /// Supported units: ns, us, µs, ms, s, m, h. Returns null on parse failure.
    /// </summary>
    private static long? ParseDuration(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }
        var i = 0;
        var negative = false;
        if (s[i] == '-') { negative = true; i++; }
        else if (s[i] == '+') { i++; }
        if (i == s.Length)
        {
            return null;
        }
        long total = 0;
        while (i < s.Length)
        {
            // parse number (integer + optional fraction)
            var numStart = i;
            while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.'))
            {
                i++;
            }
            if (numStart == i)
            {
                return null;
            }
            if (!double.TryParse(s.AsSpan(numStart, i - numStart), NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            {
                return null;
            }
            // parse unit
            long unitNanos;
            if (i < s.Length - 1 && s[i] == 'n' && s[i + 1] == 's') { unitNanos = 1; i += 2; }
            else if (i < s.Length - 1 && (s[i] == 'u' || s[i] == 'µ') && s[i + 1] == 's') { unitNanos = 1_000; i += 2; }
            else if (i < s.Length - 1 && s[i] == 'm' && s[i + 1] == 's') { unitNanos = 1_000_000; i += 2; }
            else if (i < s.Length && s[i] == 's') { unitNanos = 1_000_000_000; i += 1; }
            else if (i < s.Length && s[i] == 'm') { unitNanos = 60L * 1_000_000_000; i += 1; }
            else if (i < s.Length && s[i] == 'h') { unitNanos = 3600L * 1_000_000_000; i += 1; }
            else
            {
                return null;
            }
            try { total = checked(total + (long)(num * unitNanos)); }
            catch (OverflowException) { return null; }
        }
        return negative ? -total : total;
    }
}
