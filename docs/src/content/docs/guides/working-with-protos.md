---
title: Working with protos
description: Use Google.Protobuf-generated messages as CEL object types via a proto-aware ITypeProvider.
---

CEL was originally designed against protobuf, and the spec's `proto2.textproto`
and `proto3.textproto` corpora exercise wrapper unwrapping, presence
semantics, well-known types, and oneofs. `Cel.NET` ships a reference proto
type provider in the conformance harness — read it as the canonical worked
example and adapt it for production use.

## Setup

You need:

1. **`Google.Protobuf`** + **`Grpc.Tools`** (for codegen) referenced from
   your project.
2. The `.proto` files compiled to C# message types.
3. A registered `ITypeProvider` that knows about those messages.

```xml
<ItemGroup>
  <PackageReference Include="Google.Protobuf" Version="3.29.*" />
  <PackageReference Include="Grpc.Tools" Version="2.68.*" PrivateAssets="all" />
  <Protobuf Include="Protos/*.proto" GrpcServices="None" />
</ItemGroup>
```

## A minimal proto type provider

The full implementation lives in
`tests/DotnetCel.Conformance/ProtoTypeProvider.cs` and handles wrapper types,
well-known types (`Any`, `Value`, `ListValue`, `Struct`, `Timestamp`,
`Duration`), and proto2/proto3 presence rules. Here's the skeleton:

```csharp
using DotnetCel;
using DotnetCel.Types;
using DotnetCel.Values;
using Google.Protobuf;
using Google.Protobuf.Reflection;

public sealed class ProtoTypeProvider : ITypeProvider
{
    private readonly Dictionary<string, MessageDescriptor> _byName;

    public ProtoTypeProvider(IEnumerable<MessageDescriptor> descriptors)
    {
        _byName = new Dictionary<string, MessageDescriptor>(StringComparer.Ordinal);
        foreach (var d in descriptors)
        {
            Register(d);
        }
    }

    private void Register(MessageDescriptor d)
    {
        _byName[d.FullName] = d;
        foreach (var nested in d.NestedTypes)
        {
            Register(nested);
        }
    }

    public bool KnowsType(string typeName) => _byName.ContainsKey(typeName);

    public CelType? ResolveType(string typeName) =>
        _byName.ContainsKey(typeName) ? CelTypes.Object(typeName) : null;

    public bool IsManagedInstance(object instance) => instance is IMessage;

    public string? TypeNameOf(object instance) =>
        instance is IMessage m ? m.Descriptor.FullName : null;

    public bool TryReadField(object instance, string field, out object? value)
    {
        if (instance is not IMessage msg)
        {
            value = null;
            return false;
        }
        var fd = msg.Descriptor.FindFieldByName(field);
        if (fd is null)
        {
            value = null;
            return false;
        }
        value = ProjectValue(fd, fd.Accessor.GetValue(msg));
        return true;
    }

    public bool HasField(object instance, string field)
    {
        if (instance is not IMessage msg) return false;
        var fd = msg.Descriptor.FindFieldByName(field);
        if (fd is null) return false;
        if (fd.HasPresence) return fd.Accessor.HasValue(msg);
        // implicit presence: present iff non-default
        return !IsDefaultScalar(fd, fd.Accessor.GetValue(msg));
    }

    public object? Construct(string typeName, IReadOnlyDictionary<string, CelValue> fields)
    {
        if (!_byName.TryGetValue(typeName, out var desc)) return null;
        var msg = (IMessage)Activator.CreateInstance(desc.ClrType)!;
        foreach (var (name, value) in fields)
        {
            var fd = desc.FindFieldByName(name);
            if (fd is null) continue;
            fd.Accessor.SetValue(msg, ConvertForField(fd, value));
        }
        return msg;
    }

    public CelValue? Project(object instance)
    {
        // Wrapper types: project to their primitive.
        // Well-known types: project Value/ListValue/Struct to dyn-shaped CEL values.
        // Otherwise: keep the IMessage as ObjectValue.
        return null;
    }

    public bool? AreEqual(object a, object b)
    {
        // Override to implement NaN propagation through proto messages.
        return null;
    }

    private static object? ProjectValue(FieldDescriptor fd, object? raw) => /* ... */;
    private static bool IsDefaultScalar(FieldDescriptor fd, object? v) => /* ... */;
    private static object? ConvertForField(FieldDescriptor fd, CelValue value) => /* ... */;
}
```

Wire it into the env:

```csharp
var env = CelEnv.NewBuilder()
    .UseTypeProvider(new ProtoTypeProvider(MyDescriptors.AllMessages))
    .Variable("request", CelTypes.Object("acme.v1.Request"))
    .Build();
```

## Wrapper types

`google.protobuf.Int32Value`, `BoolValue`, etc. project to their unwrapped
primitive at runtime — but unset wrappers surface as `null`. This is what
makes the standard idiom work:

```cel
has(request.maybe_id) ? string(request.maybe_id) : "none"
```

The provider's `Project` hook is where the unwrap happens — see the
reference implementation for the full set of cases.

## Well-known types

The reference provider projects:

| Proto type | CEL value |
|------------|-----------|
| `google.protobuf.Value` | the primitive / list / map it wraps |
| `google.protobuf.ListValue` | `list<dyn>` |
| `google.protobuf.Struct` | `map<string, dyn>` |
| `google.protobuf.Any` | unpacks to the contained message |
| `google.protobuf.Timestamp` | `timestamp` |
| `google.protobuf.Duration` | `duration` |
| `google.protobuf.Empty` | `{}` (empty map) |
| `google.protobuf.FieldMask` | list of paths |

Constructing them through CEL's `pkg.Type{...}` syntax goes the other way:
`Construct` builds the proto message, populating fields from the bag.

## Enum semantics

`Cel.NET` uses **strong enum semantics**: enum field reads and constructor
calls return `EnumValue`, whose `type()` reports the qualified enum name.
Comparison with `int` still works (CEL says `EnumValue == 0` is true if the
numbers match). See the [enum tests](/reference/conformance/) for which
cel-spec sections we satisfy and which conflict with the legacy "enums are
ints" mode (we skip those with explicit reasons).

## Constructing messages from CEL

```cel
acme.v1.Request{
  user: acme.v1.User{name: 'alice', age: 25},
  region: 'us',
  tags: ['preview', 'beta'],
}
```

The provider's `Construct` is called with `typeName="acme.v1.Request"` and
the field bag. It instantiates the proto and assigns each field through the
accessor.

## Container resolution

Set a container on the env to allow unqualified type names:

```csharp
.SetContainer("acme.v1")
```

Then `User{name: 'alice'}` resolves the same as `acme.v1.User{...}`. The
checker walks the [longest-match-first](/concepts/type-system/) candidate
list.

## See also

- [`ITypeProvider` reference](/reference/api/itype-provider/) — every method
  with detailed semantics.
- [Conformance](/reference/conformance/) — proto2 / proto3 / wrappers /
  dynamic pass rates.
- [The reference
  implementation](https://github.com/your-org/cel-csharp/blob/main/tests/DotnetCel.Conformance/ProtoTypeProvider.cs)
  — production-quality starting point you can copy.
