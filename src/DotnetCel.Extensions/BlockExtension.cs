using System.Collections.Immutable;
using System.Globalization;
using DotnetCel.Ast;

namespace DotnetCel.Extensions;

/// <summary>
/// Test-only port of cel-go's <c>cel.@block</c> form, which lets the optimiser hoist common
/// subexpressions into a list and reference them by index. Implemented as two macros:
/// <c>cel.block(l, e)</c> expands to a chain of let-bindings (one per element of <c>l</c>),
/// and <c>cel.index(N)</c> expands to an identifier referencing the bound name for position
/// <c>N</c>. The conformance corpus uses these in their textual form so this implementation
/// pre-empties the optimiser pipeline that would normally lower them.
/// </summary>
public sealed class BlockExtension : ICelExtension
{
    public static readonly BlockExtension Instance = new();
    private BlockExtension() { }

    public void ConfigureEnv(CelEnv.Builder envBuilder) { }
    public void ConfigureRuntime(Action<string, OverloadFn> bindImpl) { }

    public IEnumerable<CelMacro> Macros => [BlockMacro, IndexMacro, IterVarMacro, AccuVarMacro];

    private static readonly CelMacro BlockMacro = new(
        Name: "cel.block",
        Arity: 2,
        IsReceiverStyle: false,
        Expand: ExpandBlock);

    private static readonly CelMacro IndexMacro = new(
        Name: "cel.index",
        Arity: 1,
        IsReceiverStyle: false,
        Expand: ExpandIndex);

    /// <summary><c>cel.iterVar(N, M)</c> rewrites to identifier <c>@it:N:M</c>.</summary>
    private static readonly CelMacro IterVarMacro = new(
        Name: "cel.iterVar",
        Arity: 2,
        IsReceiverStyle: false,
        Expand: (ctx, _, args) => ExpandSyntheticIdent(ctx, args, "@it"));

    /// <summary><c>cel.accuVar(N, M)</c> rewrites to identifier <c>@ac:N:M</c>.</summary>
    private static readonly CelMacro AccuVarMacro = new(
        Name: "cel.accuVar",
        Arity: 2,
        IsReceiverStyle: false,
        Expand: (ctx, _, args) => ExpandSyntheticIdent(ctx, args, "@ac"));

    private static Expr? ExpandSyntheticIdent(MacroExpansionContext ctx, ImmutableArray<Expr> args, string prefix)
    {
        if (args[0] is not ConstantExpr { Value: IntConstant n } || args[1] is not ConstantExpr { Value: IntConstant m })
        {
            ctx.Error("CEL-1402", $"{prefix} requires two int literal arguments");
            return null;
        }
        return new IdentifierExpr($"{prefix}:{n.Value.ToString(CultureInfo.InvariantCulture)}:{m.Value.ToString(CultureInfo.InvariantCulture)}")
        {
            Id = ctx.NextId(),
        };
    }

    /// <summary>
    /// <c>cel.block([e0, e1, e2, ...], result)</c> expands to a chain of let-bindings:
    /// <c>let @index0 = e0 in let @index1 = e1' in ... result'</c>, where <c>eN'</c> and
    /// <c>result'</c> have any inner <c>cel.index(K)</c> already lowered to <c>@indexK</c>
    /// references via <see cref="ExpandIndex"/>.
    /// </summary>
    private static Expr? ExpandBlock(MacroExpansionContext ctx, Expr? receiver, ImmutableArray<Expr> args)
    {
        if (args[0] is not CreateListExpr list)
        {
            ctx.Error("CEL-1400", "cel.block: first argument must be a list literal");
            return null;
        }
        // Wrap from inside out: start with the result expression, then wrap with each
        // let-binding starting from the LAST block expression. This produces a left-leaning
        // tree where outer lets bind earlier indices.
        Expr body = args[1];
        for (var i = list.Elements.Length - 1; i >= 0; i--)
        {
            body = BindingsLet(ctx, "@index" + i.ToString(CultureInfo.InvariantCulture), list.Elements[i], body);
        }
        return body;
    }

    /// <summary>
    /// <c>cel.index(N)</c> expands to an identifier <c>@indexN</c>. The corresponding binding
    /// is created by the surrounding <c>cel.block</c>; if used outside one, the identifier is
    /// undefined and the runtime errors out.
    /// </summary>
    private static Expr? ExpandIndex(MacroExpansionContext ctx, Expr? receiver, ImmutableArray<Expr> args)
    {
        if (args[0] is not ConstantExpr { Value: IntConstant n } || n.Value < 0)
        {
            ctx.Error("CEL-1401", "cel.index requires a non-negative int literal");
            return null;
        }
        return new IdentifierExpr("@index" + n.Value.ToString(CultureInfo.InvariantCulture))
        {
            Id = ctx.NextId(),
        };
    }

    /// <summary>
    /// Build a let-style binding using the same comprehension shape as cel.bind: empty
    /// iter-range, accu_var = name, accu_init = init, result = body.
    /// </summary>
    private static Expr BindingsLet(MacroExpansionContext ctx, string varName, Expr init, Expr body)
    {
        var emptyList = new CreateListExpr([], []) { Id = ctx.NextId() };
        var falseLit = new ConstantExpr(new BoolConstant(false)) { Id = ctx.NextId() };
        var stepIdent = new IdentifierExpr(varName) { Id = ctx.NextId() };
        return new ComprehensionExpr(
            IterVar: "#unused",
            IterRange: emptyList,
            AccuVar: varName,
            AccuInit: init,
            LoopCondition: falseLit,
            LoopStep: stepIdent,
            Result: body)
        {
            Id = ctx.NextId(),
        };
    }
}
