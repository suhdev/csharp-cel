using System.Collections.Immutable;
using Cel.Types;

namespace Cel.Checking;

/// <summary>
/// Type-level algorithms used by the checker: assignability, substitution over type variables,
/// argument unification with join-on-conflict, and least-upper-bound for branch joining.
/// </summary>
/// <remarks>
/// Substitutions are mutable dictionaries keyed by type-param name. Unification updates them
/// in-place and may rebind a type variable to <c>dyn</c> when two branches are not pairwise
/// assignable — this matches cel-go's gradual typing behaviour, where mismatched ternary
/// branches widen to <c>dyn</c> rather than producing a hard error.
/// </remarks>
internal static class TypeAlgebra
{
    /// <summary>
    /// Is a value of type <paramref name="source"/> usable where <paramref name="target"/> is
    /// expected? Pure (no substitution side-effects).
    /// </summary>
    public static bool IsAssignable(CelType target, CelType source) =>
        IsAssignable(target, source, new Dictionary<string, CelType>(StringComparer.Ordinal));

    public static bool IsAssignable(CelType target, CelType source, IDictionary<string, CelType> sub)
    {
        target = Substitute(target, sub);
        source = Substitute(source, sub);

        if (target is DynType || source is DynType) { return true; }
        if (target is ErrorType || source is ErrorType) { return true; }
        if (ReferenceEquals(target, source) || target.Equals(source)) { return true; }

        if (target is TypeParamType etp)
        {
            sub[etp.ParamName] = source;
            return true;
        }
        if (source is TypeParamType atp)
        {
            sub[atp.ParamName] = target;
            return true;
        }

        if (source is NullType)
        {
            return IsNullable(target);
        }

        // Wrapper unwrap: wrapper(T) ↔ T, and wrapper(T) ↔ wrapper(T)
        if (target is WrapperType tw)
        {
            if (source is PrimitiveType ps && tw.PrimKind == ps.PrimKind) { return true; }
            if (source is WrapperType ws && tw.PrimKind == ws.PrimKind) { return true; }
        }
        if (source is WrapperType sw && target is PrimitiveType pt && sw.PrimKind == pt.PrimKind)
        {
            return true;
        }

        return (target, source) switch
        {
            (PrimitiveType a, PrimitiveType b) => a.PrimKind == b.PrimKind,
            (ListType a, ListType b) => IsAssignable(a.ElementType, b.ElementType, sub),
            (MapType a, MapType b) =>
                IsAssignable(a.KeyType, b.KeyType, sub) && IsAssignable(a.ValueType, b.ValueType, sub),
            (OptionalType a, OptionalType b) => IsAssignable(a.InnerType, b.InnerType, sub),
            (TypeType, TypeType) => true,
            (ObjectType a, ObjectType b) => string.Equals(a.TypeName, b.TypeName, StringComparison.Ordinal),
            (EnumType a, EnumType b) => string.Equals(a.TypeName, b.TypeName, StringComparison.Ordinal),
            (NullType, NullType) => true,
            (DurationType, DurationType) => true,
            (TimestampType, TimestampType) => true,
            _ => false,
        };
    }

    /// <summary>Per CEL: null is a valid value for wrapper, message, optional, and null types.</summary>
    public static bool IsNullable(CelType t) =>
        t is NullType or WrapperType or ObjectType or OptionalType or DynType or ErrorType;

    /// <summary>Recursively replace bound type variables with their substitutions.</summary>
    public static CelType Substitute(CelType type, IDictionary<string, CelType> sub)
    {
        if (sub.Count == 0)
        {
            return type;
        }
        return type switch
        {
            // Avoid recursing through a self-aliasing binding ({A: TypeParam(A)} from earlier
            // unification); compare by name rather than reference so that distinct TypeParam
            // instances with the same name don't trigger an infinite chase.
            TypeParamType tp when sub.TryGetValue(tp.ParamName, out var t)
                && !(t is TypeParamType bound && bound.ParamName == tp.ParamName) =>
                Substitute(t, sub),
            ListType l => l with { ElementType = Substitute(l.ElementType, sub) },
            MapType m => m with
            {
                KeyType = Substitute(m.KeyType, sub),
                ValueType = Substitute(m.ValueType, sub),
            },
            OptionalType o => o with { InnerType = Substitute(o.InnerType, sub) },
            FunctionType f => f with
            {
                ResultType = Substitute(f.ResultType, sub),
                ArgTypes = SubstituteAll(f.ArgTypes, sub),
            },
            TypeType { Parameter: { } inner } tt => tt with { Parameter = Substitute(inner, sub) },
            ObjectType { TypeArgs.IsDefaultOrEmpty: false } o =>
                o with { TypeArgs = SubstituteAll(o.TypeArgs, sub) },
            AbstractType { Parameters.IsDefaultOrEmpty: false } a =>
                a with { Parameters = SubstituteAll(a.Parameters, sub) },
            _ => type,
        };
    }

    private static ImmutableArray<CelType> SubstituteAll(ImmutableArray<CelType> types, IDictionary<string, CelType> sub) =>
        [.. types.Select(t => Substitute(t, sub))];

    /// <summary>
    /// Compute the most-general type covering both inputs. If neither is assignable to the
    /// other, returns <c>dyn</c> (CEL's gradual-typing escape hatch).
    /// </summary>
    public static CelType MostGeneral(CelType a, CelType b)
    {
        if (a is DynType || b is DynType) { return CelTypes.Dyn; }
        if (a.Equals(b)) { return a; }
        if (IsAssignable(a, b)) { return a; }
        if (IsAssignable(b, a)) { return b; }
        return (a, b) switch
        {
            (ListType la, ListType lb) => CelTypes.List(MostGeneral(la.ElementType, lb.ElementType)),
            (MapType ma, MapType mb) =>
                CelTypes.Map(MostGeneral(ma.KeyType, mb.KeyType), MostGeneral(ma.ValueType, mb.ValueType)),
            _ => CelTypes.Dyn,
        };
    }

    /// <summary>
    /// Widen a comprehension accumulator's running type with a freshly-computed step type.
    /// Differs from <see cref="MostGeneral"/> in that <c>dyn</c> is treated as "no information"
    /// — meeting <c>dyn</c> with a concrete <c>T</c> yields <c>T</c>, so an initial <c>[]</c>
    /// (typed <c>list(dyn)</c>) doesn't drag the final accumulator down to dyn.
    /// </summary>
    public static CelType WidenAccu(CelType current, CelType incoming)
    {
        if (current is DynType) { return incoming; }
        if (incoming is DynType) { return current; }
        if (current.Equals(incoming)) { return current; }
        return (current, incoming) switch
        {
            (ListType lc, ListType li) => CelTypes.List(WidenAccu(lc.ElementType, li.ElementType)),
            (MapType mc, MapType mi) =>
                CelTypes.Map(WidenAccu(mc.KeyType, mi.KeyType), WidenAccu(mc.ValueType, mi.ValueType)),
            (OptionalType oc, OptionalType oi) => CelTypes.Optional(WidenAccu(oc.InnerType, oi.InnerType)),
            _ => MostGeneral(current, incoming),
        };
    }

    /// <summary>
    /// Try to unify <paramref name="expected"/> against <paramref name="actual"/>, accumulating
    /// type-parameter bindings into <paramref name="sub"/>. Conflicting bindings widen via
    /// <see cref="MostGeneral"/> rather than failing — that's what makes <c>cond ? 1 : "x"</c>
    /// resolve to <c>dyn</c> instead of producing an error.
    /// </summary>
    public static bool Unify(CelType expected, CelType actual, IDictionary<string, CelType> sub)
    {
        if (expected is TypeParamType etp)
        {
            BindTypeParam(etp.ParamName, Substitute(actual, sub), sub);
            return true;
        }
        if (actual is TypeParamType atp)
        {
            BindTypeParam(atp.ParamName, Substitute(expected, sub), sub);
            return true;
        }
        return IsAssignable(expected, actual, sub);
    }

    private static void BindTypeParam(string name, CelType value, IDictionary<string, CelType> sub)
    {
        // Occurs check — refuse any binding A := T(...A...). Such a binding would either be
        // identity (A := A) or self-referential (A := list(A)), both of which make Substitute
        // recurse forever.
        if (Occurs(name, value))
        {
            return;
        }
        if (sub.TryGetValue(name, out var existing))
        {
            sub[name] = MostGeneral(existing, value);
            return;
        }
        sub[name] = value;
    }

    private static bool Occurs(string name, CelType type) => type switch
    {
        TypeParamType tp => tp.ParamName == name,
        ListType l => Occurs(name, l.ElementType),
        MapType m => Occurs(name, m.KeyType) || Occurs(name, m.ValueType),
        OptionalType o => Occurs(name, o.InnerType),
        FunctionType f => Occurs(name, f.ResultType) || f.ArgTypes.Any(t => Occurs(name, t)),
        TypeType { Parameter: { } inner } => Occurs(name, inner),
        ObjectType o when !o.TypeArgs.IsDefaultOrEmpty => o.TypeArgs.Any(t => Occurs(name, t)),
        AbstractType a when !a.Parameters.IsDefaultOrEmpty => a.Parameters.Any(t => Occurs(name, t)),
        _ => false,
    };
}
