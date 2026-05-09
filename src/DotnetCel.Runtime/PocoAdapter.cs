using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using DotnetCel;

namespace DotnetCel.Runtime;

/// <summary>
/// Reflection-based field-and-property accessor for arbitrary CLR objects. The first access for
/// a given <see cref="Type"/> builds a dictionary of name → getter delegate; subsequent calls are
/// dictionary lookups.
/// </summary>
/// <remarks>
/// <para>
/// CEL-side member names are derived from CLR member names through three mechanisms (in priority
/// order):
/// </para>
/// <list type="number">
/// <item><description><see cref="JsonIgnoreAttribute"/>-annotated members are skipped entirely.</description></item>
/// <item><description><see cref="JsonPropertyNameAttribute"/> overrides the CLR name. The CLR name is not exposed.</description></item>
/// <item><description>Otherwise, the configured <see cref="PocoNamingConvention"/> transforms the CLR name.</description></item>
/// </list>
/// <para>
/// In the default <see cref="PocoNamingConvention.PascalCase"/> mode, an additional fallback
/// kicks in at lookup time: if the requested name doesn't match an exposed member, the adapter
/// re-tries with <c>snake_case</c> → <c>PascalCase</c> translation. This keeps idiomatic CEL
/// (<c>account.user_name</c>) reaching idiomatic C# (<c>UserName</c>) without explicit
/// configuration. Other conventions don't apply this fallback — exposed names are exactly what
/// the convention produced.
/// </para>
/// <para>
/// Uses runtime reflection. The class is annotated with <see cref="RequiresUnreferencedCodeAttribute"/>
/// so that AOT/trim warnings surface; a source-generated alternative is planned as a follow-up
/// for trim-clean deployments.
/// </para>
/// </remarks>
[RequiresUnreferencedCode("PocoAdapter walks public properties and fields via reflection; types may be trimmed.")]
public sealed class PocoAdapter
{
    /// <summary>Singleton in default <see cref="PocoNamingConvention.PascalCase"/> mode.</summary>
    public static readonly PocoAdapter Default = new(PocoNamingConvention.PascalCase);

    private readonly PocoNamingConvention _convention;
    private readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, Func<object, object?>>> _cache = new();

    public PocoAdapter(PocoNamingConvention convention)
    {
        _convention = convention;
    }

    /// <summary>The naming convention this adapter applies to un-annotated CLR member names.</summary>
    public PocoNamingConvention Convention => _convention;

    /// <summary>
    /// Try to read field <paramref name="name"/> from <paramref name="instance"/>. Returns
    /// <c>false</c> if the type has no matching member; getter exceptions propagate.
    /// </summary>
    public bool TryGet(object instance, string name, out object? value)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(name);
        var accessors = _cache.GetOrAdd(instance.GetType(), BuildAccessors);
        if (accessors.TryGetValue(name, out var getter))
        {
            value = getter(instance);
            return true;
        }
        if (_convention == PocoNamingConvention.PascalCase)
        {
            var pascal = ToPascalCase(name);
            if (!ReferenceEquals(pascal, name) && accessors.TryGetValue(pascal, out getter))
            {
                value = getter(instance);
                return true;
            }
        }
        value = null;
        return false;
    }

    /// <summary>Whether the type exposes a field named <paramref name="name"/> at all.</summary>
    public bool HasField(object instance, string name)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var accessors = _cache.GetOrAdd(instance.GetType(), BuildAccessors);
        if (accessors.ContainsKey(name))
        {
            return true;
        }
        if (_convention == PocoNamingConvention.PascalCase)
        {
            var pascal = ToPascalCase(name);
            return !ReferenceEquals(pascal, name) && accessors.ContainsKey(pascal);
        }
        return false;
    }

    private IReadOnlyDictionary<string, Func<object, object?>> BuildAccessors(Type type)
    {
        var dict = new Dictionary<string, Func<object, object?>>(StringComparer.Ordinal);
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0 || !prop.CanRead)
            {
                continue;
            }
            if (prop.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
            {
                continue;
            }
            var celName = ResolveName(prop.Name, prop.GetCustomAttribute<JsonPropertyNameAttribute>());
            // PropertyInfo.GetValue is slower than a typed delegate but works uniformly across
            // value/reference types. Will optimise in the SourceGen variant.
            dict[celName] = prop.GetValue!;
        }
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
            {
                continue;
            }
            var celName = ResolveName(field.Name, field.GetCustomAttribute<JsonPropertyNameAttribute>());
            dict[celName] = field.GetValue!;
        }
        return dict;
    }

    private string ResolveName(string clrName, JsonPropertyNameAttribute? jsonName) =>
        jsonName?.Name ?? _convention.Apply(clrName);

    /// <summary>
    /// Translate <c>snake_case</c> or <c>camelCase</c> to <c>PascalCase</c>. Returns the input
    /// unchanged when no translation is necessary so callers can short-circuit on reference
    /// equality.
    /// </summary>
    internal static string ToPascalCase(string s)
    {
        if (s.Length == 0)
        {
            return s;
        }
        // Already PascalCase and no underscores → no change.
        if (char.IsAsciiLetterUpper(s[0]) && !s.Contains('_'))
        {
            return s;
        }
        var sb = new StringBuilder(s.Length);
        var capitalizeNext = true;
        foreach (var c in s)
        {
            if (c == '_')
            {
                capitalizeNext = true;
                continue;
            }
            sb.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
            capitalizeNext = false;
        }
        return sb.ToString();
    }
}
