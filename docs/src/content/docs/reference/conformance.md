---
title: Conformance
description: Pass rate against the cel-spec conformance corpus, broken down by category.
---

`Cel.NET` is exercised against the cel-spec
[`tests/simple/testdata/*.textproto`](https://github.com/google/cel-spec/tree/master/tests/simple/testdata)
corpus. Each `.textproto` is the granularity unit for opting into a
feature: implementations that don't support, e.g., macros, can skip
`macros.textproto` wholesale.

## Current status

```
TOTAL: 2082 / 2257 ran (92.25%) over 2454 cases (197 skipped)
```

Skipped tests fall into four buckets: feature gaps (`unknowns`,
`type_deduction`, `proto2_ext`), spec features that conflict with this
implementation's mode choice (six `enums.textproto` legacy tests, see
below), tests using `disable_check`, and tests that match against an
unsupported result matcher (`typed_result`, `any_eval_errors`,
`any_unknowns`, `unknown`).

## Per-category breakdown

| File | Total | Pass | Fail | Skip | Rate |
|------|------:|-----:|-----:|-----:|-----:|
| `basic` | 100% |
| `bindings_ext` | 100% |
| `block_ext` | 95% |
| `comparisons` | 100% |
| `conversions` | 98% |
| `dynamic` | 97% |
| `encoders_ext` | 75% |
| `enums` | **100%** (with 6 legacy mode-conflict skips) |
| `fields` | 85% |
| `fp_math` | 100% |
| `integer_math` | 100% |
| `lists` | 100% |
| `logic` | 100% |
| `macros` | 100% |
| `macros2` | 87% |
| `math_ext` | 96% |
| `namespace` | 80% |
| `network_ext` | 100% |
| `optionals` | 79% |
| `parse` | 100% |
| `plumbing` | 100% |
| `proto2` | 73% |
| `proto2_ext` | **0%** (proto extension syntax — feature gap) |
| `proto3` | 85% |
| `string` | 100% |
| `string_ext` | 89% |
| `timestamps` | 88% |
| `type_deduction` | **0%** — feature gap |
| `unknowns` | **0%** — feature gap |
| `wrappers` | 89% |

## Categories at 100%

`basic`, `bindings_ext`, `comparisons`, `enums`, `fp_math`,
`integer_math`, `lists`, `logic`, `macros`, `network_ext`, `parse`,
`plumbing`, `string`. Plus the `enums` legacy-mode-conflict skip
described below.

## Mode-conflicting tests

`enums.textproto` ships both `legacy_proto*/type_*` and
`strong_proto*/type_*` sections that assert mutually exclusive
behaviours: legacy expects `type(EnumValue) == int`, strong expects
`type(EnumValue) == EnumName`. A single implementation can satisfy one
or the other.

`Cel.NET` adopts **strong enum semantics** (matching cel-go and
cel-cpp). The six conflicting legacy tests are deliberately skipped with
the reason "implementation uses strong enum semantics".

The 12 `strong_proto*/*` tests pass.

## Categories with feature gaps

- **`unknowns`** (0%) — partial-evaluation infrastructure. The runtime
  represents unknowns internally (`UnknownValue`); the public API for
  emitting them from activations is a pending feature.
- **`type_deduction`** (0%) — the spec's mechanism for tests to check
  the inferred result type. We don't yet honour the `typed_result`
  matcher; the result-type API exists (`CompiledProgram.ResultType`) but
  the conformance harness doesn't evaluate against it.
- **`proto2_ext`** (0%) — proto2 message extensions (the `extend`
  block). Requires syntax support in the parser plus extension lookup
  in the proto type provider.

## Run the harness yourself

```sh
dotnet run --project tests/Cel.Conformance \
    -- /path/to/cel-spec/tests/simple/testdata
```

Filter to a subset:

```sh
dotnet run --project tests/Cel.Conformance \
    -- /path/to/cel-spec/tests/simple/testdata \
    --only basic comparisons enums
```

Set `CEL_FULL_FAILURES=1` to print every failing test (the default caps
at 5 per file).

## What "Pass" means

A test is `Pass` when:

1. The expression compiles successfully (or matches `eval_error`).
2. The runtime result equals the expected value via CEL equality.

Equality goes through `CelEquality.Equals` so cross-numeric, NaN,
list/map structural, and proto-semantic comparisons match the spec —
not CLR record equality.

## See also

- [Evaluation model](/concepts/evaluation-model/) — the phases each
  test exercises.
- [Errors & unknowns](/concepts/errors-and-unknowns/) — the `unknowns`
  feature gap.
- [cel-spec corpus](https://github.com/google/cel-spec/tree/master/tests/simple/testdata)
  — the upstream tests.
