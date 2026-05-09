---
title: Hello, world
description: Build a complete CEL evaluation pipeline in five minutes.
---

This walkthrough builds a tiny access-control rule end-to-end: declare an
environment, compile an expression, evaluate it against multiple inputs, and
inspect the result. By the end you'll know the three objects you'll be using
forever: **`CelEnv`**, **`CelExpression`**, and **`CompiledProgram`**.

## The scenario

We want to allow a request when:

```
account.is_admin || (request.size <= account.max_size && account.region == request.region)
```

## 1. Build an environment

A `CelEnv` is the static configuration the type checker needs: the names and
types of the variables your expression can reference, plus any extra functions
or extensions.

```csharp
using Cel;
using Cel.Types;

var env = CelEnv.NewBuilder()
    .Variable("account", CelTypes.Object("Account"))
    .Variable("request", CelTypes.Object("Request"))
    .Build();
```

`CelTypes.Object("Account")` is a placeholder type — the checker won't
introspect it, so field access on `account` is treated as `dyn` (dynamic) and
resolved at runtime via the POCO adapter. For tighter typing, see [working
with POCOs](/guides/working-with-pocos/).

## 2. Compile

`CelExpression.Compile` parses the source, type-checks it against the env, and
returns a reusable program object:

```csharp
var program = CelExpression.Compile(
    "account.is_admin || (request.size <= account.max_size && account.region == request.region)",
    env);
```

A `CelCompileException` is thrown if either phase produces errors — the message
contains every diagnostic with line/column info.

## 3. Evaluate

`CompiledProgram` exposes three convenient evaluation entry points:

```csharp
// Anonymous root: each top-level property becomes a variable.
bool allowed = (bool)program.Eval(new
{
    account = new { is_admin = false, max_size = 1024, region = "us" },
    request = new { size = 512, region = "us" }
})!;

// Dictionary: explicit name -> value.
var allowed2 = program.Eval(new Dictionary<string, object?>
{
    ["account"] = new Account(false, 1024, "us"),
    ["request"] = new Request(512, "us"),
});

// IActivation: full control. See [Activations](/reference/api/activations/).
var allowed3 = program.Eval(new MyCustomActivation(...));
```

The result is the unwrapped CLR value: `bool`, `long`, `double`, `string`,
`List<object?>`, `Dictionary<...>`, or your original object instance.

## 4. Reuse the program

Compilation is the expensive step. **Compile once, eval many**:

```csharp
var program = CelExpression.Compile(source, env);

foreach (var (acc, req) in incoming)
{
    if ((bool)program.Eval(new { account = acc, request = req })!)
    {
        Allow(req);
    }
}
```

The `CompiledProgram` is thread-safe for evaluation; the activations you pass
in are not (because they're owned by the call site).

## 5. Handle errors

CEL's evaluator treats errors as values that short-circuit through the
expression — only when an error reaches the top of the tree does it surface to
your code as a `CelEvaluationException`:

```csharp
try
{
    var v = program.Eval(activation);
}
catch (CelEvaluationException ex)
{
    Console.WriteLine($"runtime: {ex.Message}");
}
```

For more on this model, see [Errors &
unknowns](/concepts/errors-and-unknowns/).

## What just happened?

Three objects, three jobs:

1. **`CelEnv`** — the *static* world the checker sees. Variables, functions,
   container, type provider.
2. **`CelExpression.Compile`** — runs parse + check, returns a
   `CompiledProgram`.
3. **`CompiledProgram`** — the *runtime* surface. Holds the checked AST and
   bound function table; you call `Eval` on it.

The next page lays out the [core concepts](/getting-started/concepts/) you'll
hit as you keep building.
