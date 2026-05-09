---
title: Compile & evaluate
description: The lifecycle of a CEL program — env, compile, evaluate, dispose. With recipes for the cases you'll meet most.
---

This guide is the practical companion to the [evaluation
model](/concepts/evaluation-model/) page. Each section is a recipe.

## Recipe: simplest possible program

```csharp
using Cel;
using Cel.Types;

var env = CelEnv.NewBuilder()
    .Variable("x", CelTypes.Int)
    .Build();

var program = CelExpression.Compile("x * 2", env);

var result = program.Eval(new Dictionary<string, object?> { ["x"] = 21 });
Console.WriteLine(result); // 42
```

## Recipe: anonymous-object activation

```csharp
var program = CelExpression.Compile(
    "user.name + ' (' + string(user.age) + ')'",
    CelEnv.NewBuilder()
        .Variable("user", CelTypes.Object("User"))
        .Build());

var greeting = (string)program.Eval(new
{
    user = new { Name = "alice", Age = 30 }
})!;
```

The anonymous object's top-level properties (`user`) become CEL variables;
nested field access (`user.name`) is resolved by the POCO adapter — see
[Working with POCOs](/guides/working-with-pocos/).

## Recipe: reuse a program across requests

```csharp
public sealed class FraudCheck
{
    private readonly CompiledProgram _program;

    public FraudCheck(string rule)
    {
        var env = CelEnv.NewBuilder()
            .Variable("event", CelTypes.Object("Event"))
            .Variable("user", CelTypes.Object("User"))
            .Build();
        _program = CelExpression.Compile(rule, env);
    }

    public bool IsSuspicious(Event evt, User user) =>
        (bool)_program.Eval(new { @event = evt, user })!;
}
```

`CompiledProgram` is thread-safe for evaluation — one shared instance can
serve many concurrent requests.

## Recipe: lots of programs, one env

```csharp
var env = CelEnv.NewBuilder()
    .Variable("event", CelTypes.Object("Event"))
    .Build();

var programs = rules.ToDictionary(
    r => r.Id,
    r => CelExpression.Compile(r.Source, env));

bool Allowed(string ruleId, Event evt) =>
    programs.TryGetValue(ruleId, out var p)
        && (bool)p.Eval(new { @event = evt })!;
```

## Recipe: extending an env

`CelEnv` is immutable. Use `Extend()` to derive a child without rebuilding
from scratch:

```csharp
var baseEnv = CelEnv.NewBuilder()
    .Use(StringsExtension.Instance)
    .Build();

var requestEnv = baseEnv.Extend()
    .Variable("request", CelTypes.Object("Request"))
    .Build();

var responseEnv = baseEnv.Extend()
    .Variable("response", CelTypes.Object("Response"))
    .Build();
```

Both child envs share the standard library and the strings extension; each
adds its own variable.

## Recipe: handling compile-time errors

```csharp
try
{
    var program = CelExpression.Compile(userSubmittedRule, env);
}
catch (CelCompileException ex)
{
    foreach (var diag in ex.Diagnostics)
    {
        Console.WriteLine($"{diag.Line}:{diag.Column} {diag.Severity} {diag.Message}");
    }
}
```

Diagnostics include line/column positions sourced from the parser's source
info. They're suitable to surface in a UI as "your rule has these
problems".

## Recipe: handling runtime errors

Most operator-level mishaps are absorbed by short-circuiting. The cases that
do escape get wrapped in `CelEvaluationException`:

```csharp
try
{
    var v = program.Eval(activation);
}
catch (CelEvaluationException ex)
{
    log.Warning("CEL rule failed: {Message}", ex.Message);
    return _defaults.Deny;
}
```

See [Errors & unknowns](/concepts/errors-and-unknowns/) for what does and
doesn't short-circuit.

## Recipe: returning rich values

The default `Eval(...)` overloads unwrap to a CLR object. If you want the
full `CelValue` (e.g. to distinguish `null` from "key not present"), use
`EvaluateRaw`:

```csharp
using Cel.Runtime;
using Cel.Values;

CelValue raw = program.EvaluateRaw(new MapActivation(bindings));

switch (raw)
{
    case BoolValue b:    return b.Value;
    case IntValue i:     return i.Value;
    case ListValue l:    return l.Elements;
    case OptionalValue o when o.HasValue: return o.Inner;
    case OptionalValue:  return null;
    case ErrorValue e:   throw new InvalidOperationException(e.Message);
    case UnknownValue:   return _placeholder;
    default:             return raw.ToClrObject();
}
```

This is the right shape for layered rule engines that pass intermediate CEL
results between stages without round-tripping through CLR types.

## Recipe: lifecycle in a long-running service

```csharp
public sealed class RuleEngine : IAsyncDisposable
{
    private readonly Dictionary<string, CompiledProgram> _programs = new();
    private readonly CelEnv _env;

    public RuleEngine(CelEnv env) => _env = env;

    public void Load(string id, string source)
    {
        _programs[id] = CelExpression.Compile(source, _env);
    }

    public bool Check(string id, IActivation activation) =>
        (bool)_programs[id].Eval(activation)!;

    public ValueTask DisposeAsync()
    {
        _programs.Clear();   // CompiledProgram has no unmanaged resources.
        return ValueTask.CompletedTask;
    }
}
```

There is no native cleanup needed — `CompiledProgram` is pure managed state.

## What can go wrong

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| `undeclared reference to 'x'` at compile | Variable not on env, or wrong container | `.Variable("x", ...)` or set container |
| `no matching overload` at compile | Operator/function called with wrong types | check inputs; consider `dyn(...)` if intended |
| Value comes back as `dyn` | Field is on an opaque object type | register `ITypeProvider`, or live with `dyn` |
| `CelEvaluationException` at eval | Top-level error not absorbed by `\|\|`/`&&`/`?:` | wrap with `has(...)` or rewrite the rule |
| Slow `Compile` in a loop | You're compiling per-request | cache the `CompiledProgram` |
