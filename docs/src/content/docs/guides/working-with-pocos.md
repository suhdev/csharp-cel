---
title: Working with POCOs
description: Bind plain .NET objects directly — the POCO adapter, opaque object types, and the trade-offs vs. registering a type provider.
---

The marquee feature of `Cel.NET`: you do not need protobuf to use CEL. Plain
.NET objects work out of the box via the reflection-backed POCO adapter.

## The default path

If you declare a variable with an opaque object type, the checker treats
field selects on it as `dyn` — which means "resolve at runtime". At runtime,
the evaluator hands the instance plus the field name to the POCO adapter,
which uses reflection to find a matching public property or field.

```csharp
var env = CelEnv.NewBuilder()
    .Variable("user", CelTypes.Object("User"))
    .Build();

var program = CelExpression.Compile(
    "user.name.startsWith('a') && user.age >= 18",
    env);

bool ok = (bool)program.Eval(new
{
    user = new { Name = "alice", Age = 25 }
})!;
```

That's it. No proto schema, no codegen, no `ITypeProvider`.

## What the POCO adapter does

For a CEL `select` `obj.field`, the adapter:

1. Looks for a public **property** named `field` (case-sensitive).
2. Falls back to a public **field** named `field`.
3. Returns the CLR value, which the runtime wraps via `ValueAdapter` into a
   `CelValue`.
4. If neither is found, returns "not present" — the call surfaces as a CEL
   `no_such_field` error.

For a `has(obj.field)` check, the adapter returns true if the property/field
exists *and* its value is non-default. (Default = `null` for reference types,
`0` / `false` / `""` for value types, empty for collections.)

For `obj["k"]` index access, if `obj` is `IDictionary<,>` or `IDictionary`,
the adapter goes through that interface; otherwise it tries property/field
lookup the same way.

## Naming — conventions and `[JsonPropertyName]`

CEL is case-sensitive, but you have three layered ways to control how CLR
member names map to CEL field names.

### `[JsonPropertyName]` — explicit, per-member

```csharp
public sealed class User
{
    [JsonPropertyName("user_name")]
    public string UserName { get; init; } = "";

    [JsonIgnore]
    public string SessionToken { get; init; } = "";
}
```

```cel
user.user_name        // → reads UserName
user.UserName         // error: field hidden by attribute
user.session_token    // error: ignored entirely
```

`[JsonPropertyName]` always wins over the convention; the original CLR
name is no longer exposed (matching `System.Text.Json` behaviour).
`[JsonIgnore]` removes the member entirely.

### A naming convention — global, per-env

```csharp
var env = CelEnv.NewBuilder()
    .UsePocoNaming(PocoNamingConvention.SnakeCase)
    .Variable("user", CelTypes.Object("User"))
    .Build();
```

| Convention | `UserName` becomes |
|------------|--------------------|
| `PascalCase` *(default)* | `UserName` (CLR name as-is) |
| `CamelCase` | `userName` |
| `SnakeCase` | `user_name` |
| `ScreamingSnakeCase` | `USER_NAME` |
| `KebabCase` | `user-name` |

Acronym runs are handled sensibly: `HTTPMethod` becomes `httpMethod`
(camelCase) or `http_method` (snake_case).

### Default behaviour

`PascalCase` is the default and preserves a useful fallback: if CEL asks
for `user_name` but no matching member exposes that name, the adapter
re-tries with `snake_case` → `PascalCase` translation and finds
`UserName`. Other conventions are strict — only the transformed name is
exposed.

### Precedence

For each property/field, the CEL-side name is decided as follows:

1. `[JsonIgnore]` → not exposed.
2. `[JsonPropertyName("foo")]` → exposed as `foo`.
3. Otherwise → the configured convention's transform of the CLR name.

If neither attribute is present and you're in `PascalCase` mode, the
fallback above also applies at lookup time.

If you want stricter than this — `ITypeProvider` mediates names with no
ambiguity at all.

## Lists and maps

A property of type `List<T>`, `T[]`, or any `IEnumerable<T>` projects to a
CEL `list<dyn>` (the element type is `dyn` at this layer). Indexing,
`size()`, `in`, and the comprehension macros all work.

Similarly, `IDictionary<K, V>` and `IDictionary` project to `map<dyn, dyn>`
— with the natural caveat that `K` must be a type CEL can use as a map key
(string, int, uint, bool).

## Nested objects

Nested POCO references are resolved lazily. `user.address.city` walks two
field reads; each one uses the adapter. There's no eager projection — the
runtime keeps the CLR instance live for as long as you need to read fields
off it.

## Strong-typing your model

When you want compile-time errors instead of runtime ones, register an
`ITypeProvider` that knows your model's shape:

```csharp
public sealed class UserProvider : ITypeProvider
{
    public bool KnowsType(string typeName) => typeName == "User";
    public CelType? ResolveType(string typeName) =>
        typeName == "User" ? CelTypes.Object("User") : null;

    public bool TryReadField(object instance, string field, out object? value)
    {
        if (instance is User u)
        {
            value = field switch
            {
                "name" => u.Name,
                "age" => u.Age,
                _ => null,
            };
            return value is not null || field is "name" or "age";
        }
        value = null;
        return false;
    }

    public bool HasField(object instance, string field) =>
        instance is User u && (field == "name" || field == "age");

    public bool IsManagedInstance(object instance) => instance is User;
    public string? TypeNameOf(object instance) => instance is User ? "User" : null;
    public object? Construct(string typeName, IReadOnlyDictionary<string, CelValue> fields) =>
        typeName == "User" ? new User { Name = (string)fields["name"].ToClrObject()!, Age = (long)fields["age"].ToClrObject()! } : null;
    public CelValue? Project(object instance) => null;
    public bool? AreEqual(object a, object b) => null;
}
```

```csharp
var env = CelEnv.NewBuilder()
    .UseTypeProvider(new UserProvider())
    .Variable("user", CelTypes.Object("User"))
    .Build();
```

Now the checker knows what fields exist on `User` and at which CEL types.
Misspelt fields fail at compile time, not runtime.

This is more work — but for hot types, or types whose schema you actually
control, it's worth it. The provider is also where you decorate fields with
proto-style presence semantics (`HasField` vs. "set to default") and where
you customise equality.

## Trade-offs at a glance

| | POCO adapter | Custom `ITypeProvider` |
|---|---|---|
| Setup cost | zero | ~50 LoC per type |
| Compile-time field checking | no | yes |
| Performance | reflection-cached, modest | direct dispatch, fast |
| Trim-safe / AOT | no (uses reflection) | yes (you write the code) |
| Best for | hosting flexibility, prototypes | hot paths, AOT, proto-like presence |

## The `[RequiresUnreferencedCode]` annotation

`Cel.CompiledProgram`, `ObjectActivation`, and the POCO adapter are marked
`[RequiresUnreferencedCode]` because they reflect over runtime types. Linker /
trimmer warnings are expected on those code paths in trimmed apps. To go
trim-safe, register `ITypeProvider`s for every type you'd otherwise reflect
over and ensure your activation is dictionary-backed. See [Performance &
trimming](/guides/performance-and-trimming/).

## See also

- [Working with protos](/guides/working-with-protos/) — the same shape, with
  generated proto adapters.
- [`ITypeProvider` reference](/reference/api/itype-provider/) — the full
  interface, every method explained.
