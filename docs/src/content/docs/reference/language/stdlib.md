---
title: Standard library
description: Built-in functions available in every CEL environment.
---

The standard library is included by default. Call
`CelEnv.NewBuilder().WithoutStandardLibrary()` if you want a stripped-down
env (rare — usually for sandboxing).

## Conversions

```cel
int(x)            // x: int / uint / double / string / timestamp / duration / enum
uint(x)           // x: int / uint / double / string
double(x)         // x: int / uint / double / string
string(x)         // x: any printable
bytes(x)          // x: bytes / string
type(x)           // any → type
duration(s)       // s: string ('5m', '1h30m', ...)
timestamp(s)      // s: string (RFC 3339)
dyn(x)            // any → dyn (gradual typing escape hatch)
```

Conversions throw on out-of-range or malformed input.

## `size`

```cel
size(s)           // string → int (utf8 char count)
size(b)           // bytes  → int (byte count)
size(l)           // list   → int (element count)
size(m)           // map    → int (entry count)
```

## `has`

```cel
has(obj.field)    // proto presence / has-key / has-property
```

`has` is a **macro**, not a function — it only accepts attribute selects.
See [Errors & unknowns](/concepts/errors-and-unknowns/) for the semantics
across object types.

## String predicates

```cel
s.startsWith(prefix)
s.endsWith(suffix)
s.contains(sub)
s.matches(regex)         // regex follows RE2 syntax
```

For richer string ops (replace, split, format), use the [strings
extension](/reference/extensions/#strings).

## Comprehensions (macros)

```cel
list.all(x, p)            // ∀ x ∈ list, p(x)
list.exists(x, p)         // ∃ x ∈ list, p(x)
list.exists_one(x, p)     // exactly one
list.map(x, f)            // [f(x) for x in list]
list.filter(x, p)         // [x for x in list if p(x)]
```

`map` and `filter` work the same on map keys (`m.map(k, f)`).

Two-iter forms (cel-spec macros2):

```cel
list.map(i, v, expr)      // i is index, v is value
list.filter(i, v, expr)
list.all(i, v, expr)
list.exists(i, v, expr)
```

For the parser-level macro framework, see [Parser
macros](/guides/parser-macros/).

## Time

```cel
ts.getFullYear()
ts.getMonth()
ts.getDayOfMonth()
ts.getDayOfWeek()
ts.getHours()
ts.getMinutes()
ts.getSeconds()
ts.getMilliseconds()
ts.getDate()              // alias for getDayOfMonth
ts.getDayOfYear()

dur.getHours()
dur.getMinutes()
dur.getSeconds()
dur.getMilliseconds()
```

All getters take an optional second argument: a timezone string
(`'America/Los_Angeles'`, `'UTC'`, `'+05:30'`):

```cel
ts.getHours('UTC')
```

## Arithmetic on time

```cel
timestamp - timestamp     // → duration
timestamp - duration      // → timestamp
timestamp + duration      // → timestamp
duration  + duration      // → duration
duration  - duration      // → duration
```

## Optionals (when extension is enabled)

```cel
optional.of(x)
optional.none()
opt.value()
opt.orValue(default)
opt.hasValue()
optional.ofNonZeroValue(x)   // null/zero → none, else of(x)
```

## Reflection

```cel
type(x)                   // returns a type value
type(x) == int
type(x) == list           // unparametrised compare for parametric types
```

## Error & dynamic

```cel
dyn(x)                    // type at runtime; bypasses static checks
```

## See also

- [Operators](/reference/language/operators/) — the operator-shaped
  built-ins.
- [Macros](/reference/language/macros/) — comprehensions and `has`.
- [Extensions](/reference/extensions/) — the catalog of optional libraries.
