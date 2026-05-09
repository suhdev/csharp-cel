using System.Collections.Immutable;

namespace DotnetCel.Ast;

/// <summary>
/// A literal constant occurring in a CEL expression.
/// </summary>
/// <remarks>
/// <para>
/// CEL has six primitive literal forms (bool, int, uint, double, string, bytes) plus
/// <c>null</c>. There is no literal syntax for <c>duration</c> or <c>timestamp</c>;
/// those arise from constructor calls.
/// </para>
/// <para>
/// Equality is structural so two <see cref="StringConstant"/>s with the same value compare equal,
/// independent of how they were parsed.
/// </para>
/// </remarks>
public abstract record ConstValue;

public sealed record NullConstant : ConstValue
{
    public static readonly NullConstant Instance = new();
    private NullConstant() { }
}

public sealed record BoolConstant(bool Value) : ConstValue;

public sealed record IntConstant(long Value) : ConstValue;

public sealed record UintConstant(ulong Value) : ConstValue;

public sealed record DoubleConstant(double Value) : ConstValue;

public sealed record StringConstant(string Value) : ConstValue;

public sealed record BytesConstant(ImmutableArray<byte> Value) : ConstValue
{
    public bool Equals(BytesConstant? other) =>
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
