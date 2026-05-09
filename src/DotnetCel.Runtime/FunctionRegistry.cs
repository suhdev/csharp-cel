namespace DotnetCel.Runtime;

/// <summary>
/// Maps an overload id (e.g. <c>add_int_int_int</c>) to its runtime implementation. The checker
/// resolves call sites to overload ids; the evaluator dispatches against this registry.
/// </summary>
public sealed class FunctionRegistry
{
    private readonly Dictionary<string, OverloadFn> _impls = new(StringComparer.Ordinal);

    public void Bind(string overloadId, OverloadFn impl)
    {
        ArgumentNullException.ThrowIfNull(overloadId);
        ArgumentNullException.ThrowIfNull(impl);
        _impls[overloadId] = impl;
    }

    public bool TryGet(string overloadId, out OverloadFn? impl) =>
        _impls.TryGetValue(overloadId, out impl);

    /// <summary>Build a registry pre-populated with the standard library implementations.</summary>
    public static FunctionRegistry CreateStandard()
    {
        var reg = new FunctionRegistry();
        Stdlib.Register(reg);
        return reg;
    }
}
