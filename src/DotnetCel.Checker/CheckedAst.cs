using System.Collections.Immutable;
using DotnetCel.Ast;
using DotnetCel.Diagnostics;
using DotnetCel.Types;

namespace DotnetCel;

/// <summary>
/// A successfully type-checked CEL expression, plus the side-tables produced by checking:
/// every <see cref="Expr"/> id maps to a <see cref="CelType"/>, and identifiers / function calls
/// map to a <see cref="ResolvedReference"/> describing how the name was resolved (qualified
/// name, picked overload).
/// </summary>
public sealed record CheckedAst(
    Expr Expression,
    SourceInfo SourceInfo,
    ImmutableDictionary<long, CelType> TypeMap,
    ImmutableDictionary<long, ResolvedReference> ReferenceMap,
    CelType ResultType);

/// <summary>
/// Records the outcome of resolving an identifier or call site. <see cref="OverloadId"/> is set
/// for call sites and identifies the matched <see cref="OverloadDecl"/> for runtime dispatch.
/// <see cref="TargetIsNamespace"/> is true for call sites where the target chain is a namespace
/// prefix (e.g. <c>math</c> in <c>math.greatest(1, 2)</c>) rather than a value to evaluate; the
/// evaluator skips evaluating the target in that case.
/// </summary>
public sealed record ResolvedReference(
    string Name,
    string? OverloadId = null,
    ConstValue? Value = null,
    bool TargetIsNamespace = false);

/// <summary>Outcome of type-checking. <see cref="Ast"/> is null when the source had errors.</summary>
public sealed record CheckResult(
    CheckedAst? Ast,
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
