---
title: CelEnv
description: The static configuration handed to the type checker — variables, functions, container, type provider, extensions.
---

`Cel.CelEnv` is the immutable bundle of declarations that govern how a CEL
expression is type-checked. It also carries the type provider and the list
of extensions, so that `CompiledProgram` can finish wiring up the runtime.

## Construction

```csharp
var env = CelEnv.NewBuilder()
    .SetContainer("acme.v1")
    .UseTypeProvider(new MyProvider())
    .Use(StringsExtension.Instance)
    .Variable("user", CelTypes.Object("User"))
    .Function("greet",
        new OverloadDecl("greet_string", [CelTypes.String], CelTypes.String))
    .Build();
```

## Properties

| Property | Type | What |
|----------|------|------|
| `Container` | `string` | Default namespace for unqualified identifier resolution. |
| `Variables` | `ImmutableDictionary<string, VariableDecl>` | Declared variables. |
| `Functions` | `ImmutableDictionary<string, FunctionDecl>` | Declared functions (merged across extensions and stdlib). |
| `Extensions` | `ImmutableArray<ICelExtension>` | Registered extensions; consulted by `CompiledProgram` to bind runtime impls. |
| `Macros` | `ImmutableArray<CelMacro>` | Aggregated parser macros from extensions. |
| `TypeProvider` | `ITypeProvider` | The provider for object types and managed instances. Defaults to `NullTypeProvider.Instance`. |

## Methods

```csharp
public static Builder NewBuilder();
public Builder Extend();

public VariableDecl? ResolveVariable(string name);
public FunctionDecl? ResolveFunction(string name);
public IEnumerable<string> QualifiedCandidates(string name);
```

`ResolveVariable` / `ResolveFunction` walk the [container's candidate
list](/concepts/type-system/) — try unqualified, then container-prefixed,
returning the longest match.

`QualifiedCandidates` exposes the same candidate list for inspection (handy
when building rule-author tooling).

`Extend()` returns a new builder seeded from the existing env. Use it to
derive a child env without rebuilding from scratch.

## `CelEnv.Builder`

```csharp
public sealed class Builder
{
    public Builder SetContainer(string container);
    public Builder UseTypeProvider(ITypeProvider provider);
    public Builder Variable(string name, CelType type);
    public Builder Variable(VariableDecl decl);
    public Builder Function(FunctionDecl decl);
    public Builder Function(string name, params OverloadDecl[] overloads);
    public Builder Use(ICelExtension extension);
    public Builder WithoutStandardLibrary();
    public CelEnv Build();
}
```

## VariableDecl

```csharp
public sealed record VariableDecl(string Name, CelType Type);
```

A name + type pair. Variable bindings at runtime are looked up by `Name`.

## FunctionDecl

```csharp
public sealed record FunctionDecl(string Name, ImmutableArray<OverloadDecl> Overloads)
{
    public FunctionDecl Merge(FunctionDecl other);
}
```

Merging two `FunctionDecl`s under the same name unions their overloads;
duplicate ids fail at build time.

## OverloadDecl

```csharp
public sealed record OverloadDecl(
    string Id,
    ImmutableArray<CelType> ArgTypes,
    CelType ResultType,
    bool IsReceiverStyle = false,
    ImmutableArray<string> TypeParams = default)
{
    // Convenience ctor with array literals:
    public OverloadDecl(string id, CelType[] args, CelType result, ...);
}
```

The `Id` is what binds the declaration to its runtime impl in
`ICelExtension.ConfigureRuntime`. Use stable ids — they cross
declaration / impl / cached `CompiledProgram` boundaries.

## Without the standard library

`WithoutStandardLibrary()` disables the default stdlib injection. Useful
for sandboxed envs where you want to constrain the language to a strict
subset:

```csharp
var sandbox = CelEnv.NewBuilder()
    .WithoutStandardLibrary()
    .Function("eq",
        new OverloadDecl("eq_int_int", [CelTypes.Int, CelTypes.Int], CelTypes.Bool))
    .Build();
```

You'll need to provide your own implementations for everything you keep —
the runtime won't have `+`, `==`, `&&`, `size`, etc. unless you bring
them.

## Immutability

`CelEnv` is immutable; the builder is mutable. Build once, share the
resulting env across many compiles. To customise per-rule, call `Extend()`
on a base env.

## See also

- [`ICelExtension`](/reference/api/icel-extension/) — the
  declarations-plus-impls bundle.
- [Declaring variables](/guides/declaring-variables/) — practical guide.
- [Declaring functions](/guides/declaring-functions/) — practical guide.
