namespace DotnetCel.Checking;

/// <summary>
/// Implements CEL's container/namespace candidate-name resolution. Given a container
/// <c>a.b.c</c> and a reference <c>x</c>, the resolver tries names from most-specific to
/// least-specific: <c>a.b.c.x</c>, <c>a.b.x</c>, <c>a.x</c>, <c>x</c>. A leading dot on the
/// reference (<c>.x</c>) bypasses the container entirely and yields just the bare name.
/// </summary>
internal static class Namespaces
{
    public static IEnumerable<string> CandidateNames(string container, string name)
    {
        if (name.Length > 0 && name[0] == '.')
        {
            yield return name[1..];
            yield break;
        }

        if (string.IsNullOrEmpty(container))
        {
            yield return name;
            yield break;
        }

        var end = container.Length;
        while (end > 0)
        {
            yield return $"{container[..end]}.{name}";
            var prev = container.LastIndexOf('.', end - 1);
            if (prev < 0)
            {
                break;
            }
            end = prev;
        }
        yield return name;
    }
}
