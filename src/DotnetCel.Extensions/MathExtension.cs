using DotnetCel.Types;
using DotnetCel.Values;

namespace DotnetCel.Extensions;

/// <summary>
/// Port of cel-go's <c>ext/math</c> library: <c>math.greatest</c>, <c>math.least</c>,
/// <c>math.ceil</c>, <c>math.floor</c>, <c>math.round</c>, <c>math.trunc</c>, <c>math.abs</c>,
/// <c>math.sign</c>, <c>math.isNaN</c>, <c>math.isInf</c>, <c>math.isFinite</c>, <c>math.sqrt</c>,
/// and bit-fiddling helpers (<c>math.bitAnd</c>, <c>math.bitOr</c>, etc.). All exposed under the
/// <c>math</c> namespace; the checker resolves <c>math.fn(x)</c> via namespaced-call dispatch.
/// </summary>
public sealed class MathExtension : ICelExtension
{
    public static readonly MathExtension Instance = new();
    private MathExtension() { }

    public void ConfigureEnv(CelEnv.Builder b)
    {
        // greatest / least: 1-5 arg parametric variants + list form. Cross-numeric mixes widen
        // A to dyn via the checker's MostGeneral; runtime uses CelEquality.Compare to order.
        var A = CelTypes.TypeParam("A");
        foreach (var name in new[] { "math.greatest", "math.least" })
        {
            var prefix = name == "math.greatest" ? "greatest_" : "least_";
            // Order matters: the list form is more specific than the 1-arg parametric, so it
            // must come first or the checker (which picks the first match) will treat
            // `math.greatest([...])` as the 1-arg identity.
            b.Function(name,
                new OverloadDecl(prefix + "list", [CelTypes.List(A)], A, TypeParams: ["A"]),
                new OverloadDecl(prefix + "1", [A], A, TypeParams: ["A"]),
                new OverloadDecl(prefix + "2", [A, A], A, TypeParams: ["A"]),
                new OverloadDecl(prefix + "3", [A, A, A], A, TypeParams: ["A"]),
                new OverloadDecl(prefix + "4", [A, A, A, A], A, TypeParams: ["A"]),
                new OverloadDecl(prefix + "5", [A, A, A, A, A], A, TypeParams: ["A"]));
        }

        b.Function("math.ceil", DoubleUnary("math_ceil"));
        b.Function("math.floor", DoubleUnary("math_floor"));
        b.Function("math.round", DoubleUnary("math_round"));
        b.Function("math.trunc", DoubleUnary("math_trunc"));
        b.Function("math.sqrt", DoubleUnary("math_sqrt"));

        b.Function("math.abs",
            new OverloadDecl("math_abs_int", [CelTypes.Int], CelTypes.Int),
            new OverloadDecl("math_abs_uint", [CelTypes.Uint], CelTypes.Uint),
            new OverloadDecl("math_abs_double", [CelTypes.Double], CelTypes.Double));

        b.Function("math.sign",
            new OverloadDecl("math_sign_int", [CelTypes.Int], CelTypes.Int),
            new OverloadDecl("math_sign_uint", [CelTypes.Uint], CelTypes.Uint),
            new OverloadDecl("math_sign_double", [CelTypes.Double], CelTypes.Double));

        b.Function("math.isNaN", new OverloadDecl("math_is_nan", [CelTypes.Double], CelTypes.Bool));
        b.Function("math.isInf", new OverloadDecl("math_is_inf", [CelTypes.Double], CelTypes.Bool));
        b.Function("math.isFinite", new OverloadDecl("math_is_finite", [CelTypes.Double], CelTypes.Bool));

        b.Function("math.bitAnd",
            new OverloadDecl("math_bit_and_int", [CelTypes.Int, CelTypes.Int], CelTypes.Int),
            new OverloadDecl("math_bit_and_uint", [CelTypes.Uint, CelTypes.Uint], CelTypes.Uint));
        b.Function("math.bitOr",
            new OverloadDecl("math_bit_or_int", [CelTypes.Int, CelTypes.Int], CelTypes.Int),
            new OverloadDecl("math_bit_or_uint", [CelTypes.Uint, CelTypes.Uint], CelTypes.Uint));
        b.Function("math.bitXor",
            new OverloadDecl("math_bit_xor_int", [CelTypes.Int, CelTypes.Int], CelTypes.Int),
            new OverloadDecl("math_bit_xor_uint", [CelTypes.Uint, CelTypes.Uint], CelTypes.Uint));
        b.Function("math.bitNot",
            new OverloadDecl("math_bit_not_int", [CelTypes.Int], CelTypes.Int),
            new OverloadDecl("math_bit_not_uint", [CelTypes.Uint], CelTypes.Uint));
        b.Function("math.bitShiftLeft",
            new OverloadDecl("math_bit_shl_int", [CelTypes.Int, CelTypes.Int], CelTypes.Int),
            new OverloadDecl("math_bit_shl_uint", [CelTypes.Uint, CelTypes.Int], CelTypes.Uint));
        b.Function("math.bitShiftRight",
            new OverloadDecl("math_bit_shr_int", [CelTypes.Int, CelTypes.Int], CelTypes.Int),
            new OverloadDecl("math_bit_shr_uint", [CelTypes.Uint, CelTypes.Int], CelTypes.Uint));
    }

    public void ConfigureRuntime(Action<string, OverloadFn> bind)
    {
        OverloadFn maxArgs = static a =>
        {
            var best = a[0];
            for (var i = 1; i < a.Length; i++)
            {
                if (Runtime.CelEquality.Compare(a[i], best) > 0) { best = a[i]; }
            }
            return best;
        };
        OverloadFn minArgs = static a =>
        {
            var best = a[0];
            for (var i = 1; i < a.Length; i++)
            {
                if (Runtime.CelEquality.Compare(a[i], best) < 0) { best = a[i]; }
            }
            return best;
        };
        OverloadFn maxList = static a => ReduceVariadic((ListValue)a[0], static (x, y) =>
            Runtime.CelEquality.Compare(x, y) >= 0 ? x : y);
        OverloadFn minList = static a => ReduceVariadic((ListValue)a[0], static (x, y) =>
            Runtime.CelEquality.Compare(x, y) <= 0 ? x : y);

        for (var n = 1; n <= 5; n++)
        {
            bind($"greatest_{n}", maxArgs);
            bind($"least_{n}", minArgs);
        }
        bind("greatest_list", maxList);
        bind("least_list", minList);

        bind("math_ceil", static a => CelValue.Of(Math.Ceiling(D(a[0]))));
        bind("math_floor", static a => CelValue.Of(Math.Floor(D(a[0]))));
        bind("math_round", static a => CelValue.Of(Math.Round(D(a[0]), MidpointRounding.AwayFromZero)));
        bind("math_trunc", static a => CelValue.Of(Math.Truncate(D(a[0]))));
        bind("math_sqrt", static a => CelValue.Of(Math.Sqrt(D(a[0]))));

        bind("math_abs_int", static a =>
        {
            var v = I(a[0]);
            if (v == long.MinValue) { return CelValue.Error("integer overflow"); }
            return CelValue.Of(Math.Abs(v));
        });
        bind("math_abs_uint", static a => a[0]);
        bind("math_abs_double", static a => CelValue.Of(Math.Abs(D(a[0]))));

        bind("math_sign_int", static a => CelValue.Of((long)Math.Sign(I(a[0]))));
        bind("math_sign_uint", static a => CelValue.Of(U(a[0]) == 0UL ? 0UL : 1UL));
        bind("math_sign_double", static a =>
        {
            var v = D(a[0]);
            return CelValue.Of(double.IsNaN(v) ? double.NaN : Math.Sign(v));
        });

        bind("math_is_nan", static a => CelValue.Of(double.IsNaN(D(a[0]))));
        bind("math_is_inf", static a => CelValue.Of(double.IsInfinity(D(a[0]))));
        bind("math_is_finite", static a => CelValue.Of(double.IsFinite(D(a[0]))));

        bind("math_bit_and_int", static a => CelValue.Of(I(a[0]) & I(a[1])));
        bind("math_bit_and_uint", static a => CelValue.Of(U(a[0]) & U(a[1])));
        bind("math_bit_or_int", static a => CelValue.Of(I(a[0]) | I(a[1])));
        bind("math_bit_or_uint", static a => CelValue.Of(U(a[0]) | U(a[1])));
        bind("math_bit_xor_int", static a => CelValue.Of(I(a[0]) ^ I(a[1])));
        bind("math_bit_xor_uint", static a => CelValue.Of(U(a[0]) ^ U(a[1])));
        bind("math_bit_not_int", static a => CelValue.Of(~I(a[0])));
        bind("math_bit_not_uint", static a => CelValue.Of(~U(a[0])));
        bind("math_bit_shl_int", ShiftLeftInt);
        bind("math_bit_shl_uint", ShiftLeftUint);
        bind("math_bit_shr_int", ShiftRightInt);
        bind("math_bit_shr_uint", ShiftRightUint);
    }

    private static OverloadDecl DoubleUnary(string id) =>
        new(id, [CelTypes.Double], CelTypes.Double);

    private static long I(CelValue v) => ((IntValue)v).Value;
    private static ulong U(CelValue v) => ((UintValue)v).Value;
    private static double D(CelValue v) => ((DoubleValue)v).Value;

    private static CelValue ReduceVariadic(ListValue list, Func<CelValue, CelValue, CelValue> fold)
    {
        if (list.Elements.IsDefaultOrEmpty)
        {
            return CelValue.Error("empty list");
        }
        var acc = list.Elements[0];
        for (var i = 1; i < list.Elements.Length; i++)
        {
            acc = fold(acc, list.Elements[i]);
        }
        return acc;
    }

    private static CelValue ShiftLeftInt(ReadOnlySpan<CelValue> a)
    {
        var n = I(a[1]);
        if (n < 0 || n >= 64) { return CelValue.Error($"shift out of range: {n}"); }
        return CelValue.Of(I(a[0]) << (int)n);
    }

    private static CelValue ShiftLeftUint(ReadOnlySpan<CelValue> a)
    {
        var n = I(a[1]);
        if (n < 0 || n >= 64) { return CelValue.Error($"shift out of range: {n}"); }
        return CelValue.Of(U(a[0]) << (int)n);
    }

    private static CelValue ShiftRightInt(ReadOnlySpan<CelValue> a)
    {
        var n = I(a[1]);
        if (n < 0 || n >= 64) { return CelValue.Error($"shift out of range: {n}"); }
        return CelValue.Of(I(a[0]) >> (int)n);
    }

    private static CelValue ShiftRightUint(ReadOnlySpan<CelValue> a)
    {
        var n = I(a[1]);
        if (n < 0 || n >= 64) { return CelValue.Error($"shift out of range: {n}"); }
        return CelValue.Of(U(a[0]) >> (int)n);
    }
}
