---
title: Parser macros
description: When a regular function isn't enough — implementing macros that rewrite the AST before type-checking.
---

A parser macro is sugar that the parser expands into other CEL constructs
*before* the checker runs. Use macros when you need:

- **Lazy evaluation** of an argument (e.g. the body of `filter`).
- **A new binding** that the body can reference (e.g. the iter variable).
- **Control flow** that can't be expressed as a regular function call.

The standard CEL macros (`has`, `all`, `exists`, `exists_one`, `map`,
`filter`) are baked into the parser. Extensions contribute additional
macros via the `ICelExtension.Macros` property.

## When *not* to write a macro

Most "looks like a function" needs are best served by a regular
`OverloadDecl` + runtime binding (see [Declaring
functions](/guides/declaring-functions/)). The sign you need a macro is
that you'd like one of your arguments to *not* evaluate eagerly.

## The interface

```csharp
public sealed record CelMacro(
    string Name,
    int ArgCount,                    // -1 for variadic
    bool IsReceiverStyle,
    Func<MacroExpansionContext, Expr?, ImmutableArray<Expr>, Expr?> Expand);
```

`Expand` returns either:
- a rewritten `Expr` (the macro applies; the parser uses this AST instead),
- or `null` (the macro doesn't apply; the parser keeps the original call).

## A complete example: `default(expr, fallback)`

Goal: a macro that returns `expr` if it doesn't error, otherwise `fallback`.
That's `expr.orValue(fallback)` for an optional, but generalises to *any*
expression including ones that might error.

You can almost spell this in CEL: `has(...) ? ... : ...`. But `has` only
works on attribute selects. A macro can rewrite arbitrary `expr` cases.

```csharp
using System.Collections.Immutable;
using Cel;
using Cel.Ast;
using Cel.Values;

public sealed class DefaultMacroExtension : ICelExtension
{
    public static readonly DefaultMacroExtension Instance = new();
    private DefaultMacroExtension() { }

    public void ConfigureEnv(CelEnv.Builder b) { /* nothing to declare */ }
    public void ConfigureRuntime(Action<string, OverloadFn> bind) { /* no runtime */ }

    public IEnumerable<CelMacro> Macros => new[]
    {
        new CelMacro(
            Name: "default",
            ArgCount: 2,
            IsReceiverStyle: false,
            Expand: ExpandDefault),
    };

    private static Expr? ExpandDefault(
        MacroExpansionContext ctx,
        Expr? receiver,           // null for global-style macros
        ImmutableArray<Expr> args)
    {
        // default(x, y)  ⇒  has(x) ? x : y
        // (Limitation: only meaningful when x is a select — has() requires it.)
        var x = args[0];
        var y = args[1];
        if (x is not Expr.Select)
        {
            return null; // fall through to a regular call
        }
        var hasCall = ctx.NewCall(name: "has", target: null, args: [x]);
        return ctx.NewTernary(cond: hasCall, t: x, f: y);
    }
}
```

Use it:

```cel
default(account.region, "us")
// ⇒ rewritten to: has(account.region) ? account.region : "us"
```

The runtime never sees the macro — only the rewritten AST.

## What `MacroExpansionContext` gives you

- `NextId()` — allocates a fresh AST node id and records its source position
  so error diagnostics point back to the macro call site.
- Helper builders — `NewCall`, `NewTernary`, `NewSelect`, `NewIdent`,
  `NewLiteral`, etc. — that allocate the right AST shape with the right
  ids.

(See the source under `Cel.Core/CelMacro.cs` for the full helper set.)

## How macro expansion fits into the pipeline

```
source
  └─ Lexer ──► tokens
        └─ Parser ──► raw AST
              └─ Macro expansion (this is where macros run)
                    └─ AST after expansion
                          └─ Checker ──► CheckedAst
                                └─ Evaluator
```

Macros produce ASTs that the checker then validates. If your macro emits
something the checker rejects, the user sees a checker error — not a
parser error — at the macro call site.

## Caveats and gotchas

- **Don't capture mutable state in `Expand`.** Macros may run repeatedly
  (e.g. across many compiles); they should be pure.
- **Allocate ids via `ctx.NextId()`.** Reusing AST ids confuses
  comprehensions and the source-info table.
- **Receiver-style macros** (`x.fooBar(...)`) get the `target` arg
  populated; global ones (`fooBar(x, ...)`) get `target = null`.
- **Variadic macros**: set `ArgCount = -1` and check `args.Length` inside
  `Expand`.

## Macros in the bundled extensions

Real-world examples to read:

- `BindingsExtension.cs` — `cel.bind(name, init, expr)`. Rewrites to a
  comprehension whose accumulator is `name`, the iterand is empty, the
  step is `init`, and the result is `expr`. Idiomatic "let-binding".
- `OptionalsExtension.cs` — `optMap(f)`, `optFlatMap(f)`. Receiver-style
  on optionals; rewrite to a ternary on `hasValue()`.
- `BlockExtension.cs` — `cel.@block(...)`. Generates nested let-bindings
  `@index0..@indexN`; the compiler emits these for CSE.

Read those if you need a battle-tested template.

## See also

- [Building extensions](/guides/building-extensions/) — the broader pattern.
- [Language tour: macros](/concepts/language-tour/#macros) — the standard
  set of macros every CEL implementation supports.
