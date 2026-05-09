using System.Collections.Immutable;
using DotnetCel.Ast;
using DotnetCel.Parsing;

namespace DotnetCel.UnitTests.Parser;

/// <summary>
/// Functional tests for <see cref="Parser"/>. Each test asserts the structural shape
/// of the produced AST against the canonical CEL form (operators-as-calls, macros-as-comprehensions).
/// </summary>
public sealed class ParserTests
{
    private static Expr ParseOk(string src)
    {
        var r = DotnetCel.Parsing.Parser.Parse(src);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics.Select(static d => d.Message)));
        Assert.NotNull(r.Expression);
        return r.Expression!;
    }

    private static ParseResult ParseFail(string src)
    {
        var r = DotnetCel.Parsing.Parser.Parse(src);
        Assert.True(r.HasErrors, "expected parse error but got none");
        return r;
    }

    private static CallExpr AsCall(Expr e, string fn, int arity)
    {
        var c = Assert.IsType<CallExpr>(e);
        Assert.Equal(fn, c.Function);
        Assert.Equal(arity, c.Args.Length);
        return c;
    }

    [Fact]
    public void Literals()
    {
        Assert.Equal(new IntConstant(42), ((ConstantExpr)ParseOk("42")).Value);
        Assert.Equal(new IntConstant(-1), ((ConstantExpr)ParseOk("-1")).Value);
        Assert.Equal(new IntConstant(long.MinValue), ((ConstantExpr)ParseOk("-9223372036854775808")).Value);
        Assert.Equal(new UintConstant(7), ((ConstantExpr)ParseOk("7u")).Value);
        Assert.Equal(new DoubleConstant(1.5), ((ConstantExpr)ParseOk("1.5")).Value);
        Assert.Equal(new DoubleConstant(-2.5), ((ConstantExpr)ParseOk("-2.5")).Value);
        Assert.Equal(new StringConstant("hi"), ((ConstantExpr)ParseOk("\"hi\"")).Value);
        Assert.Equal(new BoolConstant(true), ((ConstantExpr)ParseOk("true")).Value);
        Assert.Equal(new BoolConstant(false), ((ConstantExpr)ParseOk("false")).Value);
        Assert.Equal(NullConstant.Instance, ((ConstantExpr)ParseOk("null")).Value);
    }

    [Fact]
    public void Int_Overflow_Reports_Error()
    {
        ParseFail("9223372036854775808");          // > long.MaxValue, no 'u'
        ParseFail("-9223372036854775809");         // < long.MinValue
    }

    [Fact]
    public void Identifier_Chain_Becomes_Select_Chain()
    {
        var e = ParseOk("a.b.c");
        var s2 = Assert.IsType<SelectExpr>(e);
        Assert.Equal("c", s2.Field);
        var s1 = Assert.IsType<SelectExpr>(s2.Operand);
        Assert.Equal("b", s1.Field);
        var ident = Assert.IsType<IdentifierExpr>(s1.Operand);
        Assert.Equal("a", ident.Name);
    }

    [Fact]
    public void Leading_Dot_Marks_Identifier_With_Dot_Prefix()
    {
        var e = ParseOk(".a.b");
        var s = Assert.IsType<SelectExpr>(e);
        var ident = Assert.IsType<IdentifierExpr>(s.Operand);
        Assert.Equal(".a", ident.Name);
    }

    [Fact]
    public void Operator_Precedence_Plus_Times()
    {
        var e = ParseOk("1 + 2 * 3");
        var add = AsCall(e, Operators.Add, 2);
        Assert.IsType<ConstantExpr>(add.Args[0]);
        var mult = AsCall(add.Args[1], Operators.Multiply, 2);
        Assert.IsType<ConstantExpr>(mult.Args[0]);
        Assert.IsType<ConstantExpr>(mult.Args[1]);
    }

    [Fact]
    public void Logical_And_Binds_Tighter_Than_Or()
    {
        var e = ParseOk("a || b && c");
        var or = AsCall(e, Operators.LogicalOr, 2);
        AsCall(or.Args[1], Operators.LogicalAnd, 2);
    }

    [Fact]
    public void Relational_Operators()
    {
        AsCall(ParseOk("1 < 2"), Operators.Less, 2);
        AsCall(ParseOk("1 <= 2"), Operators.LessEqual, 2);
        AsCall(ParseOk("1 > 2"), Operators.Greater, 2);
        AsCall(ParseOk("1 >= 2"), Operators.GreaterEqual, 2);
        AsCall(ParseOk("1 == 2"), Operators.Equal, 2);
        AsCall(ParseOk("1 != 2"), Operators.NotEqual, 2);
        AsCall(ParseOk("x in [1,2]"), Operators.In, 2);
    }

    [Fact]
    public void Unary_Operators()
    {
        AsCall(ParseOk("!x"), Operators.LogicalNot, 1);
        // -y where y is identifier becomes Negate (not folded; folding is only for literals).
        AsCall(ParseOk("-y"), Operators.Negate, 1);
    }

    [Fact]
    public void Parentheses_Override_Precedence()
    {
        var e = ParseOk("(1 + 2) * 3");
        var mult = AsCall(e, Operators.Multiply, 2);
        AsCall(mult.Args[0], Operators.Add, 2);
    }

    [Fact]
    public void Ternary_Right_Associative()
    {
        var e = ParseOk("a ? b : c ? d : e");
        var outer = AsCall(e, Operators.Conditional, 3);
        AsCall(outer.Args[2], Operators.Conditional, 3);  // else-branch holds the nested ternary
    }

    [Fact]
    public void Global_Function_Call()
    {
        var e = ParseOk("size([1,2,3])");
        var c = AsCall(e, "size", 1);
        Assert.Null(c.Target);
        Assert.IsType<CreateListExpr>(c.Args[0]);
    }

    [Fact]
    public void Receiver_Method_Call()
    {
        var e = ParseOk("\"abc\".startsWith(\"a\")");
        var c = AsCall(e, "startsWith", 1);
        Assert.NotNull(c.Target);
    }

    [Fact]
    public void Indexing_Becomes_Index_Call()
    {
        var e = ParseOk("xs[0]");
        var c = AsCall(e, Operators.Index, 2);
        Assert.IsType<IdentifierExpr>(c.Args[0]);
        Assert.IsType<ConstantExpr>(c.Args[1]);
    }

    [Fact]
    public void Optional_Index_And_Optional_Select()
    {
        AsCall(ParseOk("xs[?0]"), Operators.OptIndex, 2);
        var sel = AsCall(ParseOk("x.?f"), Operators.OptSelect, 2);
        var fieldName = Assert.IsType<ConstantExpr>(sel.Args[1]);
        Assert.Equal(new StringConstant("f"), fieldName.Value);
    }

    [Fact]
    public void List_Literal_With_Optional_Element()
    {
        var e = (CreateListExpr)ParseOk("[1, ?2, 3]");
        Assert.Equal(3, e.Elements.Length);
        Assert.True(e.OptionalIndices.SequenceEqual(ImmutableArray.Create(1)));
    }

    [Fact]
    public void Map_Literal_With_Optional_Entry()
    {
        var e = (CreateMapExpr)ParseOk("{ 'a': 1, ?'b': 2 }");
        Assert.Equal(2, e.Entries.Length);
        Assert.False(e.Entries[0].IsOptional);
        Assert.True(e.Entries[1].IsOptional);
    }

    [Fact]
    public void Struct_Literal_From_Dotted_Type_Name()
    {
        var e = (CreateStructExpr)ParseOk("pkg.Type{a: 1, ?b: 2}");
        Assert.Equal("pkg.Type", e.MessageName);
        Assert.Equal(2, e.Fields.Length);
        Assert.Equal("a", e.Fields[0].Name);
        Assert.False(e.Fields[0].IsOptional);
        Assert.True(e.Fields[1].IsOptional);
    }

    [Fact]
    public void Trailing_Commas_Allowed_In_Collections()
    {
        var l = (CreateListExpr)ParseOk("[1, 2, 3,]");
        Assert.Equal(3, l.Elements.Length);
        var m = (CreateMapExpr)ParseOk("{'a': 1, 'b': 2,}");
        Assert.Equal(2, m.Entries.Length);
        var s = (CreateStructExpr)ParseOk("T{a: 1,}");
        Assert.Single(s.Fields);
    }

    // ── macros ──

    [Fact]
    public void Has_Macro_Becomes_TestOnly_Select()
    {
        var s = Assert.IsType<SelectExpr>(ParseOk("has(x.y)"));
        Assert.Equal("y", s.Field);
        Assert.True(s.TestOnly);
    }

    [Fact]
    public void Has_Of_Non_Select_Reports_Error()
    {
        ParseFail("has(x)");
    }

    [Fact]
    public void All_Macro_Expands_To_Comprehension()
    {
        var c = Assert.IsType<ComprehensionExpr>(ParseOk("xs.all(x, x > 0)"));
        Assert.Equal("x", c.IterVar);
        Assert.Equal(Operators.AccumulatorName, c.AccuVar);
        // accuInit is true
        var ai = Assert.IsType<ConstantExpr>(c.AccuInit);
        Assert.Equal(new BoolConstant(true), ai.Value);
        // loopStep is __result__ && pred
        var step = AsCall(c.LoopStep, Operators.LogicalAnd, 2);
        Assert.IsType<IdentifierExpr>(step.Args[0]);
    }

    [Fact]
    public void Exists_Macro_Uses_Or_Combiner()
    {
        var c = Assert.IsType<ComprehensionExpr>(ParseOk("xs.exists(x, x > 0)"));
        AsCall(c.LoopStep, Operators.LogicalOr, 2);
        var ai = Assert.IsType<ConstantExpr>(c.AccuInit);
        Assert.Equal(new BoolConstant(false), ai.Value);
    }

    [Fact]
    public void ExistsOne_Counts_And_Compares_To_One()
    {
        var c = Assert.IsType<ComprehensionExpr>(ParseOk("xs.exists_one(x, x > 0)"));
        var ai = Assert.IsType<ConstantExpr>(c.AccuInit);
        Assert.Equal(new IntConstant(0), ai.Value);
        // result is __result__ == 1
        AsCall(c.Result, Operators.Equal, 2);
    }

    [Fact]
    public void Map_Macro_Two_Arg_Builds_List_Append()
    {
        var c = Assert.IsType<ComprehensionExpr>(ParseOk("xs.map(x, x * 2)"));
        // step appends transform to accu
        AsCall(c.LoopStep, Operators.Add, 2);
        Assert.IsType<CreateListExpr>(c.AccuInit);
    }

    [Fact]
    public void Map_Macro_Three_Arg_Has_Filter()
    {
        var c = Assert.IsType<ComprehensionExpr>(ParseOk("xs.map(x, x > 0, x * 2)"));
        // step is conditional(filter, append, accu)
        var cond = AsCall(c.LoopStep, Operators.Conditional, 3);
        AsCall(cond.Args[0], Operators.Greater, 2);
    }

    [Fact]
    public void Filter_Macro_Appends_Iter_Var()
    {
        var c = Assert.IsType<ComprehensionExpr>(ParseOk("xs.filter(x, x > 0)"));
        AsCall(c.LoopStep, Operators.Conditional, 3);
    }

    // ── error recovery ──

    [Fact]
    public void Unterminated_Group_Reports_Error()
    {
        ParseFail("(1 + 2");
        ParseFail("[1, 2");
        ParseFail("{a: 1");
    }

    [Fact]
    public void Reserved_Keyword_Is_An_Error()
    {
        ParseFail("let x = 1");
    }

    [Fact]
    public void Trailing_Tokens_Are_An_Error()
    {
        ParseFail("1 + 2 hello");
    }
}
