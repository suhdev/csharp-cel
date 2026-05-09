---
title: Language tour
description: A guided walk through the CEL language with runnable .NET examples.
---

This page is the canonical "what does CEL syntax look like?" page. Every
snippet compiles against `Cel.NET`. For the full grammar see the
[cel-spec](https://github.com/google/cel-spec/blob/master/doc/langdef.md).

## Literals

```cel
true            // bool
42              // int (signed 64-bit)
42u             // uint (unsigned 64-bit)
3.14            // double
"hello"         // string
b"\x00\xff"     // bytes
null            // null
[1, 2, 3]       // list<int>
{"a": 1}        // map<string, int>
```

Strings can be single- or double-quoted, and triple-quoted strings allow
unescaped newlines. Raw strings are prefixed with `r`: `r"\n"` is two
characters.

## Identifiers and selection

```cel
account.user.name              // dotted attribute access
account["region"]              // index access (string key)
account.tags[0]                // index access (int key)
```

There is no separate "method call" form on objects — only `select` and `call`.

## Operators

```cel
1 + 2 * 3                      // arithmetic, standard precedence
"foo" + "bar"                  // string / list / bytes concatenation
[1, 2] + [3, 4]                // list concat → [1, 2, 3, 4]

a == b                         // equality across compatible types
a != b
a < b                          // ordering (same type)

x in [1, 2, 3]                 // membership
x in {"a": 1}                  // map key membership

p && q || r                    // logical (short-circuiting)
!p                             // negation

cond ? a : b                   // ternary
```

Equality is **symmetric and well-defined across CEL's numeric types** — `1 ==
1.0 == 1u` is true. Ordering across mixed numerics goes via `double` (lossy
above 2^53). Bool/string/bytes/duration/timestamp compare within their own
type.

## Built-in functions

Selected highlights — see the [stdlib reference](/reference/language/stdlib/)
for the full list:

```cel
size("hello")                  // 5
size([1, 2, 3])                // 3
size({"a": 1, "b": 2})         // 2

int("42")                      // 42
double(3)                      // 3.0
string(3.14)                   // "3.14"
bytes("hi")                    // b"\x68\x69"

type(42)                       // int
type("hi") == string           // true
has(account.tags)              // true if field present
```

## Macros

Macros look like function calls but are expanded at parse time. The standard
ones are comprehensions and `has`:

```cel
// has(): presence check (only on attribute select forms).
has(request.body)

// all/exists/exists_one: bounded quantifiers over a list or map's keys.
items.all(i, i.in_stock)
items.exists(i, i.flagged)
items.exists_one(i, i.is_default)

// map/filter: transform / keep elements.
items.map(i, i.id)                       // list of ids
items.filter(i, i.price < 100)           // sub-list

// Two-iter (cel-spec macros2): index + value.
items.map(i, v, "${i}: ${v.name}")
```

Extensions add more macros — `cel.bind(name, init, expr)` for aliasing,
`opt.optMap` for map-on-optional, and so on. See [parser
macros](/guides/parser-macros/).

## Comprehension scope

The iter variable is bound only inside the loop body:

```cel
items.filter(i, i.price < 100).size()    // i is gone here
```

`map` and `filter` produce new lists; they don't mutate. CEL has no
assignment.

## Optionals

```cel
optional.of(42)                          // optional<int> with a value
optional.none()                          // empty optional
v.orValue(0)                             // v.value or 0 if empty
v.hasValue()                             // bool
```

The optional extension also adds `?.field` and `?[k]` for null-safe selects.
See [Optionals & null](/guides/optionals-and-null/).

## Type names as values

`type(x)` returns a *type* value; types compare by structural identity:

```cel
type(42) == int
type(["x"]) == list           // unparametrized — list<T> isn't compared
type(account) == Account      // for declared object types
```

## Strings — what's available

Strings carry a small built-in set: `startsWith`, `endsWith`, `contains`,
`matches`. The [strings extension](/reference/extensions/#strings) adds
`replace`, `split`, `format`, `quote`, `lowerAscii`, ...

## Errors as values

`1 / 0`, an out-of-range index, a missing field on a typed message — these
produce errors. `&&`, `||`, and `?:` short-circuit on them per the spec:

```cel
account.is_admin || resource.owner == request.user.id
//                  ^ may error if request.user is missing
// → if account.is_admin is true, the whole expression is true regardless.
```

See [Errors & unknowns](/concepts/errors-and-unknowns/) for the full model.

## What's not in CEL

- **No assignment** — `x = 1` is a syntax error.
- **No statements / blocks** — the program is one expression.
- **No user-defined functions in-language** — functions come from the host
  environment.
- **No while/for/loop** — comprehensions only, bounded by their iterand.
- **No I/O / clock / network** — the host injects all data. `now()` is not in
  the spec; if your env declares it, it's a host-supplied function and you
  control caching and determinism.

That's the whole language in a few hundred words. See the [evaluation
model](/concepts/evaluation-model/) for what happens between
`CelExpression.Compile` and `program.Eval(...)`.
