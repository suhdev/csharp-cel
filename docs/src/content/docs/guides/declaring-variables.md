---
title: Declaring variables
description: How variables flow from the env, through the checker, to the activation at runtime.
---

A CEL **variable** is the bridge between your host code and an expression.
Every identifier in the CEL source must resolve to *something* on the env —
typically a variable declaration. This guide is the cookbook.

## The two halves

A variable has two halves:

- **Declaration** — name + CEL type. Lives on `CelEnv`. Used by the checker.
- **Binding** — name + CLR value. Lives in the `IActivation` you pass to
  `Eval`. Used by the runtime.

Mismatch between the two is your responsibility. The checker doesn't see the
runtime values, and the runtime doesn't enforce that bound values match
declared types. (It mostly does the right thing because `CelValue`'s
discriminator tells the runtime what type each value is — but
`Variable("x", CelTypes.Int)` then `bindings["x"] = "hello"` will produce
runtime errors when an int is expected.)

## The simplest case

```csharp
var env = CelEnv.NewBuilder()
    .Variable("count", CelTypes.Int)
    .Variable("label", CelTypes.String)
    .Build();

var program = CelExpression.Compile("label + ': ' + string(count)", env);

var s = (string)program.Eval(new Dictionary<string, object?>
{
    ["count"] = 42,
    ["label"] = "answer",
})!;
// "answer: 42"
```

## Compound types

```csharp
.Variable("tags", CelTypes.List(CelTypes.String))
.Variable("counters", CelTypes.Map(CelTypes.String, CelTypes.Int))
.Variable("user", CelTypes.Object("User"))
.Variable("flagged", CelTypes.Optional(CelTypes.Bool))
```

The CLR-side bindings:

```csharp
new Dictionary<string, object?>
{
    ["tags"] = new[] { "a", "b" },                            // any IEnumerable<string>
    ["counters"] = new Dictionary<string, int> { ["a"] = 1 }, // any IDictionary
    ["user"] = userPoco,                                      // any object
    ["flagged"] = true,                                       // bool? null = "no value"
}
```

`null` is allowed for **wrapper**, **optional**, and **object** types. It
will produce runtime errors if used where a non-nullable primitive is
expected.

## Object types

There are three flavours of "object type" depending on how strict you want
to be:

```csharp
// 1. Opaque — checker treats fields as dyn, runtime uses POCO reflection.
.Variable("user", CelTypes.Object("User"))

// 2. Provider-backed — checker validates field names and types.
.UseTypeProvider(new MyProvider())
.Variable("user", CelTypes.Object("User"))

// 3. Anonymous root — top-level properties of an object become variables.
program.Eval(new { user = ..., request = ... })
```

See [Working with POCOs](/guides/working-with-pocos/) for the trade-offs.

## Container-qualified names

Set a `Container` on the env to give the checker a default namespace:

```csharp
var env = CelEnv.NewBuilder()
    .SetContainer("acme.v1")
    .Variable("acme.v1.request", CelTypes.Object("acme.v1.Request"))
    .Build();

var program = CelExpression.Compile("request.user.name", env);
```

The checker resolves `request` by walking candidate names: first the
unqualified `request`, then `acme.v1.request`. The longest match that
matches a declared variable wins. This mirrors proto's package resolution
rules.

## Variable shadowing

Comprehension iter variables and `cel.bind` accumulator names take
precedence over variables of the same name from the env. So:

```cel
items.map(items, items.id)
//        ^      ^
//        iter   refers to iter, not the outer 'items' list
```

This is a CEL spec rule, not a .NET-specific quirk.

## Renaming or deriving variable types

`CelEnv` is immutable, but `Extend()` returns a builder seeded from the
existing env. Use it to derive specialized envs for different rule
categories:

```csharp
var baseEnv = CelEnv.NewBuilder()
    .Use(StringsExtension.Instance)
    .Build();

var requestEnv = baseEnv.Extend()
    .Variable("request", CelTypes.Object("acme.v1.Request"))
    .Build();

var responseEnv = baseEnv.Extend()
    .Variable("response", CelTypes.Object("acme.v1.Response"))
    .Build();
```

## Listing what's declared

```csharp
foreach (var (name, decl) in env.Variables)
{
    Console.WriteLine($"{name}: {decl.Type}");
}
```

This is the env's "schema" surface — useful for building rule-authoring
UIs that autocomplete variable names.

## Common mistakes

- **Forgetting to declare** — a CEL identifier that has no env entry produces
  `undeclared reference to 'x'` at compile time.
- **Wrong CEL type** — `Variable("count", CelTypes.String)` then
  `bindings["count"] = 42` produces runtime errors when `count` is used
  as a string. Pick the type that matches your data.
- **Treating object types as schema** — `CelTypes.Object("User")` does not
  describe `User`'s fields. To describe them, register a type provider.
- **Mixing case** — CEL is case-sensitive. `Count` and `count` are
  different.

## See also

- [`CelEnv` reference](/reference/api/cel-env/) — the full builder API.
- [Activations](/reference/api/activations/) — how variables are looked up
  at runtime.
- [Working with POCOs](/guides/working-with-pocos/) — when to use
  `Object("...")` vs. anonymous-root activation.
