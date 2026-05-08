using System.Collections.Immutable;
using Cel.Types;

namespace Cel;

/// <summary>A typed variable made available to a CEL expression.</summary>
public sealed record VariableDecl(string Name, CelType Type);

/// <summary>A function (or operator) with one or more overloads sharing a common simple name.</summary>
public sealed record FunctionDecl(string Name, ImmutableArray<OverloadDecl> Overloads)
{
    public FunctionDecl Merge(FunctionDecl other)
    {
        if (!string.Equals(Name, other.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"cannot merge function decls with different names: '{Name}' vs '{other.Name}'");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = ImmutableArray.CreateBuilder<OverloadDecl>(Overloads.Length + other.Overloads.Length);
        foreach (var o in Overloads)
        {
            if (seen.Add(o.Id))
            {
                merged.Add(o);
            }
        }
        foreach (var o in other.Overloads)
        {
            if (seen.Add(o.Id))
            {
                merged.Add(o);
            }
        }
        return new FunctionDecl(Name, merged.ToImmutable());
    }
}

/// <summary>
/// One overload of a function. <see cref="Id"/> is a stable identifier used by the runtime to
/// dispatch (mirroring cel-go's overload IDs such as "add_int_int_int").
/// </summary>
/// <remarks>
/// When <see cref="IsInstance"/> is <c>true</c> the first entry of <see cref="ArgTypes"/> is the
/// receiver (the call surface looks like <c>receiver.fn(rest...)</c>). Otherwise the call is a
/// global function and there is no implicit receiver.
/// </remarks>
public sealed record OverloadDecl(
    string Id,
    ImmutableArray<CelType> ArgTypes,
    CelType ResultType,
    ImmutableArray<string> TypeParams = default,
    bool IsInstance = false)
{
    public int Arity => ArgTypes.Length;

    public ImmutableArray<string> SafeTypeParams =>
        TypeParams.IsDefault ? [] : TypeParams;
}
