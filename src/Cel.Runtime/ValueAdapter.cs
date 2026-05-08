using System.Collections;
using System.Collections.Immutable;
using Cel.Values;

namespace Cel.Runtime;

/// <summary>
/// Converts raw CLR objects supplied by callers (via activations or POCO field access) into the
/// runtime's <see cref="CelValue"/> representation, and back. The mapping is recursive for
/// collections and lazy for arbitrary objects (those become <see cref="ObjectValue"/> with the
/// original instance retained for later field access).
/// </summary>
public static class ValueAdapter
{
    /// <summary>
    /// Wrap an arbitrary CLR value as a <see cref="CelValue"/>. Returns the same instance if
    /// the value is already a <see cref="CelValue"/>.
    /// </summary>
    public static CelValue ToCelValue(object? value)
    {
        switch (value)
        {
            case null: return CelValue.Null;
            case CelValue cv: return cv;
            case bool b: return CelValue.Of(b);
            case sbyte i: return CelValue.Of((long)i);
            case short i: return CelValue.Of((long)i);
            case int i: return CelValue.Of((long)i);
            case long i: return CelValue.Of(i);
            case byte u: return CelValue.Of((ulong)u);
            case ushort u: return CelValue.Of((ulong)u);
            case uint u: return CelValue.Of((ulong)u);
            case ulong u: return CelValue.Of(u);
            case float f: return CelValue.Of((double)f);
            case double d: return CelValue.Of(d);
            case string s: return CelValue.Of(s);
            case byte[] ba: return new BytesValue(ImmutableArray.Create(ba));
            case ImmutableArray<byte> ia: return new BytesValue(ia);
            case ReadOnlyMemory<byte> rom: return new BytesValue(ImmutableArray.Create(rom.Span));
            case Memory<byte> m: return new BytesValue(ImmutableArray.Create(m.Span));
            case CelTimestamp ct: return CelValue.Of(ct);
            case CelDuration cd: return CelValue.Of(cd);
            case DateTimeOffset dto: return CelValue.Of(CelTimestamp.FromDateTimeOffset(dto));
            case DateTime dt: return CelValue.Of(CelTimestamp.FromDateTimeOffset(dt.Kind == DateTimeKind.Utc ? new DateTimeOffset(dt, TimeSpan.Zero) : dt.ToUniversalTime()));
            case TimeSpan ts: return CelValue.Of(CelDuration.FromTimeSpan(ts));
            case Enum e: return CelValue.Of(Convert.ToInt64(e, System.Globalization.CultureInfo.InvariantCulture));
        }

        // Map-like before list-like (IDictionary IS IEnumerable).
        if (value is IDictionary nonGenericDict)
        {
            return WrapMap(nonGenericDict);
        }

        if (value is IEnumerable enumerable)
        {
            return WrapList(enumerable);
        }

        // Anything else: a host object we'll do field access on later.
        var t = value.GetType();
        return new ObjectValue(t.FullName ?? t.Name, value);
    }

    private static CelValue WrapList(IEnumerable enumerable)
    {
        var builder = ImmutableArray.CreateBuilder<CelValue>();
        foreach (var item in enumerable)
        {
            builder.Add(ToCelValue(item));
        }
        return new ListValue(builder.ToImmutable());
    }

    private static CelValue WrapMap(IDictionary dict)
    {
        var builder = ImmutableDictionary.CreateBuilder<CelValue, CelValue>();
        foreach (DictionaryEntry entry in dict)
        {
            var key = ToCelValue(entry.Key);
            var val = ToCelValue(entry.Value);
            builder[key] = val;
        }
        return new MapValue(builder.ToImmutable());
    }

    /// <summary>
    /// Convert a <see cref="CelValue"/> back to a raw CLR value for surfacing at the public API
    /// boundary. Errors throw; unknowns surface as null.
    /// </summary>
    public static object? ToClr(CelValue value) => value switch
    {
        NullValue => null,
        BoolValue b => b.Value,
        IntValue i => i.Value,
        UintValue u => u.Value,
        DoubleValue d => d.Value,
        StringValue s => s.Value,
        BytesValue b => b.Value.ToArray(),
        DurationValue d => d.Value,
        TimestampValue t => t.Value,
        ListValue l => l.Elements.Select(ToClr).ToList(),
        MapValue m => m.Entries.ToDictionary(
            static kv => ToClr(kv.Key) ?? throw new InvalidOperationException("null map key"),
            static kv => ToClr(kv.Value)),
        ObjectValue o => o.Native,
        TypeValue t => t.Inner,
        OptionalValue o => o.HasValue ? ToClr(o.Inner!) : null,
        ErrorValue e => throw new Diagnostics.CelEvaluationException(e.Message),
        UnknownValue => null,
        _ => null,
    };
}
