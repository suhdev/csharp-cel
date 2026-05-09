---
title: Performance & trimming
description: Make CEL fast in production — caching, hot-path tips, and what's needed for AOT / trim-safe deployment.
---

CEL is not a hot inner loop language by design — it sits at the *policy*
layer and runs maybe once per request. Still, there's a delta between
"obvious code" and "tuned code" that matters when rules are called
millions of times a day.

## Rule one: compile once

The single biggest win is caching `CompiledProgram` instances. Compilation
includes parsing, macro expansion, type-checking, overload resolution,
and registry-building. None of that depends on the runtime values. Pay it
once.

```csharp
// 50–200 µs per call:
foreach (var input in inputs)
{
    var p = CelExpression.Compile(rule, env);   // ❌
    Process(p.Eval(input));
}

// 50–200 µs once + 0.5–2 µs per eval:
var p = CelExpression.Compile(rule, env);       // ✅
foreach (var input in inputs)
{
    Process(p.Eval(input));
}
```

If your rules are user-authored and stored in a database, build a small
cache keyed on `(envHash, source)` and refresh on rule changes.

## Rule two: prefer dictionaries over anonymous objects

`Eval(object)` allocates an `ObjectActivation` and reflects over the
object's properties. `Eval(IReadOnlyDictionary<string, object?>)` is
essentially a hash lookup with no reflection.

```csharp
// Convenient, slower:
program.Eval(new { request = req, user = u });

// Slightly more code, faster:
program.Eval(new Dictionary<string, object?>
{
    ["request"] = req,
    ["user"] = u,
});
```

For one-off scripts, the difference doesn't matter. For a hot rule
evaluator, it's a noticeable speedup.

## Rule three: register `ITypeProvider`s for hot types

The POCO adapter is convenient and slow. It uses `Type.GetProperties()` /
`Type.GetField()` and a small reflection cache. For types you evaluate
millions of times against, write an `ITypeProvider` that does direct
field access:

```csharp
public bool TryReadField(object instance, string field, out object? value)
{
    if (instance is User u)
    {
        switch (field)
        {
            case "name": value = u.Name; return true;
            case "age": value = u.Age; return true;
            default: value = null; return false;
        }
    }
    value = null;
    return false;
}
```

Bench the difference before you do this — for most apps, the POCO adapter
is fine.

## Rule four: avoid heavy work inside CEL functions

Custom functions execute synchronously on the calling thread. Don't put
remote calls, file I/O, or DB queries behind CEL identifiers — pre-fetch
the data and put it on the activation as a value.

If a CEL rule needs derived data, compute it once before evaluation:

```csharp
var fingerprint = ComputeFingerprint(req);
var bindings = new Dictionary<string, object?>
{
    ["request"] = req,
    ["fingerprint"] = fingerprint,
};
program.Eval(bindings);
```

## Rule five: use `cel.bind` to deduplicate sub-expressions

If you find yourself writing the same sub-expression several times in
one rule:

```cel
// Evaluates user.profile.image.url three times.
has(user.profile) && has(user.profile.image) && user.profile.image.url != ''
```

`cel.bind` evaluates once:

```cel
cel.bind(img, user.?profile.?image.?url,
  img.hasValue() && img.value() != '')
```

The bindings extension is on by default in many starter envs; see the
[bindings reference](/reference/extensions/#bindings).

## AOT / trimming considerations

`CompiledProgram` and `ObjectActivation` are marked
`[RequiresUnreferencedCode]` because they reflect over runtime types. In a
trimmed/AOT app, those code paths produce trim warnings. To go fully
trim-safe:

1. **Avoid `program.Eval(object)`.** Use the dictionary or `IActivation`
   overload.
2. **Register `ITypeProvider`s for every type CEL touches.** This replaces
   the reflection-backed POCO adapter with code you wrote — no trim
   warnings.
3. **Avoid runtime-loaded extensions.** Use the same set of extensions in
   prod as you compiled with.

The conformance harness uses both paths intentionally — production code
generally takes the typed path.

## Memory

Each `Eval` allocates `CelValue` instances for intermediate results.
There's no reuse pool — the GC handles it. For most rules, the
allocation rate is well within Gen0 territory. If you measure GC
pressure as a problem:

- Profile with `dotnet-counters` to confirm CEL is the source.
- Reduce intermediate allocations in your rules — fewer `+` of strings,
  fewer comprehensions over large lists.
- Consider avoiding `EvaluateRaw` and consuming `CelValue` instances in
  your host (they're the same allocations, different ownership).

## Bench numbers

Indicative, on a modern x86_64. Single thread, .NET 10 release build.

| Operation | Time |
|-----------|------|
| `Compile("x + 1")` | ~50 µs |
| `Compile("user.role == 'admin' \|\| user.id in resource.owners")` | ~150 µs |
| `Eval` on a 5-op boolean over POCO (anonymous object root) | ~3 µs |
| `Eval` on the same with a dictionary activation | ~1.2 µs |
| `Eval` of a 100-element comprehension with simple body | ~30 µs |
| `Eval` of the same with a typed `ITypeProvider` | ~12 µs |

Treat these as orders-of-magnitude, not precise. Bench your own rules.

## See also

- [Evaluation model](/concepts/evaluation-model/) — when each phase runs.
- [`CompiledProgram` reference](/reference/api/compiled-program/) — the
  thread-safety contract.
- [Working with POCOs](/guides/working-with-pocos/) — the trade-offs of
  the reflection path vs. typed providers.
