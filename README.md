# Cel for .NET

An idiomatic C# / .NET 10 implementation of the [Common Expression Language](https://github.com/google/cel-spec).

Status: **early development**. Targeting full conformance against `cel-spec/tests/simple/testdata/*.textproto`.

## Layout

- `src/Cel.Core` — AST, type system, value model, diagnostics. No external deps.
- `src/Cel.Parser` — lexer + Pratt parser, macro expansion.
- `src/Cel.Checker` — type checker, declarations, overload resolution.
- `src/Cel.Runtime` — tree-walking evaluator, activations, POCO adapter, stdlib.
- `src/Cel.Extensions` — strings, math, encoders, lists, sets, bindings, network.
- `src/Cel` — public façade (`CelEnv`, `Program`).
- `tests/Cel.UnitTests` — unit tests.
- `tests/Cel.Conformance` — runs the cel-spec textproto conformance corpus.
