using System.Collections.Immutable;
using DotnetCel.Diagnostics;

namespace DotnetCel.Ast;

/// <summary>
/// Side-table mapping AST node ids to their source positions and macro provenance. Kept out
/// of <see cref="Expr"/> so that AST records remain pure value objects.
/// </summary>
public sealed record SourceInfo(
    string? Source,
    ImmutableDictionary<long, SourceLocation> Positions,
    ImmutableDictionary<long, ImmutableArray<long>> MacroCalls)
{
    public static readonly SourceInfo Empty = new(
        Source: null,
        Positions: ImmutableDictionary<long, SourceLocation>.Empty,
        MacroCalls: ImmutableDictionary<long, ImmutableArray<long>>.Empty);

    public SourceLocation LocationOf(long exprId) =>
        Positions.TryGetValue(exprId, out var loc) ? loc : SourceLocation.Unknown;
}

/// <summary>Builder used by the parser to accumulate source info incrementally.</summary>
public sealed class SourceInfoBuilder
{
    private readonly ImmutableDictionary<long, SourceLocation>.Builder _positions =
        ImmutableDictionary.CreateBuilder<long, SourceLocation>();
    private readonly ImmutableDictionary<long, ImmutableArray<long>>.Builder _macroCalls =
        ImmutableDictionary.CreateBuilder<long, ImmutableArray<long>>();

    public string? Source { get; set; }

    public void RecordPosition(long exprId, SourceLocation location) =>
        _positions[exprId] = location;

    public void RecordMacroCall(long expandedNodeId, IEnumerable<long> originalNodeIds) =>
        _macroCalls[expandedNodeId] = [.. originalNodeIds];

    public SourceInfo Build() => new(Source, _positions.ToImmutable(), _macroCalls.ToImmutable());
}
