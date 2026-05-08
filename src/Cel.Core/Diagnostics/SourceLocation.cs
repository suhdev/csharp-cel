namespace Cel.Diagnostics;

/// <summary>
/// A 1-based line/column position in a CEL source. <see cref="ByteOffset"/> is a 0-based UTF-16
/// code-unit offset into the original string.
/// </summary>
public readonly record struct SourceLocation(int Line, int Column, int ByteOffset, int Length = 0)
{
    public static readonly SourceLocation Unknown = new(0, 0, 0);

    public bool IsKnown => Line > 0;

    public override string ToString() =>
        IsKnown ? $"{Line}:{Column}" : "<unknown>";
}
