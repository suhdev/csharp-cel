using System.Collections.Generic;
using Cel.Extensions;
using Cel.Types;

namespace Cel.UnitTests.Extensions;

public sealed class ExtensionsTests
{
    private static CelEnv WithStrings() => CelEnv.NewBuilder().Use(StringsExtension.Instance).Build();
    private static CelEnv WithMath() => CelEnv.NewBuilder().Use(MathExtension.Instance).Build();
    private static CelEnv WithEncoders() => CelEnv.NewBuilder().Use(EncodersExtension.Instance).Build();
    private static CelEnv WithSets() => CelEnv.NewBuilder().Use(SetsExtension.Instance).Build();

    private static object? Eval(string source, CelEnv env) =>
        CelExpression.Compile(source, env).Eval((IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>());

    // ── strings ──

    [Fact]
    public void Strings_CharAt()
    {
        Assert.Equal("e", Eval("'hello'.charAt(1)", WithStrings()));
        Assert.Equal("", Eval("'hello'.charAt(5)", WithStrings()));
        Assert.Equal("\U0001F600", Eval("'\U0001F600x'.charAt(0)", WithStrings()));
    }

    [Fact]
    public void Strings_IndexOf_And_LastIndexOf()
    {
        Assert.Equal(2L, Eval("'hello'.indexOf('ll')", WithStrings()));
        Assert.Equal(-1L, Eval("'hello'.indexOf('z')", WithStrings()));
        Assert.Equal(2L, Eval("'hello'.indexOf('l')", WithStrings()));
        Assert.Equal(3L, Eval("'hello'.lastIndexOf('l')", WithStrings()));
    }

    [Fact]
    public void Strings_Lower_Upper_Ascii()
    {
        Assert.Equal("hello", Eval("'HELLO'.lowerAscii()", WithStrings()));
        Assert.Equal("HELLO", Eval("'hello'.upperAscii()", WithStrings()));
    }

    [Fact]
    public void Strings_Replace()
    {
        Assert.Equal("hxllo", Eval("'hello'.replace('e', 'x')", WithStrings()));
        Assert.Equal("hexxo", Eval("'hello'.replace('l', 'x', 2)", WithStrings()));
        Assert.Equal("hexlo", Eval("'hello'.replace('l', 'x', 1)", WithStrings()));
        Assert.Equal("hello", Eval("'hello'.replace('l', 'x', 0)", WithStrings()));
    }

    [Fact]
    public void Strings_Split()
    {
        Assert.Equal(new object?[] { "a", "b", "c" }, Eval("'a,b,c'.split(',')", WithStrings()));
        Assert.Equal(new object?[] { "a", "b,c" }, Eval("'a,b,c'.split(',', 2)", WithStrings()));
    }

    [Fact]
    public void Strings_Substring_And_Trim()
    {
        Assert.Equal("ell", Eval("'hello'.substring(1, 4)", WithStrings()));
        Assert.Equal("hello", Eval("'  hello  '.trim()", WithStrings()));
    }

    [Fact]
    public void Strings_Join_And_Reverse()
    {
        Assert.Equal("a,b,c", Eval("['a', 'b', 'c'].join(',')", WithStrings()));
        Assert.Equal("abc", Eval("['a', 'b', 'c'].join()", WithStrings()));
        Assert.Equal("olleh", Eval("'hello'.reverse()", WithStrings()));
        Assert.Equal("\U0001F600x", Eval("'x\U0001F600'.reverse()", WithStrings()));
    }

    // ── math ──

    [Fact]
    public void Math_Greatest_Least()
    {
        Assert.Equal(7L, Eval("math.greatest(3, 7)", WithMath()));
        Assert.Equal(3L, Eval("math.least(3, 7)", WithMath()));
        Assert.Equal(9L, Eval("math.greatest([1, 9, 3])", WithMath()));
        Assert.Equal(1.5, Eval("math.least([1.5, 7.5])", WithMath()));
    }

    [Fact]
    public void Math_Rounding()
    {
        Assert.Equal(3.0, Eval("math.ceil(2.3)", WithMath()));
        Assert.Equal(2.0, Eval("math.floor(2.9)", WithMath()));
        Assert.Equal(3.0, Eval("math.round(2.5)", WithMath()));
        Assert.Equal(2.0, Eval("math.trunc(2.9)", WithMath()));
    }

    [Fact]
    public void Math_Abs_Sign()
    {
        Assert.Equal(5L, Eval("math.abs(-5)", WithMath()));
        Assert.Equal(2.5, Eval("math.abs(-2.5)", WithMath()));
        Assert.Equal(-1L, Eval("math.sign(-7)", WithMath()));
        Assert.Equal(0L, Eval("math.sign(0)", WithMath()));
        Assert.Equal(1L, Eval("math.sign(7)", WithMath()));
    }

    [Fact]
    public void Math_NaN_Inf_Finite()
    {
        Assert.Equal(true, Eval("math.isNaN(0.0 / 0.0)", WithMath()));
        Assert.Equal(false, Eval("math.isNaN(1.0)", WithMath()));
        Assert.Equal(true, Eval("math.isInf(1.0 / 0.0)", WithMath()));
        Assert.Equal(true, Eval("math.isFinite(1.5)", WithMath()));
        Assert.Equal(false, Eval("math.isFinite(1.0 / 0.0)", WithMath()));
    }

    [Fact]
    public void Math_Bit_Operations()
    {
        Assert.Equal(0xF0L, Eval("math.bitAnd(0xFF, 0xF0)", WithMath()));
        Assert.Equal(0xFFL, Eval("math.bitOr(0xF0, 0x0F)", WithMath()));
        Assert.Equal(0xF0L, Eval("math.bitXor(0xFF, 0x0F)", WithMath()));
        Assert.Equal(unchecked(~0xF0L), Eval("math.bitNot(0xF0)", WithMath()));
        Assert.Equal(8L, Eval("math.bitShiftLeft(1, 3)", WithMath()));
        Assert.Equal(1L, Eval("math.bitShiftRight(8, 3)", WithMath()));
    }

    [Fact]
    public void Math_Sqrt()
    {
        Assert.Equal(2.0, Eval("math.sqrt(4.0)", WithMath()));
    }

    // ── encoders ──

    [Fact]
    public void Base64_RoundTrip()
    {
        Assert.Equal("aGVsbG8=", Eval("base64.encode(b'hello')", WithEncoders()));
        var decoded = (byte[])Eval("base64.decode('aGVsbG8=')", WithEncoders())!;
        Assert.Equal((byte[])[0x68, 0x65, 0x6C, 0x6C, 0x6F], decoded);
    }

    [Fact]
    public void Base64_Decode_Invalid_Errors()
    {
        Assert.Throws<Diagnostics.CelEvaluationException>(
            () => Eval("base64.decode('!!!')", WithEncoders()));
    }

    // ── sets ──

    [Fact]
    public void Sets_Contains()
    {
        Assert.Equal(true, Eval("sets.contains([1, 2, 3], [2])", WithSets()));
        Assert.Equal(true, Eval("sets.contains([1, 2, 3], [1, 3])", WithSets()));
        Assert.Equal(false, Eval("sets.contains([1, 2], [1, 3])", WithSets()));
    }

    [Fact]
    public void Sets_Equivalent_And_Intersects()
    {
        Assert.Equal(true, Eval("sets.equivalent([1, 2], [2, 1])", WithSets()));
        Assert.Equal(false, Eval("sets.equivalent([1, 2], [1])", WithSets()));
        Assert.Equal(true, Eval("sets.intersects([1, 2, 3], [3, 4])", WithSets()));
        Assert.Equal(false, Eval("sets.intersects([1, 2], [3, 4])", WithSets()));
    }

    [Fact]
    public void Sets_Use_Cel_Equality_Across_Numeric_Types()
    {
        Assert.Equal(true, Eval("sets.contains([1u, 2u], [1])", WithSets()));
    }

    // ── compose multiple extensions ──

    [Fact]
    public void Multiple_Extensions_Compose()
    {
        var env = CelEnv.NewBuilder()
            .Use(StringsExtension.Instance)
            .Use(MathExtension.Instance)
            .Build();
        Assert.Equal(true, Eval("'hello'.upperAscii() == 'HELLO' && math.abs(-1) == 1", env));
    }
}
