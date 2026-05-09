---
title: Macros
description: The parser-level macros — has, comprehensions — plus the macros2 two-iter forms and how extensions add more.
---

A **macro** in CEL is a parser-level rewrite. It looks like a function call
but is expanded to other CEL constructs before type-checking. Macros are
how CEL gets lazy evaluation, control flow, and new bindings without a
real procedural runtime.

## Standard macros

These are baked into every CEL implementation:

### `has(obj.field)`

True iff `field` is present on `obj`. Works on:

- proto messages — uses presence semantics (proto2 has-bit, proto3
  optional, oneof discriminator, repeated/map non-empty, message non-null).
- maps — true iff the key is present.
- POCO objects via the reflection adapter — true iff the property/field
  exists and is non-default.

```cel
has(account.region)
has(headers["X-API-Key"])
has(items[0])
```

`has` only accepts attribute / index access. `has(x)` (bare identifier)
is a parser error.

### Comprehensions

```cel
list.all(x, p(x))               // ∀ x ∈ list, p(x)
list.exists(x, p(x))            // ∃ x ∈ list, p(x)
list.exists_one(x, p(x))        // exactly one
list.map(x, f(x))               // [f(x) ...]
list.filter(x, p(x))            // [x ...] where p(x)
```

The iter variable (`x`) is bound only inside the macro body. After the
macro, it's gone.

`all`, `exists`, and `exists_one` short-circuit through error/unknown the
same way `&&` and `||` do.

### Two-iter forms (cel-spec macros2)

Most comprehensions accept an optional `(index, value)` form:

```cel
items.map(i, v, "${i}: ${v.name}")
items.filter(i, v, i % 2 == 0)
m.map(k, v, k + ' = ' + string(v))
```

For lists, `i` is the index; for maps, `i` is the key.

## Extension-contributed macros

Extensions can ship their own macros via `ICelExtension.Macros`. The
shipped extensions add:

### `cel.bind(name, init, expr)`

Sequential let-binding. Evaluates `init` once, binds it to `name`, then
evaluates `expr` with that binding.

```cel
cel.bind(d, geo.distance(a, b),
  d < 100 ? 'near' : (d < 1000 ? 'mid' : 'far'))
```

Internally rewritten to a comprehension over an empty list whose
accumulator is `name`. From the [bindings extension](/reference/extensions/#bindings).

### `optMap(f)` / `optFlatMap(f)`

Receiver-style on optionals.

```cel
maybe.optMap(x, x.upper())          // optional<T> → optional<U>
maybe.optFlatMap(x, x.maybe_other)  // optional<T> → optional<U>, flattening
```

From the [optionals extension](/reference/extensions/#optionals).

### `cel.@block(...)`

Compiler-emitted CSE block. Generates nested let-bindings
`@index0..@indexN` that the runtime resolves through scoped activations.
You don't write this by hand — the cel-go optimiser emits it.

From the [block extension](/reference/extensions/#block).

## Writing your own

See [Parser macros](/guides/parser-macros/) for the full mechanics:
allocate AST ids, build child expressions, return either a rewritten
`Expr` or `null` to fall through to a regular call.

## Macro vs. function

| | Macro | Function |
|---|-------|----------|
| Args evaluated eagerly? | no — macro decides | yes |
| Can introduce new bindings? | yes (e.g. iter var) | no |
| Where it runs | parser | runtime |
| Type-checked | yes (after expansion) | yes (declaration-driven) |
| Uses `OverloadDecl`? | no | yes |
| Uses `OverloadFn`? | no | yes |

If you don't need lazy evaluation or new bindings, write a function. Macros
are heavier and more error-prone — only reach for them when needed.

## See also

- [Standard library](/reference/language/stdlib/) — comprehensions are
  listed there too.
- [Building extensions](/guides/building-extensions/) — packaging macros
  with declarations.
