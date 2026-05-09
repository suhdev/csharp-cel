---
title: CelExpression
description: The static façade for compiling CEL source.
---

`Cel.CelExpression` is a one-call entry point: it parses, type-checks, and
returns a reusable `CompiledProgram`.

## Signature

```csharp
namespace Cel;

public static class CelExpression
{
    public static CompiledProgram Compile(string source, CelEnv env);
}
```

## Behaviour

- **Parses** `source` using the env's macros (standard set + any contributed
  by extensions).
- **Type-checks** the resulting AST against `env`'s declared variables,
  functions, type provider, and container.
- Returns a `CompiledProgram` if both phases succeed.
- **Throws** `Cel.Diagnostics.CelCompileException` (with the full diagnostic
  list) if either phase fails.

## Example

```csharp
using Cel;
using Cel.Types;

var env = CelEnv.NewBuilder()
    .Variable("x", CelTypes.Int)
    .Build();

var program = CelExpression.Compile("x * 2 + 1", env);

Console.WriteLine(program.Eval(new Dictionary<string, object?> { ["x"] = 7 }));
// 15
```

## Diagnostics

```csharp
try
{
    var p = CelExpression.Compile("undeclared_var * 2", env);
}
catch (CelCompileException ex)
{
    foreach (var d in ex.Diagnostics)
    {
        Console.WriteLine($"{d.Line}:{d.Column} {d.Severity} {d.Message}");
    }
}
```

`Diagnostics` is an `ImmutableArray<Diagnostic>`. Each carries a line,
column, severity, and message.

## See also

- [`CelEnv`](/reference/api/cel-env/) — the environment passed in.
- [`CompiledProgram`](/reference/api/compiled-program/) — what comes out.
