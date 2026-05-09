---
title: Activations
description: IActivation and the built-in implementations — MapActivation, ObjectActivation, ChainedActivation.
---

An **activation** is the runtime side of the variable system. `CelEnv`
declares names and types at compile time; `IActivation` resolves names to
values at evaluation time.

## The interface

```csharp
namespace Cel.Runtime;

public interface IActivation
{
    bool TryResolve(string name, out object? value);
}
```

The contract is intentionally tiny. Returning `true` with `value = null`
means "I have a binding for this name and its value is null." Returning
`false` means "I do not provide this name." The runtime relies on this
distinction for chained activations.

## `MapActivation`

Wraps an `IReadOnlyDictionary<string, object?>`.

```csharp
var bindings = new Dictionary<string, object?>
{
    ["x"] = 42,
    ["name"] = "alice",
    ["maybe"] = null,
};

program.Eval(bindings);    // implicit: wraps in MapActivation
program.Eval(new MapActivation(bindings));   // explicit
```

There's also `MapActivation.From(IDictionary)` for non-generic
dictionaries.

## `ObjectActivation`

Reflects over a root object's public properties and fields. Each member
becomes an addressable variable.

```csharp
[RequiresUnreferencedCode]
public sealed class ObjectActivation : IActivation
{
    public ObjectActivation(object root);
}

program.Eval(new
{
    request = req,
    user = u,
});
```

The constructor walks `root.GetType()` once and caches a name → value map,
so subsequent `TryResolve` calls are dictionary lookups. Mutating the
root after building the activation does **not** update the cached values.

> **Trim warning**: `ObjectActivation` is `[RequiresUnreferencedCode]`. To
> stay trim-safe, prefer `MapActivation`. See [Performance &
> trimming](/guides/performance-and-trimming/).

## `ChainedActivation`

Tries each child activation in order; the first to claim a name wins.
Useful for layering globals under per-request bindings.

```csharp
var globals = new MapActivation(new Dictionary<string, object?>
{
    ["NOW"] = DateTimeOffset.UtcNow,
    ["VERSION"] = "1.2.3",
});

var perRequest = new MapActivation(new Dictionary<string, object?>
{
    ["request"] = req,
});

var combined = new ChainedActivation(perRequest, globals);
program.Eval(combined);
```

## Custom activations

Implementing `IActivation` directly is the right move when you want
something the built-ins can't give you — typically lazy resolution or a
backing store.

### Lazy activation

Defer expensive work until CEL actually references it:

```csharp
public sealed class LazyActivation : IActivation
{
    private readonly Func<string, object?> _fetch;
    private readonly Dictionary<string, object?> _cache = new(StringComparer.Ordinal);

    public LazyActivation(Func<string, object?> fetch) => _fetch = fetch;

    public bool TryResolve(string name, out object? value)
    {
        if (_cache.TryGetValue(name, out value)) return true;
        value = _fetch(name);
        if (value is null) return false;
        _cache[name] = value;
        return true;
    }
}
```

If your CEL rule only references `user`, you don't pay for fetching
`account` or `request`. Make sure `_fetch` is cheap to call for "I don't
have this name" — those early-exits are the common case.

### Read-through to a session / context

```csharp
public sealed class ContextActivation : IActivation
{
    private readonly HttpContext _ctx;
    public ContextActivation(HttpContext ctx) => _ctx = ctx;

    public bool TryResolve(string name, out object? value) => name switch
    {
        "user" => Try(_ctx.User?.Identity?.Name, out value),
        "ip" => Try(_ctx.Connection.RemoteIpAddress?.ToString(), out value),
        "path" => Try(_ctx.Request.Path.Value, out value),
        _ => Fail(out value),
    };

    private static bool Try<T>(T v, out object? value)
    {
        value = v;
        return v is not null;
    }
    private static bool Fail(out object? value) { value = null; return false; }
}
```

## What an activation must NOT do

- **Mutate state in `TryResolve`.** The runtime may call it more than once
  for the same name (e.g. when a comprehension references a variable in
  its body and step), and it has to be deterministic.
- **Throw arbitrary exceptions.** If a name resolution genuinely fails,
  return `false` — the runtime turns that into a CEL `no_such_attribute`
  error which short-circuits cleanly.
- **Block on I/O for a "common" name.** Activations run on the eval
  thread; expensive lookups in the hot path will dominate.

## Returning a `CelValue` directly

If your activation already has a `CelValue` for a binding (e.g. you
constructed it from a CEL value rather than a CLR object), return it as
the `value`. The runtime detects `CelValue` instances and skips the
re-wrap step:

```csharp
public bool TryResolve(string name, out object? value)
{
    if (name == "preset") { value = CelValue.Of(42); return true; }
    value = null;
    return false;
}
```

## See also

- [`CompiledProgram`](/reference/api/compiled-program/) — how activations
  are passed in.
- [Declaring variables](/guides/declaring-variables/) — the
  declaration side.
