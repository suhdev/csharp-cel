using System.Collections.Immutable;
using Cel.Ast;
using Cel.Diagnostics;

namespace Cel;

/// <summary>
/// A parser-level macro registered by an extension. Macros transform a recognized
/// <see cref="Cel.Ast.CallExpr"/> shape into a different AST subtree at parse time, before
/// the type checker sees it. Use this for syntax that can't be expressed via plain function
/// declarations — host-flavored binding forms, comprehension shortcuts, etc.
/// </summary>
/// <param name="Name">
/// For receiver-style macros (<see cref="IsReceiverStyle"/> = true), the bare method name
/// (e.g. <c>"optMap"</c>). For namespaced macros, the fully-qualified call name
/// (e.g. <c>"cel.bind"</c>) that the parser matches against the flattened
/// <c>target.function</c> chain.
/// </param>
/// <param name="Arity">Expected number of arguments. Use <c>-1</c> for variadic.</param>
/// <param name="IsReceiverStyle">
/// True for <c>e.macroName(args)</c>; false for <c>pkg.macroName(args)</c> dispatched as a
/// global call.
/// </param>
/// <param name="Expand">
/// Builds the expanded AST. Returns null when the macro recognises the call shape but cannot
/// expand (a diagnostic should be reported through <see cref="MacroExpansionContext.Diagnostics"/>);
/// returning null also tells the parser to fall back to a regular call.
/// </param>
public sealed record CelMacro(
    string Name,
    int Arity,
    bool IsReceiverStyle,
    Func<MacroExpansionContext, Expr?, ImmutableArray<Expr>, Expr?> Expand);

/// <summary>
/// State handed to a <see cref="CelMacro"/>'s expansion function. Provides the AST id
/// generator, source-info side-table, diagnostics bag, and the source location the parser
/// associates with the macro call site.
/// </summary>
public sealed class MacroExpansionContext
{
    public IdGenerator Ids { get; }
    public SourceInfoBuilder SourceInfo { get; }
    public DiagnosticBag Diagnostics { get; }
    public SourceLocation Location { get; }

    public MacroExpansionContext(
        IdGenerator ids,
        SourceInfoBuilder sourceInfo,
        DiagnosticBag diagnostics,
        SourceLocation location)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(sourceInfo);
        ArgumentNullException.ThrowIfNull(diagnostics);
        Ids = ids;
        SourceInfo = sourceInfo;
        Diagnostics = diagnostics;
        Location = location;
    }

    /// <summary>
    /// Allocate a fresh AST id and record it as originating from the macro call site.
    /// Use the <c>{ Id = ctx.NextId() }</c> object-initializer pattern to attach to a record.
    /// </summary>
    public long NextId()
    {
        var id = Ids.Next();
        SourceInfo.RecordPosition(id, Location);
        return id;
    }

    /// <summary>Like <see cref="NextId()"/> but stamps a custom location.</summary>
    public long NextId(SourceLocation location)
    {
        var id = Ids.Next();
        SourceInfo.RecordPosition(id, location);
        return id;
    }

    /// <summary>Convenience: report an error through the diagnostic bag at the macro call site.</summary>
    public void Error(string code, string message) =>
        Diagnostics.Error(code, message, Location);
}
