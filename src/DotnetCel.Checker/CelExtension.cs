namespace DotnetCel;

/// <summary>
/// A pluggable bundle of CEL declarations and runtime implementations. Extensions are how
/// optional features (string utilities, math, encoders, set predicates, networking helpers,
/// custom domain functions) attach to a <see cref="CelEnv"/>.
/// </summary>
/// <remarks>
/// <para>
/// An extension implements two halves: <see cref="ConfigureEnv"/> registers function
/// declarations (and any required type bindings) onto the environment builder; the same
/// extension instance is held by the built env and later asked, via <see cref="ConfigureRuntime"/>,
/// to bind matching runtime implementations.
/// </para>
/// <para>
/// Implementations should be stateless (or thread-safe) so a single instance can serve any
/// number of compiled programs.
/// </para>
/// </remarks>
public interface ICelExtension
{
    /// <summary>Apply declarations (variables, functions) to the supplied env builder.</summary>
    void ConfigureEnv(CelEnv.Builder envBuilder);

    /// <summary>
    /// Bind runtime implementations for the overloads declared in <see cref="ConfigureEnv"/>.
    /// The supplied <paramref name="bindImpl"/> callback registers an implementation against an
    /// overload id (the same id passed to <see cref="OverloadDecl"/>).
    /// </summary>
    void ConfigureRuntime(Action<string, OverloadFn> bindImpl);

    /// <summary>
    /// Optional parser-level macros (e.g. <c>cel.bind</c>, <c>optMap</c>). Returned macros are
    /// consulted by the parser after its hardcoded set (<c>has</c>, <c>all</c>, <c>exists</c>,
    /// ...). Default is empty.
    /// </summary>
    IEnumerable<CelMacro> Macros => Array.Empty<CelMacro>();
}
