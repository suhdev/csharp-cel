---
title: Extensions catalog
description: Every shipped extension, what it adds, and how to enable it.
---

`Cel.Extensions` ships eight optional bundles. Each is a singleton that
adds declarations, runtime impls, and (sometimes) parser macros.
Enable with `.Use(...)` on the env builder:

```csharp
var env = CelEnv.NewBuilder()
    .Use(StringsExtension.Instance)
    .Use(MathExtension.Instance)
    .Use(EncodersExtension.Instance)
    .Use(SetsExtension.Instance)
    .Use(OptionalsExtension.Instance)
    .Use(BindingsExtension.Instance)
    .Use(NetworkExtension.Instance)
    .Use(BlockExtension.Instance)
    .Build();
```

## Strings

Adds string-manipulation functions beyond the built-in `startsWith`,
`endsWith`, `contains`, `matches`.

```cel
s.charAt(i)
s.indexOf(sub)
s.indexOf(sub, fromIdx)
s.lastIndexOf(sub)
s.lastIndexOf(sub, fromIdx)
s.lowerAscii()
s.upperAscii()
s.replace(old, new)
s.replace(old, new, n)
s.split(sep)
s.split(sep, n)
s.substring(start)
s.substring(start, end)
s.trim()
s.format([args])           // printf-style: %s %d %f %e %x %o %b
s.matches(regex)
s.reverse()
s.quote()
strings.join(parts)
strings.join(parts, sep)
```

The `format` directives (`%s`, `%d`, `%f`, `%e`, `%x`, `%X`, `%o`, `%b`,
`%t`) follow the cel-spec; locale is fixed to en-US so output is
deterministic. See the [conformance status](/reference/conformance/) for
which directives are at 100%.

## Math

Numeric helpers, parametric over `int` / `uint` / `double` where it
makes sense.

```cel
math.abs(x)
math.sign(x)
math.greatest(a, b, ...)        // 1–5 args, plus list form
math.least(a, b, ...)           // 1–5 args, plus list form
math.ceil(x)
math.floor(x)
math.round(x)
math.trunc(x)
math.sqrt(x)
math.isNaN(x)
math.isInf(x)
math.isFinite(x)
math.bitAnd(a, b)
math.bitOr(a, b)
math.bitXor(a, b)
math.bitNot(a)
math.bitShiftLeft(a, n)
math.bitShiftRight(a, n)
```

## Encoders

```cel
base64.encode(bytes)
base64.decode(string)
hex.encode(bytes)
hex.decode(string)
```

`decode` returns `bytes`; `encode` returns `string`. Errors on malformed
input.

## Sets

Set predicates over lists (CEL has no separate set type — these treat
lists as multisets).

```cel
sets.contains(super, sub)        // ⊇
sets.equivalent(a, b)            // same elements (order-insensitive)
sets.intersects(a, b)            // ∩ ≠ ∅
```

## Optionals

The `optional<T>` type and its operations.

```cel
optional.of(x)                    // → optional<T>
optional.none()                   // → optional<dyn>
optional.ofNonZeroValue(x)        // null / "" / 0 / 0u / 0.0 / [] / {} → none

opt.value()                       // → T (errors if empty)
opt.hasValue()                    // → bool
opt.orValue(default)              // → T
```

Plus the `?.` and `?[k]` operators (parsed regardless of whether the
extension is enabled, but only meaningful when it is) and the parser
macros `optMap` / `optFlatMap`.

See [Optionals & null](/guides/optionals-and-null/) for the patterns.

## Bindings

A single parser macro: `cel.bind(name, init, expr)`.

```cel
cel.bind(square, x * x,
  cel.bind(double, square + square,
    double > 100))
```

Internally rewritten to a comprehension whose accumulator is the binding
name. The init expression evaluates once; the body sees `name` bound to
its result.

## Network

Network-address types and predicates. The `Cel.Extensions.NetworkExtension`
adds `ip`, `cidr`, `family`, `containsIP`, `containsCIDR`.

```cel
ip("192.168.1.1")               // → ip
ip("2001:db8::1")               // → ip (v6)
cidr("10.0.0.0/8")              // → cidr
cidr("2001:db8::/32")           // → cidr (v6)

ip.family()                      // 4 or 6
cidr.family()                    // 4 or 6
ip.string()                      // canonical form
cidr.string()                    // canonical form

cidr.contains(ip)                // bool
cidr.containsCIDR(otherCidr)     // bool
```

CEL types: `net.IP`, `net.CIDR`. Both are abstract types (not part of the
spec's primitive set); they round-trip through `string()` and `==`.

The runtime is strict about input — `192.168.1` (only 3 octets) is an
error, mixed dot/colon notation is an error.

## Block

`cel.@block(...)` is the compiler-emitted CSE block. You don't write it
by hand — the cel-go optimiser emits this when it identifies common
sub-expressions worth deduplicating.

The runtime resolves `@index0..@indexN` references via nested let-binding
scopes. Mostly there for cross-implementation consistency on programs
emitted by upstream optimisers.

## Mixing extensions

Order matters in one direction: later `.Use(...)` calls override earlier
ones if they declare the same function name. Overload ids must remain
unique across the whole env.

If you want a "kitchen sink" env:

```csharp
public static CelEnv DefaultEnv(ITypeProvider? provider = null)
{
    var b = CelEnv.NewBuilder()
        .Use(StringsExtension.Instance)
        .Use(MathExtension.Instance)
        .Use(EncodersExtension.Instance)
        .Use(SetsExtension.Instance)
        .Use(OptionalsExtension.Instance)
        .Use(BindingsExtension.Instance)
        .Use(NetworkExtension.Instance)
        .Use(BlockExtension.Instance);
    if (provider is not null) b.UseTypeProvider(provider);
    return b.Build();
}
```

This mirrors the conformance harness's setup and is the safest baseline
for "I want everything CEL has to offer".

## See also

- [Building extensions](/guides/building-extensions/) — the pattern for
  shipping your own.
- [Parser macros](/guides/parser-macros/) — what's inside `cel.bind` and
  the optionals macros.
- [Conformance](/reference/conformance/) — pass-rate breakdown per
  extension.
