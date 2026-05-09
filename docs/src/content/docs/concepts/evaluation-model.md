---
title: Evaluation model
description: What happens between source code and a result — and how to think about cost.
---

A CEL evaluation has three phases. Each runs at a different time, with
different cost profile and different ways to fail.

## Phase 1 — Parse (cheap, runs once)

```csharp
CelExpression.Compile(source, env)
//   └─ internally: Parsing.Parser.Parse(source, ...)
```

The hand-written lexer + Pratt parser produces an AST plus source-info
metadata (line/column positions). It also expands macros (`has`, `all`,
`exists`, comprehensions, plus any contributed by extensions).

If the source has syntax errors, you get a `CelCompileException` with all
diagnostics aggregated.

**Cost**: linear in source length. Microseconds for typical rules.

## Phase 2 — Type check (cheap, runs once)

The checker walks the AST, resolves identifiers against the env, dispatches
overloads, unifies type parameters, and decorates every node with its
`CelType`. The output is a `CheckedAst`.

If a function call is ambiguous, an identifier is undeclared, or a type
constraint can't be satisfied, you again get a `CelCompileException`.

**Cost**: linear in AST size, but with a constant factor for overload
resolution. Still microseconds.

## Phase 3 — Evaluate (hot path)

```csharp
program.Eval(activation)
```

A tree-walking evaluator. Each AST node maps to a small visitor method that
calls into:

- the `IActivation` for variable lookups
- the `FunctionRegistry` for function/operator dispatch (binding overload id
  → implementation `OverloadFn`)
- the `ITypeProvider` for object reads/writes/has/construct/projection
- the `PocoAdapter` for reflection-based field access on plain CLR types when
  no provider claims a value

The evaluator is intentionally simple — there is no JIT, no caching of
sub-tree results, no AST rewriting at runtime. The perf comes from CEL's
tightness: small expressions, short ASTs, almost no allocation in the hot
path beyond the result `CelValue`s.

**Cost**: depends on the expression. A field access + comparison is hundreds
of nanoseconds; a comprehension over a 1k-element list is a few microseconds
plus the cost of the body.

## Compile once, evaluate many

The first two phases produce a reusable `CompiledProgram`. The compiled state
includes:

- the checked AST (immutable),
- a function registry built from the env's stdlib + extensions (immutable
  once built),
- a reference to the type provider (shared).

A `CompiledProgram` is **safe to share** across threads for evaluation. The
activation you pass to `Eval` is *your* responsibility — typically scoped to
one request.

```csharp
public sealed class PolicyService
{
    private readonly CompiledProgram _program;

    public PolicyService(string rule, CelEnv env)
    {
        _program = CelExpression.Compile(rule, env);
    }

    public bool Allows(Request req) =>
        (bool)_program.Eval(new { req })!;
}
```

## Activations: how variables flow in

An `IActivation` is just `bool TryResolve(string name, out object? value)`.
Three built-in implementations cover most cases:

- **`MapActivation`** — wraps an `IReadOnlyDictionary<string, object?>`. The
  default for `program.Eval(IDictionary)`.
- **`ObjectActivation`** — reflects a single root POCO and exposes its public
  properties / fields by name. The default for `program.Eval(object)`.
- **`ChainedActivation`** — tries each child in order; first to claim wins.
  Useful for layering "globals" under per-request bindings.

You can implement your own — e.g. a `LazyActivation` that fetches expensive
values only when CEL actually references them.

## Errors short-circuit through logical operators

Per the [spec](https://github.com/google/cel-spec/blob/master/doc/langdef.md):

```
true  || error  → true
false || error  → error
false && error  → false
true  && error  → error
true  ? a : error → a
false ? error : b → b
```

Internally, the runtime represents an error as `ErrorValue`. It only escapes
to your code as a `CelEvaluationException` when the **top-level** result is
an error — i.e. nothing short-circuited it.

This is why `program.Eval(...)` is much "calmer" than CLR equivalents — many
runtime issues that would throw in normal code are absorbed by the operator
semantics.

## Unknowns (partial evaluation)

The runtime supports an `UnknownValue` value type. It marks an attribute that
the host hasn't supplied yet. Logical operators short-circuit through
unknowns the same way they do errors. The `unknowns.textproto` corpus drives
this; the .NET implementation does not yet emit unknowns from public APIs —
it's a planned feature for partial-evaluation use cases.

See [Errors & unknowns](/concepts/errors-and-unknowns/) for the full
hierarchy.

## Performance anchors

Rough orders of magnitude on a modern x86 / ARM machine, single-threaded:

| Operation | Time |
|-----------|------|
| `Compile` (10-line rule) | 50–200 µs |
| `Eval` (5-op boolean over POCOs) | 0.5–2 µs |
| `Eval` (comprehension over 1k items, simple body) | 50–200 µs |
| Compile-then-eval for one-shot use | almost always pay once for the first |

The dominant runtime cost in well-formed programs is reflection on POCO
fields. Use `ITypeProvider` for hot types if you measure it as the bottleneck
— see [Performance & trimming](/guides/performance-and-trimming/).
