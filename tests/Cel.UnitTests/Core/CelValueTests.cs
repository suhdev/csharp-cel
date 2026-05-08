using System.Collections.Immutable;
using Cel.Diagnostics;
using Cel.Types;
using Cel.Values;

namespace Cel.UnitTests.Core;

public sealed class CelValueTests
{
    [Fact]
    public void Bool_Singletons_Are_Reference_Equal()
    {
        Assert.Same(CelValue.True, CelValue.Of(true));
        Assert.Same(CelValue.False, CelValue.Of(false));
    }

    [Fact]
    public void Null_Singleton_Is_Reference_Equal()
    {
        Assert.Same(CelValue.Null, NullValue.Instance);
        Assert.Equal(CelTypes.Null, CelValue.Null.Type);
    }

    [Fact]
    public void List_Equality_Is_Structural()
    {
        var a = new ListValue([CelValue.Of(1L), CelValue.Of(2L)]);
        var b = new ListValue([CelValue.Of(1L), CelValue.Of(2L)]);
        var c = new ListValue([CelValue.Of(1L), CelValue.Of(3L)]);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Bytes_Equality_Compares_Content()
    {
        var a = new BytesValue([1, 2, 3]);
        var b = new BytesValue([1, 2, 3]);
        var c = new BytesValue([1, 2, 4]);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Error_To_Clr_Object_Throws_With_Original_Message()
    {
        var err = new ErrorValue("division by zero");
        var ex = Assert.Throws<CelEvaluationException>(() => err.ToClrObject());
        Assert.Equal("division by zero", ex.Message);
    }

    [Fact]
    public void Optional_None_And_Of_Behave_As_Spec()
    {
        Assert.False(OptionalValue.None.HasValue);
        var some = OptionalValue.Of(CelValue.Of("x"));
        Assert.True(some.HasValue);
        Assert.Equal("x", ((StringValue)some.Inner!).Value);
    }

    [Fact]
    public void Map_Lookup_Uses_Cel_Value_Equality()
    {
        var dict = ImmutableDictionary.CreateBuilder<CelValue, CelValue>();
        dict.Add(CelValue.Of("k"), CelValue.Of(42L));
        var m = new MapValue(dict.ToImmutable());
        Assert.True(m.Entries.ContainsKey(CelValue.Of("k")));
    }
}
