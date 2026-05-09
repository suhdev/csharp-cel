using System.Collections.Immutable;
using DotnetCel.Diagnostics;

namespace DotnetCel.Parsing;

/// <summary>
/// A lexed token. <see cref="Lexeme"/> is the original source text of the token; <see cref="Value"/>
/// is the parsed literal payload for literal tokens (bool / long / ulong / double / string /
/// <see cref="ImmutableArray{Byte}"/>) or null otherwise.
/// </summary>
public readonly record struct Token(
    TokenKind Kind,
    string Lexeme,
    SourceLocation Location,
    object? Value = null)
{
    public override string ToString() =>
        Value is null ? $"{Kind}({Lexeme}) @ {Location}" : $"{Kind}({Value}) @ {Location}";
}
