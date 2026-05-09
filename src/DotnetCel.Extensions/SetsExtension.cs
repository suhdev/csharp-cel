using DotnetCel.Runtime;
using DotnetCel.Types;
using DotnetCel.Values;

namespace DotnetCel.Extensions;

/// <summary>
/// Port of cel-go's <c>ext/sets</c>: <c>sets.contains(haystack, needle)</c>,
/// <c>sets.equivalent(a, b)</c>, <c>sets.intersects(a, b)</c>. All operate on lists and use CEL
/// equality (so <c>sets.contains([1u], [1])</c> is true).
/// </summary>
public sealed class SetsExtension : ICelExtension
{
    public static readonly SetsExtension Instance = new();
    private SetsExtension() { }

    public void ConfigureEnv(CelEnv.Builder b)
    {
        var listA = CelTypes.List(CelTypes.TypeParam("A"));
        var listB = CelTypes.List(CelTypes.TypeParam("B"));

        b.Function("sets.contains",
            new OverloadDecl("sets_contains_list_list", [listA, listB], CelTypes.Bool, TypeParams: ["A", "B"]));
        b.Function("sets.equivalent",
            new OverloadDecl("sets_equivalent_list_list", [listA, listB], CelTypes.Bool, TypeParams: ["A", "B"]));
        b.Function("sets.intersects",
            new OverloadDecl("sets_intersects_list_list", [listA, listB], CelTypes.Bool, TypeParams: ["A", "B"]));
    }

    public void ConfigureRuntime(Action<string, OverloadFn> bind)
    {
        bind("sets_contains_list_list", static a =>
        {
            var haystack = (ListValue)a[0];
            var needle = (ListValue)a[1];
            foreach (var n in needle.Elements)
            {
                if (!Contains(haystack, n))
                {
                    return CelValue.False;
                }
            }
            return CelValue.True;
        });

        bind("sets_equivalent_list_list", static a =>
        {
            var x = (ListValue)a[0];
            var y = (ListValue)a[1];
            return CelValue.Of(IsSubset(x, y) && IsSubset(y, x));
        });

        bind("sets_intersects_list_list", static a =>
        {
            var x = (ListValue)a[0];
            var y = (ListValue)a[1];
            foreach (var e in x.Elements)
            {
                if (Contains(y, e))
                {
                    return CelValue.True;
                }
            }
            return CelValue.False;
        });
    }

    private static bool Contains(ListValue list, CelValue item)
    {
        foreach (var e in list.Elements)
        {
            if (CelEquality.Equals(e, item))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSubset(ListValue subset, ListValue superset)
    {
        foreach (var e in subset.Elements)
        {
            if (!Contains(superset, e))
            {
                return false;
            }
        }
        return true;
    }
}
