using System.Collections.Immutable;
using DotnetCel.Types;

namespace DotnetCel.Values;

/// <summary>
/// A first-class CEL evaluation result. Subclasses form a closed sum type:
/// <see cref="BoolValue"/>, <see cref="IntValue"/>, <see cref="UintValue"/>, <see cref="DoubleValue"/>,
/// <see cref="StringValue"/>, <see cref="BytesValue"/>, <see cref="NullValue"/>,
/// <see cref="DurationValue"/>, <see cref="TimestampValue"/>, <see cref="ListValue"/>,
/// <see cref="MapValue"/>, <see cref="ObjectValue"/>, <see cref="OptionalValue"/>,
/// <see cref="TypeValue"/>, <see cref="ErrorValue"/>, <see cref="UnknownValue"/>.
/// </summary>
/// <remarks>
/// <para>
/// Errors and unknowns are <em>values</em>, not exceptions. Operators such as <c>&amp;&amp;</c>
/// and <c>||</c> short-circuit on them per the CEL spec; only the public API boundary turns a
/// surfaced error into <see cref="Diagnostics.CelEvaluationException"/>.
/// </para>
/// <para>
/// Conversion to/from raw CLR types is the responsibility of <c>DotnetCel.Runtime</c>'s value
/// adapter; the core only exposes <see cref="ToClrObject"/> for the unambiguous mappings.
/// </para>
/// </remarks>
public abstract record CelValue
{
    /// <summary>The static type of this value, as observed by the runtime.</summary>
    public abstract CelType Type { get; }

    /// <summary>Best-effort conversion to a raw CLR object for surface APIs.</summary>
    public abstract object? ToClrObject();

    private protected CelValue() { }

    public static readonly CelValue Null = NullValue.Instance;
    public static readonly CelValue True = new BoolValue(true);
    public static readonly CelValue False = new BoolValue(false);

    public static CelValue Of(bool b) => b ? True : False;
    public static CelValue Of(long i) => new IntValue(i);
    public static CelValue Of(ulong u) => new UintValue(u);
    public static CelValue Of(double d) => new DoubleValue(d);
    public static CelValue Of(string s) => new StringValue(s);
    public static CelValue Of(ReadOnlySpan<byte> bytes) => new BytesValue([.. bytes]);
    public static CelValue Of(ImmutableArray<byte> bytes) => new BytesValue(bytes);
    public static CelValue Of(CelDuration d) => new DurationValue(d);
    public static CelValue Of(CelTimestamp t) => new TimestampValue(t);

    public static CelValue Error(string message, string? code = null) => new ErrorValue(message, code);
}

public sealed record NullValue : CelValue
{
    public static readonly NullValue Instance = new();
    private NullValue() { }
    public override CelType Type => CelTypes.Null;
    public override object? ToClrObject() => null;
}

public sealed record BoolValue(bool Value) : CelValue
{
    public override CelType Type => CelTypes.Bool;
    public override object ToClrObject() => Value;
}

public sealed record IntValue(long Value) : CelValue
{
    public override CelType Type => CelTypes.Int;
    public override object ToClrObject() => Value;
}

public sealed record UintValue(ulong Value) : CelValue
{
    public override CelType Type => CelTypes.Uint;
    public override object ToClrObject() => Value;
}

public sealed record DoubleValue(double Value) : CelValue
{
    public override CelType Type => CelTypes.Double;
    public override object ToClrObject() => Value;
}

public sealed record StringValue(string Value) : CelValue
{
    public override CelType Type => CelTypes.String;
    public override object ToClrObject() => Value;
}

public sealed record BytesValue(ImmutableArray<byte> Value) : CelValue
{
    public override CelType Type => CelTypes.Bytes;
    public override object ToClrObject() => Value.IsDefault ? Array.Empty<byte>() : Value.ToArray();

    public bool Equals(BytesValue? other) =>
        other is not null && Value.AsSpan().SequenceEqual(other.Value.AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var b in Value)
        {
            hash.Add(b);
        }
        return hash.ToHashCode();
    }
}

public sealed record DurationValue(CelDuration Value) : CelValue
{
    public override CelType Type => CelTypes.Duration;
    public override object ToClrObject() => Value;
}

public sealed record TimestampValue(CelTimestamp Value) : CelValue
{
    public override CelType Type => CelTypes.Timestamp;
    public override object ToClrObject() => Value;
}

public sealed record ListValue(ImmutableArray<CelValue> Elements) : CelValue
{
    public override CelType Type => CelTypes.List(CelTypes.Dyn);

    public override object ToClrObject()
    {
        var list = new List<object?>(Elements.Length);
        foreach (var e in Elements)
        {
            list.Add(e.ToClrObject());
        }
        return list;
    }

    public bool Equals(ListValue? other) =>
        other is not null && Elements.AsSpan().SequenceEqual(other.Elements.AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var e in Elements)
        {
            hash.Add(e);
        }
        return hash.ToHashCode();
    }
}

public sealed record MapValue(ImmutableDictionary<CelValue, CelValue> Entries) : CelValue
{
    public override CelType Type => CelTypes.Map(CelTypes.Dyn, CelTypes.Dyn);

    public override object ToClrObject()
    {
        var dict = new Dictionary<object, object?>(Entries.Count);
        foreach (var (k, v) in Entries)
        {
            var key = k.ToClrObject() ?? throw new InvalidOperationException("null map key");
            dict[key] = v.ToClrObject();
        }
        return dict;
    }
}

/// <summary>
/// Wraps a host-supplied object whose CEL semantics are mediated by an external adapter.
/// The runtime's POCO adapter holds a reference to the live CLR object so field access can be
/// performed lazily.
/// </summary>
public sealed record ObjectValue(string TypeName, object Native) : CelValue
{
    public override CelType Type => CelTypes.Object(TypeName);
    public override object ToClrObject() => Native;
}

public sealed record OptionalValue(CelValue? Inner) : CelValue
{
    public bool HasValue => Inner is not null;

    public override CelType Type =>
        Inner is null ? CelTypes.Optional(CelTypes.Dyn) : CelTypes.Optional(Inner.Type);

    public override object? ToClrObject() => Inner?.ToClrObject();

    public static readonly OptionalValue None = new((CelValue?)null);

    public static OptionalValue Of(CelValue inner) => new(inner);
}

/// <summary>
/// A proto enum value carrying its declared enum type's fully qualified name. Distinct from
/// <see cref="IntValue"/> so <c>type(x)</c> can report the enum identity, but compares equal
/// to integers (and to other enums) by numeric value — matching CEL's "strong enum" semantics
/// where enums are typed for reflection but value-compatible with <c>int</c>.
/// </summary>
public sealed record EnumValue(string TypeName, long Number) : CelValue
{
    public override CelType Type => CelTypes.Object(TypeName);
    public override object ToClrObject() => Number;
}

public sealed record TypeValue(CelType Inner) : CelValue
{
    public override CelType Type => CelTypes.Type;
    public override object ToClrObject() => Inner;
}

public sealed record ErrorValue(string Message, string? Code = null) : CelValue
{
    public override CelType Type => CelTypes.Error;

    /// <summary>Errors do not have a CLR projection; surfacing one throws.</summary>
    public override object ToClrObject() =>
        throw new Diagnostics.CelEvaluationException(Message);
}

/// <summary>
/// Marker for a value that depends on input the caller has not yet supplied. Used by partial
/// evaluation. The path identifies which attribute references caused the unknown.
/// </summary>
public sealed record UnknownValue(ImmutableArray<long> AttributePath) : CelValue
{
    public override CelType Type => CelTypes.Dyn;
    public override object? ToClrObject() => null;

    public bool Equals(UnknownValue? other) =>
        other is not null && AttributePath.AsSpan().SequenceEqual(other.AttributePath.AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var id in AttributePath)
        {
            hash.Add(id);
        }
        return hash.ToHashCode();
    }
}
