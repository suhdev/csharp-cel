---
title: Error handling
description: A practical guide to making CEL rules tolerant — patterns, anti-patterns, and how to surface real failures.
---

CEL's error model (errors are values, short-circuited by `&&`/`||`/`?:`) is
expressive but unfamiliar to developers used to imperative exceptions.
This guide is the cookbook for "how do I write rules that don't blow up
on edge-case data?"

## The principle

Don't fight the operator semantics — use them. The CEL language is
designed for the common pattern:

```cel
SHORT_CIRCUITING_GUARD || INTERESTING_PREDICATE
```

The guard absorbs errors that would otherwise propagate from the
predicate. This is the idiomatic shape of "if the data isn't there, treat
this rule as not-allowing".

## Pattern 1: tolerate a missing field

```cel
// Throws if request.user isn't bound:
request.user.is_admin

// Tolerates missing request.user:
has(request.user) && request.user.is_admin
```

`has(...)` is the spec macro for "is this attribute present?" `&&`
short-circuits on a `false` first operand, absorbing whatever error the
right side might have produced.

## Pattern 2: prefer the optionals extension for chains

```cel
// Three nested has-checks:
has(account.profile) && has(account.profile.image) &&
  account.profile.image.url == 'foo'

// Same thing, with optionals enabled:
account.?profile.?image.?url.orValue('') == 'foo'
```

The optional version is shorter, harder to misread, and produces the same
short-circuit behaviour.

## Pattern 3: tolerate bad data without lying

When a rule is called against data that's *partially* invalid, you usually
want one of three things:

- **Deny safely** — return false; let the system fall through to the
  default-deny.
- **Allow safely** — return true; trusted internal flow.
- **Expose the error** — return error; let the caller log/alert.

CEL gives you the tools for all three. The host code decides which to do
when an unhandled error reaches it:

```csharp
try
{
    return (bool)program.Eval(activation)!;
}
catch (CelEvaluationException ex)
{
    metrics.RuleErrors.Add(1, new("rule_id", ruleId));
    log.Warning("CEL rule failed: {Message}", ex.Message);
    return _defaults.Deny;   // or .Allow, or rethrow
}
```

## Pattern 4: split eager validation from the rule

Sometimes the rule itself shouldn't be in the business of validating
inputs. Run a small "is this data shaped right?" pass first:

```csharp
if (!IsRequestComplete(req))
{
    return DefaultDeny;
}
return (bool)_program.Eval(new { request = req })!;
```

This keeps your rules focused on business logic and your error handling
focused on data shape problems.

## Pattern 5: type-check at compile time

The best error handling is the error you never have. Declaring an
`ITypeProvider` for your domain types means typos and shape mismatches
fail at compile time, in the call site that compiled the rule:

```csharp
.UseTypeProvider(new MyDomainProvider())
.Variable("request", CelTypes.Object("acme.v1.Request"))
```

Now `request.maybe_admin` (a typo for `is_admin`) is a compile-time
error, not a runtime one.

## Anti-pattern: blanket try/catch around the rule

This is "I have errors, let me hide them":

```csharp
// DON'T:
try { return (bool)program.Eval(activation)!; }
catch { return false; }
```

It works, but it converts every problem — typos, schema drift, bugs in your
rule — into a silent "false". Prefer logging + denying so you can detect
the problem in metrics:

```csharp
try { return (bool)program.Eval(activation)!; }
catch (CelEvaluationException ex)
{
    log.Warning("CEL rule failed: {Message}", ex.Message);
    metrics.RuleErrors.Add(1);
    return false;
}
```

## Anti-pattern: validating in CEL with weird ternaries

```cel
// DON'T:
account.user != null && account.user.is_admin
```

Comparing to `null` is fine for typed nullable fields, but for arbitrary
attribute access prefer `has(...)`. CEL's `has` is the canonical
"the field is present" predicate; the runtime knows what that means for
proto presence semantics, plain CLR fields, and dictionary keys.

## What can be missing vs. what can be null

These are different. From the CEL spec:

| | "Field missing" | "Field null" |
|---|---|---|
| `has(x)` | false | depends — proto scalars: false; messages: false |
| `x` | error | `null` |
| `x == null` | error | true |

The provider you register decides the semantics for object types. The
default reflection-backed POCO adapter treats "not declared on the CLR
type" as missing, and "declared but null/default" as present-but-null.

For proto messages, see the [proto guide](/guides/working-with-protos/) —
the proto provider implements presence-bit-aware `has`.

## Designing rules for partial inputs

If you're going to evaluate the same rule against many partial inputs (an
admin tool that previews rules), the cleanest approach is:

1. Declare every variable on the env.
2. For each call, fill in only the variables you have.
3. Don't bind ones you don't have.
4. Catch `CelEvaluationException` at the boundary.

The runtime treats "no binding" as a fatal error on first reference, but
that's exactly what you want: the rule explicitly says it needs that data,
and you explicitly know it's not present yet.

For a more principled "evaluate as much as possible, return what's
unknown" approach, see [unknowns](/concepts/errors-and-unknowns/#unknowns)
— it's the spec mechanism for partial evaluation, but the .NET runtime
doesn't yet expose a public API for emitting them.

## See also

- [Errors & unknowns](/concepts/errors-and-unknowns/) — the underlying
  model.
- [Optionals & null](/guides/optionals-and-null/) — when to choose
  optional over null semantics.
