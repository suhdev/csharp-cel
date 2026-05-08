using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Cel.Ast;
using Cel.Diagnostics;
using Cel.Values;

namespace Cel.Runtime;

/// <summary>
/// Tree-walking interpreter for a <see cref="CheckedAst"/>. Stateless across evaluations: the
/// same <see cref="Evaluator"/> instance is safe to reuse for any number of activations and is
/// thread-safe so long as the underlying registry and POCO adapter are.
/// </summary>
/// <remarks>
/// <para>
/// Errors and unknowns are first-class values that flow through expressions. Short-circuiting
/// operators (<c>&amp;&amp;</c>, <c>||</c>, ternary) absorb them per CEL spec: <c>false &amp;&amp; (1/0)</c>
/// evaluates to <c>false</c>, never the division error.
/// </para>
/// <para>
/// Comprehensions execute with a configurable iteration cap (<see cref="MaxIterations"/>) to
/// prevent pathological inputs from running indefinitely; the cap defaults to 100k iterations.
/// </para>
/// </remarks>
[RequiresUnreferencedCode("Field access on host objects uses reflection via PocoAdapter.")]
public sealed class Evaluator
{
    private readonly CheckedAst _ast;
    private readonly FunctionRegistry _functions;
    private readonly PocoAdapter _poco;

    public int MaxIterations { get; init; } = 100_000;

    public Evaluator(CheckedAst ast, FunctionRegistry functions, PocoAdapter? pocoAdapter = null)
    {
        ArgumentNullException.ThrowIfNull(ast);
        ArgumentNullException.ThrowIfNull(functions);
        _ast = ast;
        _functions = functions;
        _poco = pocoAdapter ?? PocoAdapter.Default;
    }

    /// <summary>Evaluate the AST against the supplied activation. Returns a raw <see cref="CelValue"/>.</summary>
    public CelValue Evaluate(IActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        return Visit(_ast.Expression, activation);
    }

    // ── visitor ──

    private CelValue Visit(Expr expr, IActivation activation) => expr switch
    {
        ConstantExpr c => VisitConstant(c),
        IdentifierExpr i => VisitIdentifier(i, activation),
        SelectExpr s => VisitSelect(s, activation),
        CallExpr c => VisitCall(c, activation),
        CreateListExpr l => VisitList(l, activation),
        CreateMapExpr m => VisitMap(m, activation),
        CreateStructExpr s => VisitStruct(s, activation),
        ComprehensionExpr c => VisitComprehension(c, activation),
        _ => CelValue.Error($"unsupported expression: {expr.GetType().Name}"),
    };

    private static CelValue VisitConstant(ConstantExpr e) => e.Value switch
    {
        NullConstant => CelValue.Null,
        BoolConstant b => CelValue.Of(b.Value),
        IntConstant i => CelValue.Of(i.Value),
        UintConstant u => CelValue.Of(u.Value),
        DoubleConstant d => CelValue.Of(d.Value),
        StringConstant s => CelValue.Of(s.Value),
        BytesConstant b => new BytesValue(b.Value),
        _ => CelValue.Error("unknown constant"),
    };

    private CelValue VisitIdentifier(IdentifierExpr e, IActivation activation)
    {
        // Strip leading-dot marker the parser inserted for absolute references; the activation
        // never knows about it.
        var name = e.Name.Length > 0 && e.Name[0] == '.' ? e.Name[1..] : e.Name;
        if (activation.TryResolve(name, out var raw))
        {
            return ValueAdapter.ToCelValue(raw);
        }
        // Try the qualified name from the checker's reference map (variables resolved via
        // namespace candidates).
        if (_ast.ReferenceMap.TryGetValue(e.Id, out var refInfo) && refInfo.Name != name)
        {
            if (activation.TryResolve(refInfo.Name, out raw))
            {
                return ValueAdapter.ToCelValue(raw);
            }
        }
        return CelValue.Error($"no such variable: {e.Name}");
    }

    private CelValue VisitSelect(SelectExpr e, IActivation activation)
    {
        var operand = Visit(e.Operand, activation);
        if (operand is ErrorValue or UnknownValue)
        {
            return operand;
        }

        if (e.TestOnly)
        {
            return CelValue.Of(HasField(operand, e.Field));
        }

        return operand switch
        {
            MapValue m => MapLookup(m, CelValue.Of(e.Field), reportMissing: true),
            ObjectValue o => ReadObjectField(o, e.Field),
            NullValue => CelValue.Error($"select on null: {e.Field}"),
            _ => CelValue.Error($"cannot select '{e.Field}' on {operand.Type.Name}"),
        };
    }

    private bool HasField(CelValue operand, string field) => operand switch
    {
        MapValue m => MapContainsKey(m, CelValue.Of(field)),
        ObjectValue o => _poco.HasField(o.Native, field) && _poco.TryGet(o.Native, field, out var v) && v is not null,
        _ => false,
    };

    private CelValue ReadObjectField(ObjectValue o, string field)
    {
        if (!_poco.TryGet(o.Native, field, out var raw))
        {
            return CelValue.Error($"no such field: {field}");
        }
        return ValueAdapter.ToCelValue(raw);
    }

    private static CelValue MapLookup(MapValue map, CelValue key, bool reportMissing)
    {
        foreach (var (k, v) in map.Entries)
        {
            if (CelEquality.Equals(key, k))
            {
                return v;
            }
        }
        return reportMissing ? CelValue.Error($"no such key: {key}") : CelValue.Error("map key absent");
    }

    private static bool MapContainsKey(MapValue map, CelValue key)
    {
        foreach (var k in map.Entries.Keys)
        {
            if (CelEquality.Equals(key, k))
            {
                return true;
            }
        }
        return false;
    }

    private CelValue VisitCall(CallExpr e, IActivation activation)
    {
        // Lazy operators handle short-circuit + error/unknown absorption.
        switch (e.Function)
        {
            case Operators.LogicalOr: return EvalOr(e, activation);
            case Operators.LogicalAnd: return EvalAnd(e, activation);
            case Operators.Conditional: return EvalTernary(e, activation);
        }

        // Eager arg evaluation; first error/unknown short-circuits.
        var totalArgs = e.Args.Length + (e.Target is null ? 0 : 1);
        var args = new CelValue[totalArgs];
        var idx = 0;
        if (e.Target is not null)
        {
            var t = Visit(e.Target, activation);
            if (t is ErrorValue or UnknownValue) { return t; }
            args[idx++] = t;
        }
        foreach (var arg in e.Args)
        {
            var v = Visit(arg, activation);
            if (v is ErrorValue or UnknownValue) { return v; }
            args[idx++] = v;
        }

        if (!_ast.ReferenceMap.TryGetValue(e.Id, out var refInfo) || refInfo.OverloadId is null)
        {
            return CelValue.Error($"unresolved call: {e.Function}");
        }
        if (!_functions.TryGet(refInfo.OverloadId, out var impl) || impl is null)
        {
            return CelValue.Error($"no runtime implementation for overload: {refInfo.OverloadId}");
        }

        try
        {
            return impl(args);
        }
        catch (CelEvaluationException ex)
        {
            return CelValue.Error(ex.Message);
        }
        catch (InvalidCastException ex)
        {
            // Type checker should have prevented this; if it didn't, surface as an error value.
            return CelValue.Error($"runtime type mismatch in {refInfo.OverloadId}: {ex.Message}");
        }
    }

    private CelValue EvalOr(CallExpr e, IActivation a)
    {
        var lhs = Visit(e.Args[0], a);
        if (lhs is BoolValue { Value: true }) { return CelValue.True; }
        var rhs = Visit(e.Args[1], a);
        if (rhs is BoolValue { Value: true }) { return CelValue.True; }
        if (lhs is ErrorValue) { return lhs; }
        if (rhs is ErrorValue) { return rhs; }
        if (lhs is UnknownValue) { return lhs; }
        if (rhs is UnknownValue) { return rhs; }
        return CelValue.False;
    }

    private CelValue EvalAnd(CallExpr e, IActivation a)
    {
        var lhs = Visit(e.Args[0], a);
        if (lhs is BoolValue { Value: false }) { return CelValue.False; }
        var rhs = Visit(e.Args[1], a);
        if (rhs is BoolValue { Value: false }) { return CelValue.False; }
        if (lhs is ErrorValue) { return lhs; }
        if (rhs is ErrorValue) { return rhs; }
        if (lhs is UnknownValue) { return lhs; }
        if (rhs is UnknownValue) { return rhs; }
        return CelValue.True;
    }

    private CelValue EvalTernary(CallExpr e, IActivation a)
    {
        var cond = Visit(e.Args[0], a);
        return cond switch
        {
            BoolValue { Value: true } => Visit(e.Args[1], a),
            BoolValue { Value: false } => Visit(e.Args[2], a),
            ErrorValue or UnknownValue => cond,
            _ => CelValue.Error($"ternary condition is not bool: got {cond.Type.Name}"),
        };
    }

    private CelValue VisitList(CreateListExpr e, IActivation activation)
    {
        if (e.Elements.IsDefaultOrEmpty)
        {
            return new ListValue([]);
        }
        var optional = !e.OptionalIndices.IsDefaultOrEmpty
            ? new HashSet<int>(e.OptionalIndices)
            : null;
        var builder = ImmutableArray.CreateBuilder<CelValue>(e.Elements.Length);
        for (var i = 0; i < e.Elements.Length; i++)
        {
            var v = Visit(e.Elements[i], activation);
            if (v is ErrorValue or UnknownValue) { return v; }
            if (optional is not null && optional.Contains(i))
            {
                if (v is OptionalValue { HasValue: false })
                {
                    continue; // skip None
                }
                if (v is OptionalValue opt)
                {
                    builder.Add(opt.Inner!);
                    continue;
                }
            }
            builder.Add(v);
        }
        return new ListValue(builder.ToImmutable());
    }

    private CelValue VisitMap(CreateMapExpr e, IActivation activation)
    {
        if (e.Entries.IsDefaultOrEmpty)
        {
            return new MapValue(ImmutableDictionary<CelValue, CelValue>.Empty);
        }
        var builder = ImmutableDictionary.CreateBuilder<CelValue, CelValue>();
        foreach (var entry in e.Entries)
        {
            var key = Visit(entry.Key, activation);
            if (key is ErrorValue or UnknownValue) { return key; }
            var value = Visit(entry.Value, activation);
            if (value is ErrorValue or UnknownValue) { return value; }
            if (entry.IsOptional)
            {
                if (value is OptionalValue { HasValue: false })
                {
                    continue;
                }
                if (value is OptionalValue opt)
                {
                    value = opt.Inner!;
                }
            }
            builder[key] = value;
        }
        return new MapValue(builder.ToImmutable());
    }

    private CelValue VisitStruct(CreateStructExpr e, IActivation activation)
    {
        // Without a TypeProvider we cannot construct typed objects. For now, flatten to a map
        // keyed by string field names — usable for many test cases and clearly debuggable.
        var builder = ImmutableDictionary.CreateBuilder<CelValue, CelValue>();
        foreach (var f in e.Fields)
        {
            var v = Visit(f.Value, activation);
            if (v is ErrorValue or UnknownValue) { return v; }
            if (f.IsOptional)
            {
                if (v is OptionalValue { HasValue: false })
                {
                    continue;
                }
                if (v is OptionalValue opt)
                {
                    v = opt.Inner!;
                }
            }
            builder[CelValue.Of(f.Name)] = v;
        }
        return new MapValue(builder.ToImmutable());
    }

    private CelValue VisitComprehension(ComprehensionExpr e, IActivation activation)
    {
        var range = Visit(e.IterRange, activation);
        if (range is ErrorValue or UnknownValue) { return range; }

        var elements = range switch
        {
            ListValue l => (IReadOnlyList<CelValue>)l.Elements,
            MapValue m => [.. m.Entries.Keys],
            _ => null,
        };
        if (elements is null)
        {
            return CelValue.Error($"cannot iterate over {range.Type.Name}");
        }

        var accu = Visit(e.AccuInit, activation);
        var iterations = 0;
        var frame = new Dictionary<string, object?>(StringComparer.Ordinal);
        var scoped = new ScopedActivation(activation, frame);

        foreach (var item in elements)
        {
            if (++iterations > MaxIterations)
            {
                return CelValue.Error("comprehension iteration limit exceeded");
            }
            frame[e.IterVar] = item;
            frame[e.AccuVar] = accu;
            var cont = Visit(e.LoopCondition, scoped);
            if (cont is BoolValue { Value: false })
            {
                break;
            }
            accu = Visit(e.LoopStep, scoped);
        }

        // Result expression sees only the accumulator.
        var resultFrame = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [e.AccuVar] = accu,
        };
        return Visit(e.Result, new ScopedActivation(activation, resultFrame));
    }
}
