# Cel for .NET

An idiomatic C# / .NET 10 implementation of the [Common Expression Language](https://github.com/google/cel-spec).

CEL is Google's "safe expression" language for policy, validation, and rule
engines: small, sandboxed, totally evaluated, and stable across implementations.
This port targets full conformance with the spec and treats POCOs as
first-class — protobuf is optional.

## Quick start

### 1. Hello, world

Declare what variables an expression can reference, compile, and evaluate:

```csharp
using Cel;
using Cel.Types;

var env = CelEnv.NewBuilder()
    .Variable("name", CelTypes.String)
    .Build();

var program = CelExpression.Compile("'hello, ' + name", env);

var greeting = (string)program.Eval(new Dictionary<string, object?>
{
    ["name"] = "world",
})!;
// "hello, world"
```

### 2. A predicate over POCOs

Plain CLR objects bind directly — no schema, no codegen, no protobuf. Top-level
properties of an anonymous root become CEL variables; nested field access is
resolved at runtime by the reflection-backed POCO adapter:

```csharp
using Cel;
using Cel.Extensions;
using Cel.Types;

public sealed record User(string Name, int Age, string[] Roles);

var env = CelEnv.NewBuilder()
    .Use(StringsExtension.Instance)
    .Variable("user", CelTypes.Object("User"))
    .Build();

var program = CelExpression.Compile(
    "user.Name.startsWith('a') && user.Age >= 18 && 'admin' in user.Roles",
    env);

bool allowed = (bool)program.Eval(new
{
    user = new User("alice", 25, ["admin", "user"]),
})!;
```

`CelExpression.Compile` is the slow step (~50–200 µs); the returned
`CompiledProgram` is thread-safe and meant to be reused across millions of
evaluations.

### 3. Field naming — JsonPropertyName + conventions

Map CLR `PascalCase` properties to CEL expressions in any case style. Per-member
overrides via `[JsonPropertyName]`; per-env defaults via `UsePocoNaming(...)`:

```csharp
using System.Text.Json.Serialization;

public sealed class Account
{
    [JsonPropertyName("user_name")]
    public string UserName { get; init; } = "";

    public int Age { get; init; }

    [JsonIgnore]
    public string SessionToken { get; init; } = "";
}

var env = CelEnv.NewBuilder()
    .UsePocoNaming(PocoNamingConvention.SnakeCase)
    .Variable("acc", CelTypes.Object("Account"))
    .Build();

// CEL field names: user_name (from attribute), age (from convention).
// session_token is hidden by [JsonIgnore].
var program = CelExpression.Compile(
    "acc.user_name == 'alice' && acc.age >= 18",
    env);
```

### 4. Comprehensions

`map`, `filter`, `all`, `exists`, `exists_one` work over lists and map keys:

```csharp
var program = CelExpression.Compile(
    "items.filter(i, i.in_stock && i.price < 100).size() > 0",
    env);

bool hasAffordable = (bool)program.Eval(new
{
    items = new[]
    {
        new { in_stock = true,  price = 30 },
        new { in_stock = false, price = 50 },
        new { in_stock = true,  price = 200 },
    }
})!;
```

### 5. Custom functions

Extend the language with your own functions via `ICelExtension`:

```csharp
using Cel;
using Cel.Types;
using Cel.Values;

public sealed class GreetExtension : ICelExtension
{
    public static readonly GreetExtension Instance = new();
    private GreetExtension() { }

    public void ConfigureEnv(CelEnv.Builder b) =>
        b.Function("greet",
            new OverloadDecl("greet_string", [CelTypes.String], CelTypes.String));

    public void ConfigureRuntime(Action<string, OverloadFn> bind) =>
        bind("greet_string",
            args => CelValue.Of($"hello, {((StringValue)args[0]).Value}"));
}

var env = CelEnv.NewBuilder().Use(GreetExtension.Instance).Build();
CelExpression.Compile("greet('alice')", env).Eval(new Dictionary<string, object?>());
// "hello, alice"
```

## Documentation

Full docs live at <https://suhdev.github.io/csharp-cel/> (Astro Starlight,
sources under [`docs/`](./docs)) — getting-started, concepts, guides, and a
complete API reference. Run locally with `cd docs && npm install && npm run dev`.

## Layout

- `src/Cel.Core` — AST, type system, value model, diagnostics. No external deps.
- `src/Cel.Parser` — lexer + Pratt parser, macro expansion.
- `src/Cel.Checker` — type checker, declarations, overload resolution.
- `src/Cel.Runtime` — tree-walking evaluator, activations, POCO adapter, stdlib.
- `src/Cel.Extensions` — strings, math, encoders, sets, optionals, bindings, network, block.
- `src/Cel` — public façade (`CelExpression`, `CompiledProgram`).
- `tests/Cel.UnitTests` — unit tests (181 cases).
- `tests/Cel.Conformance` — runs the cel-spec textproto conformance corpus.
- `docs/` — Astro Starlight documentation site.

## Conformance

Run the harness against a checkout of `cel-spec` (sibling repo by default):

```sh
dotnet run --project tests/Cel.Conformance
# or with a custom path:
dotnet run --project tests/Cel.Conformance -- /path/to/cel-spec/tests/simple/testdata
# or specific files:
dotnet run --project tests/Cel.Conformance -- ../cel-spec/tests/simple/testdata --only basic comparisons
```

### Current pass rate (cel-spec corpus, all 30 files)

| Category | Status |
|----------|--------|
| `basic`, `bindings_ext`, `comparisons`, `enums`, `fp_math`, `integer_math`, `lists`, `logic`, `macros`, `network_ext`, `parse`, `plumbing`, `string` | **100%** |
| `block_ext`, `conversions`, `dynamic`, `fields`, `macros2`, `math_ext`, `proto3`, `string_ext`, `timestamps`, `wrappers` | **85–98%** |
| `encoders_ext`, `namespace`, `optionals`, `proto2` | **73–89%** |
| `proto2_ext`, `type_deduction`, `unknowns` | **0%** — feature gaps |
| **Total** | **2082 / 2257 ran (92%) over 2454 cases (197 skipped)** |

### Known gaps

- **`unknowns`** — partial-evaluation infrastructure. The runtime represents unknowns internally (`UnknownValue`); public API for emitting them from activations is pending.
- **`type_deduction`** — `typed_result` matcher in conformance harness not yet wired up; `CompiledProgram.ResultType` exists but the spec corpus doesn't drive it.
- **`proto2_ext`** — proto2 message extensions (the `extend` block) — parser + provider lookup not yet implemented.
- **Reflection POCO adapter** — annotated `[RequiresUnreferencedCode]`; SourceGen variant pending for AOT/trim.
