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

The runtime ships as five packages plus an optional extensions bundle.
The `Cel` namespace inside your code is unchanged; the package IDs are
prefixed with `CelDotNet` to disambiguate on nuget.org:

| Package | What it contains |
|---------|------------------|
| `CelDotNet` | Public façade: `CelExpression`, `CompiledProgram`. Depends on the next four. |
| `CelDotNet.Core` | AST, type system, value model, `ITypeProvider` / `ICelExtension` interfaces. |
| `CelDotNet.Parser` | Lexer + Pratt parser, macro expansion. |
| `CelDotNet.Checker` | Type checker, declarations, overload resolution, `CelEnv`. |
| `CelDotNet.Runtime` | Tree-walking evaluator, activations, POCO adapter, stdlib. |
| `CelDotNet.Extensions` | Optional: `strings`, `math`, `encoders`, `sets`, `optionals`, `bindings`, `network`, `block`. |

Install via the .NET CLI:

```sh
dotnet add package CelDotNet
dotnet add package CelDotNet.Extensions   # optional
```

`CelDotNet` transitively pulls in `CelDotNet.Core`, `CelDotNet.Parser`,
`CelDotNet.Checker`, and `CelDotNet.Runtime`, so most projects only need
the first one.

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
    <PackageReference Include="CelDotNet" Version="0.1.*" />
    <PackageReference Include="CelDotNet.Extensions" Version="0.1.*" />
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
