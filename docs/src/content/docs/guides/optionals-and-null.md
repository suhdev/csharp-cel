---
title: Optionals & null
description: How CEL distinguishes "the value is null" from "the value is missing", and how to use the optionals extension.
---

CEL has two ways to represent "no value": **`null`** and **`optional<T>`
without a value**. They're different, and you should usually use the second
one for "this might not be there" semantics.

## `null` is a regular value

```cel
null == null            // true
type(null)              // null_type
account.next_login == null   // a normal field comparison
```

You can declare a CEL variable as `null_type` (rare), or — much more
commonly — as a wrapper or message type that *might* be null at runtime.

```csharp
.Variable("account", CelTypes.Object("Account"))
// account may legitimately be null at runtime
```

When the binding is null:

```cel
account == null      // true
has(account.id)      // error: cannot get field of null
account.id           // error
```

`null` propagates as an error through `select`. Use `has(...)` to absorb
the error gracefully.

## `optional<T>` is the principled "maybe a value" type

The optional extension introduces a typed wrapper:

```csharp
var env = CelEnv.NewBuilder()
    .Use(OptionalsExtension.Instance)
    .Variable("maybe_id", CelTypes.Optional(CelTypes.Int))
    .Build();
```

```cel
maybe_id.hasValue()       // bool
maybe_id.value()          // int (errors if empty)
maybe_id.orValue(0)       // int — fallback if empty
optional.of(42)           // optional<int> with a value
optional.none()           // empty optional<dyn>
```

The runtime representation is `OptionalValue`:

```csharp
public sealed record OptionalValue(CelValue? Inner) : CelValue
{
    public bool HasValue => Inner is not null;
}
```

## The `?.` and `?[k]` operators

Optionals also enable null-safe attribute access:

```cel
account.?profile.?image_url.orValue('/default.png')
```

`x.?y` returns:
- `optional.none()` if `x` is null *or* the field is missing,
- `optional.of(x.y)` otherwise.

The result is an `optional<T>` that you typically chain through more
`?.` selects and end with `orValue(default)`.

## Wrapper types unwrap to nullable primitives

When you read a `google.protobuf.Int32Value` field via the proto type
provider, an unset wrapper surfaces as `null` (not zero), and a set wrapper
surfaces as an unwrapped `int`. This is the distinction that makes CEL
`has(msg.maybe_id) ? string(msg.maybe_id) : 'none'` work as you'd expect.

If you want optional semantics without proto, use the optional extension
directly.

## `optMap` / `optFlatMap`

The optionals extension contributes two parser macros for chaining:

```cel
maybe_user.optMap(u, u.profile.image_url)
//   ⇒ has-checked map: returns optional<string>

maybe_user.optFlatMap(u, u.maybe_avatar)
//   ⇒ flatten when the body is itself optional
```

These are equivalent to `if hasValue then of(f(value)) else none()` but
much more readable in chains.

## Null-safe field selects on POCOs

The POCO adapter treats a missing field as a `no_such_field` error rather
than null. To get null-safe access on POCO data, either:

- use `has(obj.field) ? obj.field : default`,
- or use `obj.?field.orValue(default)` if you've enabled the optionals
  extension (the parser desugars `.?` regardless of the receiver type).

## Truthiness

CEL **does not coerce to bool**. `if (s)` (where `s: string`) is a type
error. Always be explicit:

```cel
size(s) > 0
has(account.id)
account.id != ''
maybe_thing.hasValue()
```

This is intentional — implicit truthiness is a frequent source of bugs in
expression languages.

## When to use which

| Situation | Use |
|-----------|-----|
| The value is genuinely sometimes null at runtime (e.g. a proto wrapper) | `null` semantics — use `has(...)` to test |
| You're modelling a "maybe" outcome explicitly | `optional<T>` |
| You're chaining many "maybe" attribute reads | `?.` + `optional<T>` + `orValue` |
| You want an attribute to silently default to a value | `obj.?field.orValue(default)` |

## See also

- [Errors & unknowns](/concepts/errors-and-unknowns/) — when null
  propagates as an error.
- [Optionals extension reference](/reference/extensions/#optionals).
