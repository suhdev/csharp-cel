using System.Collections.Immutable;
using DotnetCel.Checking;
using DotnetCel.Types;

namespace DotnetCel;

/// <summary>
/// The static configuration handed to the CEL type checker (and, later, the runtime). Holds the
/// declared variables and functions along with the namespace container used for unqualified
/// identifier resolution.
/// </summary>
/// <remarks>
/// <para>
/// Built via the fluent <see cref="Builder"/>. The standard library is included by default; call
/// <see cref="Builder.WithoutStandardLibrary"/> on the builder to start from an empty environment.
/// </para>
/// <para>
/// Instances are immutable — derive a new env via <see cref="Extend"/> rather than mutating.
/// </para>
/// </remarks>
public sealed class CelEnv
{
    public string Container { get; }
    public ImmutableDictionary<string, VariableDecl> Variables { get; }
    public ImmutableDictionary<string, FunctionDecl> Functions { get; }
    public ImmutableArray<ICelExtension> Extensions { get; }
    public ImmutableArray<CelMacro> Macros { get; }
    public ITypeProvider TypeProvider { get; }

    /// <summary>
    /// Naming convention applied by the runtime's reflection-based POCO adapter when mapping
    /// CLR property/field names to CEL field names. Default is <see cref="PocoNamingConvention.PascalCase"/>,
    /// which preserves CLR member names (with a snake-to-PascalCase fallback at lookup time).
    /// </summary>
    public PocoNamingConvention PocoNaming { get; }

    private CelEnv(
        string container,
        ImmutableDictionary<string, VariableDecl> variables,
        ImmutableDictionary<string, FunctionDecl> functions,
        ImmutableArray<ICelExtension> extensions,
        ImmutableArray<CelMacro> macros,
        ITypeProvider typeProvider,
        PocoNamingConvention pocoNaming)
    {
        Container = container;
        Variables = variables;
        Functions = functions;
        Extensions = extensions;
        Macros = macros;
        TypeProvider = typeProvider;
        PocoNaming = pocoNaming;
    }

    public static Builder NewBuilder() => new();

    /// <summary>
    /// Resolve an identifier name to a <see cref="VariableDecl"/>, trying namespace candidates in
    /// order. Returns null if no candidate is declared.
    /// </summary>
    public VariableDecl? ResolveVariable(string name)
    {
        foreach (var candidate in Namespaces.CandidateNames(Container, name))
        {
            if (Variables.TryGetValue(candidate, out var decl))
            {
                return decl;
            }
        }
        return null;
    }

    /// <summary>Resolve a function name; namespace-aware like <see cref="ResolveVariable"/>.</summary>
    public FunctionDecl? ResolveFunction(string name)
    {
        foreach (var candidate in Namespaces.CandidateNames(Container, name))
        {
            if (Functions.TryGetValue(candidate, out var decl))
            {
                return decl;
            }
        }
        return null;
    }

    /// <summary>Iterate all qualified names that the resolver would try for <paramref name="name"/>.</summary>
    public IEnumerable<string> QualifiedCandidates(string name) =>
        Namespaces.CandidateNames(Container, name);

    public Builder Extend()
    {
        var b = new Builder();
        b.SetContainer(Container);
        b.AddVariablesFrom(Variables);
        b.AddFunctionsFrom(Functions);
        b.WithoutStandardLibrary(); // base already has it
        b.UseTypeProvider(TypeProvider);
        b.UsePocoNaming(PocoNaming);
        foreach (var ext in Extensions)
        {
            b.Use(ext);
        }
        return b;
    }

    public sealed class Builder
    {
        private string _container = "";
        private readonly Dictionary<string, VariableDecl> _vars = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FunctionDecl> _fns = new(StringComparer.Ordinal);
        private readonly List<ICelExtension> _extensions = [];
        private ITypeProvider _typeProvider = NullTypeProvider.Instance;
        private PocoNamingConvention _pocoNaming = PocoNamingConvention.PascalCase;
        private bool _includeStdlib = true;

        /// <summary>Register a type provider for proto / host-object support.</summary>
        public Builder UseTypeProvider(ITypeProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            _typeProvider = provider;
            return this;
        }

        /// <summary>
        /// Set the naming convention applied to CLR property/field names when projecting them as
        /// CEL fields through the reflection-based POCO adapter. Annotated members
        /// (<c>[JsonPropertyName]</c>, <c>[JsonIgnore]</c>) are unaffected — the convention only
        /// applies to un-annotated names.
        /// </summary>
        public Builder UsePocoNaming(PocoNamingConvention convention)
        {
            _pocoNaming = convention;
            return this;
        }

        public Builder SetContainer(string container)
        {
            ArgumentNullException.ThrowIfNull(container);
            _container = container;
            return this;
        }

        public Builder Variable(string name, CelType type)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(type);
            _vars[name] = new VariableDecl(name, type);
            return this;
        }

        public Builder Variable(VariableDecl decl)
        {
            ArgumentNullException.ThrowIfNull(decl);
            _vars[decl.Name] = decl;
            return this;
        }

        public Builder Function(FunctionDecl decl)
        {
            ArgumentNullException.ThrowIfNull(decl);
            _fns[decl.Name] = _fns.TryGetValue(decl.Name, out var existing)
                ? existing.Merge(decl)
                : decl;
            return this;
        }

        public Builder Function(string name, params OverloadDecl[] overloads) =>
            Function(new FunctionDecl(name, [.. overloads]));

        public Builder WithoutStandardLibrary()
        {
            _includeStdlib = false;
            return this;
        }

        /// <summary>
        /// Apply <paramref name="extension"/>'s declarations to this env. The extension is also
        /// remembered on the built env so the runtime can later bind matching implementations.
        /// </summary>
        public Builder Use(ICelExtension extension)
        {
            ArgumentNullException.ThrowIfNull(extension);
            extension.ConfigureEnv(this);
            _extensions.Add(extension);
            return this;
        }

        internal Builder AddVariablesFrom(IEnumerable<KeyValuePair<string, VariableDecl>> entries)
        {
            foreach (var (k, v) in entries)
            {
                _vars[k] = v;
            }
            return this;
        }

        internal Builder AddFunctionsFrom(IEnumerable<KeyValuePair<string, FunctionDecl>> entries)
        {
            foreach (var (k, v) in entries)
            {
                _fns[k] = _fns.TryGetValue(k, out var existing) ? existing.Merge(v) : v;
            }
            return this;
        }

        public CelEnv Build()
        {
            if (_includeStdlib)
            {
                Stdlib.Apply(this);
            }
            var macros = _extensions.SelectMany(static e => e.Macros).ToImmutableArray();
            return new CelEnv(
                _container,
                _vars.ToImmutableDictionary(StringComparer.Ordinal),
                _fns.ToImmutableDictionary(StringComparer.Ordinal),
                [.. _extensions],
                macros,
                _typeProvider,
                _pocoNaming);
        }
    }
}
