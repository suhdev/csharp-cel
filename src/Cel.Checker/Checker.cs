using System.Collections.Immutable;
using Cel.Ast;
using Cel.Checking;
using Cel.Diagnostics;
using Cel.Types;

namespace Cel;

/// <summary>
/// Static type checker for CEL. Walks a parsed AST against an <see cref="CelEnv"/>, assigning a
/// <see cref="CelType"/> to every node and recording how identifiers and calls were resolved.
/// </summary>
/// <remarks>
/// <para>
/// Implements gradual typing: when an operand has type <c>dyn</c> the result of any operation
/// involving it is also <c>dyn</c>, and the actual type check happens at runtime. Heterogeneous
/// numeric comparisons fall back to <c>dyn</c> via the parametric <c>_==_</c> / <c>_!=_</c>
/// overloads in <see cref="Stdlib"/>.
/// </para>
/// <para>
/// Field selection on object types currently returns <c>dyn</c> because there is no
/// <c>TypeProvider</c> yet — that hook arrives with the runtime POCO adapter in Phase 4.
/// </para>
/// </remarks>
public sealed class Checker
{
    private readonly CelEnv _env;
    private readonly SourceInfo _sourceInfo;
    private readonly DiagnosticBag _diagnostics;
    private readonly Dictionary<long, CelType> _types = [];
    private readonly Dictionary<long, ResolvedReference> _refs = [];
    private readonly Stack<Dictionary<string, CelType>> _scopes = new();

    private Checker(CelEnv env, SourceInfo sourceInfo, DiagnosticBag diagnostics)
    {
        _env = env;
        _sourceInfo = sourceInfo;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Type-check a parsed expression against an environment. Pre-existing diagnostics from
    /// parsing are passed through verbatim; checking aborts if any of them are errors.
    /// </summary>
    public static CheckResult Check(
        Expr expression,
        SourceInfo sourceInfo,
        CelEnv env,
        ImmutableArray<Diagnostic> upstreamDiagnostics = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(sourceInfo);
        ArgumentNullException.ThrowIfNull(env);
        var diag = new DiagnosticBag();
        if (!upstreamDiagnostics.IsDefault)
        {
            foreach (var d in upstreamDiagnostics)
            {
                diag.Report(d);
            }
            if (diag.HasErrors)
            {
                return new CheckResult(null, [.. diag]);
            }
        }
        var checker = new Checker(env, sourceInfo, diag);
        var resultType = checker.Visit(expression);
        if (diag.HasErrors)
        {
            return new CheckResult(null, [.. diag]);
        }
        var ast = new CheckedAst(
            expression,
            sourceInfo,
            checker._types.ToImmutableDictionary(),
            checker._refs.ToImmutableDictionary(),
            resultType);
        return new CheckResult(ast, [.. diag]);
    }

    // ── visitor ──

    private CelType Visit(Expr expr)
    {
        var t = expr switch
        {
            ConstantExpr c => VisitConstant(c),
            IdentifierExpr i => VisitIdentifier(i),
            SelectExpr s => VisitSelect(s),
            CallExpr c => VisitCall(c),
            CreateListExpr l => VisitList(l),
            CreateMapExpr m => VisitMap(m),
            CreateStructExpr s => VisitStruct(s),
            ComprehensionExpr c => VisitComprehension(c),
            _ => CelTypes.Error,
        };
        _types[expr.Id] = t;
        return t;
    }

    private static CelType VisitConstant(ConstantExpr e) => e.Value switch
    {
        NullConstant => CelTypes.Null,
        BoolConstant => CelTypes.Bool,
        IntConstant => CelTypes.Int,
        UintConstant => CelTypes.Uint,
        DoubleConstant => CelTypes.Double,
        StringConstant => CelTypes.String,
        BytesConstant => CelTypes.Bytes,
        _ => CelTypes.Error,
    };

    private CelType VisitIdentifier(IdentifierExpr e)
    {
        // Comprehension-scoped variable wins over env.
        foreach (var frame in _scopes)
        {
            if (frame.TryGetValue(e.Name, out var t))
            {
                _refs[e.Id] = new ResolvedReference(e.Name);
                return t;
            }
        }

        var v = _env.ResolveVariable(e.Name);
        if (v is not null)
        {
            _refs[e.Id] = new ResolvedReference(v.Name);
            return v.Type;
        }

        // Could be a reference to a function name (not currently allowed for first-class call),
        // a type, or an enum value. Report as undeclared for now.
        Report("CEL-2001", $"undeclared reference to '{e.Name}'", LocationOf(e));
        return CelTypes.Error;
    }

    private CelType VisitSelect(SelectExpr e)
    {
        var operandType = Visit(e.Operand);

        if (e.TestOnly)
        {
            // has(e.f) — always returns bool, defers field-presence check to runtime.
            return CelTypes.Bool;
        }

        return operandType switch
        {
            DynType => CelTypes.Dyn,
            ErrorType => CelTypes.Error,
            MapType m => m.ValueType,
            // Object field types require a TypeProvider; until Phase 4 plugs one in, defer.
            ObjectType => CelTypes.Dyn,
            _ => DiagnoseTypeMismatch(e, $"cannot select '{e.Field}' on {operandType.Name}"),
        };
    }

    private CelType VisitCall(CallExpr e)
    {
        // First, see if the target chain is actually a namespace prefix referring to a
        // qualified function (e.g. `math.greatest(1, 2)` where `math` isn't a variable but
        // `math.greatest` is a declared function). If so, dispatch as a global call without
        // ever evaluating the target as a value.
        if (e.Target is not null)
        {
            var prefix = TryFlattenIdentChain(e.Target);
            if (prefix is not null)
            {
                var qualifiedName = $"{prefix}.{e.Function}";
                var qualifiedFn = _env.ResolveFunction(qualifiedName);
                if (qualifiedFn is not null)
                {
                    var argTypesNs = ImmutableArray.CreateBuilder<CelType>(e.Args.Length);
                    foreach (var a in e.Args)
                    {
                        argTypesNs.Add(Visit(a));
                    }
                    var (m, r) = TryMatchOverload(qualifiedFn, receiverType: null, argTypesNs.ToImmutable());
                    if (m is not null)
                    {
                        _refs[e.Id] = new ResolvedReference(qualifiedFn.Name, m.Id, TargetIsNamespace: true);
                        return r;
                    }
                }
            }
        }

        var receiverType = e.Target is null ? null : (CelType?)Visit(e.Target);
        var argTypes = ImmutableArray.CreateBuilder<CelType>(e.Args.Length);
        foreach (var a in e.Args)
        {
            argTypes.Add(Visit(a));
        }
        var actualArgTypes = argTypes.ToImmutable();

        var fn = _env.ResolveFunction(e.Function);
        if (fn is null)
        {
            Report("CEL-2002", $"undeclared function '{e.Function}'", LocationOf(e));
            return CelTypes.Error;
        }

        var (matched, resultType) = TryMatchOverload(fn, receiverType, actualArgTypes);

        if (matched is null)
        {
            var argList = string.Join(", ", actualArgTypes.Select(static t => t.Name));
            var receiverDesc = receiverType is null ? "" : $"{receiverType.Name}.";
            Report("CEL-2003",
                $"no matching overload for {receiverDesc}{e.Function}({argList})",
                LocationOf(e));
            return CelTypes.Error;
        }

        _refs[e.Id] = new ResolvedReference(fn.Name, matched.Id);
        return resultType;
    }

    private CelType VisitList(CreateListExpr e)
    {
        if (e.Elements.IsDefaultOrEmpty)
        {
            return CelTypes.List(CelTypes.Dyn);
        }
        var element = Visit(e.Elements[0]);
        for (var i = 1; i < e.Elements.Length; i++)
        {
            element = TypeAlgebra.MostGeneral(element, Visit(e.Elements[i]));
        }
        // Optional-marked elements introduce optional<T>; the contained list element is T.
        if (!e.OptionalIndices.IsDefaultOrEmpty)
        {
            // unwrap any optional<T> to T for the element type calculation
            if (element is OptionalType opt)
            {
                element = opt.InnerType;
            }
        }
        return CelTypes.List(element);
    }

    private CelType VisitMap(CreateMapExpr e)
    {
        if (e.Entries.IsDefaultOrEmpty)
        {
            return CelTypes.Map(CelTypes.Dyn, CelTypes.Dyn);
        }
        CelType keyT = Visit(e.Entries[0].Key);
        CelType valT = Visit(e.Entries[0].Value);
        for (var i = 1; i < e.Entries.Length; i++)
        {
            keyT = TypeAlgebra.MostGeneral(keyT, Visit(e.Entries[i].Key));
            valT = TypeAlgebra.MostGeneral(valT, Visit(e.Entries[i].Value));
        }
        return CelTypes.Map(keyT, valT);
    }

    private CelType VisitStruct(CreateStructExpr e)
    {
        // Without a TypeProvider, we cannot validate field names or types — type-check the
        // value sub-expressions and assign the named object type; runtime will catch mismatches.
        foreach (var f in e.Fields)
        {
            Visit(f.Value);
        }
        return CelTypes.Object(e.MessageName);
    }

    private CelType VisitComprehension(ComprehensionExpr e)
    {
        var rangeType = Visit(e.IterRange);
        var (iterType, iterType2) = ComprehensionElementTypes(rangeType, e, out var rangeOk);
        if (!rangeOk)
        {
            Report("CEL-2010", $"cannot iterate over value of type {rangeType.Name}", LocationOf(e.IterRange));
        }

        var accuType = Visit(e.AccuInit);

        // Scope for loop_condition and loop_step: iter (+ iter2) + accu.
        var loopFrame = new Dictionary<string, CelType>(StringComparer.Ordinal)
        {
            [e.IterVar] = iterType,
            [e.AccuVar] = accuType,
        };
        if (e.IterVar2 is not null && iterType2 is not null)
        {
            loopFrame[e.IterVar2] = iterType2;
        }
        _scopes.Push(loopFrame);

        var condType = Visit(e.LoopCondition);
        if (!IsBoolOrFlexible(condType))
        {
            Report("CEL-2011", $"comprehension loop condition must be bool, got {condType.Name}", LocationOf(e.LoopCondition));
        }

        var stepType = Visit(e.LoopStep);
        // Widen accumulator type to cover the step result; uses WidenAccu (not MostGeneral) so
        // an empty initial list/map [] doesn't drag the final type to list(dyn).
        var widenedAccu = TypeAlgebra.WidenAccu(accuType, stepType);
        loopFrame[e.AccuVar] = widenedAccu;
        _scopes.Pop();

        // Result is evaluated with only accu in scope.
        _scopes.Push(new Dictionary<string, CelType>(StringComparer.Ordinal)
        {
            [e.AccuVar] = widenedAccu,
        });
        var resultType = Visit(e.Result);
        _scopes.Pop();

        return resultType;
    }

    private static (CelType IterType, CelType? IterType2) ComprehensionElementTypes(
        CelType rangeType, ComprehensionExpr e, out bool ok)
    {
        switch (rangeType)
        {
            case ListType l:
                ok = true;
                return (l.ElementType, null);
            case MapType m:
                ok = true;
                return e.IterVar2 is null ? (m.KeyType, null) : (m.KeyType, m.ValueType);
            case DynType:
                ok = true;
                return (CelTypes.Dyn, e.IterVar2 is null ? null : CelTypes.Dyn);
            case ErrorType:
                ok = true; // already errored upstream; don't double-report
                return (CelTypes.Error, null);
            default:
                ok = false;
                return (CelTypes.Error, null);
        }
    }

    private CelType DiagnoseTypeMismatch(Expr e, string message)
    {
        Report("CEL-2004", message, LocationOf(e));
        return CelTypes.Error;
    }

    private void Report(string code, string message, SourceLocation location) =>
        _diagnostics.Error(code, message, location);

    private SourceLocation LocationOf(Expr e) => _sourceInfo.LocationOf(e.Id);

    private static bool IsBoolOrFlexible(CelType t) =>
        CheckerHelpers.IsBool(t) || t is DynType or ErrorType;

    /// <summary>
    /// Try to match the supplied actual argument types against any of <paramref name="fn"/>'s
    /// overloads. Returns the first overload whose arg types unify and the substituted result
    /// type. Receiver-bearing calls match instance overloads; null receiver matches global.
    /// </summary>
    private static (OverloadDecl? Overload, CelType ResultType) TryMatchOverload(
        FunctionDecl fn, CelType? receiverType, ImmutableArray<CelType> argTypes)
    {
        ImmutableArray<CelType>? instanceArgs = receiverType is null
            ? null
            : (ImmutableArray<CelType>)[receiverType, .. argTypes];
        ImmutableArray<CelType>? globalArgs = receiverType is null ? argTypes : null;

        foreach (var overload in fn.Overloads)
        {
            var actual = (overload.IsInstance, instanceArgs.HasValue) switch
            {
                (true, true) => instanceArgs!.Value,
                (false, false) => globalArgs!.Value,
                _ => default,
            };
            if (actual.IsDefault || actual.Length != overload.ArgTypes.Length)
            {
                continue;
            }
            var sub = new Dictionary<string, CelType>(StringComparer.Ordinal);
            var ok = true;
            for (var i = 0; i < actual.Length; i++)
            {
                if (!TypeAlgebra.Unify(overload.ArgTypes[i], actual[i], sub))
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
            {
                return (overload, TypeAlgebra.Substitute(overload.ResultType, sub));
            }
        }
        return (null, CelTypes.Error);
    }

    /// <summary>
    /// Flatten a chain of <see cref="SelectExpr"/> ending in an <see cref="IdentifierExpr"/>
    /// into a dotted name, mirroring the convention used for namespace prefixes. Returns null
    /// if any part of the chain is something other than ident/select.
    /// </summary>
    private static string? TryFlattenIdentChain(Expr e)
    {
        var parts = new Stack<string>();
        var cur = e;
        while (cur is SelectExpr s && !s.TestOnly)
        {
            parts.Push(s.Field);
            cur = s.Operand;
        }
        if (cur is IdentifierExpr ident)
        {
            var name = ident.Name;
            if (name.Length > 0 && name[0] == '.')
            {
                name = name[1..];
            }
            parts.Push(name);
            return string.Join('.', parts);
        }
        return null;
    }
}

file static class CheckerHelpers
{
    public static bool IsBool(CelType t) => t is PrimitiveType { PrimKind: PrimitiveKind.Bool };
}
