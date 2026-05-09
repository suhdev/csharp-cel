---
title: What is CEL?
description: Background, design goals, and where CEL fits among other expression languages.
---

The **Common Expression Language** is a small, sandboxed expression language
built by Google for policy, configuration, and rule engines. It's the engine
behind Cloud IAM conditions, Kubernetes admission policies, GCP organization
policies, the gRPC and Envoy proxy rule systems, Tink keyset selection, and
several internal Google products.

It was *not* designed to be a programming language. It is designed to be:

- **Total** — every well-typed expression terminates. There are no while-loops,
  no recursion, no goto. Comprehensions are bounded by their iterand.
- **Safe to host** — there is no I/O, no syscalls, no network, no file system.
  The host decides what variables and functions exist.
- **Cheap to validate** — programs are parsed and statically type-checked once.
  The runtime is a stripped-down tree walker; common rules evaluate in
  microseconds.
- **Stable** — the [language definition][langdef] is small (one Markdown file)
  and the [conformance corpus][conformance] is the spec's executable form.
  Implementations across languages stay in sync because they all dispatch the
  same `.textproto` test cases.

[langdef]: https://github.com/google/cel-spec/blob/master/doc/langdef.md
[conformance]: https://github.com/google/cel-spec/tree/master/tests/simple/testdata

## When to reach for CEL

The pattern that fits CEL well: *you have a rule a user wants to author, and
you need to evaluate it against your application's data without giving them
arbitrary code execution.*

- **Authorization** — `request.user.role == "admin" || resource.owner == request.user.id`
- **Validation** — `size(input.name) <= 64 && input.name.matches('^[a-z0-9-]+$')`
- **Routing / dispatch** — `event.type in ['order.placed', 'order.fulfilled']`
- **Feature gating** — `user.tier == "pro" && experiments.x42 == "treatment"`
- **Filtering** — `items.filter(i, i.price < 100 && i.in_stock)`

When **not** to reach for CEL: imperative side-effects, multi-step procedural
code, anything that needs loops with mutable state. Use a real scripting
language for those.

## What you write vs. what runs

CEL syntax is C-family (operators, function calls, dotted attribute access)
with a thin functional flavour (comprehensions, ternary). A program is one
expression — there are no statements, no `return`, no functions you define
in-language.

```cel
account.is_admin || (
  request.size <= account.max_size
    && request.region in account.allowed_regions
)
```

Under the hood, the parser produces a tagged AST (`call`, `select`, `ident`,
`literal`, `comprehension`, ...), the checker decorates each node with a
resolved type, and the runtime walks the tree against an *activation* (a
name → value lookup).

## Variants and dialects

The spec is the canonical surface, but real deployments add libraries:

- **Standard library** — operators, conversions, `size()`, `type()`, `has()`,
  string predicates. Always present.
- **String extensions** — `strings.replace`, `strings.split`, `format`, ...
- **Math** — `math.abs`, `math.greatest`, `math.bitAnd`, ...
- **Encoders** — base64, hex.
- **Sets** — `sets.contains`, `sets.intersects`, `sets.equivalent`.
- **Optionals** — `optional<T>`, `opt.value()`, `opt.orValue(...)`.
- **Bindings / block** — `cel.bind` for aliasing subexpressions; `cel.@block`
  for compiler-emitted CSE.
- **Networking** — `ip()`, `cidr()`, `containsCIDR()`.

CEL for .NET ships all of the above as opt-in `ICelExtension`s. See the
[extensions reference](/reference/extensions/) for the full catalog.

## How this implementation relates to the spec

`Cel.NET` is a **clean-room C# port of the cel-spec**. It depends on the same
`.proto` definitions for AST and value wire formats, and the conformance
harness exercises the spec's `tests/simple/testdata/*.textproto` corpus. We
target full coverage and currently pass 92% — see the [conformance
breakdown](/reference/conformance/) for which categories are at 100%, which
are nearly there, and which feature gaps remain.

## Further reading

- [CEL specification][langdef] — the canonical document.
- [cel-spec repository][celspec] — the protos, the corpus, and the governance
  model.
- [Language tour](/concepts/language-tour/) — a guided walk through CEL syntax
  with .NET examples.

[celspec]: https://github.com/google/cel-spec
