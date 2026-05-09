using System.Text;

namespace DotnetCel;

/// <summary>
/// How the <see cref="PocoAdapter"/> derives a CEL-side field name from a CLR property or field
/// name when no explicit attribute (<c>[JsonPropertyName]</c>) overrides it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PascalCase"/> is the default and preserves backward-compatible behaviour: the
/// CLR member name is exposed as-is, with a built-in fallback that lets <c>user_name</c> resolve
/// to <c>UserName</c>. The other conventions are strict — only the transformed name is exposed,
/// so consumers must spell CEL fields in the chosen style.
/// </para>
/// <para>
/// All transforms operate on the ASCII portion of the CLR identifier. Non-ASCII characters
/// pass through unchanged, since they can't appear in C# identifiers without being treated as
/// letters by the language anyway.
/// </para>
/// </remarks>
public enum PocoNamingConvention
{
    /// <summary>Expose CLR names as-is (with a snake-to-PascalCase fallback at lookup time).</summary>
    PascalCase = 0,

    /// <summary>Lowercase the first character — <c>UserName</c> becomes <c>userName</c>.</summary>
    CamelCase,

    /// <summary>Insert underscores before each capital — <c>UserName</c> becomes <c>user_name</c>.</summary>
    SnakeCase,

    /// <summary>Snake-case in upper — <c>UserName</c> becomes <c>USER_NAME</c>.</summary>
    ScreamingSnakeCase,

    /// <summary>Insert hyphens before each capital — <c>UserName</c> becomes <c>user-name</c>.</summary>
    KebabCase,
}

internal static class PocoNamingConventionExtensions
{
    /// <summary>
    /// Apply the convention's name transform to a CLR member name. Returns the input unchanged
    /// for <see cref="PocoNamingConvention.PascalCase"/> so callers can short-circuit.
    /// </summary>
    public static string Apply(this PocoNamingConvention convention, string clrName) => convention switch
    {
        PocoNamingConvention.PascalCase => clrName,
        PocoNamingConvention.CamelCase => ToCamelCase(clrName),
        PocoNamingConvention.SnakeCase => InsertSeparator(clrName, '_', upper: false),
        PocoNamingConvention.ScreamingSnakeCase => InsertSeparator(clrName, '_', upper: true),
        PocoNamingConvention.KebabCase => InsertSeparator(clrName, '-', upper: false),
        _ => clrName,
    };

    private static string ToCamelCase(string s)
    {
        if (s.Length == 0 || !char.IsAsciiLetterUpper(s[0]))
        {
            return s;
        }
        // Lowercase a single leading capital, or a run of capitals up to the second-last
        // character ("HTTPRequest" -> "httpRequest"). The "second-last" guard preserves the
        // word break before the final lowercase letter.
        var i = 1;
        while (i < s.Length - 1 && char.IsAsciiLetterUpper(s[i]) && char.IsAsciiLetterUpper(s[i + 1]))
        {
            i++;
        }
        var sb = new StringBuilder(s.Length);
        for (var k = 0; k < s.Length; k++)
        {
            sb.Append(k < i ? char.ToLowerInvariant(s[k]) : s[k]);
        }
        return sb.ToString();
    }

    private static string InsertSeparator(string s, char sep, bool upper)
    {
        if (s.Length == 0)
        {
            return s;
        }
        var sb = new StringBuilder(s.Length + 4);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0 && char.IsAsciiLetterUpper(c)
                && (char.IsAsciiLetterLower(s[i - 1]) || char.IsAsciiDigit(s[i - 1])
                    || (i + 1 < s.Length && char.IsAsciiLetterLower(s[i + 1]))))
            {
                sb.Append(sep);
            }
            sb.Append(upper ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
