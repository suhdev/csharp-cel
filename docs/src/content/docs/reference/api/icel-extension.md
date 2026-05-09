---
title: ICelExtension
description: A pluggable bundle of declarations, runtime impls, and parser macros.
---

`Cel.ICelExtension` is the unit of reuse for CEL functionality. The standard
library is internally one; every shipped extension (`StringsExtension`,
`MathExtension`, ...) is one. Your own bundle of domain functions should be.

## The interface

```csharp
namespace DotnetCel;

public interface ICelExtension
{
    void ConfigureEnv(CelEnv.Builder envBuilder);
    void ConfigureRuntime(Action<string, OverloadFn> bindImpl);
    IEnumerable<CelMacro> Macros => Array.Empty<CelMacro>();
}
```

Three halves:
- **Declarations** — `ConfigureEnv` adds variables and function overloads
  to the env builder.
- **Runtime impls** — `ConfigureRuntime` binds an `OverloadFn` for every
  overload id this extension declared.
- **Macros** — optional parser-level rewrites. Default: empty.

## Lifecycle

```
extension instance
   ├─ ConfigureEnv(builder)   ← runs at env build time
   │
   ↓
CelEnv (holds the extension reference)
   │
   ↓
CelExpression.Compile(source, env)
   │
   ↓
CompiledProgram
   ├─ ConfigureRuntime(bind)  ← runs once per program
```

`ConfigureEnv` runs once when `builder.Use(extension)` is called.
`ConfigureRuntime` runs once when a `CompiledProgram` is constructed from
the env. The same extension instance can serve any number of envs and
programs.

## Singleton pattern

Built-in extensions follow this shape — one instance per process:

```csharp
public sealed class MyExtension : ICelExtension
{
    public static readonly MyExtension Instance = new();
    private MyExtension() { }

    public void ConfigureEnv(CelEnv.Builder b) { /* ... */ }
    public void ConfigureRuntime(Action<string, OverloadFn> bind) { /* ... */ }
}
```

If your extension genuinely needs configuration (a service handle, a
feature flag), accept it in the constructor and document that each
instance is independent.

## A complete example

```csharp
using DotnetCel;
using DotnetCel.Types;
using DotnetCel.Values;

public sealed class GreetExtension : ICelExtension
{
    public static readonly GreetExtension Instance = new();
    private GreetExtension() { }

    public void ConfigureEnv(CelEnv.Builder b)
    {
        b.Function("greet",
            new OverloadDecl("greet_string",
                [CelTypes.String], CelTypes.String));
    }

    public void ConfigureRuntime(Action<string, OverloadFn> bind)
    {
        bind("greet_string", args =>
        {
            var name = ((StringValue)args[0]).Value;
            return CelValue.Of($"hello, {name}");
        });
    }
}
```

```csharp
var env = CelEnv.NewBuilder()
    .Use(GreetExtension.Instance)
    .Build();

var program = CelExpression.Compile("greet('world')", env);
Console.WriteLine(program.Eval(new Dictionary<string, object?>())); // hello, world
```

## Parser macros

Override `Macros` to contribute parser-level sugar:

```csharp
public IEnumerable<CelMacro> Macros => new[]
{
    new CelMacro(
        Name: "fooBar",
        ArgCount: 2,
        IsReceiverStyle: false,
        Expand: (ctx, target, args) => /* return rewritten Expr or null */),
};
```

Macros run during parsing, before type-checking — see [Parser
macros](/guides/parser-macros/).

## Composing multiple

Extensions stack via repeated `Use(...)` calls:

```csharp
var env = CelEnv.NewBuilder()
    .Use(StringsExtension.Instance)
    .Use(MathExtension.Instance)
    .Use(GreetExtension.Instance)
    .Build();
```

The order matters in one direction: later `Use(...)` calls can override
earlier function declarations under the same name. Overload ids must
remain unique across the whole env — duplicate ids fail at build time.

## Overlapping function names

If two extensions both declare a function called `foo`, the env keeps both
overload sets. Resolution is by overload id; collisions in id space are an
error. As an extension author, prefix your overload ids with your library
name (`weather_celsius`, not just `celsius`) to avoid collisions with
other libraries.

## See also

- [Building extensions](/guides/building-extensions/) — practical guide.
- [`OverloadFn`](/reference/api/cel-env/#overloaddecl) — the signature your
  runtime impls match.
- [Parser macros](/guides/parser-macros/) — the `Macros` half of the
  interface.
