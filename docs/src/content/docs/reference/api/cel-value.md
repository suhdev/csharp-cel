---
title: CelValue
description: The runtime value sum type — every value the evaluator manipulates is one of these.
---

`Cel.Values.CelValue` is an `abstract record` whose subtypes form a closed
sum. Every value the evaluator produces or consumes is exactly one of
these types.

## The hierarchy

```csharp
public abstract record CelValue
{
    public abstract CelType Type { get; }
    public abstract object? ToClrObject();
}
```

| Subtype | CEL type | CLR projection |
|---------|----------|----------------|
| `NullValue` | `null_type` | `null` |
| `BoolValue` | `bool` | `bool` |
| `IntValue` | `int` | `long` |
| `UintValue` | `uint` | `ulong` |
| `DoubleValue` | `double` | `double` |
| `StringValue` | `string` | `string` |
| `BytesValue` | `bytes` | `byte[]` |
| `DurationValue` | `duration` | `CelDuration` |
| `TimestampValue` | `timestamp` | `CelTimestamp` |
| `ListValue` | `list<T>` | `List<object?>` |
| `MapValue` | `map<K, V>` | `Dictionary<object, object?>` |
| `ObjectValue` | named object type | the wrapped CLR instance |
| `EnumValue` | object type (enum) | `long` (the enum's number) |
| `OptionalValue` | `optional<T>` | inner value or `null` |
| `TypeValue` | `type` | the wrapped `CelType` |
| `ErrorValue` | `error` | throws `CelEvaluationException` |
| `UnknownValue` | `dyn` | `null` |

## Constructing values

The `CelValue` static class exposes factory helpers:

```csharp
CelValue.Null;
CelValue.True;
CelValue.False;
CelValue.Of(true);
CelValue.Of(42L);                   // → IntValue
CelValue.Of(42UL);                  // → UintValue
CelValue.Of(3.14);                  // → DoubleValue
CelValue.Of("hello");               // → StringValue
CelValue.Of(new byte[] {1, 2, 3});  // → BytesValue
CelValue.Of(new CelDuration(...));
CelValue.Of(new CelTimestamp(...));
CelValue.Error("message", code: "OPT");

new ListValue([...elements...]);
new MapValue([...entries...].ToImmutableDictionary());
new ObjectValue("acme.v1.User", userInstance);
new EnumValue("acme.v1.GlobalEnum", 2);
new OptionalValue(inner: CelValue.Of(42));
OptionalValue.None;
new TypeValue(CelTypes.Int);
```

## `Type` property

Each subtype's `Type` returns its CEL static type:

```csharp
CelValue.Of(42L).Type           // CelTypes.Int
CelValue.Of("hi").Type          // CelTypes.String
new ListValue(...).Type         // CelTypes.List(CelTypes.Dyn)
new ObjectValue("User", u).Type // CelTypes.Object("User")
new EnumValue("E", 2).Type      // CelTypes.Object("E")
```

This is what `type(x)` reflects on at runtime.

## `ToClrObject` projection

Returns a sensible CLR representation:

```csharp
CelValue.Of(42L).ToClrObject()              // 42L (long)
CelValue.Of("hi").ToClrObject()             // "hi"
new ListValue([...]).ToClrObject()          // List<object?>
new MapValue(...).ToClrObject()             // Dictionary<object, object?>
new ObjectValue("User", u).ToClrObject()    // u (the original instance)
```

`ErrorValue.ToClrObject()` throws `CelEvaluationException` — this is what
makes `program.Eval(...)` raise on top-level errors. To inspect an error
without throwing, pattern-match on the `ErrorValue` instead.

## Equality

`CelValue` records use C#'s built-in record equality by default, but CEL
semantics differ in important ways (cross-numeric `1 == 1.0`, NaN
asymmetry, list/map structural compare). Use `Cel.Runtime.CelEquality`
for any "is the CEL spec semantics" comparison:

```csharp
Cel.Runtime.CelEquality.Equals(CelValue.Of(1L), CelValue.Of(1.0));
// → true (CEL says yes)

CelValue.Of(1L) == CelValue.Of(1.0);
// → false (record equality is type-then-value)
```

## Special-case rules

- **NaN ≠ anything**, including itself. Per IEEE 754; CEL adopts this.
- **`null == null` → true** (one of the few comparisons that survives).
- **Enums compare to ints by number** — `EnumValue(_, 2) == IntValue(2)`.
- **Lists/maps compare structurally**, recursively using these same
  rules.
- **Object equality** routes through the `ITypeProvider`'s `AreEqual`
  hook when one is registered.

## Pattern-matching results

The closed-sum nature makes pattern-matching ergonomic:

```csharp
return result switch
{
    BoolValue b           => b.Value ? "yes" : "no",
    IntValue { Value: 0 } => "zero",
    IntValue i            => $"int {i.Value}",
    StringValue s         => s.Value,
    ListValue l           => $"{l.Elements.Length} items",
    OptionalValue { HasValue: false } => "missing",
    OptionalValue o       => Format(o.Inner!),
    ErrorValue e          => throw new InvalidOperationException(e.Message),
    _                      => result.ToClrObject()?.ToString() ?? "null",
};
```

## See also

- [Type system](/concepts/type-system/) — the static side.
- [Evaluation model](/concepts/evaluation-model/) — how values flow
  through the evaluator.
