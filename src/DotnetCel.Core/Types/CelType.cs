using System.Collections.Immutable;

namespace DotnetCel.Types;

/// <summary>
/// The CEL type lattice. Equality is structural via record semantics, so e.g.
/// <c>list(int)</c> and <c>list(int)</c> compare equal regardless of how they were constructed.
/// </summary>
public abstract record CelType
{
    /// <summary>The CEL canonical string form of this type, e.g. <c>list(map(string, int))</c>.</summary>
    public abstract string Name { get; }

    public sealed override string ToString() => Name;
}

public sealed record PrimitiveType(PrimitiveKind PrimKind) : CelType
{
    public override string Name => PrimKind switch
    {
        PrimitiveKind.Bool => "bool",
        PrimitiveKind.Int => "int",
        PrimitiveKind.Uint => "uint",
        PrimitiveKind.Double => "double",
        PrimitiveKind.String => "string",
        PrimitiveKind.Bytes => "bytes",
        _ => "<invalid>",
    };
}

/// <summary>The static type of <c>null</c>.</summary>
public sealed record NullType : CelType
{
    internal NullType() { }
    public override string Name => "null_type";
}

/// <summary>The "any" / dynamically typed CEL type. Type checks defer to runtime.</summary>
public sealed record DynType : CelType
{
    internal DynType() { }
    public override string Name => "dyn";
}

/// <summary>The static type of evaluation errors. Cannot be named in source.</summary>
public sealed record ErrorType : CelType
{
    internal ErrorType() { }
    public override string Name => "*error*";
}

public sealed record DurationType : CelType
{
    internal DurationType() { }
    public override string Name => "google.protobuf.Duration";
}

public sealed record TimestampType : CelType
{
    internal TimestampType() { }
    public override string Name => "google.protobuf.Timestamp";
}

public sealed record ListType(CelType ElementType) : CelType
{
    public override string Name => $"list({ElementType.Name})";
}

public sealed record MapType(CelType KeyType, CelType ValueType) : CelType
{
    public override string Name => $"map({KeyType.Name}, {ValueType.Name})";
}

/// <summary>A named user/proto type, optionally generic.</summary>
public sealed record ObjectType(string TypeName, ImmutableArray<CelType> TypeArgs = default) : CelType
{
    public override string Name =>
        TypeArgs.IsDefaultOrEmpty
            ? TypeName
            : $"{TypeName}({string.Join(", ", TypeArgs.Select(static t => t.Name))})";
}

/// <summary>
/// An enum is a named integral type. CEL surfaces enums as <c>int</c> at the value level.
/// </summary>
public sealed record EnumType(string TypeName) : CelType
{
    public override string Name => TypeName;
}

/// <summary>A type variable used in parametric overloads (e.g. <c>list(T) -&gt; T</c>).</summary>
public sealed record TypeParamType(string ParamName) : CelType
{
    public override string Name => ParamName;
}

/// <summary>
/// The type of <c>type(expr)</c>. The optional inner records the represented type
/// when known (e.g. <c>type(1)</c> is <c>type(int)</c>).
/// </summary>
public sealed record TypeType(CelType? Parameter = null) : CelType
{
    public override string Name => Parameter is null ? "type" : $"type({Parameter.Name})";
}

/// <summary>The type of a function reference / overload at check time.</summary>
public sealed record FunctionType(CelType ResultType, ImmutableArray<CelType> ArgTypes) : CelType
{
    public override string Name =>
        $"({string.Join(", ", ArgTypes.Select(static t => t.Name))}) -> {ResultType.Name}";
}

/// <summary>Wrapper types correspond to <c>google.protobuf.{Bool,Int,...}Value</c>.</summary>
public sealed record WrapperType(PrimitiveKind PrimKind) : CelType
{
    public override string Name => PrimKind switch
    {
        PrimitiveKind.Bool => "wrapper(bool)",
        PrimitiveKind.Int => "wrapper(int)",
        PrimitiveKind.Uint => "wrapper(uint)",
        PrimitiveKind.Double => "wrapper(double)",
        PrimitiveKind.String => "wrapper(string)",
        PrimitiveKind.Bytes => "wrapper(bytes)",
        _ => "wrapper(<invalid>)",
    };
}

/// <summary>Optional value type (extension). Models presence as a first-class type.</summary>
public sealed record OptionalType(CelType InnerType) : CelType
{
    public override string Name => $"optional_type({InnerType.Name})";
}

/// <summary>An opaque, host-defined type with optional parameters (e.g. <c>set(int)</c>).</summary>
public sealed record AbstractType(string TypeName, ImmutableArray<CelType> Parameters = default) : CelType
{
    public override string Name =>
        Parameters.IsDefaultOrEmpty
            ? TypeName
            : $"{TypeName}({string.Join(", ", Parameters.Select(static p => p.Name))})";
}
