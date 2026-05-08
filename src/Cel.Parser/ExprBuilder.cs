using System.Collections.Immutable;
using Cel.Ast;
using Cel.Diagnostics;

namespace Cel.Parsing;

/// <summary>
/// Internal helper that hides AST id assignment and source-location bookkeeping from the
/// parser body, so the parser reads as a near-direct transcription of the grammar.
/// </summary>
internal sealed class ExprBuilder
{
    private readonly IdGenerator _ids;
    private readonly SourceInfoBuilder _sourceInfo;

    public ExprBuilder(IdGenerator ids, SourceInfoBuilder sourceInfo)
    {
        _ids = ids;
        _sourceInfo = sourceInfo;
    }

    public IdGenerator Ids => _ids;
    public SourceInfoBuilder SourceInfo => _sourceInfo;

    public ConstantExpr Constant(ConstValue value, SourceLocation loc) =>
        Track(new ConstantExpr(value) { Id = _ids.Next() }, loc);

    public IdentifierExpr Identifier(string name, SourceLocation loc) =>
        Track(new IdentifierExpr(name) { Id = _ids.Next() }, loc);

    public SelectExpr Select(Expr operand, string field, SourceLocation loc, bool testOnly = false) =>
        Track(new SelectExpr(operand, field, testOnly) { Id = _ids.Next() }, loc);

    public CallExpr Call(Expr? target, string function, ImmutableArray<Expr> args, SourceLocation loc) =>
        Track(new CallExpr(target, function, args) { Id = _ids.Next() }, loc);

    public CreateListExpr List(ImmutableArray<Expr> elements, ImmutableArray<int> optional, SourceLocation loc) =>
        Track(new CreateListExpr(elements, optional) { Id = _ids.Next() }, loc);

    public CreateMapExpr Map(ImmutableArray<MapEntry> entries, SourceLocation loc) =>
        Track(new CreateMapExpr(entries) { Id = _ids.Next() }, loc);

    public CreateStructExpr Struct(string typeName, ImmutableArray<StructField> fields, SourceLocation loc) =>
        Track(new CreateStructExpr(typeName, fields) { Id = _ids.Next() }, loc);

    public ComprehensionExpr Comprehension(
        string iterVar,
        Expr iterRange,
        string accuVar,
        Expr accuInit,
        Expr loopCondition,
        Expr loopStep,
        Expr result,
        SourceLocation loc,
        string? iterVar2 = null) =>
        Track(
            new ComprehensionExpr(iterVar, iterRange, accuVar, accuInit, loopCondition, loopStep, result, iterVar2)
            {
                Id = _ids.Next(),
            },
            loc);

    public MapEntry NewMapEntry(Expr key, Expr value, bool isOptional, SourceLocation loc)
    {
        var id = _ids.Next();
        _sourceInfo.RecordPosition(id, loc);
        return new MapEntry(id, key, value, isOptional);
    }

    public StructField NewStructField(string name, Expr value, bool isOptional, SourceLocation loc)
    {
        var id = _ids.Next();
        _sourceInfo.RecordPosition(id, loc);
        return new StructField(id, name, value, isOptional);
    }

    private T Track<T>(T expr, SourceLocation loc) where T : Expr
    {
        _sourceInfo.RecordPosition(expr.Id, loc);
        return expr;
    }
}
