using System.Collections;

namespace DotnetCel.Diagnostics;

/// <summary>
/// Mutable accumulator used by the parser and checker to gather all errors before bailing.
/// </summary>
public sealed class DiagnosticBag : IReadOnlyList<Diagnostic>
{
    private readonly List<Diagnostic> _items = [];

    public int Count => _items.Count;

    public Diagnostic this[int index] => _items[index];

    public bool HasErrors
    {
        get
        {
            foreach (var d in _items)
            {
                if (d.Severity == DiagnosticSeverity.Error)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public void Report(Diagnostic diagnostic) => _items.Add(diagnostic);

    public void Error(string code, string message, SourceLocation location, string? source = null) =>
        Report(new Diagnostic(DiagnosticSeverity.Error, code, message, location, source));

    public void Warning(string code, string message, SourceLocation location, string? source = null) =>
        Report(new Diagnostic(DiagnosticSeverity.Warning, code, message, location, source));

    public IReadOnlyList<Diagnostic> Snapshot() => _items.ToArray();

    public IEnumerator<Diagnostic> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
