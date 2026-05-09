---
title: Declaring functions
description: Add custom functions to CEL — declarations, overloads, runtime impls, and the receiver vs. global call forms.
---

The standard library covers operators and a few dozen built-ins. Anything
else — domain functions, host integrations, computed pseudo-fields — comes
in via custom function declarations. This guide covers the full pipeline:
declaration on the env, overload selection, runtime binding.

## Anatomy of a function

A function has three parts:

1. A **name** (`my.func`).
2. One or more **`OverloadDecl`s** — each defines arg types, result type,
   and a unique **overload id**.
3. A **runtime implementation** for each overload id.

```csharp
using Cel;
using Cel.Types;

var env = CelEnv.NewBuilder()
    .Function("greet",
        new OverloadDecl(
            id:       "greet_string",
            args:     [CelTypes.String],
            result:   CelTypes.String))
    .Build();
```

## Wiring up the runtime

Bare `CelEnv` declarations have no runtime side. To bind implementations,
package the function as an `ICelExtension`:

```csharp
public sealed class GreetExtension : ICelExtension
{
    public void ConfigureEnv(CelEnv.Builder b)
    {
        b.Function("greet",
            new OverloadDecl("greet_string", [CelTypes.String], CelTypes.String));
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

Use it on the env:

```csharp
var env = CelEnv.NewBuilder()
    .Use(new GreetExtension())
    .Build();

var program = CelExpression.Compile("greet('alice')", env);
Console.WriteLine(program.Eval(new Dictionary<string, object?>())); // hello, alice
```

The env stores the extension; `CompiledProgram` constructs a
`FunctionRegistry`, calls each extension's `ConfigureRuntime` to bind
implementations, and dispatches by overload id at eval time.

## Multiple overloads

```csharp
public void ConfigureEnv(CelEnv.Builder b) =>
    b.Function("greet",
        new OverloadDecl("greet_string",  [CelTypes.String],  CelTypes.String),
        new OverloadDecl("greet_object",  [CelTypes.Object("User")], CelTypes.String));

public void ConfigureRuntime(Action<string, OverloadFn> bind)
{
    bind("greet_string", args => CelValue.Of($"hello, {((StringValue)args[0]).Value}"));
    bind("greet_object", args => /* read user.name from args[0] */ ...);
}
```

The checker picks the overload whose arg types match. If multiple match,
the most specific wins; if multiple match equally, you get a compile-time
ambiguity error.

## Receiver-style ("method" calls)

CEL has no methods, but it has *receiver-style* calls: `x.foo(y)` is sugar
for a function `foo` whose first arg is the receiver. Declare them with
`isReceiverStyle: true`:

```csharp
b.Function("foo",
    new OverloadDecl(
        id: "foo_receiver",
        args: [CelTypes.String, CelTypes.Int],
        result: CelTypes.String,
        isReceiverStyle: true));
```

Now `s.foo(3)` resolves to `foo_receiver` with `args[0] = s`, `args[1] = 3`.

## Type-parameter generics

```csharp
var A = CelTypes.TypeParam("A");

b.Function("first",
    new OverloadDecl(
        id: "first_list",
        args: [CelTypes.List(A)],
        result: A,
        typeParams: ["A"]));
```

The checker unifies `A` against the actual list's element type. `first(['a',
'b'])` returns `string`; `first([1, 2])` returns `int`.

## Returning errors

```csharp
bind("greet_string", args =>
{
    var name = ((StringValue)args[0]).Value;
    if (name.Length == 0)
    {
        return CelValue.Error("greet: empty name");
    }
    return CelValue.Of($"hello, {name}");
});
```

`CelValue.Error(...)` produces an `ErrorValue`. It short-circuits through
operators per the [error model](/concepts/errors-and-unknowns/).

## Reading args ergonomically

The runtime hands you a `ReadOnlySpan<CelValue>`. Pattern-matching is the
nicest read:

```csharp
bind("clamp_3", args =>
{
    var v = ((IntValue)args[0]).Value;
    var lo = ((IntValue)args[1]).Value;
    var hi = ((IntValue)args[2]).Value;
    return CelValue.Of(Math.Clamp(v, lo, hi));
});
```

For `dyn`-typed args, you'll see various `CelValue` subtypes — switch over
them or guard with type checks.

## Stateful or expensive functions

Bind a closure over your service object:

```csharp
public sealed class GeoExtension : ICelExtension
{
    private readonly IGeoService _geo;
    public GeoExtension(IGeoService geo) => _geo = geo;

    public void ConfigureEnv(CelEnv.Builder b) =>
        b.Function("geo.distance",
            new OverloadDecl("geo_distance_2",
                [CelTypes.String, CelTypes.String], CelTypes.Double));

    public void ConfigureRuntime(Action<string, OverloadFn> bind)
    {
        // Local copy so the lambda captures by value.
        var geo = _geo;
        bind("geo_distance_2", args =>
        {
            var a = ((StringValue)args[0]).Value;
            var b = ((StringValue)args[1]).Value;
            return CelValue.Of(geo.Distance(a, b));
        });
    }
}
```

Be aware that **CEL extensions run on whatever thread `Eval` runs on** —
your service must be thread-safe for concurrent evaluations. Avoid I/O
unless your evaluator is single-threaded; expression languages are not the
right place for blocking calls.

## Determinism and caching

CEL doesn't cache function results across calls (or even within an
expression). If your function is expensive and pure, that's fine — but if
it's expensive *and* called multiple times with the same args inside the
same expression, you should restructure the rule with `cel.bind`:

```cel
cel.bind(d, geo.distance(a, b),
  d < 100 ? 'near' : (d < 1000 ? 'mid' : 'far'))
```

`cel.bind` evaluates the init expression once and reuses the result. See
the [bindings extension](/reference/extensions/#bindings).

## See also

- [Building extensions](/guides/building-extensions/) — packaging multiple
  related functions, plus parser-level macros.
- [`OverloadDecl` reference](/reference/api/cel-env/#overloaddecl) — every
  field explained.
- [Standard library](/reference/language/stdlib/) — what's already there
  before you add yours.
