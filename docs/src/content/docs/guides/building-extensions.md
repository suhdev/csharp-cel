---
title: Building extensions
description: Package related declarations, runtime impls, and macros into a reusable ICelExtension.
---

An extension is the unit of reuse for CEL functionality. Anything you might
ship as a library — a math lib, a string-formatting helper, a domain DSL —
should be an `ICelExtension`. Built-in extensions ship as the canonical
examples (`StringsExtension`, `MathExtension`, ...); read their source and
copy the pattern.

## The interface

```csharp
public interface ICelExtension
{
    void ConfigureEnv(CelEnv.Builder envBuilder);
    void ConfigureRuntime(Action<string, OverloadFn> bindImpl);
    IEnumerable<CelMacro> Macros => Array.Empty<CelMacro>();
}
```

Three halves: declarations, runtime impls, and (optional) parser macros.

## A complete example

A `weather` extension that adds two functions and a small helper macro.

```csharp
using Cel;
using Cel.Types;
using Cel.Values;

public sealed class WeatherExtension : ICelExtension
{
    public static readonly WeatherExtension Instance = new();
    private WeatherExtension() { }

    public void ConfigureEnv(CelEnv.Builder b)
    {
        b.Function("weather.fahrenheitToCelsius",
            new OverloadDecl("f2c_double", [CelTypes.Double], CelTypes.Double));

        b.Function("weather.feelsLike",
            new OverloadDecl(
                "feelsLike_2",
                [CelTypes.Double, CelTypes.Double],
                CelTypes.Double));
    }

    public void ConfigureRuntime(Action<string, OverloadFn> bind)
    {
        bind("f2c_double", args =>
        {
            var f = ((DoubleValue)args[0]).Value;
            return CelValue.Of((f - 32) * 5.0 / 9.0);
        });

        bind("feelsLike_2", args =>
        {
            var t = ((DoubleValue)args[0]).Value; // celsius
            var w = ((DoubleValue)args[1]).Value; // wind km/h
            // Simple wind-chill approximation.
            var feels = 13.12 + 0.6215 * t - 11.37 * Math.Pow(w, 0.16)
                + 0.3965 * t * Math.Pow(w, 0.16);
            return CelValue.Of(feels);
        });
    }
}
```

Use it:

```csharp
var env = CelEnv.NewBuilder()
    .Use(WeatherExtension.Instance)
    .Variable("temp", CelTypes.Double)
    .Variable("wind", CelTypes.Double)
    .Build();

var program = CelExpression.Compile(
    "weather.feelsLike(weather.fahrenheitToCelsius(temp), wind)",
    env);

double feel = (double)program.Eval(new Dictionary<string, object?>
{
    ["temp"] = 32.0,
    ["wind"] = 20.0,
})!;
```

## Naming conventions

- Use a **dotted prefix** that identifies the library: `math.abs`,
  `strings.replace`, `weather.feelsLike`. Users invoke them with the prefix
  literally — there's no `using`.
- Use **stable overload ids**: `feelsLike_2`, `f2c_double`. The id is what
  ties the declaration to the runtime impl; renaming it is a breaking
  change for any consumer that has cached compiled programs.

## Adding parser macros

Some extensions provide *parser-level* sugar — functions that aren't
implementable as regular calls because they need lazy evaluation of their
args, control flow, or new bindings. Examples in built-ins:

- `cel.bind(name, init, expr)` — sequential let-bindings (BindingsExtension)
- `optMap(f)` — map over an optional (OptionalsExtension)
- `cel.@block(...)` — compiler-emitted CSE blocks (BlockExtension)

To contribute your own, override `Macros`:

```csharp
public IEnumerable<CelMacro> Macros => new[]
{
    new CelMacro(
        Name: "weather.withDefaults",
        ArgCount: 1,
        IsReceiverStyle: false,
        Expand: (ctx, target, args) =>
        {
            // Return a rewritten Expr or null to fall through.
            // ctx.NextId() allocates new AST ids and records source positions.
            // ...
            return null;
        }),
};
```

Macros run during parsing, before type checking. They produce AST nodes
that the checker then validates as if you'd written them by hand.

For the full mechanics, see [Parser macros](/guides/parser-macros/).

## Statelessness

Built-in extensions follow the **singleton pattern**:

```csharp
public sealed class WeatherExtension : ICelExtension
{
    public static readonly WeatherExtension Instance = new();
    private WeatherExtension() { }
    // ...
}
```

This guarantees one instance per process. The extension itself holds no
mutable state — it just exposes declarations and impls. If you genuinely
need configuration (a service handle, a feature flag), accept it in the
constructor and document that each instance is independent.

## Composition

```csharp
var env = CelEnv.NewBuilder()
    .Use(StringsExtension.Instance)
    .Use(MathExtension.Instance)
    .Use(WeatherExtension.Instance)
    .Use(MyDomainExtension.Instance)
    .Build();
```

Order matters in one direction: later `Use(...)` calls can override
earlier function declarations under the same name. (The .NET implementation
merges overloads under the same function name; ids must remain unique.)

## What's bundled

`Cel.Extensions` ships these out of the box:

- `StringsExtension` — `replace`, `split`, `format`, `quote`, ...
- `MathExtension` — `abs`, `greatest`, `least`, `bitAnd`, ...
- `EncodersExtension` — base64 / hex.
- `SetsExtension` — `contains`, `intersects`, `equivalent`.
- `OptionalsExtension` — `optional<T>` operations + `optMap` / `optFlatMap`.
- `BindingsExtension` — `cel.bind`.
- `NetworkExtension` — `ip`, `cidr`, `containsCIDR`.
- `BlockExtension` — `cel.@block`.

See the [extensions reference](/reference/extensions/) for each one's
function catalog.

## Testing

A unit test for an extension typically:

1. Builds a tiny env with the extension.
2. Compiles a fixture expression.
3. Asserts the runtime result.

```csharp
[Fact]
public void FeelsLike_AtFreezing_WithWind_IsColder()
{
    var env = CelEnv.NewBuilder().Use(WeatherExtension.Instance)
        .Variable("t", CelTypes.Double)
        .Variable("w", CelTypes.Double)
        .Build();

    var program = CelExpression.Compile("weather.feelsLike(t, w)", env);
    var feel = (double)program.Eval(new Dictionary<string, object?>
    {
        ["t"] = 0.0,
        ["w"] = 30.0,
    })!;

    Assert.True(feel < 0.0);
}
```

For more involved extensions, follow the conformance harness pattern: a
table of `(expr, expected)` cases.
