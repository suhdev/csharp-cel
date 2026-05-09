# Cel for .NET

An idiomatic C# / .NET 10 implementation of the [Common Expression Language](https://github.com/google/cel-spec).

```csharp
var env = CelEnv.NewBuilder()
    .Use(StringsExtension.Instance)
    .Use(MathExtension.Instance)
    .Variable("user", CelTypes.Object("User"))
    .Build();

var program = CelExpression.Compile(
    "user.user_name.startsWith('a') && math.abs(user.age) > 18",
    env);

bool ok = (bool)program.Eval(new {
    user = new { UserName = "alice", Age = 25 }
})!;
```

## Documentation

Full docs live under [`docs/`](./docs) — Astro Starlight site with
getting-started, concepts, guides, and a complete API reference. Run
locally with `cd docs && npm install && npm run dev`.

## Layout

- `src/Cel.Core` — AST, type system, value model, diagnostics. No external deps.
- `src/Cel.Parser` — lexer + Pratt parser, macro expansion.
- `src/Cel.Checker` — type checker, declarations, overload resolution.
- `src/Cel.Runtime` — tree-walking evaluator, activations, POCO adapter, stdlib.
- `src/Cel.Extensions` — strings, math, encoders, sets, optionals, bindings, network, block.
- `src/Cel` — public façade (`CelExpression`, `CompiledProgram`).
- `tests/Cel.UnitTests` — unit tests (167 cases).
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
