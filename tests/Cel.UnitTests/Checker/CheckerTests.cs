using Cel;
using Cel.Diagnostics;
using Cel.Types;

namespace Cel.UnitTests.Checker;

/// <summary>
/// Functional tests for <see cref="Cel.Checker"/>. Each test parses a small expression and
/// asserts the inferred result type and (where relevant) error diagnostics.
/// </summary>
public sealed class CheckerTests
{
    private static CheckResult CheckOk(string source, CelEnv? env = null)
    {
        var parsed = Cel.Parsing.Parser.Parse(source);
        Assert.False(parsed.HasErrors, "parse: " + string.Join("; ", parsed.Diagnostics.Select(static d => d.Message)));
        env ??= CelEnv.NewBuilder().Build();
        var result = Cel.Checker.Check(parsed.Expression!, parsed.SourceInfo, env, parsed.Diagnostics);
        Assert.False(result.HasErrors, "check: " + string.Join("; ", result.Diagnostics.Select(static d => d.Message)));
        Assert.NotNull(result.Ast);
        return result;
    }

    private static CheckResult CheckFail(string source, CelEnv? env = null)
    {
        var parsed = Cel.Parsing.Parser.Parse(source);
        env ??= CelEnv.NewBuilder().Build();
        var result = Cel.Checker.Check(parsed.Expression!, parsed.SourceInfo, env, parsed.Diagnostics);
        Assert.True(result.HasErrors, "expected check error but got none");
        return result;
    }

    [Fact]
    public void Literals_Get_Their_Types()
    {
        Assert.Equal(CelTypes.Bool, CheckOk("true").Ast!.ResultType);
        Assert.Equal(CelTypes.Int, CheckOk("1").Ast!.ResultType);
        Assert.Equal(CelTypes.Uint, CheckOk("1u").Ast!.ResultType);
        Assert.Equal(CelTypes.Double, CheckOk("1.5").Ast!.ResultType);
        Assert.Equal(CelTypes.String, CheckOk("\"x\"").Ast!.ResultType);
        Assert.Equal(CelTypes.Bytes, CheckOk("b\"x\"").Ast!.ResultType);
        Assert.Equal(CelTypes.Null, CheckOk("null").Ast!.ResultType);
    }

    [Fact]
    public void Arithmetic_Same_Type()
    {
        Assert.Equal(CelTypes.Int, CheckOk("1 + 2").Ast!.ResultType);
        Assert.Equal(CelTypes.Uint, CheckOk("1u + 2u").Ast!.ResultType);
        Assert.Equal(CelTypes.Double, CheckOk("1.5 * 2.0").Ast!.ResultType);
        Assert.Equal(CelTypes.String, CheckOk("\"a\" + \"b\"").Ast!.ResultType);
    }

    [Fact]
    public void Arithmetic_Mixed_Type_Fails()
    {
        // CEL is strict on numerics — int + uint is not auto-promoted.
        CheckFail("1 + 2u");
        CheckFail("1 + 2.5");
    }

    [Fact]
    public void Comparison_Returns_Bool()
    {
        Assert.Equal(CelTypes.Bool, CheckOk("1 < 2").Ast!.ResultType);
        Assert.Equal(CelTypes.Bool, CheckOk("\"a\" == \"b\"").Ast!.ResultType);
        Assert.Equal(CelTypes.Bool, CheckOk("1 == 1u").Ast!.ResultType); // parametric ==, widens via dyn
    }

    [Fact]
    public void Logical_And_Returns_Bool()
    {
        Assert.Equal(CelTypes.Bool, CheckOk("true && false").Ast!.ResultType);
        Assert.Equal(CelTypes.Bool, CheckOk("!(1 < 2)").Ast!.ResultType);
    }

    [Fact]
    public void Ternary_Both_Branches_Same_Type()
    {
        Assert.Equal(CelTypes.Int, CheckOk("true ? 1 : 2").Ast!.ResultType);
        Assert.Equal(CelTypes.String, CheckOk("false ? \"a\" : \"b\"").Ast!.ResultType);
    }

    [Fact]
    public void Ternary_Mixed_Branches_Widen_To_Dyn()
    {
        Assert.Equal(CelTypes.Dyn, CheckOk("true ? 1 : \"x\"").Ast!.ResultType);
    }

    [Fact]
    public void List_Literal_Element_Inferred()
    {
        Assert.Equal(CelTypes.List(CelTypes.Int), CheckOk("[1, 2, 3]").Ast!.ResultType);
        Assert.Equal(CelTypes.List(CelTypes.Dyn), CheckOk("[1, \"a\"]").Ast!.ResultType);
        Assert.Equal(CelTypes.List(CelTypes.Dyn), CheckOk("[]").Ast!.ResultType);
    }

    [Fact]
    public void Map_Literal_Key_Value_Inferred()
    {
        Assert.Equal(CelTypes.Map(CelTypes.String, CelTypes.Int),
            CheckOk("{\"a\": 1, \"b\": 2}").Ast!.ResultType);
    }

    [Fact]
    public void Indexing_List_Yields_Element_Type()
    {
        var env = CelEnv.NewBuilder().Variable("xs", CelTypes.List(CelTypes.Int)).Build();
        Assert.Equal(CelTypes.Int, CheckOk("xs[0]", env).Ast!.ResultType);
    }

    [Fact]
    public void Indexing_Map_Yields_Value_Type()
    {
        var env = CelEnv.NewBuilder()
            .Variable("m", CelTypes.Map(CelTypes.String, CelTypes.Bool))
            .Build();
        Assert.Equal(CelTypes.Bool, CheckOk("m[\"k\"]", env).Ast!.ResultType);
    }

    [Fact]
    public void Has_Macro_Returns_Bool()
    {
        var env = CelEnv.NewBuilder()
            .Variable("m", CelTypes.Map(CelTypes.String, CelTypes.Bool))
            .Build();
        Assert.Equal(CelTypes.Bool, CheckOk("has(m.x)", env).Ast!.ResultType);
    }

    [Fact]
    public void Comprehension_All_Returns_Bool()
    {
        var env = CelEnv.NewBuilder().Variable("xs", CelTypes.List(CelTypes.Int)).Build();
        Assert.Equal(CelTypes.Bool, CheckOk("xs.all(x, x > 0)", env).Ast!.ResultType);
    }

    [Fact]
    public void Comprehension_Map_Returns_List_Of_Transform()
    {
        var env = CelEnv.NewBuilder().Variable("xs", CelTypes.List(CelTypes.Int)).Build();
        Assert.Equal(CelTypes.List(CelTypes.Int), CheckOk("xs.map(x, x * 2)", env).Ast!.ResultType);
    }

    [Fact]
    public void Comprehension_Filter_Returns_Same_List_Type()
    {
        var env = CelEnv.NewBuilder().Variable("xs", CelTypes.List(CelTypes.String)).Build();
        Assert.Equal(CelTypes.List(CelTypes.String), CheckOk("xs.filter(x, size(x) > 0)", env).Ast!.ResultType);
    }

    [Fact]
    public void Comprehension_Iter_Var_Visible_Only_In_Loop()
    {
        var env = CelEnv.NewBuilder().Variable("xs", CelTypes.List(CelTypes.Int)).Build();
        // 'x' is not visible after the comprehension finishes.
        CheckFail("xs.all(x, x > 0) && x > 0", env);
    }

    [Fact]
    public void Undeclared_Identifier_Reports_Error()
    {
        var diag = CheckFail("nope").Diagnostics;
        Assert.Contains(diag, static d => d.Code == "CEL-2001");
    }

    [Fact]
    public void Undeclared_Function_Reports_Error()
    {
        var diag = CheckFail("nope_fn(1)").Diagnostics;
        Assert.Contains(diag, static d => d.Code == "CEL-2002");
    }

    [Fact]
    public void Wrong_Arity_Reports_No_Match()
    {
        // size has overloads of arity 1 only.
        var diag = CheckFail("size()").Diagnostics;
        Assert.Contains(diag, static d => d.Code == "CEL-2003");
    }

    [Fact]
    public void Container_Resolves_From_Most_Specific_To_Least()
    {
        var env = CelEnv.NewBuilder()
            .SetContainer("a.b")
            .Variable("a.b.x", CelTypes.Int)
            .Variable("x", CelTypes.String)
            .Build();
        // Bare 'x' should resolve to a.b.x first.
        Assert.Equal(CelTypes.Int, CheckOk("x", env).Ast!.ResultType);
    }

    [Fact]
    public void Leading_Dot_Bypasses_Container()
    {
        var env = CelEnv.NewBuilder()
            .SetContainer("a.b")
            .Variable("a.b.x", CelTypes.Int)
            .Variable("x", CelTypes.String)
            .Build();
        // Leading-dot forces top-level resolution.
        Assert.Equal(CelTypes.String, CheckOk(".x", env).Ast!.ResultType);
    }

    [Fact]
    public void Method_Style_Call_Picks_Instance_Overload()
    {
        Assert.Equal(CelTypes.Bool, CheckOk("\"hello\".startsWith(\"h\")").Ast!.ResultType);
        Assert.Equal(CelTypes.Bool, CheckOk("\"hello\".contains(\"ell\")").Ast!.ResultType);
    }

    [Fact]
    public void Type_Function_Returns_Type_Of_Argument()
    {
        var ast = CheckOk("type(1)").Ast!;
        var t = ast.ResultType;
        var typeT = Assert.IsType<TypeType>(t);
        Assert.Equal(CelTypes.Int, typeT.Parameter);
    }

    [Fact]
    public void Object_Field_Selection_Defers_To_Dyn()
    {
        var env = CelEnv.NewBuilder()
            .Variable("u", CelTypes.Object("app.User"))
            .Build();
        // Without a TypeProvider, selects on objects fall back to dyn — runtime validates.
        Assert.Equal(CelTypes.Dyn, CheckOk("u.name", env).Ast!.ResultType);
    }

    [Fact]
    public void Reference_Map_Records_Resolved_Overload_Id()
    {
        var ast = CheckOk("1 + 2").Ast!;
        // The outermost call is `_+_` resolved to add_int_int_int.
        var rootRef = ast.ReferenceMap[ast.Expression.Id];
        Assert.Equal("_+_", rootRef.Name);
        Assert.Equal("add_int_int_int", rootRef.OverloadId);
    }
}
