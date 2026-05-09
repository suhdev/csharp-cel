using System.Collections.Immutable;

namespace DotnetCel.Ast;

/// <summary>
/// Base record for all CEL AST nodes. Each node carries a stable, monotonically increasing
/// <see cref="Id"/> assigned by the parser; checker results (types, references) are stored in
/// side-tables keyed by this id rather than mutated onto the node.
/// </summary>
/// <remarks>
/// The hierarchy is closed: every concrete subtype is sealed and lives in this file, allowing
/// exhaustive <c>switch</c> expressions over <see cref="Expr"/> in the parser, checker, and
/// evaluator. Children are <see cref="ImmutableArray{T}"/> so that nodes are deeply immutable.
/// </remarks>
public abstract record Expr
{
    public required long Id { get; init; }

    private protected Expr() { }
}

/// <summary>A literal constant: <c>1</c>, <c>"x"</c>, <c>true</c>, <c>null</c>, etc.</summary>
public sealed record ConstantExpr(ConstValue Value) : Expr;

/// <summary>An unqualified identifier, possibly a namespace prefix to be resolved by container.</summary>
public sealed record IdentifierExpr(string Name) : Expr;

/// <summary>Field access (<c>a.b</c>) or, when <see cref="TestOnly"/>, a presence test (<c>has(a.b)</c>).</summary>
public sealed record SelectExpr(Expr Operand, string Field, bool TestOnly = false) : Expr;

/// <summary>
/// A function or method call. <see cref="Target"/> is null for free functions and set for
/// receiver-style calls (<c>x.foo(y)</c>).
/// </summary>
public sealed record CallExpr(Expr? Target, string Function, ImmutableArray<Expr> Args) : Expr;

/// <summary>A list literal: <c>[a, b, c]</c>. <see cref="OptionalIndices"/> marks elements introduced via <c>?elem</c>.</summary>
public sealed record CreateListExpr(ImmutableArray<Expr> Elements, ImmutableArray<int> OptionalIndices) : Expr;

/// <summary>A map literal: <c>{k: v, ...}</c>.</summary>
public sealed record CreateMapExpr(ImmutableArray<MapEntry> Entries) : Expr;

/// <summary>A message/struct literal: <c>some.Type{f: v, ...}</c>.</summary>
public sealed record CreateStructExpr(string MessageName, ImmutableArray<StructField> Fields) : Expr;

/// <summary>
/// A comprehension is the lowered form of every CEL macro (<c>has</c>, <c>all</c>, <c>exists</c>,
/// <c>exists_one</c>, <c>map</c>, <c>filter</c>, <c>map_filter</c>). It expresses a fold over
/// <see cref="IterRange"/> with an accumulator threaded through <see cref="LoopStep"/>.
/// <see cref="IterVar2"/> is set for two-variable comprehensions over maps (key/value pairs).
/// </summary>
public sealed record ComprehensionExpr(
    string IterVar,
    Expr IterRange,
    string AccuVar,
    Expr AccuInit,
    Expr LoopCondition,
    Expr LoopStep,
    Expr Result,
    string? IterVar2 = null) : Expr;

public sealed record MapEntry(long Id, Expr Key, Expr Value, bool IsOptional = false);

public sealed record StructField(long Id, string Name, Expr Value, bool IsOptional = false);
