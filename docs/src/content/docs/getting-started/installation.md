---
title: Installation
description: Add CEL for .NET to your project and verify it works.
---

`Cel.NET` targets **.NET 10** and uses **C# 14** language features (records,
init-only properties, collection expressions, pattern-matching enhancements). The
project layout is five small assemblies with no required external dependencies.

## Prerequisites

- .NET SDK **10.0** or newer
- Any operating system that runs .NET 10 (macOS, Linux, Windows)

## NuGet packages

The runtime ships as five packages plus an optional extensions bundle:

| Package | What it contains |
|---------|------------------|
| `Cel` | Public façade: `CelExpression`, `CompiledProgram`. Depends on the next four. |
| `Cel.Core` | AST, type system, value model, `ITypeProvider` / `ICelExtension` interfaces. |
| `Cel.Parser` | Lexer + Pratt parser, macro expansion. |
| `Cel.Checker` | Type checker, declarations, overload resolution, `CelEnv`. |
| `Cel.Runtime` | Tree-walking evaluator, activations, POCO adapter, stdlib. |
| `Cel.Extensions` | Optional: `strings`, `math`, `encoders`, `sets`, `optionals`, `bindings`, `network`, `block`. |

Install via the .NET CLI:

```sh
dotnet add package Cel
dotnet add package Cel.Extensions   # optional
```

`Cel` transitively pulls in `Cel.Core`, `Cel.Parser`, `Cel.Checker`, and
`Cel.Runtime`, so most projects only need the first one.

## Project file

If you prefer hand-editing your `.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>preview</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Cel" Version="0.1.*" />
    <PackageReference Include="Cel.Extensions" Version="0.1.*" />
  </ItemGroup>
</Project>
```

`Nullable` is recommended (the API is null-aware), and `LangVersion=preview`
lets you take advantage of C# 14 features in your own glue code.

## Smoke test

Drop this in `Program.cs`:

```csharp
using Cel;
using Cel.Types;

var env = CelEnv.NewBuilder()
    .Variable("name", CelTypes.String)
    .Build();

var program = CelExpression.Compile("'hello, ' + name", env);

Console.WriteLine(program.Eval(new Dictionary<string, object?> { ["name"] = "world" }));
// hello, world
```

Run it:

```sh
dotnet run
```

If you see `hello, world`, you're set. Continue with [Hello,
world](/getting-started/hello-world/) for a real walkthrough.
