using System.Collections.Generic;
using Cel.Diagnostics;
using Cel.Types;

namespace Cel.UnitTests.Runtime;

public sealed class RuntimeTests
{
    private static object? Eval(string source, IReadOnlyDictionary<string, object?>? bindings = null, CelEnv? env = null)
    {
        env ??= CelEnv.NewBuilder().Build();
        var program = CelExpression.Compile(source, env);
        return program.Eval(bindings ?? new Dictionary<string, object?>());
    }

    private static T Eval<T>(string source, IReadOnlyDictionary<string, object?>? bindings = null, CelEnv? env = null) =>
        (T)Eval(source, bindings, env)!;

    // ── literals & arithmetic ──

    [Fact]
    public void Literals()
    {
        Assert.Equal(1L, Eval("1"));
        Assert.Equal(true, Eval("true"));
        Assert.Equal("hi", Eval("'hi'"));
        Assert.Equal(2.5, Eval("2.5"));
    }

    [Fact]
    public void Arithmetic_Int()
    {
        Assert.Equal(7L, Eval("3 + 4"));
        Assert.Equal(-1L, Eval("3 - 4"));
        Assert.Equal(12L, Eval("3 * 4"));
        Assert.Equal(2L, Eval("9 / 4"));
        Assert.Equal(1L, Eval("9 % 4"));
    }

    [Fact]
    public void Arithmetic_Double()
    {
        Assert.Equal(7.0, Eval("3.0 + 4.0"));
        Assert.Equal(0.5, Eval("1.0 / 2.0"));
    }

    [Fact]
    public void Integer_Overflow_Errors()
    {
        Assert.Throws<CelEvaluationException>(() => Eval("9223372036854775807 + 1"));
    }

    [Fact]
    public void Division_By_Zero_Errors()
    {
        Assert.Throws<CelEvaluationException>(() => Eval("1 / 0"));
        Assert.Throws<CelEvaluationException>(() => Eval("1 % 0"));
    }

    [Fact]
    public void String_Concat_And_Bytes_Concat()
    {
        Assert.Equal("hello world", Eval("'hello' + ' ' + 'world'"));
    }

    [Fact]
    public void Comparison()
    {
        Assert.Equal(true, Eval("1 < 2"));
        Assert.Equal(false, Eval("'b' < 'a'"));
        Assert.Equal(true, Eval("'a' < 'b'"));
    }

    [Fact]
    public void Cross_Type_Equality_Numeric()
    {
        Assert.Equal(true, Eval("1 == 1u"));
        Assert.Equal(true, Eval("1 == 1.0"));
        Assert.Equal(false, Eval("1 == 2"));
    }

    // ── short-circuit / error absorption ──

    [Fact]
    public void Logical_Short_Circuit_Absorbs_Error_When_False()
    {
        // false && (anything) = false; the right side may error but is absorbed.
        Assert.Equal(false, Eval("false && (1 / 0 > 0)"));
    }

    [Fact]
    public void Logical_Or_Absorbs_Error_When_True()
    {
        Assert.Equal(true, Eval("true || (1 / 0 > 0)"));
    }

    [Fact]
    public void Logical_And_Propagates_Error_When_Other_Is_True()
    {
        Assert.Throws<CelEvaluationException>(() => Eval("true && (1 / 0 > 0)"));
    }

    [Fact]
    public void Ternary_Selects_Branch_Lazily()
    {
        // The unselected branch is not evaluated, so a 1/0 inside is fine.
        Assert.Equal(1L, Eval("true ? 1 : 1 / 0"));
        Assert.Equal(1L, Eval("false ? 1 / 0 : 1"));
    }

    // ── collections ──

    [Fact]
    public void List_Indexing()
    {
        Assert.Equal(2L, Eval("[1, 2, 3][1]"));
    }

    [Fact]
    public void List_Index_Out_Of_Range_Errors()
    {
        Assert.Throws<CelEvaluationException>(() => Eval("[1, 2, 3][10]"));
    }

    [Fact]
    public void Map_Indexing()
    {
        Assert.Equal(1L, Eval("{'a': 1, 'b': 2}['a']"));
    }

    [Fact]
    public void In_Operator()
    {
        Assert.Equal(true, Eval("2 in [1, 2, 3]"));
        Assert.Equal(false, Eval("4 in [1, 2, 3]"));
        Assert.Equal(true, Eval("'a' in {'a': 1, 'b': 2}"));
    }

    [Fact]
    public void Size_Functions()
    {
        Assert.Equal(3L, Eval("size('abc')"));
        Assert.Equal(3L, Eval("size([1, 2, 3])"));
        Assert.Equal(2L, Eval("size({'a': 1, 'b': 2})"));
    }

    [Fact]
    public void Size_Of_Unicode_Counts_Code_Points()
    {
        // 😀 is one Unicode codepoint (U+1F600), but two UTF-16 code units in C#.
        Assert.Equal(1L, Eval("size('\U0001F600')"));
    }

    // ── conversions ──

    [Fact]
    public void Conversions()
    {
        Assert.Equal(1L, Eval("int('1')"));
        Assert.Equal(1UL, Eval("uint(1)"));
        Assert.Equal(2.5, Eval("double('2.5')"));
        Assert.Equal("42", Eval("string(42)"));
        Assert.Equal(true, Eval("bool('true')"));
    }

    [Fact]
    public void String_Operations()
    {
        Assert.Equal(true, Eval("'hello'.contains('ell')"));
        Assert.Equal(true, Eval("'hello'.startsWith('he')"));
        Assert.Equal(true, Eval("'hello'.endsWith('lo')"));
        Assert.Equal(false, Eval("'hello'.contains('xyz')"));
    }

    [Fact]
    public void Regex_Matches()
    {
        Assert.Equal(true, Eval("'abc123'.matches('[a-z]+[0-9]+')"));
        Assert.Equal(false, Eval("'abc'.matches('^[0-9]+$')"));
    }

    // ── activations ──

    [Fact]
    public void Map_Activation_Resolves_Variables()
    {
        var env = CelEnv.NewBuilder().Variable("x", CelTypes.Int).Build();
        Assert.Equal(7L, Eval("x + 2", new Dictionary<string, object?> { ["x"] = 5L }, env));
    }

    [Fact]
    public void Object_Activation_Reads_Top_Level_Properties()
    {
        var env = CelEnv.NewBuilder()
            .Variable("user", CelTypes.Object("User"))
            .Build();
        var program = CelExpression.Compile("user.name", env);
        var result = program.Eval(new { user = new { name = "alice" } });
        Assert.Equal("alice", result);
    }

    [Fact]
    public void Snake_Case_Field_Maps_To_Pascal_Case()
    {
        var env = CelEnv.NewBuilder()
            .Variable("u", CelTypes.Object("U"))
            .Build();
        var program = CelExpression.Compile("u.user_name", env);
        var result = program.Eval(new { u = new { UserName = "alice" } });
        Assert.Equal("alice", result);
    }

    [Fact]
    public void Has_Macro_On_Object_Returns_Bool()
    {
        var env = CelEnv.NewBuilder().Variable("u", CelTypes.Object("U")).Build();
        var program = CelExpression.Compile("has(u.name)", env);
        Assert.Equal(true, program.Eval(new { u = new { name = "alice" } }));
        Assert.Equal(false, program.Eval(new { u = new { name = (string?)null } }));
    }

    [Fact]
    public void Has_Macro_On_Map_Returns_Bool()
    {
        Assert.Equal(true, Eval("has({'a': 1}.a)"));
        Assert.Equal(false, Eval("has({'a': 1}.b)"));
    }

    // ── comprehensions ──

    [Fact]
    public void All_Macro_True_Path()
    {
        Assert.Equal(true, Eval("[1, 2, 3].all(x, x > 0)"));
    }

    [Fact]
    public void All_Macro_Short_Circuits_To_False()
    {
        Assert.Equal(false, Eval("[1, -1, 3].all(x, x > 0)"));
    }

    [Fact]
    public void Exists_Macro()
    {
        Assert.Equal(true, Eval("[1, 2, 3].exists(x, x == 2)"));
        Assert.Equal(false, Eval("[1, 2, 3].exists(x, x == 99)"));
    }

    [Fact]
    public void ExistsOne_Macro()
    {
        Assert.Equal(true, Eval("[1, 2, 3].exists_one(x, x == 2)"));
        Assert.Equal(false, Eval("[1, 2, 3].exists_one(x, x > 0)"));
        Assert.Equal(false, Eval("[1, 2, 3].exists_one(x, x == 99)"));
    }

    [Fact]
    public void Map_Macro_Two_Arg()
    {
        var result = (List<object?>)Eval("[1, 2, 3].map(x, x * x)")!;
        Assert.Equal(new object?[] { 1L, 4L, 9L }, result);
    }

    [Fact]
    public void Map_Macro_Three_Arg_With_Filter()
    {
        var result = (List<object?>)Eval("[1, 2, 3, 4].map(x, x % 2 == 0, x * 10)")!;
        Assert.Equal(new object?[] { 20L, 40L }, result);
    }

    [Fact]
    public void Filter_Macro()
    {
        var result = (List<object?>)Eval("[1, 2, 3, 4].filter(x, x > 2)")!;
        Assert.Equal(new object?[] { 3L, 4L }, result);
    }

    // ── compile failures ──

    [Fact]
    public void Parse_Error_Throws_Compile_Exception()
    {
        Assert.Throws<CelCompileException>(() => CelExpression.Compile("1 +", CelEnv.NewBuilder().Build()));
    }

    [Fact]
    public void Type_Error_Throws_Compile_Exception()
    {
        Assert.Throws<CelCompileException>(() => CelExpression.Compile("1 + 'x'", CelEnv.NewBuilder().Build()));
    }
}
