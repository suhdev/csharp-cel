using System.Collections.Immutable;
using Cel.Ast;

namespace Cel.UnitTests.Core;

public sealed class AstTests
{
    [Fact]
    public void IdGenerator_Yields_Increasing_Ids()
    {
        var gen = new IdGenerator();
        Assert.Equal(1, gen.Next());
        Assert.Equal(2, gen.Next());
        Assert.Equal(3, gen.Peek());
        Assert.Equal(3, gen.Next());
    }

    [Fact]
    public void Sealed_Hierarchy_Pattern_Matches_Exhaustively()
    {
        var ids = new IdGenerator();

        Expr root = new CallExpr(
            Target: null,
            Function: "_+_",
            Args:
            [
                new ConstantExpr(new IntConstant(1)) { Id = ids.Next() },
                new IdentifierExpr("x") { Id = ids.Next() },
            ])
        { Id = ids.Next() };

        var label = root switch
        {
            ConstantExpr => "const",
            IdentifierExpr => "ident",
            CallExpr c => $"call:{c.Function}:{c.Args.Length}",
            SelectExpr => "select",
            CreateListExpr => "list",
            CreateMapExpr => "map",
            CreateStructExpr => "struct",
            ComprehensionExpr => "comprehension",
            _ => "unknown",
        };
        Assert.Equal("call:_+_:2", label);
    }

    [Fact]
    public void Records_Equal_By_Structure()
    {
        var a = new ConstantExpr(new StringConstant("hi")) { Id = 7 };
        var b = new ConstantExpr(new StringConstant("hi")) { Id = 7 };
        Assert.Equal(a, b);
    }

    [Fact]
    public void Bytes_Constant_Equality_Is_Structural()
    {
        var a = new BytesConstant([1, 2, 3]);
        var b = new BytesConstant([1, 2, 3]);
        var c = new BytesConstant([1, 2, 4]);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Comprehension_Records_Macro_Provenance_In_Source_Info()
    {
        var sb = new SourceInfoBuilder { Source = "x.all(y, y > 0)" };
        sb.RecordMacroCall(expandedNodeId: 10, originalNodeIds: [1, 2, 3]);
        var info = sb.Build();
        Assert.True(info.MacroCalls[10].SequenceEqual(ImmutableArray.Create(1L, 2L, 3L)));
    }
}
