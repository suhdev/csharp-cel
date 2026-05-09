using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Cel.Diagnostics;
using Cel.Runtime;
using Cel.Values;

namespace Cel;

/// <summary>
/// One-call entry point: parse, type-check, and evaluate a CEL expression.
/// Most callers should hold the <see cref="CompiledProgram"/> returned by <see cref="Compile"/>
/// rather than recompiling on each evaluation.
/// </summary>
public static class CelExpression
{
    /// <summary>
    /// Parse and type-check <paramref name="source"/> against <paramref name="env"/>. Throws
    /// <see cref="CelCompileException"/> if either phase produces errors.
    /// </summary>
    public static CompiledProgram Compile(string source, CelEnv env)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(env);

        var parsed = Parsing.Parser.Parse(source);
        if (parsed.HasErrors)
        {
            throw new CelCompileException(parsed.Diagnostics);
        }

        var checkResult = Checker.Check(parsed.Expression!, parsed.SourceInfo, env, parsed.Diagnostics);
        if (checkResult.HasErrors || checkResult.Ast is null)
        {
            throw new CelCompileException(checkResult.Diagnostics);
        }

        return new CompiledProgram(checkResult.Ast, env);
    }
}

/// <summary>A compiled CEL expression ready for repeated evaluation against different inputs.</summary>
[RequiresUnreferencedCode("Field access on host objects uses reflection; runtime types may be trimmed.")]
public sealed class CompiledProgram
{
    private readonly Evaluator _evaluator;

    public CheckedAst Ast { get; }

    internal CompiledProgram(CheckedAst ast, CelEnv env)
    {
        Ast = ast;
        var registry = FunctionRegistry.CreateStandard();
        // Override stdlib equals / not_equals to consult the type provider so protocol-specific
        // equality (proto NaN propagation, host object structural compare) takes precedence
        // over the default record-style equality.
        var provider = env.TypeProvider;
        registry.Bind("equals", args => Cel.Values.CelValue.Of(Cel.Runtime.CelEquality.Equals(args[0], args[1], provider)));
        registry.Bind("not_equals", args => Cel.Values.CelValue.Of(!Cel.Runtime.CelEquality.Equals(args[0], args[1], provider)));
        foreach (var ext in env.Extensions)
        {
            ext.ConfigureRuntime(registry.Bind);
        }
        _evaluator = new Evaluator(ast, registry, typeProvider: env.TypeProvider);
    }

    /// <summary>Result type as inferred by the checker.</summary>
    public Cel.Types.CelType ResultType => Ast.ResultType;

    /// <summary>Evaluate against an explicit <see cref="IActivation"/>.</summary>
    public CelValue EvaluateRaw(IActivation activation) => _evaluator.Evaluate(activation);

    /// <summary>
    /// Evaluate and unwrap to a raw CLR value. Errors throw <see cref="CelEvaluationException"/>;
    /// nulls and missing keys surface as null.
    /// </summary>
    public object? Eval(IActivation activation)
    {
        var v = _evaluator.Evaluate(activation);
        if (v is ErrorValue err)
        {
            throw new CelEvaluationException(err.Message);
        }
        return ValueAdapter.ToClr(v);
    }

    /// <summary>Evaluate against a dictionary-backed activation.</summary>
    public object? Eval(IReadOnlyDictionary<string, object?> bindings) =>
        Eval(new MapActivation(bindings));

    /// <summary>
    /// Evaluate against a single root POCO whose top-level public properties / fields define the
    /// activation namespace. Convenient for the <c>new { account, request }</c> pattern.
    /// </summary>
    public object? Eval(object root) =>
        Eval(new ObjectActivation(root));

    /// <summary>Evaluate against an <see cref="IDictionary"/> (e.g. a non-generic dictionary).</summary>
    public object? Eval(IDictionary bindings) =>
        Eval(MapActivation.From(bindings));
}
