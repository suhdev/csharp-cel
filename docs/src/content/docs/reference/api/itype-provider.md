---
title: ITypeProvider
description: The pluggable type-system extension for object types — proto messages, host POCOs, anything you can describe.
---

`Cel.ITypeProvider` is the integration point for object types. The default
provider knows nothing; everything it claims a CEL expression sees gets
treated as opaque (`dyn` field selects, reflection-backed reads). A custom
provider is how you give CEL knowledge of your domain types — proto
messages, generated DTOs, virtual types backed by JSON-flavoured maps.

## The interface

```csharp
namespace Cel;

public interface ITypeProvider
{
    bool KnowsType(string typeName);
    CelType? ResolveType(string typeName);

    bool IsManagedInstance(object instance);
    string? TypeNameOf(object instance);

    bool TryReadField(object instance, string field, out object? value);
    bool HasField(object instance, string field);

    object? Construct(string typeName, IReadOnlyDictionary<string, CelValue> fields);

    CelValue? Project(object instance);
    bool? AreEqual(object a, object b);
}
```

## What each method does

### `KnowsType(string typeName)`

Returns true if the provider claims this fully-qualified name. Used by
the env to decide whether `CelTypes.Object("Foo")` references a
provider-managed type.

### `ResolveType(string typeName)`

Returns a `CelType` for a known name; null otherwise. Almost always
returns `CelTypes.Object(typeName)` — the provider's job is to decide
whether the name is known, not to invent a non-object type.

### `IsManagedInstance(object instance)`

Returns true if `instance` is a runtime value the provider handles. The
runtime asks this whenever it has an `ObjectValue` — to decide whether to
route reads through the provider or the POCO adapter.

### `TypeNameOf(object instance)`

The canonical CEL type name for an instance. For proto, `Descriptor.FullName`.
Returns null for instances the provider doesn't manage.

### `TryReadField(object, string, out object?)`

Read a single field. Returns false if the field isn't part of the type.
Returns true with `value` set to the projected value on success.

Implementations must apply protocol-specific projections:
- **Wrappers** unwrap to their primitive (or null for unset).
- **Enum fields** project to `EnumValue` (or `IntValue` for legacy).
- **Repeated/map fields** pass through as `IList` / `IDictionary`.
- **Message fields** stay as `IMessage` so subsequent selects descend.

### `HasField(object, string)`

`has(instance.field)` semantics. Three flavours:

| Field kind | `Has` returns |
|------------|---------------|
| Scalar with explicit presence (proto2, proto3 `optional`, oneof) | true iff explicitly set |
| Scalar with implicit presence (plain proto3) | true iff value differs from the default |
| Repeated / map | true iff non-empty |
| Message | true iff non-null |

### `Construct(string typeName, IReadOnlyDictionary<string, CelValue> fields)`

`pkg.Type{...}` constructor calls. Returns a CLR instance with the supplied
fields populated, or null if the type or any field is incompatible.

### `Project(object instance)`

Optionally projects a managed instance to its idiomatic CEL value.
Returns null when the value should remain an `ObjectValue`. Used for:

- proto wrapper types — project `Int32Value{value: -123}` to `IntValue(-123)`.
- well-known types — project `google.protobuf.Value` to its primitive,
  `ListValue` to `list<dyn>`, `Struct` to `map<string, dyn>`.
- `Any` — unpack and project the contained message.

### `AreEqual(object a, object b)`

Optional override for managed-instance equality. Return null when no
opinion (caller falls back to `object.Equals`). Used for:

- **NaN propagation** through proto messages — two TestAllTypes with NaN
  doubles must compare unequal even though proto's generated `Equals`
  uses bitwise compare.
- Domain semantics — e.g. user-defined "structurally equal" beyond
  default record/POCO equality.

## `NullTypeProvider`

The default. Claims nothing, manages nothing.

```csharp
public sealed class NullTypeProvider : ITypeProvider
{
    public static readonly NullTypeProvider Instance = new();
}
```

When no provider is registered, the runtime falls through to the
reflection-based POCO adapter for any object value.

## Implementing a provider

The reference implementation lives in
[`tests/Cel.Conformance/ProtoTypeProvider.cs`](https://github.com/your-org/cel-csharp/blob/main/tests/Cel.Conformance/ProtoTypeProvider.cs).
It's ~700 lines and handles the entire proto2/proto3 surface: presence
semantics, wrapper unwrapping, well-known types, oneofs, enum projection,
NaN propagation. Read it as the canonical worked example.

For a domain-specific provider, the rough shape:

```csharp
public sealed class MyProvider : ITypeProvider
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "MyApp.User",
        "MyApp.Account",
        "MyApp.Request",
    };

    public bool KnowsType(string typeName) => Known.Contains(typeName);

    public CelType? ResolveType(string typeName) =>
        Known.Contains(typeName) ? CelTypes.Object(typeName) : null;

    public bool IsManagedInstance(object instance) =>
        instance is MyApp.User or MyApp.Account or MyApp.Request;

    public string? TypeNameOf(object instance) => instance switch
    {
        MyApp.User    => "MyApp.User",
        MyApp.Account => "MyApp.Account",
        MyApp.Request => "MyApp.Request",
        _ => null,
    };

    public bool TryReadField(object instance, string field, out object? value)
    {
        switch (instance)
        {
            case MyApp.User u: return TryUser(u, field, out value);
            // ...
            default: value = null; return false;
        }
    }

    public bool HasField(object instance, string field) =>
        TryReadField(instance, field, out var v) && v is not null;

    public object? Construct(string typeName, IReadOnlyDictionary<string, CelValue> fields) =>
        // build an instance
        null;

    public CelValue? Project(object instance) => null;
    public bool? AreEqual(object a, object b) => null;

    private static bool TryUser(MyApp.User u, string field, out object? v) => /* ... */;
}
```

## See also

- [Working with POCOs](/guides/working-with-pocos/) — when reflection
  vs. provider is the right call.
- [Working with protos](/guides/working-with-protos/) — the canonical
  use case.
- [`CelType`](/reference/api/cel-types/) — the types you'll be returning
  from `ResolveType`.
