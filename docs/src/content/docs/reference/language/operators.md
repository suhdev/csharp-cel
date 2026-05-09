---
title: Operators
description: Every CEL operator, with type rules and short-circuit semantics.
---

CEL operators are sugar for ordinary function calls. The checker resolves
each operator to one or more overloads (e.g. `+` has overloads for `int`,
`uint`, `double`, `string`, `bytes`, `list`); the runtime dispatches by
overload id.

## Arithmetic

| Op | Types | Notes |
|----|-------|-------|
| `+` | `int`, `uint`, `double`, `string`, `bytes`, `list<T>` | concatenation for non-numeric kinds |
| `-` | `int`, `uint`, `double`, `timestamp - timestamp → duration`, `timestamp - duration → timestamp` | unary `-` for numeric |
| `*` | `int`, `uint`, `double` | overflow → error |
| `/` | `int`, `uint`, `double` | divide-by-zero → error |
| `%` | `int`, `uint` | divide-by-zero → error |

## Comparison

| Op | Types |
|----|-------|
| `==`, `!=` | any |
| `<`, `<=`, `>`, `>=` | `bool`, `int`, `uint`, `double`, `string`, `bytes`, `duration`, `timestamp`; cross-numeric mixes via double |

CEL equality is **symmetric across compatible numeric types**:
`1 == 1u == 1.0` is true. `null == null` is true. **NaN is never equal to
anything**, including itself.

## Logical

| Op | Notes |
|----|-------|
| `&&` | short-circuits; absorbs errors when result is `false` |
| `\|\|` | short-circuits; absorbs errors when result is `true` |
| `!` | negation |

The short-circuit / error-absorption rules are documented under [Errors
& unknowns](/concepts/errors-and-unknowns/).

## Membership

| Op | Notes |
|----|-------|
| `x in list` | true if any element equals `x` |
| `k in map` | true if `k` is a key |

## Ternary

```cel
cond ? a : b
```

`cond` must be `bool`. Errors short-circuit per the spec table.

## Indexing & selection

| Form | Meaning |
|------|---------|
| `obj.field` | attribute select |
| `obj["k"]` | index access (string key on map / dyn) |
| `list[i]` | index access (int key on list) |
| `obj.?field` | optional select — see [Optionals](/guides/optionals-and-null/) |
| `obj[?"k"]` | optional index |

## Construction

| Form | Meaning |
|------|---------|
| `[1, 2, 3]` | list literal |
| `{"a": 1, "b": 2}` | map literal |
| `pkg.Type{a: 1}` | object construction (provider-mediated) |

## Operator overload ids

For function-style binding (e.g. when overriding equality on the runtime
registry):

| Operator | Overload ids |
|----------|--------------|
| `+` | `add_int_int`, `add_uint_uint`, `add_double_double`, `add_string_string`, `add_bytes_bytes`, `add_list_list`, `add_timestamp_duration`, `add_duration_duration`, `add_duration_timestamp` |
| `-` | `subtract_int_int`, `subtract_uint_uint`, `subtract_double_double`, `subtract_timestamp_timestamp`, `subtract_timestamp_duration`, `subtract_duration_duration`, `negate_int`, `negate_double` |
| `*` | `multiply_int_int`, `multiply_uint_uint`, `multiply_double_double` |
| `/` | `divide_int_int`, `divide_uint_uint`, `divide_double_double` |
| `%` | `modulo_int_int`, `modulo_uint_uint` |
| `==` | `equals` (parametric over types) |
| `!=` | `not_equals` |
| `<`, `<=`, `>`, `>=` | `less`, `less_equals`, `greater`, `greater_equals` (cross-type and same-type variants) |
| `&&`, `\|\|`, `!` | `logical_and`, `logical_or`, `logical_not` |
| `in` | `in_list`, `in_map` |
| `?:` | `conditional` |

These ids are stable across versions — feel free to bind alternative
implementations on the runtime registry if you need custom semantics
(see e.g. how `CompiledProgram` overrides `equals` to consult the
`ITypeProvider`).

## Precedence

From highest to lowest:

1. `()`, `[]`, `.`, function call
2. `!`, unary `-`
3. `*`, `/`, `%`
4. `+`, `-`
5. `<`, `<=`, `>`, `>=`
6. `==`, `!=`
7. `in`
8. `&&`
9. `||`
10. `?:`

This matches the cel-spec grammar. Use parentheses when the structure
matters; CEL does not support custom operators.

## See also

- [Standard library](/reference/language/stdlib/) — built-in functions
  that aren't operators.
- [Errors & unknowns](/concepts/errors-and-unknowns/) — short-circuit
  table.
