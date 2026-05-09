---
title: Gradual typing
description: How `dyn` lets you opt out of static type checking — and when you should.
---

CEL is **gradually typed**. Most expressions are checked statically; values
of type `dyn` are checked at runtime. This page explains when `dyn` shows
up, what it does to type-checking, and how to use it deliberately.

## What `dyn` is

`dyn` is a marker type that says "skip the static check for this value;
trust me; resolve at runtime." It's similar in spirit to TypeScript's `any`
or Python's untyped values.

In `DotnetCel.Types.CelTypes`:

```csharp
public static readonly CelType Dyn = new DynType();
```

A `dyn` type matches anything — assignable in either direction — and it
propagates through expressions:

```cel
dyn(1) + 2                  // result type: dyn
dyn([1, 2, 3]).filter(x, x > 0)
                             // result type: dyn (the iter var is dyn too)
type(dyn(1))                 // int — runtime knows the actual value
```

## Where `dyn` enters

You will see `dyn` in three places:

1. **Explicit conversion** — `dyn(x)` is a built-in that retypes its
   argument. The most common reason is to *force* a heterogeneous list /
   map literal, where the checker would otherwise pick a single `T`.

   ```cel
   [1, 2u, 3.0]              // checker error: mixed types
   [dyn(1), 2u, 3.0]         // OK: list<dyn>
   ```

2. **Object types without a provider** — when you declare
   `Variable("user", CelTypes.Object("User"))` and no `ITypeProvider` claims
   `"User"`, the checker treats field selects on it as `dyn`. This is what
   makes the POCO path work without you registering schema:

   ```csharp
   .Variable("user", CelTypes.Object("User"))
   ```

   ```cel
   user.name + " — " + string(user.age)
   //   ^ user.name is dyn at compile time; runtime asks the POCO adapter.
   ```

3. **Type-parameter unification conflicts** — when overload resolution can't
   pick a single `A` for a parametric function, the unifier widens to `dyn`
   rather than failing the type check.

   ```cel
   [1, "two"].filter(x, true)
   // The list literal would otherwise be a checker error; CEL's "join on
   // conflict" rule widens it to list<dyn>.
   ```

## What `dyn` does to evaluation

Runtime is unaffected — every value is already a typed `CelValue`
(`IntValue`, `StringValue`, ...) regardless of how the checker labelled it.
`dyn` only changes the static check.

That has two practical consequences:

- **Errors come later.** A `string + int` that would be a compile-time error
  if both sides were typed will instead be a runtime error if either side is
  `dyn`.
- **Overloads still dispatch correctly.** The runtime looks at the actual
  value's `CelValue` subtype, not the static `dyn`.

## When to reach for `dyn`

- **Heterogeneous data without schema** — JSON-shaped maps, proto `Struct`
  values. CEL projects `google.protobuf.Value`, `ListValue`, and `Struct` to
  CEL `dyn`-shaped maps/lists.
- **Late-bound types** — when your activation provides values whose type
  isn't known at env-build time.
- **Partial-evaluation prototypes** — getting something working before
  committing to a typed env.

## When *not* to use `dyn`

Static typing is your safety net for an expression language users author. If
you can declare the types — including object types backed by an
`ITypeProvider` — do it. The static check catches typos, wrong-number-of-args
calls, type mismatches, missing fields, and overload ambiguities *at
compile time*, in the call site that compiled the rule, with line/column
diagnostics. None of that protection is available once you've widened to
`dyn`.

## Gradual typing in the spec

The full algorithm — most-general type, join-on-conflict, parametric
unification — is documented in
[`langdef.md`](https://github.com/google/cel-spec/blob/master/doc/langdef.md#gradual-type-checking).
The .NET checker implements it in `DotnetCel.Checker.TypeAlgebra`; you do not need
to understand the algorithm to use the language, but the section is short
and worth reading if you ever puzzle over a checker decision.

## Related

- [Type system](/concepts/type-system/) — the fixed types.
- [Working with POCOs](/guides/working-with-pocos/) — the main reason `dyn`
  matters in .NET hosting.
- [Evaluation model](/concepts/evaluation-model/) — what runs at compile
  time vs. at eval time.
