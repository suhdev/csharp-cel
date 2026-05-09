using System.Collections.Immutable;
using Cel.Ast;

namespace Cel.Extensions;

/// <summary>
/// Parser-level macros for the optional helpers <c>optMap</c> and <c>optFlatMap</c>. The
/// optional value type itself, plus <c>optional.of</c> / <c>optional.none</c> /
/// <c>optional.ofNonZeroValue</c> / <c>.hasValue()</c> / <c>.value()</c> / <c>.or()</c> /
/// <c>.orValue()</c> live in the checker stdlib already; only the comprehension-style
/// transformers need parser hooks because their second argument is a name introduced into
/// scope (akin to <c>map(v, t)</c>).
/// </summary>
public sealed class OptionalsExtension : ICelExtension
{
    public static readonly OptionalsExtension Instance = new();
    private OptionalsExtension() { }

    public void ConfigureEnv(CelEnv.Builder envBuilder) { }
    public void ConfigureRuntime(Action<string, OverloadFn> bindImpl) { }

    public IEnumerable<CelMacro> Macros => [OptMapMacro, OptFlatMapMacro];

    private static readonly CelMacro OptMapMacro = new(
        Name: "optMap",
        Arity: 2,
        IsReceiverStyle: true,
        Expand: ExpandOptMap);

    private static readonly CelMacro OptFlatMapMacro = new(
        Name: "optFlatMap",
        Arity: 2,
        IsReceiverStyle: true,
        Expand: ExpandOptFlatMap);

    /// <summary>
    /// <c>opt.optMap(v, transform)</c> →
    /// <code>
    /// opt.hasValue()
    ///     ? optional.of(let v = opt.value() in transform)
    ///     : optional.none()
    /// </code>
    /// where <c>let</c> uses the same comprehension shape as cel.bind.
    /// </summary>
    private static Expr? ExpandOptMap(MacroExpansionContext ctx, Expr? receiver, ImmutableArray<Expr> args)
    {
        if (receiver is null || args[0] is not IdentifierExpr ident)
        {
            ctx.Error("CEL-1300", "optMap: first argument must be an identifier");
            return null;
        }
        var varName = ident.Name;
        var bindBody = args[1];
        var bind = BuildLetBinding(ctx, receiver, varName, bindBody);
        var hasValue = NewInstanceCall(ctx, receiver, "hasValue", []);
        var optionalOf = NewGlobalCall(ctx, "optional.of", [bind]);
        var optionalNone = NewGlobalCall(ctx, "optional.none", []);
        return NewGlobalCall(ctx, "_?_:_", [hasValue, optionalOf, optionalNone]);
    }

    /// <summary>
    /// <c>opt.optFlatMap(v, transform)</c> — like <c>optMap</c> but the transform itself
    /// must return an optional, so the success branch returns the result directly without
    /// wrapping in <c>optional.of</c>.
    /// </summary>
    private static Expr? ExpandOptFlatMap(MacroExpansionContext ctx, Expr? receiver, ImmutableArray<Expr> args)
    {
        if (receiver is null || args[0] is not IdentifierExpr ident)
        {
            ctx.Error("CEL-1301", "optFlatMap: first argument must be an identifier");
            return null;
        }
        var varName = ident.Name;
        var bindBody = args[1];
        var bind = BuildLetBinding(ctx, receiver, varName, bindBody);
        var hasValue = NewInstanceCall(ctx, receiver, "hasValue", []);
        var optionalNone = NewGlobalCall(ctx, "optional.none", []);
        return NewGlobalCall(ctx, "_?_:_", [hasValue, bind, optionalNone]);
    }

    /// <summary>
    /// Build the <c>let v = opt.value() in body</c> equivalent — same comprehension shape as
    /// cel.bind: empty iter range, accu_var=v, accu_init=opt.value(), result=body.
    /// </summary>
    private static Expr BuildLetBinding(MacroExpansionContext ctx, Expr opt, string varName, Expr body)
    {
        var valueCall = NewInstanceCall(ctx, opt, "value", []);
        var emptyList = new CreateListExpr([], []) { Id = ctx.NextId() };
        var falseLit = new ConstantExpr(new BoolConstant(false)) { Id = ctx.NextId() };
        var stepIdent = new IdentifierExpr(varName) { Id = ctx.NextId() };
        return new ComprehensionExpr(
            IterVar: "#unused",
            IterRange: emptyList,
            AccuVar: varName,
            AccuInit: valueCall,
            LoopCondition: falseLit,
            LoopStep: stepIdent,
            Result: body)
        {
            Id = ctx.NextId(),
        };
    }

    private static CallExpr NewInstanceCall(MacroExpansionContext ctx, Expr receiver, string fn, ImmutableArray<Expr> args) =>
        new(receiver, fn, args) { Id = ctx.NextId() };

    private static CallExpr NewGlobalCall(MacroExpansionContext ctx, string fn, ImmutableArray<Expr> args) =>
        new(null, fn, args) { Id = ctx.NextId() };
}
