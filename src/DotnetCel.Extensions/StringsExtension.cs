using System.Collections.Immutable;
using System.Text;
using DotnetCel.Types;
using DotnetCel.Values;

namespace DotnetCel.Extensions;

/// <summary>
/// Port of cel-go's <c>ext/strings</c> library: <c>charAt</c>, <c>indexOf</c>, <c>lastIndexOf</c>,
/// <c>lowerAscii</c>, <c>upperAscii</c>, <c>replace</c>, <c>split</c>, <c>substring</c>,
/// <c>trim</c>, <c>join</c>, <c>reverse</c>. All but <c>join</c> are receiver-style on string;
/// <c>join</c> is receiver-style on <c>list(string)</c>.
/// </summary>
/// <remarks>
/// The reference cel-go implementation operates on UTF-8 byte offsets while this port uses
/// .NET's UTF-16 code-unit semantics. For ASCII inputs the two are indistinguishable; for
/// strings with multi-byte characters, indices and lengths can differ. Multi-codepoint
/// operations (<c>charAt</c>, <c>reverse</c>) explicitly iterate runes so emoji and other
/// surrogate-pair characters round-trip correctly.
/// </remarks>
public sealed class StringsExtension : ICelExtension
{
    public static readonly StringsExtension Instance = new();
    private StringsExtension() { }

    public void ConfigureEnv(CelEnv.Builder b)
    {
        b.Function("charAt",
            new OverloadDecl("string_char_at_int", [CelTypes.String, CelTypes.Int], CelTypes.String, IsInstance: true));

        b.Function("indexOf",
            new OverloadDecl("string_index_of_string",
                [CelTypes.String, CelTypes.String], CelTypes.Int, IsInstance: true),
            new OverloadDecl("string_index_of_string_int",
                [CelTypes.String, CelTypes.String, CelTypes.Int], CelTypes.Int, IsInstance: true));

        b.Function("lastIndexOf",
            new OverloadDecl("string_last_index_of_string",
                [CelTypes.String, CelTypes.String], CelTypes.Int, IsInstance: true),
            new OverloadDecl("string_last_index_of_string_int",
                [CelTypes.String, CelTypes.String, CelTypes.Int], CelTypes.Int, IsInstance: true));

        b.Function("lowerAscii",
            new OverloadDecl("string_lower_ascii", [CelTypes.String], CelTypes.String, IsInstance: true));
        b.Function("upperAscii",
            new OverloadDecl("string_upper_ascii", [CelTypes.String], CelTypes.String, IsInstance: true));

        b.Function("replace",
            new OverloadDecl("string_replace_string_string",
                [CelTypes.String, CelTypes.String, CelTypes.String], CelTypes.String, IsInstance: true),
            new OverloadDecl("string_replace_string_string_int",
                [CelTypes.String, CelTypes.String, CelTypes.String, CelTypes.Int], CelTypes.String, IsInstance: true));

        b.Function("split",
            new OverloadDecl("string_split_string",
                [CelTypes.String, CelTypes.String], CelTypes.List(CelTypes.String), IsInstance: true),
            new OverloadDecl("string_split_string_int",
                [CelTypes.String, CelTypes.String, CelTypes.Int], CelTypes.List(CelTypes.String), IsInstance: true));

        b.Function("substring",
            new OverloadDecl("string_substring_int",
                [CelTypes.String, CelTypes.Int], CelTypes.String, IsInstance: true),
            new OverloadDecl("string_substring_int_int",
                [CelTypes.String, CelTypes.Int, CelTypes.Int], CelTypes.String, IsInstance: true));

        b.Function("trim",
            new OverloadDecl("string_trim", [CelTypes.String], CelTypes.String, IsInstance: true));

        b.Function("join",
            new OverloadDecl("list_string_join",
                [CelTypes.List(CelTypes.String)], CelTypes.String, IsInstance: true),
            new OverloadDecl("list_string_join_string",
                [CelTypes.List(CelTypes.String), CelTypes.String], CelTypes.String, IsInstance: true));

        b.Function("reverse",
            new OverloadDecl("string_reverse", [CelTypes.String], CelTypes.String, IsInstance: true));

        // strings.quote(s) — namespaced JSON-style quoting helper.
        b.Function("strings.quote",
            new OverloadDecl("strings_quote", [CelTypes.String], CelTypes.String));

        // string.format(args) — printf-style substitution.
        b.Function("format",
            new OverloadDecl("string_format",
                [CelTypes.String, CelTypes.List(CelTypes.Dyn)], CelTypes.String, IsInstance: true));
    }

    public void ConfigureRuntime(Action<string, OverloadFn> bind)
    {
        bind("string_char_at_int", CharAt);
        bind("string_index_of_string", IndexOf);
        bind("string_index_of_string_int", IndexOfFrom);
        bind("string_last_index_of_string", LastIndexOf);
        bind("string_last_index_of_string_int", LastIndexOfFrom);
        bind("string_lower_ascii", LowerAscii);
        bind("string_upper_ascii", UpperAscii);
        bind("string_replace_string_string", ReplaceAll);
        bind("string_replace_string_string_int", ReplaceN);
        bind("string_split_string", SplitAll);
        bind("string_split_string_int", SplitN);
        bind("string_substring_int", SubstringFrom);
        bind("string_substring_int_int", SubstringRange);
        bind("string_trim", Trim);
        bind("list_string_join", Join);
        bind("list_string_join_string", JoinSep);
        bind("string_reverse", Reverse);
        bind("strings_quote", Quote);
        bind("string_format", Format);
    }

    /// <summary>
    /// CEL printf-style format. Supports a useful subset of the cel-go specifiers:
    /// <c>%s</c> (CEL string of any value), <c>%d</c> (int/uint), <c>%f</c> (double, default
    /// precision 6, custom via <c>%.Nf</c>), <c>%e</c>/<c>%E</c> (scientific, custom via
    /// <c>%.Ne</c>), <c>%b</c>/<c>%o</c>/<c>%x</c>/<c>%X</c> (binary/octal/hex), <c>%t</c>
    /// (bool), <c>%%</c> (literal percent). Unsupported specifiers and arity mismatches surface
    /// as errors.
    /// </summary>
    private static CelValue Format(ReadOnlySpan<CelValue> args)
    {
        var fmt = S(args[0]);
        var values = (ListValue)args[1];
        var sb = new StringBuilder(fmt.Length);
        var argIdx = 0;
        for (var i = 0; i < fmt.Length; i++)
        {
            var c = fmt[i];
            if (c != '%')
            {
                sb.Append(c);
                continue;
            }
            if (i + 1 >= fmt.Length)
            {
                return CelValue.Error("trailing '%' in format string");
            }
            i++;
            if (fmt[i] == '%')
            {
                sb.Append('%');
                continue;
            }
            int? precision = null;
            if (fmt[i] == '.')
            {
                i++;
                var pStart = i;
                while (i < fmt.Length && char.IsAsciiDigit(fmt[i]))
                {
                    i++;
                }
                if (pStart == i || !int.TryParse(fmt.AsSpan(pStart, i - pStart), out var prec))
                {
                    return CelValue.Error($"invalid format precision at offset {pStart}");
                }
                precision = prec;
            }
            if (i >= fmt.Length)
            {
                return CelValue.Error("incomplete format spec");
            }
            if (argIdx >= values.Elements.Length)
            {
                return CelValue.Error("format: not enough arguments");
            }
            var arg = values.Elements[argIdx++];
            var verb = fmt[i];
            var formatted = FormatOne(verb, precision, arg);
            if (formatted is null)
            {
                return CelValue.Error($"format: cannot apply %{verb} to {arg.Type.Name}");
            }
            sb.Append(formatted);
        }
        return CelValue.Of(sb.ToString());
    }

    private static string? FormatOne(char verb, int? precision, CelValue arg)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        switch (verb)
        {
            case 's':
                return arg switch
                {
                    StringValue sv => sv.Value,
                    BoolValue bv => bv.Value ? "true" : "false",
                    IntValue iv => iv.Value.ToString(inv),
                    UintValue uv => uv.Value.ToString(inv),
                    DoubleValue dv => dv.Value.ToString("R", inv),
                    NullValue => "null",
                    BytesValue byv => Convert.ToHexString(byv.Value.AsSpan()),
                    _ => arg.ToClrObject()?.ToString() ?? "",
                };
            case 'd':
                return arg switch
                {
                    IntValue i => i.Value.ToString(inv),
                    UintValue u => u.Value.ToString(inv),
                    _ => null,
                };
            case 'f':
                {
                    if (arg is not DoubleValue d)
                    {
                        return null;
                    }
                    var prec = precision ?? 6;
                    return d.Value.ToString($"F{prec}", inv);
                }
            case 'e':
            case 'E':
                {
                    if (arg is not DoubleValue d)
                    {
                        return null;
                    }
                    var prec = precision ?? 6;
                    var raw = d.Value.ToString($"{verb}{prec}", inv);
                    // .NET emits "e+003" / "e+03" depending; cel-go emits "e+03". Normalise the
                    // exponent to two digits.
                    return NormalizeExponent(raw);
                }
            case 'b':
                {
                    long n = arg switch
                    {
                        IntValue i => i.Value,
                        UintValue u => (long)u.Value,
                        _ => 0,
                    };
                    if (arg is not (IntValue or UintValue))
                    {
                        return null;
                    }
                    return Convert.ToString(n, 2);
                }
            case 'o':
                {
                    if (arg is not (IntValue or UintValue))
                    {
                        return null;
                    }
                    long n = arg is IntValue i ? i.Value : (long)((UintValue)arg).Value;
                    return Convert.ToString(n, 8);
                }
            case 'x':
            case 'X':
                {
                    if (arg is not (IntValue or UintValue))
                    {
                        return null;
                    }
                    long n = arg is IntValue i ? i.Value : (long)((UintValue)arg).Value;
                    return n.ToString(verb == 'x' ? "x" : "X", inv);
                }
            case 't':
                return arg is BoolValue tv ? (tv.Value ? "true" : "false") : null;
            default:
                return null;
        }
    }

    private static string NormalizeExponent(string s)
    {
        var eIdx = s.IndexOfAny(['e', 'E']);
        if (eIdx < 0 || eIdx + 2 >= s.Length)
        {
            return s;
        }
        var sign = s[eIdx + 1];
        if (sign != '+' && sign != '-')
        {
            return s;
        }
        var digits = s[(eIdx + 2)..];
        // Trim leading zeros down to a minimum of two digits.
        var trimmed = digits.TrimStart('0');
        if (trimmed.Length == 0)
        {
            trimmed = "0";
        }
        if (trimmed.Length < 2)
        {
            trimmed = trimmed.PadLeft(2, '0');
        }
        return string.Concat(s.AsSpan(0, eIdx + 2), trimmed);
    }

    /// <summary>
    /// Quote a CEL string: wrap in <c>"..."</c> with control chars and quotes escaped using
    /// the standard CEL escape forms (<c>\n</c>, <c>\t</c>, <c>\\</c>, <c>\"</c>, etc.). Used
    /// by <c>strings.quote(s)</c>.
    /// </summary>
    private static CelValue Quote(ReadOnlySpan<CelValue> args)
    {
        var s = S(args[0]);
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\v': sb.Append("\\v"); break;
                case '\a': sb.Append("\\a"); break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return CelValue.Of(sb.ToString());
    }

    // ── implementations ──

    private static string S(CelValue v) => ((StringValue)v).Value;
    private static long I(CelValue v) => ((IntValue)v).Value;

    private static CelValue CharAt(ReadOnlySpan<CelValue> args)
    {
        var s = S(args[0]);
        var idx = I(args[1]);
        if (idx < 0) { return CelValue.Error($"index out of range: {idx}"); }
        var i = 0L;
        foreach (var rune in s.EnumerateRunes())
        {
            if (i == idx) { return CelValue.Of(rune.ToString()); }
            i++;
        }
        // CEL semantics: idx == len returns "" rather than erroring.
        return idx == i ? CelValue.Of("") : CelValue.Error($"index out of range: {idx}");
    }

    private static CelValue IndexOf(ReadOnlySpan<CelValue> args) =>
        CelValue.Of((long)S(args[0]).IndexOf(S(args[1]), StringComparison.Ordinal));

    private static CelValue IndexOfFrom(ReadOnlySpan<CelValue> args)
    {
        var s = S(args[0]);
        var sub = S(args[1]);
        var start = I(args[2]);
        if (start < 0 || start > s.Length) { return CelValue.Error($"index out of range: {start}"); }
        return CelValue.Of((long)s.IndexOf(sub, (int)start, StringComparison.Ordinal));
    }

    private static CelValue LastIndexOf(ReadOnlySpan<CelValue> args) =>
        CelValue.Of((long)S(args[0]).LastIndexOf(S(args[1]), StringComparison.Ordinal));

    private static CelValue LastIndexOfFrom(ReadOnlySpan<CelValue> args)
    {
        var s = S(args[0]);
        var sub = S(args[1]);
        var start = I(args[2]);
        if (start < 0 || start > s.Length) { return CelValue.Error($"index out of range: {start}"); }
        // CEL semantics: returns the largest i such that s.Substring(i, sub.Length) == sub
        // AND i <= start. Empty sub matches at min(start, s.Length).
        if (sub.Length == 0) { return CelValue.Of(Math.Min(start, (long)s.Length)); }
        var maxI = (int)Math.Min(start, s.Length - sub.Length);
        var result = -1;
        for (var i = 0; i <= maxI; i++)
        {
            if (string.CompareOrdinal(s, i, sub, 0, sub.Length) == 0)
            {
                result = i;
            }
        }
        return CelValue.Of((long)result);
    }

    private static CelValue LowerAscii(ReadOnlySpan<CelValue> args)
    {
        var s = S(args[0]);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c is >= 'A' and <= 'Z' ? (char)(c + 32) : c);
        }
        return CelValue.Of(sb.ToString());
    }

    private static CelValue UpperAscii(ReadOnlySpan<CelValue> args)
    {
        var s = S(args[0]);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c is >= 'a' and <= 'z' ? (char)(c - 32) : c);
        }
        return CelValue.Of(sb.ToString());
    }

    private static CelValue ReplaceAll(ReadOnlySpan<CelValue> args) =>
        CelValue.Of(S(args[0]).Replace(S(args[1]), S(args[2]), StringComparison.Ordinal));

    private static CelValue ReplaceN(ReadOnlySpan<CelValue> args)
    {
        var s = S(args[0]);
        var oldVal = S(args[1]);
        var newVal = S(args[2]);
        var n = I(args[3]);
        if (n == 0 || oldVal.Length == 0) { return CelValue.Of(s); }
        if (n < 0) { return CelValue.Of(s.Replace(oldVal, newVal, StringComparison.Ordinal)); }

        var sb = new StringBuilder(s.Length);
        var i = 0;
        long count = 0;
        while (i < s.Length && count < n)
        {
            var idx = s.IndexOf(oldVal, i, StringComparison.Ordinal);
            if (idx < 0) { break; }
            sb.Append(s, i, idx - i).Append(newVal);
            i = idx + oldVal.Length;
            count++;
        }
        sb.Append(s, i, s.Length - i);
        return CelValue.Of(sb.ToString());
    }

    private static CelValue SplitAll(ReadOnlySpan<CelValue> args) =>
        BuildList(S(args[0]).Split(S(args[1]), StringSplitOptions.None));

    private static CelValue SplitN(ReadOnlySpan<CelValue> args)
    {
        var s = S(args[0]);
        var sep = S(args[1]);
        var limit = I(args[2]);
        if (limit == 0) { return new ListValue([]); }
        if (limit < 0) { return BuildList(s.Split(sep, StringSplitOptions.None)); }
        return BuildList(s.Split(sep, (int)limit, StringSplitOptions.None));
    }

    private static CelValue SubstringFrom(ReadOnlySpan<CelValue> args)
    {
        var s = S(args[0]);
        var start = I(args[1]);
        if (start < 0 || start > s.Length) { return CelValue.Error($"index out of range: {start}"); }
        return CelValue.Of(s[(int)start..]);
    }

    private static CelValue SubstringRange(ReadOnlySpan<CelValue> args)
    {
        var s = S(args[0]);
        var start = I(args[1]);
        var end = I(args[2]);
        if (start < 0 || end > s.Length || start > end)
        {
            return CelValue.Error($"index out of range: [{start}, {end}]");
        }
        return CelValue.Of(s[(int)start..(int)end]);
    }

    private static CelValue Trim(ReadOnlySpan<CelValue> args) =>
        CelValue.Of(S(args[0]).Trim());

    private static CelValue Join(ReadOnlySpan<CelValue> args) =>
        JoinWith(args, separator: "");

    private static CelValue JoinSep(ReadOnlySpan<CelValue> args) =>
        JoinWith(args, separator: S(args[1]));

    private static CelValue JoinWith(ReadOnlySpan<CelValue> args, string separator)
    {
        var list = (ListValue)args[0];
        var sb = new StringBuilder();
        for (var i = 0; i < list.Elements.Length; i++)
        {
            if (i > 0) { sb.Append(separator); }
            sb.Append(S(list.Elements[i]));
        }
        return CelValue.Of(sb.ToString());
    }

    private static CelValue Reverse(ReadOnlySpan<CelValue> args)
    {
        var s = S(args[0]);
        var runes = new List<System.Text.Rune>(s.Length);
        foreach (var r in s.EnumerateRunes())
        {
            runes.Add(r);
        }
        runes.Reverse();
        var sb = new StringBuilder(s.Length);
        foreach (var r in runes)
        {
            sb.Append(r.ToString());
        }
        return CelValue.Of(sb.ToString());
    }

    private static CelValue BuildList(string[] parts)
    {
        var builder = ImmutableArray.CreateBuilder<CelValue>(parts.Length);
        foreach (var p in parts)
        {
            builder.Add(CelValue.Of(p));
        }
        return new ListValue(builder.ToImmutable());
    }
}
