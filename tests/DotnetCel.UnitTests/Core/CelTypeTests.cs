using DotnetCel.Types;

namespace DotnetCel.UnitTests.Core;

public sealed class CelTypeTests
{
    [Fact]
    public void Primitives_Have_Spec_Names()
    {
        Assert.Equal("bool", CelTypes.Bool.Name);
        Assert.Equal("int", CelTypes.Int.Name);
        Assert.Equal("uint", CelTypes.Uint.Name);
        Assert.Equal("double", CelTypes.Double.Name);
        Assert.Equal("string", CelTypes.String.Name);
        Assert.Equal("bytes", CelTypes.Bytes.Name);
        Assert.Equal("null_type", CelTypes.Null.Name);
        Assert.Equal("dyn", CelTypes.Dyn.Name);
    }

    [Fact]
    public void List_And_Map_Render_Canonically()
    {
        Assert.Equal("list(int)", CelTypes.List(CelTypes.Int).Name);
        Assert.Equal("map(string, int)", CelTypes.Map(CelTypes.String, CelTypes.Int).Name);
        Assert.Equal("list(map(string, dyn))",
            CelTypes.List(CelTypes.Map(CelTypes.String, CelTypes.Dyn)).Name);
    }

    [Fact]
    public void Equal_Types_Are_Structurally_Equal()
    {
        Assert.Equal(CelTypes.List(CelTypes.Int), CelTypes.List(CelTypes.Int));
        Assert.NotEqual(CelTypes.List(CelTypes.Int), CelTypes.List(CelTypes.String));
    }

    [Fact]
    public void Object_With_Type_Args_Renders_Generically()
    {
        var t = CelTypes.Object("my.Pair", CelTypes.Int, CelTypes.String);
        Assert.Equal("my.Pair(int, string)", t.Name);
    }
}
