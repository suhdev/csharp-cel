using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace DotnetCel.Runtime;

/// <summary>An activation backed by a single dictionary of name → raw CLR value.</summary>
public sealed class MapActivation : IActivation
{
    private readonly IReadOnlyDictionary<string, object?> _map;

    public MapActivation(IReadOnlyDictionary<string, object?> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        _map = map;
    }

    public bool TryResolve(string name, out object? value) =>
        _map.TryGetValue(name, out value);

    /// <summary>Build a <see cref="MapActivation"/> from an <see cref="IDictionary"/>.</summary>
    public static MapActivation From(IDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is string k)
            {
                dict[k] = entry.Value;
            }
        }
        return new MapActivation(dict);
    }
}

/// <summary>
/// An activation that exposes a single root POCO's top-level public properties / fields under
/// their declared names. Useful for the common <c>new { account, request }</c> pattern.
/// </summary>
public sealed class ObjectActivation : IActivation
{
    private readonly Dictionary<string, object?> _fields;

    [RequiresUnreferencedCode("Reflects over the root object's public properties and fields.")]
    public ObjectActivation(object root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        var t = root.GetType();
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0)
            {
                continue;
            }
            _fields[p.Name] = p.GetValue(root);
        }
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            _fields[f.Name] = f.GetValue(root);
        }
    }

    public bool TryResolve(string name, out object? value) => _fields.TryGetValue(name, out value);
}

/// <summary>Tries each child activation in order; first to claim wins.</summary>
public sealed class ChainedActivation(params IActivation[] children) : IActivation
{
    private readonly IActivation[] _children = children
        ?? throw new ArgumentNullException(nameof(children));

    public bool TryResolve(string name, out object? value)
    {
        foreach (var c in _children)
        {
            if (c.TryResolve(name, out value))
            {
                return true;
            }
        }
        value = null;
        return false;
    }
}

/// <summary>
/// A frame that overlays a parent activation with named bindings. Used by the evaluator to
/// expose comprehension iter / accumulator variables to nested expressions without rebuilding
/// the underlying activation.
/// </summary>
internal sealed class ScopedActivation : IActivation
{
    private readonly IActivation _parent;
    private readonly Dictionary<string, object?> _frame;

    public ScopedActivation(IActivation parent, Dictionary<string, object?> frame)
    {
        _parent = parent;
        _frame = frame;
    }

    public bool TryResolve(string name, out object? value)
    {
        if (_frame.TryGetValue(name, out value))
        {
            return true;
        }
        return _parent.TryResolve(name, out value);
    }
}
