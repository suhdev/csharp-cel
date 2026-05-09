using DotnetCel.Values;

namespace DotnetCel.Runtime;

/// <summary>
/// CEL-semantic equality and ordering. Differs from CLR record equality in two important ways:
/// (1) cross-type numeric equality (<c>1 == 1u == 1.0</c>), and (2) deep structural comparison
/// of lists and maps using these same rules recursively.
/// </summary>
public static class CelEquality
{
    /// <summary>Returns true iff <paramref name="a"/> and <paramref name="b"/> compare equal under CEL's rules.</summary>
    public static bool Equals(CelValue a, CelValue b) => Equals(a, b, provider: null);

    /// <summary>
    /// Returns true iff <paramref name="a"/> and <paramref name="b"/> compare equal under CEL's rules.
    /// When <paramref name="provider"/> is supplied and an <see cref="ObjectValue"/> pair maps to
    /// a managed instance, the provider's <see cref="ITypeProvider.AreEqual"/> hook gets first
    /// crack — needed for protocol-specific semantics (e.g. NaN propagation through proto messages).
    /// </summary>
    public static bool Equals(CelValue a, CelValue b, ITypeProvider? provider)
    {
        // NaN ≠ anything (including itself) per IEEE 754; CEL spec adopts this.
        if (a is DoubleValue ad && double.IsNaN(ad.Value)) { return false; }
        if (b is DoubleValue bd && double.IsNaN(bd.Value)) { return false; }

        // Treat enums as int-compatible for value comparisons. Enum-vs-enum still requires the
        // same numeric value — type identity is informational and surfaces via type(), not ==.
        if (a is EnumValue ea) { a = new IntValue(ea.Number); }
        if (b is EnumValue eb) { b = new IntValue(eb.Number); }

        // Cross-numeric handles int/uint/double in any pairing.
        if (IsNumeric(a) && IsNumeric(b))
        {
            return CompareNumeric(a, b) == 0;
        }

        return (a, b) switch
        {
            (NullValue, NullValue) => true,
            (BoolValue x, BoolValue y) => x.Value == y.Value,
            (StringValue x, StringValue y) => string.Equals(x.Value, y.Value, StringComparison.Ordinal),
            (BytesValue x, BytesValue y) => x.Value.AsSpan().SequenceEqual(y.Value.AsSpan()),
            (DurationValue x, DurationValue y) => x.Value.Nanos == y.Value.Nanos,
            (TimestampValue x, TimestampValue y) => x.Value.UnixNanos == y.Value.UnixNanos,
            (ListValue x, ListValue y) => ListEquals(x, y),
            (MapValue x, MapValue y) => MapEquals(x, y),
            (TypeValue x, TypeValue y) => x.Inner.Equals(y.Inner),
            (OptionalValue x, OptionalValue y) =>
                x.HasValue == y.HasValue && (!x.HasValue || Equals(x.Inner!, y.Inner!)),
            // Compare ObjectValue by the wrapped instance's own equality. The provider gets
            // first crack so that proto-aware semantics (NaN propagation through messages) win
            // over the auto-generated proto Equals (which uses bit-equality on doubles and
            // therefore says NaN == NaN).
            (ObjectValue x, ObjectValue y) =>
                string.Equals(x.TypeName, y.TypeName, StringComparison.Ordinal)
                && (provider?.AreEqual(x.Native, y.Native) ?? object.Equals(x.Native, y.Native)),
            _ => a.GetType() == b.GetType() && a.Equals(b),
        };
    }

    /// <summary>
    /// CEL-style ordering. Returns &lt; 0 / 0 / &gt; 0 for less / equal / greater. Non-comparable
    /// values throw — this should never be reached when types are checked.
    /// </summary>
    public static int Compare(CelValue a, CelValue b)
    {
        if (a is EnumValue ea) { a = new IntValue(ea.Number); }
        if (b is EnumValue eb) { b = new IntValue(eb.Number); }
        if (IsNumeric(a) && IsNumeric(b))
        {
            return CompareNumeric(a, b);
        }
        return (a, b) switch
        {
            (StringValue x, StringValue y) => string.CompareOrdinal(x.Value, y.Value),
            (BytesValue x, BytesValue y) => x.Value.AsSpan().SequenceCompareTo(y.Value.AsSpan()),
            (BoolValue x, BoolValue y) => x.Value.CompareTo(y.Value),
            (DurationValue x, DurationValue y) => x.Value.Nanos.CompareTo(y.Value.Nanos),
            (TimestampValue x, TimestampValue y) => x.Value.UnixNanos.CompareTo(y.Value.UnixNanos),
            _ => throw new InvalidOperationException($"cannot order {a.Type.Name} and {b.Type.Name}"),
        };
    }

    public static bool IsNumeric(CelValue v) => v is IntValue or UintValue or DoubleValue;

    private static int CompareNumeric(CelValue a, CelValue b)
    {
        // Promote to double for cross-type compare. Loses precision for large int/uint magnitudes
        // (>2^53); callers needing exact integer comparison should use same-type overloads. This
        // pragmatic approach matches "good enough" cross-type semantics; conformance corpus will
        // surface any case where we must refine to a precision-preserving comparator.
        var ad = a switch
        {
            IntValue i => (double)i.Value,
            UintValue u => (double)u.Value,
            DoubleValue d => d.Value,
            _ => throw new InvalidOperationException(),
        };
        var bd = b switch
        {
            IntValue i => (double)i.Value,
            UintValue u => (double)u.Value,
            DoubleValue d => d.Value,
            _ => throw new InvalidOperationException(),
        };
        if (double.IsNaN(ad) || double.IsNaN(bd))
        {
            return double.IsNaN(ad) && double.IsNaN(bd) ? 0 : -1;
        }
        return ad.CompareTo(bd);
    }

    private static bool ListEquals(ListValue a, ListValue b)
    {
        if (a.Elements.Length != b.Elements.Length)
        {
            return false;
        }
        for (var i = 0; i < a.Elements.Length; i++)
        {
            if (!Equals(a.Elements[i], b.Elements[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool MapEquals(MapValue a, MapValue b)
    {
        if (a.Entries.Count != b.Entries.Count)
        {
            return false;
        }
        foreach (var (key, valA) in a.Entries)
        {
            var found = false;
            foreach (var (kb, valB) in b.Entries)
            {
                if (Equals(key, kb))
                {
                    if (!Equals(valA, valB))
                    {
                        return false;
                    }
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return false;
            }
        }
        return true;
    }
}
