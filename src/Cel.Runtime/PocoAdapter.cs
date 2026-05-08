using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;

namespace Cel.Runtime;

/// <summary>
/// Reflection-based field-and-property accessor for arbitrary CLR objects. The first access for
/// a given <see cref="Type"/> builds a dictionary of name → getter delegate; subsequent calls are
/// dictionary lookups. Name resolution falls back from exact match to <c>snake_case</c>-to-<c>PascalCase</c>
/// translation so that idiomatic CEL expressions like <c>account.balance</c> reach idiomatic C#
/// properties like <c>Balance</c>.
/// </summary>
/// <remarks>
/// Uses runtime reflection. The class is annotated with <see cref="RequiresUnreferencedCodeAttribute"/>
/// so that AOT/trim warnings surface; a source-generated alternative is planned as a follow-up
/// for trim-clean deployments.
/// </remarks>
[RequiresUnreferencedCode("PocoAdapter walks public properties and fields via reflection; types may be trimmed.")]
public sealed class PocoAdapter
{
    public static readonly PocoAdapter Default = new();

    private readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, Func<object, object?>>> _cache = new();

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
        var pascal = ToPascalCase(name);
        if (!ReferenceEquals(pascal, name) && accessors.TryGetValue(pascal, out getter))
        {
            value = getter(instance);
            return true;
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
        var pascal = ToPascalCase(name);
        return !ReferenceEquals(pascal, name) && accessors.ContainsKey(pascal);
    }

    private static IReadOnlyDictionary<string, Func<object, object?>> BuildAccessors(Type type)
    {
        var dict = new Dictionary<string, Func<object, object?>>(StringComparer.Ordinal);
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0)
            {
                continue;
            }
            if (!prop.CanRead)
            {
                continue;
            }
            // PropertyInfo.GetValue is slower than a typed delegate but works uniformly across
            // value/reference types. Will optimise in the SourceGen variant.
            dict[prop.Name] = prop.GetValue!;
        }
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            dict[field.Name] = field.GetValue!;
        }
        return dict;
    }

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
