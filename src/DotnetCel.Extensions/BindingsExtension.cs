using System.Collections.Immutable;
using DotnetCel.Ast;

namespace DotnetCel.Extensions;

/// <summary>
/// Port of cel-go's <c>ext/bindings</c>: provides <c>cel.bind(name, init, expr)</c>, a
/// let-style binding form. Implemented as a parser-level macro (no runtime functions to
/// register) — every <c>cel.bind</c> call expands at parse time to a comprehension over an
/// empty list whose accumulator carries the bound variable.
/// </summary>
public sealed class BindingsExtension : ICelExtension
{
    public static readonly BindingsExtension Instance = new();
    private BindingsExtension() { }

    public void ConfigureEnv(CelEnv.Builder envBuilder) { }

    public void ConfigureRuntime(Action<string, OverloadFn> bindImpl) { }

    public IEnumerable<CelMacro> Macros => [BindMacro];

    /// <summary>
    /// <c>cel.bind(varName, init, expr)</c> →
    /// <code>
    /// ComprehensionExpr {
    ///   iter_var:        "#unused",
    ///   iter_range:      [],                       // empty list — never iterates
    ///   accu_var:        varName,
    ///   accu_init:       init,
    ///   loop_condition:  false,
    ///   loop_step:       varName,                  // identity, never reached
    ///   result:          expr,                     // body sees `varName`
    /// }
    /// </code>
    /// Mirrors cel-go's <c>bind</c> macro shape.
    /// </summary>
    private static readonly CelMacro BindMacro = new(
        Name: "cel.bind",
        Arity: 3,
        IsReceiverStyle: false,
        Expand: ExpandBind);

    private static Expr? ExpandBind(MacroExpansionContext ctx, Expr? receiver, ImmutableArray<Expr> args)
    {
        if (args[0] is not IdentifierExpr ident)
        {
            ctx.Error("CEL-1200", "cel.bind: first argument must be a simple identifier");
            return null;
        }
        var varName = ident.Name;
        var init = args[1];
        var body = args[2];

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
