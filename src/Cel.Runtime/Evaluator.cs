using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Cel.Ast;
using Cel.Diagnostics;
using Cel.Types;
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
    private readonly ITypeProvider _typeProvider;

    public int MaxIterations { get; init; } = 100_000;

    public Evaluator(
        CheckedAst ast,
        FunctionRegistry functions,
        PocoAdapter? pocoAdapter = null,
        ITypeProvider? typeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(ast);
        ArgumentNullException.ThrowIfNull(functions);
        _ast = ast;
        _functions = functions;
        _poco = pocoAdapter ?? PocoAdapter.Default;
        _typeProvider = typeProvider ?? NullTypeProvider.Instance;
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

    /// <summary>
    /// Built-in type denotations (<c>bool</c>, <c>int</c>, ..., plus the well-known
    /// <c>google.protobuf.Timestamp</c> / <c>Duration</c>). Resolved before the activation so
    /// they always carry the right <see cref="TypeValue"/> regardless of how a host configures
    /// the env.
    /// </summary>
    private static readonly Dictionary<string, CelValue> TypeDenotations = new(StringComparer.Ordinal)
    {
        ["bool"] = new TypeValue(CelTypes.Bool),
        ["int"] = new TypeValue(CelTypes.Int),
        ["uint"] = new TypeValue(CelTypes.Uint),
        ["double"] = new TypeValue(CelTypes.Double),
        ["string"] = new TypeValue(CelTypes.String),
        ["bytes"] = new TypeValue(CelTypes.Bytes),
        ["null_type"] = new TypeValue(CelTypes.Null),
        ["list"] = new TypeValue(CelTypes.List(CelTypes.Dyn)),
        ["map"] = new TypeValue(CelTypes.Map(CelTypes.Dyn, CelTypes.Dyn)),
        ["type"] = new TypeValue(CelTypes.Type),
        ["google.protobuf.Timestamp"] = new TypeValue(CelTypes.Timestamp),
        ["google.protobuf.Duration"] = new TypeValue(CelTypes.Duration),
        // Network extension type denotations — kept here so `type(ip(x)) == net.IP` works
        // without each host having to seed them in the activation.
        ["net.IP"] = new TypeValue(CelTypes.Object("net.IP")),
        ["net.CIDR"] = new TypeValue(CelTypes.Object("net.CIDR")),
    };

    private CelValue VisitIdentifier(IdentifierExpr e, IActivation activation)
    {
        var name = e.Name.Length > 0 && e.Name[0] == '.' ? e.Name[1..] : e.Name;

        // The checker may have resolved this identifier to a qualified name (e.g. `y` in
        // container `x` resolved to `x.y`). Use that name first so the activation key matches
        // the declaration the type checker bound to.
        var resolvedName = _ast.ReferenceMap.TryGetValue(e.Id, out var refInfo) ? refInfo.Name : name;

        // Built-in type denotations short-circuit any activation lookup.
        if (TypeDenotations.TryGetValue(resolvedName, out var denotation))
        {
            return denotation;
        }

        if (!string.Equals(resolvedName, name, StringComparison.Ordinal)
            && activation.TryResolve(resolvedName, out var rawQualified))
        {
            return WrapManagedAware(rawQualified);
        }

        if (activation.TryResolve(name, out var raw))
        {
            return WrapManagedAware(raw);
        }
        return CelValue.Error($"no such variable: {e.Name}");
    }

    private CelValue VisitSelectAsQualifiedVariable(string qualifiedName, IActivation activation)
    {
        // Type denotations like `net.IP` and `google.protobuf.Timestamp` arrive here because
        // the parser builds them as SelectExpr chains; resolve through the same dictionary
        // the bare-identifier path uses.
        if (TypeDenotations.TryGetValue(qualifiedName, out var denotation))
        {
            return denotation;
        }
        if (activation.TryResolve(qualifiedName, out var raw))
        {
            return WrapManagedAware(raw);
        }
        return CelValue.Error($"no such variable: {qualifiedName}");
    }

    private CelValue VisitSelect(SelectExpr e, IActivation activation)
    {
        // The checker may have collapsed the entire ident chain to a qualified variable name
        // (e.g. `x.y` resolves to a declared variable named "x.y"). The reference map is the
        // source of truth.
        if (_ast.ReferenceMap.TryGetValue(e.Id, out var refInfo)
            && refInfo.OverloadId is null && !e.TestOnly)
        {
            return VisitSelectAsQualifiedVariable(refInfo.Name, activation);
        }

        var operand = Visit(e.Operand, activation);
        if (operand is ErrorValue or UnknownValue)
        {
            return operand;
        }

        if (e.TestOnly)
        {
            return CelValue.Of(HasField(operand, e.Field));
        }

        // Auto-unwrap select on optional: None propagates, Some(v) does the select on v and
        // re-wraps successful access in Some(...). Failures still surface as errors.
        if (operand is OptionalValue opt)
        {
            if (!opt.HasValue)
            {
                return OptionalValue.None;
            }
            var inner = ReadField(opt.Inner!, e.Field);
            return inner is ErrorValue ? inner : OptionalValue.Of(inner);
        }
        return ReadField(operand, e.Field);
    }

    private CelValue ReadField(CelValue operand, string field) => operand switch
    {
        MapValue m => MapLookup(m, CelValue.Of(field), reportMissing: true),
        ObjectValue o => ReadObjectField(o, field),
        NullValue => CelValue.Error($"select on null: {field}"),
        _ => CelValue.Error($"cannot select '{field}' on {operand.Type.Name}"),
    };

    private bool HasField(CelValue operand, string field) => operand switch
    {
        MapValue m => MapContainsKey(m, CelValue.Of(field)),
        ObjectValue o => HasObjectField(o, field),
        _ => false,
    };

    private bool HasObjectField(ObjectValue o, string field)
    {
        // Type-provider-managed instances apply proto presence semantics.
        if (_typeProvider.IsManagedInstance(o.Native))
        {
            return _typeProvider.HasField(o.Native, field);
        }
        return _poco.HasField(o.Native, field)
            && _poco.TryGet(o.Native, field, out var v)
            && v is not null;
    }

    private CelValue ReadObjectField(ObjectValue o, string field)
    {
        // Try the type provider first (proto types unwrap wrappers, route oneof, etc.).
        if (_typeProvider.IsManagedInstance(o.Native)
            && _typeProvider.TryReadField(o.Native, field, out var protoVal))
        {
            return WrapManagedAware(protoVal);
        }
        if (!_poco.TryGet(o.Native, field, out var raw))
        {
            return CelValue.Error($"no such field: {field}");
        }
        return WrapManagedAware(raw);
    }

    /// <summary>
    /// Wrap a raw CLR value as a CelValue, asking the type provider for the proper proto type
    /// name when the result is a managed message instance, and projecting wrapper / well-known
    /// types to their unwrapped CEL primitives. Already-wrapped <see cref="CelValue"/> inputs
    /// are still routed through projection so a binding holding an <see cref="ObjectValue"/>
    /// over a wrapper proto unwraps to its primitive.
    /// </summary>
    private CelValue WrapManagedAware(object? raw)
    {
        if (raw is null) { return CelValue.Null; }
        var cel = raw is CelValue cv ? cv : ValueAdapter.ToCelValue(raw);
        if (cel is ObjectValue ov && _typeProvider.IsManagedInstance(ov.Native))
        {
            var projected = _typeProvider.Project(ov.Native);
            if (projected is not null) { return projected; }

            var name = _typeProvider.TypeNameOf(ov.Native);
            if (name is not null && !string.Equals(name, ov.TypeName, StringComparison.Ordinal))
            {
                return new ObjectValue(name, ov.Native);
            }
        }
        return cel;
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
            case Operators.OptSelect: return EvalOptSelect(e, activation);
        }

        // The checker may have resolved this call as a namespaced global (e.g. `math.greatest`),
        // in which case the AST's "target" is a namespace prefix and must NOT be evaluated.
        var hasResolvedRef = _ast.ReferenceMap.TryGetValue(e.Id, out var refInfo);
        var skipTarget = hasResolvedRef && refInfo!.TargetIsNamespace;

        // Eager arg evaluation; first error/unknown short-circuits.
        var totalArgs = e.Args.Length + (e.Target is null || skipTarget ? 0 : 1);
        var args = new CelValue[totalArgs];
        var idx = 0;
        if (e.Target is not null && !skipTarget)
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

        if (!hasResolvedRef || refInfo!.OverloadId is null)
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

    /// <summary>
    /// Optional select <c>e.?field</c>: returns <see cref="OptionalValue.Of"/> when the field
    /// is present, <see cref="OptionalValue.None"/> when absent. Errors only on hard failures
    /// (a field path that's structurally wrong, like selecting on a number).
    /// </summary>
    private CelValue EvalOptSelect(CallExpr e, IActivation a)
    {
        var operand = Visit(e.Args[0], a);
        if (operand is ErrorValue or UnknownValue) { return operand; }

        // The parser always emits the field name as a literal string constant.
        if (e.Args[1] is not ConstantExpr { Value: StringConstant sc })
        {
            return CelValue.Error("optional select: field name must be a string literal");
        }
        var field = sc.Value;

        return SelectOptional(operand, field);
    }

    private CelValue SelectOptional(CelValue operand, string field) => operand switch
    {
        MapValue m => MapContainsKey(m, CelValue.Of(field))
            ? OptionalValue.Of(MapLookupRaw(m, CelValue.Of(field))!)
            : OptionalValue.None,
        ObjectValue o => _poco.TryGet(o.Native, field, out var raw) && raw is not null
            ? OptionalValue.Of(ValueAdapter.ToCelValue(raw))
            : OptionalValue.None,
        OptionalValue opt => opt.HasValue ? SelectOptional(opt.Inner!, field) : OptionalValue.None,
        _ => CelValue.Error($"cannot optional-select '{field}' on {operand.Type.Name}"),
    };

    private static CelValue? MapLookupRaw(MapValue map, CelValue key)
    {
        foreach (var (k, v) in map.Entries)
        {
            if (CelEquality.Equals(key, k))
            {
                return v;
            }
        }
        return null;
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
        // Resolve fields to CelValues (with optional unwrap).
        var fields = new Dictionary<string, CelValue>(StringComparer.Ordinal);
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
            fields[f.Name] = v;
        }

        // The checker may have resolved a container-relative type name ("TestAllTypes") to its
        // qualified form ("cel.expr.conformance.proto3.TestAllTypes"). Prefer that over the raw
        // source-text name on the AST node.
        var typeName = _ast.ReferenceMap.TryGetValue(e.Id, out var refInfo) ? refInfo.Name : e.MessageName;

        // Prefer the type provider for typed construction (proto messages). The result goes
        // through WrapManagedAware so wrapper types project to their unwrapped CEL primitives.
        if (_typeProvider.KnowsType(typeName))
        {
            var instance = _typeProvider.Construct(typeName, fields);
            if (instance is null)
            {
                return CelValue.Error($"failed to construct {typeName}");
            }
            return WrapManagedAware(instance);
        }

        // Fallback: flatten to a map keyed by field name.
        var mapBuilder = ImmutableDictionary.CreateBuilder<CelValue, CelValue>();
        foreach (var (name, value) in fields)
        {
            mapBuilder[CelValue.Of(name)] = value;
        }
        return new MapValue(mapBuilder.ToImmutable());
    }

    private CelValue VisitComprehension(ComprehensionExpr e, IActivation activation)
    {
        var range = Visit(e.IterRange, activation);
        if (range is ErrorValue or UnknownValue) { return range; }

        // Two-iterator macros (macros2): list.all(i, v, p) binds index+element; map.all(k, v, p)
        // binds key+value. Single-iterator stays as element / key like before.
        var iterPairs = EnumerateRange(range, e.IterVar2 is not null);
        if (iterPairs is null)
        {
            return CelValue.Error($"cannot iterate over {range.Type.Name}");
        }

        var accu = Visit(e.AccuInit, activation);
        var iterations = 0;
        var frame = new Dictionary<string, object?>(StringComparer.Ordinal);
        var scoped = new ScopedActivation(activation, frame);

        foreach (var (a, b) in iterPairs)
        {
            if (++iterations > MaxIterations)
            {
                return CelValue.Error("comprehension iteration limit exceeded");
            }
            frame[e.IterVar] = a;
            if (e.IterVar2 is not null)
            {
                frame[e.IterVar2] = b;
            }
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

    /// <summary>
    /// Iterate the range as (iter_var, iter_var2) pairs. For two-iter mode: list yields
    /// (index, element) and map yields (key, value). For single-iter, the second element is
    /// ignored and we yield (element, _) for lists or (key, _) for maps.
    /// </summary>
    private static IEnumerable<(CelValue, CelValue)>? EnumerateRange(CelValue range, bool twoIter)
    {
        switch (range)
        {
            case ListValue l:
                return EnumerateList(l, twoIter);
            case MapValue m:
                return EnumerateMap(m);
            default:
                return null;
        }

        static IEnumerable<(CelValue, CelValue)> EnumerateList(ListValue list, bool twoIter)
        {
            for (var i = 0; i < list.Elements.Length; i++)
            {
                if (twoIter)
                {
                    yield return (CelValue.Of((long)i), list.Elements[i]);
                }
                else
                {
                    yield return (list.Elements[i], CelValue.Null);
                }
            }
        }

        static IEnumerable<(CelValue, CelValue)> EnumerateMap(MapValue map)
        {
            foreach (var (k, v) in map.Entries)
            {
                yield return (k, v);
            }
        }
    }
}
