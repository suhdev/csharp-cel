namespace DotnetCel.Diagnostics;

/// <summary>
/// Base type for all CEL-originated exceptions surfaced at the public API boundary.
/// Errors that occur during evaluation are propagated as <em>values</em> internally and only
/// converted to exceptions when the caller asks for a CLR result.
/// </summary>
public class CelException : Exception
{
    public CelException(string message) : base(message) { }
    public CelException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when CEL parsing or type-checking produces one or more errors.
/// </summary>
public sealed class CelCompileException : CelException
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public CelCompileException(IReadOnlyList<Diagnostic> diagnostics)
        : base(FormatMessage(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    private static string FormatMessage(IReadOnlyList<Diagnostic> diagnostics)
    {
        var errors = new List<Diagnostic>();
        foreach (var d in diagnostics)
        {
            if (d.Severity == DiagnosticSeverity.Error)
            {
                errors.Add(d);
            }
        }
        return errors.Count switch
        {
            0 => "compilation failed",
            1 => errors[0].Message,
            _ => $"{errors.Count} compilation errors; first: {errors[0].Message}",
        };
    }
}

/// <summary>
/// Thrown at the API boundary when an evaluation fails to produce a value (e.g. uncaught error).
/// </summary>
public sealed class CelEvaluationException : CelException
{
    public CelEvaluationException(string message) : base(message) { }
    public CelEvaluationException(string message, Exception inner) : base(message, inner) { }
}
