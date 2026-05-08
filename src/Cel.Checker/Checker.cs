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
        var receiverType = e.Target is null ? null : (CelType?)Visit(e.Target);
        var argTypes = ImmutableArray.CreateBuilder<CelType>(e.Args.Length);
        foreach (var a in e.Args)
        {
            argTypes.Add(Visit(a));
        }

        var fn = _env.ResolveFunction(e.Function);
        if (fn is null)
        {
            Report("CEL-2002", $"undeclared function '{e.Function}'", LocationOf(e));
            return CelTypes.Error;
        }

        // Build the actual argument list (with receiver prepended for instance overloads).
        ImmutableArray<CelType>? instanceArgs = receiverType is null
            ? null
            : (ImmutableArray<CelType>)[receiverType, .. argTypes];
        ImmutableArray<CelType>? globalArgs = receiverType is null
            ? argTypes.ToImmutable()
            : null;

        OverloadDecl? matched = null;
        CelType resultType = CelTypes.Error;

        foreach (var overload in fn.Overloads)
        {
            var actual = (overload.IsInstance, instanceArgs.HasValue) switch
            {
                (true, true) => instanceArgs!.Value,
                (false, false) => globalArgs!.Value,
                _ => default,
            };
            if (actual.IsDefault)
            {
                continue;
            }
            if (actual.Length != overload.ArgTypes.Length)
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
            if (!ok)
            {
                continue;
            }
            matched = overload;
            resultType = TypeAlgebra.Substitute(overload.ResultType, sub);
            break;
        }

        if (matched is null)
        {
            var argList = string.Join(", ", argTypes.Select(static t => t.Name));
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
}

file static class CheckerHelpers
{
    public static bool IsBool(CelType t) => t is PrimitiveType { PrimKind: PrimitiveKind.Bool };
}
