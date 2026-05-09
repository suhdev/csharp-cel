---
title: Type system
description: How CEL types map to .NET — primitives, wrappers, lists, maps, objects, and the gradual `dyn` escape hatch.
---

CEL has its own type system. It's small, structural, and intentionally
distinct from the host's type system so that a CEL expression has the same
meaning whether the host is Go, C++, Java, or .NET.

## The primitive types

| CEL | `Cel.Types.CelTypes` | Runtime `CelValue` | Native |
|-----|----------------------|--------------------|--------|
| `bool` | `Bool` | `BoolValue` | `bool` |
| `int` | `Int` | `IntValue` | `long` (signed 64-bit) |
| `uint` | `Uint` | `UintValue` | `ulong` |
| `double` | `Double` | `DoubleValue` | `double` |
| `string` | `String` | `StringValue` | `string` |
| `bytes` | `Bytes` | `BytesValue` | `ImmutableArray<byte>` |
| `null_type` | `Null` | `NullValue` | `null` |

Note: CEL's `int` is *signed* 64-bit. CLR `int` (`Int32`) widens to
`IntValue` at the boundary; the runtime stores `long`.

## Composite types

```csharp
CelTypes.List(CelTypes.String)            // list<string>
CelTypes.Map(CelTypes.String, CelTypes.Int) // map<string, int>
CelTypes.Optional(CelTypes.Bool)          // optional<bool>
```

These are constructed from the static methods on `CelTypes`. `ListType`,
`MapType`, `OptionalType` are records — they compare by structural equality.

## Object types

```csharp
CelTypes.Object("com.acme.Account")
```

An `ObjectType` names an externally-defined type. The checker doesn't
introspect it; it relies on the registered `ITypeProvider` to resolve fields,
construct instances, and decide assignability.

If no provider claims the type name, the type is treated as **opaque** — field
selects on values of that type fall back to the reflection-based POCO adapter
(see [working with POCOs](/guides/working-with-pocos/)).

## Wrapper types

Proto wrappers (`google.protobuf.Int32Value`, `BoolValue`, ...) project to
their primitive at runtime, but with one extra capability: they can be
**unset**, which surfaces as `null` rather than the zero value.

```csharp
CelTypes.IntWrapper       // wraps int, may be null
CelTypes.StringWrapper
```

When you read a wrapper field on a proto message, the value is either the
unwrapped primitive or `null`.

## Special types

```csharp
CelTypes.Dyn              // gradual typing escape hatch
CelTypes.Type             // type values: type(42) returns a TypeValue
CelTypes.Error            // internal — errors carry this type
CelTypes.Duration
CelTypes.Timestamp
```

`dyn` is documented separately under [Gradual
typing](/concepts/gradual-typing/). Briefly: a `dyn`-typed value bypasses
static type checks and is dispatched at runtime.

## Function types

A `FunctionType` wraps a result type and an array of arg types. You don't
construct these directly — they emerge from `OverloadDecl`s when you declare
functions on an env.

## Type parameters (parametric polymorphism)

The standard library's `size`, `+`, `[]`, and most extension functions are
parametric:

```csharp
new OverloadDecl("list_concat",
    args:    [CelTypes.List(CelTypes.TypeParam("A")),
              CelTypes.List(CelTypes.TypeParam("A"))],
    result:  CelTypes.List(CelTypes.TypeParam("A")),
    typeParams: ["A"])
```

The checker unifies `A` against the actual argument types. If the unification
conflicts (`list<int> + list<string>`), the result widens to `list<dyn>`
under [gradual typing](/concepts/gradual-typing/) rules.

## Assignability

CEL's assignability rules ("can a value of type T be passed where U is
expected?") differ subtly from CLR conversion:

- `null` is assignable to `null_type`, to any wrapper type, to `dyn`, and to
  any *message* (object) type. It is **not** assignable to a primitive.
- A primitive is assignable to its corresponding wrapper.
- `dyn` is assignable in either direction.
- Object types are nominal — `Account` is not assignable to `User` even if
  they have the same fields.

## How types travel through evaluation

1. Source → `Parser` → AST.
2. AST + `CelEnv` → `Checker` → `CheckedAst`. Every node now carries a
   resolved `CelType`.
3. `CheckedAst` + `IActivation` → `Evaluator` → `CelValue`. The runtime walks
   the AST, asks the activation for variables, and dispatches function calls
   by overload id.

The runtime *does* preserve enough type information at each step to support
`type()` reflection and to drive overload selection. But it doesn't re-run the
type check; the static check is what catches most errors before you ever call
`Eval`.

## See also

- [Gradual typing](/concepts/gradual-typing/) — how `dyn` works in practice.
- [`CelType` reference](/reference/api/cel-types/) — every CelType subtype with
  examples.
- [Working with POCOs](/guides/working-with-pocos/) — declaring object types
  for your CLR classes.
