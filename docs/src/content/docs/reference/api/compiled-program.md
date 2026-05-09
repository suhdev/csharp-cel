---
title: CompiledProgram
description: A reusable, thread-safe CEL program ready for repeated evaluation.
---

`Cel.CompiledProgram` is what `CelExpression.Compile` returns. It carries the
checked AST plus a function registry built from the env's stdlib and
extensions. Evaluation walks the AST against an `IActivation`.

## Signature

```csharp
namespace DotnetCel;

[RequiresUnreferencedCode("Field access on host objects uses reflection; runtime types may be trimmed.")]
public sealed class CompiledProgram
{
    public CheckedAst Ast { get; }
    public DotnetCel.Types.CelType ResultType { get; }

    public CelValue EvaluateRaw(IActivation activation);
    public object? Eval(IActivation activation);
    public object? Eval(IReadOnlyDictionary<string, object?> bindings);
    public object? Eval(IDictionary bindings);
    public object? Eval(object root);
}
```

## `Eval` overloads

| Overload | When to use |
|----------|-------------|
| `Eval(IActivation)` | Full control. Use a custom `IActivation` for layered or lazy bindings. |
| `Eval(IReadOnlyDictionary<string, object?>)` | Most common. Cheapest for hot paths. |
| `Eval(IDictionary)` | Non-generic dictionaries (e.g. interop with older code). |
| `Eval(object root)` | Convenience: uses `ObjectActivation` to reflect over the root's public properties. |

The non-`Raw` overloads unwrap the result via `ValueAdapter.ToClr` — a
`CelValue` becomes a CLR `bool`, `long`, `double`, `string`, `List<object?>`,
`Dictionary<,>`, or your original object instance. Errors throw
`CelEvaluationException`.

## `EvaluateRaw`

Returns the raw `CelValue` — useful when you need to distinguish nulls,
optionals, errors, and unknowns programmatically:

```csharp
using DotnetCel.Values;

CelValue v = program.EvaluateRaw(activation);
return v switch
{
    BoolValue b => b.Value,
    OptionalValue { HasValue: false } => null,
    ErrorValue e => throw new InvalidOperationException(e.Message),
    UnknownValue => "<deferred>",
    _ => v.ToClrObject(),
};
```

## `ResultType`

The CEL type the checker inferred for the program's top-level expression.
Useful for "tell me what this rule produces" UIs:

```csharp
Console.WriteLine(program.ResultType);   // bool, list<string>, dyn, ...
```

## Thread safety

`CompiledProgram` is **safe to call from many threads concurrently**. The
program holds:

- the checked AST (immutable),
- a function registry (immutable once built),
- a reference to the env's `ITypeProvider` (must itself be thread-safe).

The activation you pass to `Eval` is **not** shared — it's per-call.

## Lifetime

There's no `Dispose` — `CompiledProgram` is pure managed state. Drop it
when you're done; the GC collects.

## Common patterns

```csharp
// Cache by source for a long-lived service.
private readonly ConcurrentDictionary<string, CompiledProgram> _cache = new();

public bool Check(string rule, IActivation activation) =>
    (bool)_cache
        .GetOrAdd(rule, src => CelExpression.Compile(src, _env))
        .Eval(activation)!;
```

```csharp
// Strongly-typed wrapper around a single rule.
public sealed class FraudRule
{
    private readonly CompiledProgram _program;
    public FraudRule(CelEnv env, string source) =>
        _program = CelExpression.Compile(source, env);

    public bool Matches(Event evt, User user) =>
        (bool)_program.Eval(new { evt, user })!;
}
```

## See also

- [Activations](/reference/api/activations/) — the input shapes.
- [`CelValue`](/reference/api/cel-value/) — what `EvaluateRaw` returns.
- [Evaluation model](/concepts/evaluation-model/) — what's happening
  inside `Eval`.
