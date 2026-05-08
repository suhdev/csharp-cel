using System.Collections.Immutable;
using Cel.Ast;
using Cel.Diagnostics;

namespace Cel.Parsing;

/// <summary>
/// Outcome of parsing a CEL source string.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Expression"/> is non-null whenever the parser produced any AST, even if
/// <see cref="Diagnostics"/> contains errors — partial recovery is intentional so callers can
/// inspect what was parsed before failure. Use <see cref="HasErrors"/> as the gate for moving
/// on to the type checker.
/// </para>
/// </remarks>
public sealed record ParseResult(
    Expr? Expression,
    SourceInfo SourceInfo,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool HasErrors
    {
        get
        {
            foreach (var d in Diagnostics)
            {
                if (d.Severity == DiagnosticSeverity.Error)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
