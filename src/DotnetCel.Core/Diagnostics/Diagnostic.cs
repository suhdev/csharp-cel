namespace DotnetCel.Diagnostics;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// A structured compile- or runtime-time message.
/// </summary>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    SourceLocation Location,
    string? Source = null)
{
    public override string ToString()
    {
        var prefix = Source is null
            ? Location.ToString()
            : $"{Source}:{Location}";
        return $"{prefix}: {Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
    }
}
