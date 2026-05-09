using DotnetCel.Types;
using DotnetCel.Values;

namespace DotnetCel;

/// <summary>
/// Pluggable type system extension that gives CEL access to host-defined object types
/// (typically proto messages) — their fields, presence semantics, and construction.
/// </summary>
/// <remarks>
/// <para>
/// Without a type provider, the runtime falls back to <c>DotnetCel.Runtime.PocoAdapter</c> reflection
/// over public properties / fields. A type provider gets first crack at any object value so that
/// proto-aware semantics (wrapper unwrapping, presence-bit handling, oneof dispatch) take
/// precedence over generic reflection.
/// </para>
/// <para>
/// Implementations are expected to be stateless and thread-safe; one instance is shared by all
/// programs sharing the same environment.
/// </para>
/// </remarks>
public interface ITypeProvider
{
    /// <summary>Whether this provider claims the given fully-qualified type name.</summary>
    bool KnowsType(string typeName);

    /// <summary>
    /// Resolve a fully-qualified type name to a <see cref="CelType"/>. Used at type-check time
    /// when the env declares a variable of an object type that this provider owns.
    /// </summary>
    CelType? ResolveType(string typeName);

    /// <summary>
    /// Whether <paramref name="instance"/> belongs to a type managed by this provider — i.e.
    /// reads/writes/has operations should route here rather than the generic POCO adapter.
    /// </summary>
    bool IsManagedInstance(object instance);

    /// <summary>
    /// Return the canonical CEL type name for a managed instance (e.g. the protobuf message
    /// full name). Returns null if this provider doesn't manage <paramref name="instance"/>.
    /// </summary>
    string? TypeNameOf(object instance);

    /// <summary>
    /// Read a field. Returns false if the field is not present on the type. Implementations
    /// must apply protocol-specific projections (e.g. unwrap <c>google.protobuf.Int32Value</c>
    /// to a CLR <see cref="int"/>; surface unset wrappers as <c>null</c>).
    /// </summary>
    bool TryReadField(object instance, string field, out object? value);

    /// <summary>
    /// Whether <c>has(instance.field)</c> evaluates to true. Proto presence rules:
    /// <list type="bullet">
    /// <item>scalar with explicit presence (proto2, proto3 <c>optional</c>, oneof) — true iff explicitly set</item>
    /// <item>scalar without explicit presence (plain proto3) — false when value equals the default</item>
    /// <item>repeated / map — true iff non-empty</item>
    /// <item>message — true iff non-null</item>
    /// </list>
    /// </summary>
    bool HasField(object instance, string field);

    /// <summary>
    /// Construct an instance of <paramref name="typeName"/> from a field bag. The values may be
    /// raw CLR objects or already-wrapped <see cref="CelValue"/>s — the provider unwraps as
    /// needed. Returns null if the type is unknown or any field is incompatible.
    /// </summary>
    object? Construct(string typeName, IReadOnlyDictionary<string, CelValue> fields);

    /// <summary>
    /// Project a managed instance to its idiomatic CEL value. Used for proto wrapper types
    /// where the standalone value should appear as the unwrapped primitive
    /// (constructing <c>google.protobuf.Int32Value{value: -123}</c> yields
    /// <see cref="IntValue"/>(-123) rather than an <see cref="ObjectValue"/> wrapping the
    /// proto instance). Returns null if the instance has no special projection — caller
    /// keeps it as <see cref="ObjectValue"/>.
    /// </summary>
    CelValue? Project(object instance);

    /// <summary>
    /// Equality comparison for two managed instances of (presumably) the same type. Returns
    /// null when the provider doesn't have an opinion — caller falls back to CLR
    /// <see cref="object.Equals(object?, object?)"/>. Used for protocol-specific semantics
    /// like NaN-propagation through proto messages: two TestAllTypes with NaN double fields
    /// must compare unequal even though their proto-generated <c>Equals</c> would say they match.
    /// </summary>
    bool? AreEqual(object a, object b);
}

/// <summary>A no-op provider used when no real provider is registered.</summary>
public sealed class NullTypeProvider : ITypeProvider
{
    public static readonly NullTypeProvider Instance = new();
    private NullTypeProvider() { }

    public bool KnowsType(string typeName) => false;
    public CelType? ResolveType(string typeName) => null;
    public bool IsManagedInstance(object instance) => false;
    public string? TypeNameOf(object instance) => null;
    public bool TryReadField(object instance, string field, out object? value)
    {
        value = null;
        return false;
    }
    public bool HasField(object instance, string field) => false;
    public object? Construct(string typeName, IReadOnlyDictionary<string, CelValue> fields) => null;
    public CelValue? Project(object instance) => null;
    public bool? AreEqual(object a, object b) => null;
}
