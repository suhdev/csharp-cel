---
title: Errors & unknowns
description: How CEL treats errors as first-class values and what that means for your host code.
---

CEL's evaluation model treats both **errors** and **unknowns** as values that
flow through the AST. They short-circuit through logical operators and the
ternary, just like booleans do. This page is the canonical reference for how
that works in `Cel.NET`.

## Errors are not exceptions

When something goes wrong at runtime — a divide by zero, an out-of-range
index, a missing field on a typed message — the evaluator does **not** throw.
It returns an `ErrorValue` with a message and an optional code. Your host
code only sees a `CelEvaluationException` when an error reaches the top of
the expression with nothing absorbing it.

```csharp
public sealed record ErrorValue(string Message, string? Code = null) : CelValue { }
```

## Short-circuiting

Per the spec, the logical operators absorb errors when their result is
already determined:

| Expression | Result |
|------------|--------|
| `true \|\| error` | `true` |
| `false \|\| error` | `error` |
| `false && error` | `false` |
| `true && error` | `error` |
| `error \|\| true` | `true` (right side dominates) |
| `error && false` | `false` (right side dominates) |
| `true ? a : error` | `a` |
| `false ? error : b` | `b` |

This makes "tolerant" rules natural to write:

```cel
account.is_admin || resource.owner == request.user.id
//                  ^ may error if request.user is missing
```

If `account.is_admin` is true, the right side never evaluates and its
potential error is irrelevant.

## What raises errors

The standard library produces errors for:

- arithmetic overflow (`int + int` past 2^63, `int + uint` past 2^64,
  `int - uint` mismatched, etc.)
- division/modulo by zero (any numeric type)
- conversions out of range (`int(double.MaxValue)`, `int("not a number")`)
- proto-or-host integer field assignment past int32 range when the field is
  a `int32`
- bad regex in `matches`
- malformed timestamp / duration parses
- index out of range on lists
- missing key on a typed map (`m["k"]` where the env declared `m: map<string,
  int>` and `k` is absent — CEL spec says this is an error, not `null`)

Extensions add their own — `math.sqrt(-1)`, `net.ip("not an address")`,
`encoders.base64Decode("not base64")`, ...

## Catching errors at the boundary

```csharp
try
{
    var v = program.Eval(activation);
}
catch (CelEvaluationException ex)
{
    // ex.Message has the spec-flavoured error string.
    log.Error("CEL: {Message}", ex.Message);
    return Defaults.Deny;
}
```

In services, the typical pattern is:

```csharp
public bool Allows(Request req)
{
    try { return (bool)_program.Eval(new { req })!; }
    catch (CelEvaluationException) { return false; }
}
```

The runtime is intentionally conservative — it does not throw for "the data
shape was unexpected" cases that the operator semantics already absorb. So if
you *do* see a `CelEvaluationException`, something genuinely interesting
happened.

## Unknowns

`UnknownValue` is the second short-circuiting value. It represents an
attribute that the host hasn't yet supplied — used by partial-evaluation
flows where some inputs are deferred.

```csharp
public sealed record UnknownValue(ImmutableArray<long> AttributePath) : CelValue { }
```

The `AttributePath` is the AST id chain that produced the unknown. This lets
the caller build a "what would I need to fully evaluate this rule?" set of
attribute references.

The conformance corpus has an `unknowns.textproto` file driving this. The
.NET runtime represents unknowns internally; it does not yet expose a public
API for emitting them from activations. Tracking issue: see the
[conformance status](/reference/conformance/) page (`unknowns` is currently
0% — feature gap rather than test failures).

## Errors vs. nulls

These are different things:

| | Error | Null |
|---|-------|------|
| Type | `error` | `null_type` |
| Runtime | `ErrorValue` | `NullValue` |
| `==` | error propagates | `null == null` → true |
| `&&` / `\|\|` short-circuit | yes | no |
| Surfaces as | `CelEvaluationException` at boundary | `null` at boundary |

`null` is a normal value. `error` is the absence of a sensible result. The
distinction is what makes "tolerant" predicates clean: a missing optional
field maps to `null`, not an error; a divide by zero maps to error, not null.

## Designing for graceful degradation

If you want a rule that should "just be false on bad inputs", wrap it in a
`||` or `&&` that absorbs:

```cel
// No: errors out if request.user is missing.
request.user.is_admin

// Yes: errors out the same way.
request.user.is_admin     // still errors when select fails

// Yes (graceful): treat missing field as not-admin.
has(request.user) && request.user.is_admin
```

The `has(...)` form short-circuits via `&&`, so a missing `request.user` is
absorbed. This is the idiomatic CEL pattern for "tolerant" predicates.

## Going deeper

- The full short-circuit table lives in
  [`langdef.md`](https://github.com/google/cel-spec/blob/master/doc/langdef.md#logical-operators).
- The `comparisons.textproto` and `dynamic.textproto` corpus files exercise
  the trickier cases.
- For the runtime sum type, see [`CelValue`
  reference](/reference/api/cel-value/).
