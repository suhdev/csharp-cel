using System.Collections.Immutable;
using Cel.Types;

namespace Cel;

/// <summary>
/// Built-in declarations registered into <see cref="CelEnv"/> by default. Mirrors cel-go's
/// <c>common/stdlib/standard.go</c> in shape; this is an initial subset large enough to type-check
/// the bulk of CEL expressions: every operator overload arm, core conversions, <c>size</c>,
/// <c>type</c>, and the synthetic helpers used by macro expansion. Higher-level surface — string
/// methods, regex, timestamp accessors — will be added as Phase 7 lands.
/// </summary>
internal static class Stdlib
{
    public static void Apply(CelEnv.Builder builder)
    {
        // ── arithmetic ──
        builder.Function("_+_",
            Bin("add_int_int_int", CelTypes.Int, CelTypes.Int, CelTypes.Int),
            Bin("add_uint_uint_uint", CelTypes.Uint, CelTypes.Uint, CelTypes.Uint),
            Bin("add_double_double_double", CelTypes.Double, CelTypes.Double, CelTypes.Double),
            Bin("add_string_string_string", CelTypes.String, CelTypes.String, CelTypes.String),
            Bin("add_bytes_bytes_bytes", CelTypes.Bytes, CelTypes.Bytes, CelTypes.Bytes),
            Bin("add_list_A_list_A", CelTypes.List(A), CelTypes.List(A), CelTypes.List(A), TypeParams: ["A"]),
            Bin("add_timestamp_duration_timestamp", CelTypes.Timestamp, CelTypes.Duration, CelTypes.Timestamp),
            Bin("add_duration_timestamp_timestamp", CelTypes.Duration, CelTypes.Timestamp, CelTypes.Timestamp),
            Bin("add_duration_duration_duration", CelTypes.Duration, CelTypes.Duration, CelTypes.Duration));

        builder.Function("_-_",
            Bin("subtract_int_int_int", CelTypes.Int, CelTypes.Int, CelTypes.Int),
            Bin("subtract_uint_uint_uint", CelTypes.Uint, CelTypes.Uint, CelTypes.Uint),
            Bin("subtract_double_double_double", CelTypes.Double, CelTypes.Double, CelTypes.Double),
            Bin("subtract_timestamp_timestamp_duration", CelTypes.Timestamp, CelTypes.Timestamp, CelTypes.Duration),
            Bin("subtract_timestamp_duration_timestamp", CelTypes.Timestamp, CelTypes.Duration, CelTypes.Timestamp),
            Bin("subtract_duration_duration_duration", CelTypes.Duration, CelTypes.Duration, CelTypes.Duration));

        builder.Function("_*_",
            Bin("multiply_int_int_int", CelTypes.Int, CelTypes.Int, CelTypes.Int),
            Bin("multiply_uint_uint_uint", CelTypes.Uint, CelTypes.Uint, CelTypes.Uint),
            Bin("multiply_double_double_double", CelTypes.Double, CelTypes.Double, CelTypes.Double));

        builder.Function("_/_",
            Bin("divide_int_int_int", CelTypes.Int, CelTypes.Int, CelTypes.Int),
            Bin("divide_uint_uint_uint", CelTypes.Uint, CelTypes.Uint, CelTypes.Uint),
            Bin("divide_double_double_double", CelTypes.Double, CelTypes.Double, CelTypes.Double));

        builder.Function("_%_",
            Bin("modulo_int_int_int", CelTypes.Int, CelTypes.Int, CelTypes.Int),
            Bin("modulo_uint_uint_uint", CelTypes.Uint, CelTypes.Uint, CelTypes.Uint));

        builder.Function("-_",
            Un("negate_int_int", CelTypes.Int, CelTypes.Int),
            Un("negate_double_double", CelTypes.Double, CelTypes.Double),
            Un("negate_duration_duration", CelTypes.Duration, CelTypes.Duration));

        // ── logical ──
        builder.Function("!_",
            Un("logical_not", CelTypes.Bool, CelTypes.Bool));
        builder.Function("_&&_",
            Bin("logical_and", CelTypes.Bool, CelTypes.Bool, CelTypes.Bool));
        builder.Function("_||_",
            Bin("logical_or", CelTypes.Bool, CelTypes.Bool, CelTypes.Bool));
        builder.Function(Operators.NotStrictlyFalse,
            Un("not_strictly_false", CelTypes.Bool, CelTypes.Bool));

        // ── conditional ──
        builder.Function("_?_:_",
            new OverloadDecl(
                "conditional",
                [CelTypes.Bool, A, A],
                A,
                TypeParams: ["A"]));

        // ── relational (homogeneous) ──
        foreach (var (id, op, type) in new[]
        {
            ("less", "_<_", (CelType)CelTypes.Int), ("less_uint", "_<_", CelTypes.Uint),
            ("less_double", "_<_", CelTypes.Double), ("less_string", "_<_", CelTypes.String),
            ("less_bytes", "_<_", CelTypes.Bytes), ("less_timestamp", "_<_", CelTypes.Timestamp),
            ("less_duration", "_<_", CelTypes.Duration),
        })
        {
            // expanded below per-op for clarity
            _ = (id, op, type);
        }
        AddOrdering(builder, "_<_", "less");
        AddOrdering(builder, "_<=_", "less_equals");
        AddOrdering(builder, "_>_", "greater");
        AddOrdering(builder, "_>=_", "greater_equals");

        // == / != are parametric and accept any pair of values (with runtime semantics for
        // cross-numeric comparisons). The checker just needs to accept the combo.
        builder.Function("_==_",
            new OverloadDecl("equals", [A, A], CelTypes.Bool, TypeParams: ["A"]));
        builder.Function("_!=_",
            new OverloadDecl("not_equals", [A, A], CelTypes.Bool, TypeParams: ["A"]));

        // ── containment ──
        builder.Function("@in",
            new OverloadDecl("in_list", [A, CelTypes.List(A)], CelTypes.Bool, TypeParams: ["A"]),
            new OverloadDecl("in_map", [A, CelTypes.Map(A, B)], CelTypes.Bool, TypeParams: ["A", "B"]));

        // ── indexing ──
        builder.Function("_[_]",
            new OverloadDecl("index_list", [CelTypes.List(A), CelTypes.Int], A, TypeParams: ["A"]),
            new OverloadDecl("index_map", [CelTypes.Map(A, B), A], B, TypeParams: ["A", "B"]));

        // Optional select / index produce optional<T>.
        builder.Function(Operators.OptIndex,
            new OverloadDecl("optindex_list", [CelTypes.List(A), CelTypes.Int],
                CelTypes.Optional(A), TypeParams: ["A"]),
            new OverloadDecl("optindex_map", [CelTypes.Map(A, B), A],
                CelTypes.Optional(B), TypeParams: ["A", "B"]));

        // Optional select `e.?field` — the parser emits `Call(null, "_?._", [operand, "field"])`.
        // Two overloads cover the static-typed map case and the gradual ObjectValue/dyn case.
        builder.Function(Operators.OptSelect,
            new OverloadDecl("optselect_map_string",
                [CelTypes.Map(A, B), CelTypes.String], CelTypes.Optional(B), TypeParams: ["A", "B"]),
            new OverloadDecl("optselect_dyn_string",
                [CelTypes.Dyn, CelTypes.String], CelTypes.Optional(CelTypes.Dyn)));

        // optional.of / optional.none / optional.ofNonZeroValue
        builder.Function("optional.of",
            new OverloadDecl("optional_of", [A], CelTypes.Optional(A), TypeParams: ["A"]));
        builder.Function("optional.none",
            new OverloadDecl("optional_none", [], CelTypes.Optional(CelTypes.Dyn)));
        builder.Function("optional.ofNonZeroValue",
            new OverloadDecl("optional_of_non_zero_value", [A], CelTypes.Optional(A), TypeParams: ["A"]));

        // Instance methods on optional<A>.
        builder.Function("hasValue",
            new OverloadDecl("optional_has_value",
                [CelTypes.Optional(A)], CelTypes.Bool, TypeParams: ["A"], IsInstance: true));
        builder.Function("value",
            new OverloadDecl("optional_value",
                [CelTypes.Optional(A)], A, TypeParams: ["A"], IsInstance: true));
        builder.Function("or",
            new OverloadDecl("optional_or",
                [CelTypes.Optional(A), CelTypes.Optional(A)], CelTypes.Optional(A),
                TypeParams: ["A"], IsInstance: true));
        builder.Function("orValue",
            new OverloadDecl("optional_or_value",
                [CelTypes.Optional(A), A], A, TypeParams: ["A"], IsInstance: true));

        // ── size / type / dyn ──
        builder.Function("size",
            new OverloadDecl("size_string", [CelTypes.String], CelTypes.Int),
            new OverloadDecl("size_bytes", [CelTypes.Bytes], CelTypes.Int),
            new OverloadDecl("size_list", [CelTypes.List(A)], CelTypes.Int, TypeParams: ["A"]),
            new OverloadDecl("size_map", [CelTypes.Map(A, B)], CelTypes.Int, TypeParams: ["A", "B"]));

        builder.Function("type",
            new OverloadDecl("type", [A], CelTypes.TypeOf(A), TypeParams: ["A"]));

        builder.Function("dyn",
            new OverloadDecl("to_dyn", [A], CelTypes.Dyn, TypeParams: ["A"]));

        // ── conversions ──
        builder.Function("int",
            new OverloadDecl("int_to_int", [CelTypes.Int], CelTypes.Int),
            new OverloadDecl("uint_to_int", [CelTypes.Uint], CelTypes.Int),
            new OverloadDecl("double_to_int", [CelTypes.Double], CelTypes.Int),
            new OverloadDecl("string_to_int", [CelTypes.String], CelTypes.Int),
            new OverloadDecl("timestamp_to_int", [CelTypes.Timestamp], CelTypes.Int),
            new OverloadDecl("duration_to_int", [CelTypes.Duration], CelTypes.Int));

        builder.Function("uint",
            new OverloadDecl("int_to_uint", [CelTypes.Int], CelTypes.Uint),
            new OverloadDecl("uint_to_uint", [CelTypes.Uint], CelTypes.Uint),
            new OverloadDecl("double_to_uint", [CelTypes.Double], CelTypes.Uint),
            new OverloadDecl("string_to_uint", [CelTypes.String], CelTypes.Uint));

        builder.Function("double",
            new OverloadDecl("int_to_double", [CelTypes.Int], CelTypes.Double),
            new OverloadDecl("uint_to_double", [CelTypes.Uint], CelTypes.Double),
            new OverloadDecl("double_to_double", [CelTypes.Double], CelTypes.Double),
            new OverloadDecl("string_to_double", [CelTypes.String], CelTypes.Double));

        builder.Function("string",
            new OverloadDecl("string_to_string", [CelTypes.String], CelTypes.String),
            new OverloadDecl("int_to_string", [CelTypes.Int], CelTypes.String),
            new OverloadDecl("uint_to_string", [CelTypes.Uint], CelTypes.String),
            new OverloadDecl("double_to_string", [CelTypes.Double], CelTypes.String),
            new OverloadDecl("bool_to_string", [CelTypes.Bool], CelTypes.String),
            new OverloadDecl("bytes_to_string", [CelTypes.Bytes], CelTypes.String),
            new OverloadDecl("timestamp_to_string", [CelTypes.Timestamp], CelTypes.String),
            new OverloadDecl("duration_to_string", [CelTypes.Duration], CelTypes.String));

        builder.Function("bool",
            new OverloadDecl("bool_to_bool", [CelTypes.Bool], CelTypes.Bool),
            new OverloadDecl("string_to_bool", [CelTypes.String], CelTypes.Bool));

        builder.Function("bytes",
            new OverloadDecl("bytes_to_bytes", [CelTypes.Bytes], CelTypes.Bytes),
            new OverloadDecl("string_to_bytes", [CelTypes.String], CelTypes.Bytes));

        builder.Function("timestamp",
            new OverloadDecl("string_to_timestamp", [CelTypes.String], CelTypes.Timestamp),
            new OverloadDecl("int_to_timestamp", [CelTypes.Int], CelTypes.Timestamp));

        builder.Function("duration",
            new OverloadDecl("string_to_duration", [CelTypes.String], CelTypes.Duration));

        // ── string membership / search ──
        builder.Function("contains",
            new OverloadDecl("contains_string", [CelTypes.String, CelTypes.String], CelTypes.Bool, IsInstance: true));
        builder.Function("startsWith",
            new OverloadDecl("starts_with_string", [CelTypes.String, CelTypes.String], CelTypes.Bool, IsInstance: true));
        builder.Function("endsWith",
            new OverloadDecl("ends_with_string", [CelTypes.String, CelTypes.String], CelTypes.Bool, IsInstance: true));
        builder.Function("matches",
            new OverloadDecl("matches_string", [CelTypes.String, CelTypes.String], CelTypes.Bool),
            new OverloadDecl("matches_string_method", [CelTypes.String, CelTypes.String], CelTypes.Bool, IsInstance: true));
    }

    // ── helpers ──

    private static readonly TypeParamType A = CelTypes.TypeParam("A");
    private static readonly TypeParamType B = CelTypes.TypeParam("B");

    private static OverloadDecl Bin(
        string id, CelType lhs, CelType rhs, CelType result,
        ImmutableArray<string> TypeParams = default) =>
        new(id, [lhs, rhs], result, TypeParams);

    private static OverloadDecl Un(string id, CelType arg, CelType result) =>
        new(id, [arg], result);

    private static void AddOrdering(CelEnv.Builder builder, string fn, string idPrefix)
    {
        builder.Function(fn,
            new OverloadDecl(idPrefix + "_int_int", [CelTypes.Int, CelTypes.Int], CelTypes.Bool),
            new OverloadDecl(idPrefix + "_uint_uint", [CelTypes.Uint, CelTypes.Uint], CelTypes.Bool),
            new OverloadDecl(idPrefix + "_double_double", [CelTypes.Double, CelTypes.Double], CelTypes.Bool),
            new OverloadDecl(idPrefix + "_string_string", [CelTypes.String, CelTypes.String], CelTypes.Bool),
            new OverloadDecl(idPrefix + "_bytes_bytes", [CelTypes.Bytes, CelTypes.Bytes], CelTypes.Bool),
            new OverloadDecl(idPrefix + "_timestamp_timestamp", [CelTypes.Timestamp, CelTypes.Timestamp], CelTypes.Bool),
            new OverloadDecl(idPrefix + "_duration_duration", [CelTypes.Duration, CelTypes.Duration], CelTypes.Bool),
            // Heterogeneous numeric: total ordering across int / uint / double per CEL spec.
            new OverloadDecl(idPrefix + "_int_uint", [CelTypes.Int, CelTypes.Uint], CelTypes.Bool),
            new OverloadDecl(idPrefix + "_uint_int", [CelTypes.Uint, CelTypes.Int], CelTypes.Bool),
            new OverloadDecl(idPrefix + "_int_double", [CelTypes.Int, CelTypes.Double], CelTypes.Bool),
            new OverloadDecl(idPrefix + "_double_int", [CelTypes.Double, CelTypes.Int], CelTypes.Bool),
            new OverloadDecl(idPrefix + "_uint_double", [CelTypes.Uint, CelTypes.Double], CelTypes.Bool),
            new OverloadDecl(idPrefix + "_double_uint", [CelTypes.Double, CelTypes.Uint], CelTypes.Bool));
    }
}
