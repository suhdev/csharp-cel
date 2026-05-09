---
title: CelType / CelTypes
description: The static type system — every type record and the factory helpers for constructing them.
---

`Cel.Types.CelType` is the abstract base for the static type system. The
checker decorates every AST node with one. Construct types via the
factory methods on `CelTypes`, not directly — that keeps singleton
instances reference-equal.

## The base record

```csharp
namespace Cel.Types;

public abstract record CelType
{
    public string Name { get; }
}
```

`Name` is the type's printable form: `int`, `list<string>`, `map<string,
int>`, `acme.v1.User`, `dyn`.

## Concrete types

```csharp
public sealed record PrimitiveType(PrimitiveKind PrimKind) : CelType;
public sealed record NullType : CelType;
public sealed record DynType : CelType;
public sealed record ErrorType : CelType;
public sealed record DurationType : CelType;
public sealed record TimestampType : CelType;
public sealed record ListType(CelType ElementType) : CelType;
public sealed record MapType(CelType KeyType, CelType ValueType) : CelType;
public sealed record ObjectType(string TypeName, ImmutableArray<CelType> TypeArgs = default) : CelType;
public sealed record EnumType(string TypeName) : CelType;
public sealed record TypeParamType(string ParamName) : CelType;
public sealed record TypeType(CelType? Parameter = null) : CelType;
public sealed record FunctionType(CelType ResultType, ImmutableArray<CelType> ArgTypes) : CelType;
public sealed record WrapperType(PrimitiveKind PrimKind) : CelType;
public sealed record OptionalType(CelType InnerType) : CelType;
public sealed record AbstractType(string TypeName, ImmutableArray<CelType> Parameters = default) : CelType;
```

## `CelTypes` factory helpers

```csharp
namespace Cel.Types;

public static class CelTypes
{
    // Singletons — reference-equal across the codebase.
    public static readonly CelType Bool;
    public static readonly CelType Int;
    public static readonly CelType Uint;
    public static readonly CelType Double;
    public static readonly CelType String;
    public static readonly CelType Bytes;
    public static readonly CelType Null;
    public static readonly CelType Dyn;
    public static readonly CelType Error;
    public static readonly CelType Duration;
    public static readonly CelType Timestamp;
    public static readonly CelType Type;

    // Wrappers for proto wrapper types.
    public static readonly CelType BoolWrapper;
    public static readonly CelType IntWrapper;
    public static readonly CelType UintWrapper;
    public static readonly CelType DoubleWrapper;
    public static readonly CelType StringWrapper;
    public static readonly CelType BytesWrapper;

    // Composite factories.
    public static ListType  List(CelType element);
    public static MapType   Map(CelType key, CelType value);
    public static ObjectType Object(string typeName);
    public static ObjectType Object(string typeName, params CelType[] typeArgs);
    public static EnumType  Enum(string typeName);
    public static OptionalType Optional(CelType inner);
    public static TypeParamType TypeParam(string name);
    public static TypeType  TypeOf(CelType inner);
    public static FunctionType Function(CelType result, params CelType[] args);
    public static AbstractType Abstract(string typeName, params CelType[] parameters);
}
```

## Common patterns

```csharp
// Variable declarations.
.Variable("count", CelTypes.Int)
.Variable("tags", CelTypes.List(CelTypes.String))
.Variable("counters", CelTypes.Map(CelTypes.String, CelTypes.Int))
.Variable("user", CelTypes.Object("acme.v1.User"))
.Variable("flagged", CelTypes.Optional(CelTypes.Bool))

// Function overloads.
new OverloadDecl("clamp_int",
    args:   [CelTypes.Int, CelTypes.Int, CelTypes.Int],
    result: CelTypes.Int)

// Generic overloads.
var A = CelTypes.TypeParam("A");
new OverloadDecl("first_list",
    args:   [CelTypes.List(A)],
    result: A,
    typeParams: ["A"])
```

## Type equality

Records compare by structural value: `CelTypes.Int == CelTypes.Int` is
true; `CelTypes.List(CelTypes.Int) == CelTypes.List(CelTypes.Int)` is
true.

`ObjectType` and `EnumType` compare by their `TypeName` — nominal, not
structural. Two distinct CLR types with the same declared name will
collide; pick fully qualified names (`acme.v1.User` rather than `User`).

## Assignability

The checker uses `Cel.Checker.TypeAlgebra.IsAssignable(from, to)`:

| From | To | Assignable? |
|------|----|-------------|
| `T` | `T` | yes |
| `T` | `dyn` | yes |
| `dyn` | `T` | yes |
| `null_type` | `null_type`, any wrapper, any object | yes |
| `null_type` | primitive | no |
| primitive | matching wrapper | yes |
| `list<A>` | `list<B>` | iff `A` assignable to `B` |
| `map<K1, V1>` | `map<K2, V2>` | iff `K1→K2` and `V1→V2` |

Object types are nominal — a same-shape distinct name does not coerce.

## See also

- [Type system](/concepts/type-system/) — narrative version.
- [Gradual typing](/concepts/gradual-typing/) — what `dyn` does.
- [`CelValue`](/reference/api/cel-value/) — the runtime side.
