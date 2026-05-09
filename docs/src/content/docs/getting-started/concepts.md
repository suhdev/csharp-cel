---
title: Core concepts
description: The five ideas that make CEL click — once you see them, the rest of the API is obvious.
---

You've installed it and run a hello-world. Here are the five ideas that
underpin everything else. They're short on purpose; each links to a deeper
treatment.

## 1. Compile once, evaluate many

Parsing and type checking happen in `CelExpression.Compile`; everything after
that is hot-path. A typical service compiles its rules at startup and reuses
each `CompiledProgram` for the lifetime of the process.

```csharp
// startup
_program = CelExpression.Compile(rule, env);

// hot path
var ok = (bool)_program.Eval(activation)!;
```

→ [Evaluation model](/concepts/evaluation-model/)

## 2. CEL has its own type system

CEL types (`int`, `string`, `list<T>`, `map<K, V>`, `Foo.Bar`, `dyn`, ...) live
in `Cel.Types.CelTypes`. They are **not** CLR types — they're declared on the
env and the checker uses them to validate expressions before runtime. The
runtime then represents values as `CelValue` (a closed sum: `IntValue`,
`StringValue`, `ListValue`, ...).

```csharp
.Variable("age", CelTypes.Int)
.Variable("user", CelTypes.Object("User"))
.Variable("tags", CelTypes.List(CelTypes.String))
```

→ [Type system](/concepts/type-system/)

## 3. Activations are how variables flow in

A `CompiledProgram.Eval(...)` call needs an `IActivation` — a name → value
lookup. The convenience overloads (anonymous object, dictionary) wrap an
activation for you. For full control, implement `IActivation` directly.

→ [Activations](/reference/api/activations/)

## 4. Errors are values, not exceptions

CEL specifies that `false && error == false` and `true || error == true` —
errors short-circuit through `&&`/`||`/`?:`. Internally the runtime carries
errors as `ErrorValue`; only at the public boundary do unhandled ones become
`CelEvaluationException`. This is why `program.Eval(...)` rarely throws on
"normal" data shape problems.

→ [Errors & unknowns](/concepts/errors-and-unknowns/)

## 5. Extensions are how you grow the language

The standard library covers `+ - * /`, `==`, `int()`, `string()`, `size()`,
`type()` and friends. Anything beyond that — `math.abs`, `strings.replace`,
`net.containsIP`, `cel.bind` — comes from an `ICelExtension`. Bringing your
own domain functions is the same shape:

```csharp
public sealed class FraudExtension : ICelExtension
{
    public void ConfigureEnv(CelEnv.Builder b) { /* declare */ }
    public void ConfigureRuntime(Action<string, OverloadFn> bind) { /* impl */ }
}

env.NewBuilder().Use(new FraudExtension()).Build();
```

→ [Building extensions](/guides/building-extensions/)

## A mental model in one sentence

> A **`CelEnv`** is a typed dictionary of names; **`CelExpression.Compile`**
> turns source plus an env into a **`CompiledProgram`**; the program asks an
> **`IActivation`** for runtime values, walks the AST, and returns a
> **`CelValue`** that is unwrapped to a CLR object for you.

That's it. Everything else is a refinement of one of those four nouns.
