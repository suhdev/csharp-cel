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

## Layout

- `src/Cel.Core` — AST, type system, value model, diagnostics. No external deps.
- `src/Cel.Parser` — lexer + Pratt parser, macro expansion.
- `src/Cel.Checker` — type checker, declarations, overload resolution.
- `src/Cel.Runtime` — tree-walking evaluator, activations, POCO adapter, stdlib.
- `src/Cel.Extensions` — strings, math, encoders, sets.
- `src/Cel` — public façade (`CelExpression`, `CompiledProgram`).
- `tests/Cel.UnitTests` — unit tests (167 cases).
- `tests/Cel.Conformance` — runs the cel-spec textproto conformance corpus.

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
| `basic`, `fp_math`, `integer_math`, `lists`, `logic`, `macros`, `plumbing`, `string` | **100%** |
| `math_ext`, `string_ext`, `timestamps`, `conversions`, `parse`, `fields` | **82–96%** |
| `optionals`, `comparisons`, `proto2`, `proto3`, `namespace` | **57–71%** |
| `enums`, `wrappers`, `dynamic` | **23–30%** — partial proto runtime; needs Any/Struct/Value support |
| `bindings_ext`, `block_ext` | **0%** — `cel.bind` / `cel.@block` need parser-level macro hooks |
| `network_ext`, `macros2`, `proto2_ext`, `type_deduction`, `unknowns` | **0–17%** — features not yet built |
| **Total** | **1518 / 2263 ran (67%) over 2454 cases (191 skipped)** |

### Known gaps

- **Proto support** — `Cel.Runtime` has no `TypeProvider` for proto messages, so `pkg.Type{...}` constructs are flattened to maps and field access on object types defers to `dyn`. Lands when the conformance harness needs it.
- **`cel.bind` / `cel.@block`** — parser-level macros need an extension hook. Currently macros are baked into `Parser.cs`.
- **Heterogeneous numeric ordering** (`1 < 2.5`) — only same-type ordering shipped.
- **Wrapper unwrapping at runtime** — `google.protobuf.BoolValue` etc. currently surface as opaque objects.
- **Optional select / index runtime** — parsed but not yet dispatched.
- **Reflection POCO adapter** — annotated `[RequiresUnreferencedCode]`; SourceGen variant pending for AOT/trim.
