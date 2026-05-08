using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Cel.Diagnostics;

namespace Cel.Parsing;

/// <summary>
/// Hand-rolled lexer for the CEL grammar. Produces a flat <see cref="ImmutableArray{T}"/> of
/// tokens (always terminated by <see cref="TokenKind.EndOfFile"/>); errors are reported to the
/// supplied <see cref="DiagnosticBag"/> and recovery skips the offending character so that
/// downstream phases see a usable stream.
/// </summary>
public sealed class Lexer
{
    private static readonly FrozenSet<string> ReservedKeywords = new[]
    {
        "as", "break", "const", "continue", "else", "for", "function", "if",
        "import", "let", "loop", "package", "namespace", "return", "var", "void", "while",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly string _source;
    private readonly DiagnosticBag _diagnostics;
    private readonly string? _sourceName;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    public Lexer(string source, DiagnosticBag diagnostics, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(diagnostics);
        _source = source;
        _diagnostics = diagnostics;
        _sourceName = sourceName;
    }

    public static ImmutableArray<Token> Tokenize(string source, DiagnosticBag diagnostics, string? sourceName = null)
        => new Lexer(source, diagnostics, sourceName).TokenizeInternal();

    private ImmutableArray<Token> TokenizeInternal()
    {
        var tokens = ImmutableArray.CreateBuilder<Token>();
        while (true)
        {
            SkipTrivia();
            if (_pos >= _source.Length)
            {
                tokens.Add(new Token(TokenKind.EndOfFile, "", CurrentLocation()));
                return tokens.ToImmutable();
            }

            var t = ScanToken();
            if (t is not null)
            {
                tokens.Add(t.Value);
            }
        }
    }

    // ── trivia ──

    private void SkipTrivia()
    {
        while (_pos < _source.Length)
        {
            var c = _source[_pos];
            if (c is ' ' or '\t' or '\r' or '\f' or '\v')
            {
                Advance();
            }
            else if (c == '\n')
            {
                AdvanceNewline();
            }
            else if (c == '/' && _pos + 1 < _source.Length && _source[_pos + 1] == '/')
            {
                while (_pos < _source.Length && _source[_pos] != '\n')
                {
                    Advance();
                }
            }
            else
            {
                return;
            }
        }
    }

    // ── token dispatch ──

    private Token? ScanToken()
    {
        var loc = CurrentLocation();
        var c = _source[_pos];

        // String / bytes literal with prefix?
        if (TryStringPrefix(out var isRaw, out var isBytes, out var prefixLen))
        {
            var quote = _source[_pos + prefixLen];
            if (quote == '"' || quote == '\'')
            {
                return ScanString(loc, prefixLen, isRaw, isBytes);
            }
        }

        if (c == '"' || c == '\'')
        {
            return ScanString(loc, 0, isRaw: false, isBytes: false);
        }

        if (IsIdentifierStart(c))
        {
            return ScanIdentifier(loc);
        }

        if (char.IsAsciiDigit(c) || (c == '.' && _pos + 1 < _source.Length && char.IsAsciiDigit(_source[_pos + 1])))
        {
            return ScanNumber(loc);
        }

        return ScanOperator(loc);
    }

    private bool TryStringPrefix(out bool isRaw, out bool isBytes, out int prefixLen)
    {
        isRaw = false;
        isBytes = false;
        prefixLen = 0;

        if (_pos + 1 >= _source.Length)
        {
            return false;
        }

        var c0 = _source[_pos];
        var c1 = _source[_pos + 1];

        // Single-letter prefix: r, R, b, B
        if ((c0 is 'r' or 'R' or 'b' or 'B') && (c1 == '"' || c1 == '\''))
        {
            isRaw = c0 is 'r' or 'R';
            isBytes = c0 is 'b' or 'B';
            prefixLen = 1;
            return true;
        }

        // Two-letter prefix: rb, rB, Rb, RB, br, bR, Br, BR
        if (_pos + 2 < _source.Length)
        {
            var c2 = _source[_pos + 2];
            if (c2 == '"' || c2 == '\'')
            {
                var pair = (char.ToLowerInvariant(c0), char.ToLowerInvariant(c1));
                if (pair is ('r', 'b') or ('b', 'r'))
                {
                    isRaw = true;
                    isBytes = true;
                    prefixLen = 2;
                    return true;
                }
            }
        }

        return false;
    }

    // ── identifier / keyword ──

    private Token ScanIdentifier(SourceLocation start)
    {
        var startPos = _pos;
        while (_pos < _source.Length && IsIdentifierContinue(_source[_pos]))
        {
            Advance();
        }
        var lexeme = _source[startPos.._pos];
        var loc = start with { Length = _pos - startPos };

        return lexeme switch
        {
            "true" => new Token(TokenKind.True, lexeme, loc, true),
            "false" => new Token(TokenKind.False, lexeme, loc, false),
            "null" => new Token(TokenKind.Null, lexeme, loc, null),
            "in" => new Token(TokenKind.In, lexeme, loc),
            _ when ReservedKeywords.Contains(lexeme) => new Token(TokenKind.Reserved, lexeme, loc),
            _ => new Token(TokenKind.Identifier, lexeme, loc),
        };
    }

    // ── number ──

    private Token? ScanNumber(SourceLocation start)
    {
        var startPos = _pos;
        var sawDot = false;
        var sawExp = false;
        var isHex = false;

        if (_source[_pos] == '0' && _pos + 1 < _source.Length && (_source[_pos + 1] is 'x' or 'X'))
        {
            isHex = true;
            Advance(2);
            var hexStart = _pos;
            while (_pos < _source.Length && IsHexDigit(_source[_pos]))
            {
                Advance();
            }
            if (_pos == hexStart)
            {
                Error("CEL-0001", "expected hex digits after '0x'", start);
                return null;
            }
        }
        else
        {
            while (_pos < _source.Length && char.IsAsciiDigit(_source[_pos]))
            {
                Advance();
            }

            if (_pos < _source.Length && _source[_pos] == '.')
            {
                sawDot = true;
                Advance();
                while (_pos < _source.Length && char.IsAsciiDigit(_source[_pos]))
                {
                    Advance();
                }
            }

            if (_pos < _source.Length && (_source[_pos] is 'e' or 'E'))
            {
                sawExp = true;
                Advance();
                if (_pos < _source.Length && (_source[_pos] is '+' or '-'))
                {
                    Advance();
                }
                var expStart = _pos;
                while (_pos < _source.Length && char.IsAsciiDigit(_source[_pos]))
                {
                    Advance();
                }
                if (_pos == expStart)
                {
                    Error("CEL-0002", "expected digits in exponent", start);
                    return null;
                }
            }
        }

        var isUnsigned = false;
        if (_pos < _source.Length && (_source[_pos] is 'u' or 'U') && !sawDot && !sawExp)
        {
            isUnsigned = true;
            Advance();
        }

        var lexeme = _source[startPos.._pos];
        var loc = start with { Length = _pos - startPos };

        if (sawDot || sawExp)
        {
            if (!double.TryParse(lexeme, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                Error("CEL-0003", $"malformed double literal: {lexeme}", loc);
                return null;
            }
            return new Token(TokenKind.DoubleLiteral, lexeme, loc, d);
        }

        var digits = isHex ? lexeme[2..] : lexeme;
        if (isUnsigned)
        {
            digits = digits[..^1];
        }
        var style = isHex ? NumberStyles.HexNumber : NumberStyles.Integer;

        if (!ulong.TryParse(digits, style, CultureInfo.InvariantCulture, out var magnitude))
        {
            Error("CEL-0004", $"integer literal out of range: {lexeme}", loc);
            return null;
        }

        if (isUnsigned)
        {
            return new Token(TokenKind.UintLiteral, lexeme, loc, magnitude);
        }

        // Positive integer literals must fit in int64. Negative (handled by the parser via unary
        // '-') accepts magnitudes up to long.MaxValue + 1 to allow long.MinValue.
        return new Token(TokenKind.IntLiteral, lexeme, loc, magnitude);
    }

    // ── string / bytes ──

    private Token? ScanString(SourceLocation start, int prefixLen, bool isRaw, bool isBytes)
    {
        var startPos = _pos;
        Advance(prefixLen);
        var quote = _source[_pos];
        var triple = _pos + 2 < _source.Length
            && _source[_pos + 1] == quote
            && _source[_pos + 2] == quote;
        Advance(triple ? 3 : 1);

        var sb = isBytes ? null : new StringBuilder();
        var bytes = isBytes ? new List<byte>() : null;

        while (_pos < _source.Length)
        {
            if (IsStringTerminator(quote, triple))
            {
                Advance(triple ? 3 : 1);
                var lexeme = _source[startPos.._pos];
                var loc = start with { Length = _pos - startPos };
                if (isBytes)
                {
                    return new Token(TokenKind.BytesLiteral, lexeme, loc, ImmutableArray.CreateRange(bytes!));
                }
                return new Token(TokenKind.StringLiteral, lexeme, loc, sb!.ToString());
            }

            var c = _source[_pos];

            if (c == '\n' && !triple)
            {
                Error("CEL-0010", "unterminated string literal (newline)", start);
                return null;
            }

            if (c == '\\' && !isRaw)
            {
                if (!HandleEscape(start, isBytes, sb, bytes))
                {
                    return null;
                }
                continue;
            }

            // Regular char
            if (isBytes)
            {
                AppendBytesChar(c, bytes!);
            }
            else
            {
                sb!.Append(c);
            }
            if (c == '\n')
            {
                AdvanceNewline();
            }
            else
            {
                Advance();
            }
        }

        Error("CEL-0011", "unterminated string literal", start);
        return null;
    }

    private bool IsStringTerminator(char quote, bool triple)
    {
        if (triple)
        {
            return _pos + 2 < _source.Length
                && _source[_pos] == quote
                && _source[_pos + 1] == quote
                && _source[_pos + 2] == quote;
        }
        return _source[_pos] == quote;
    }

    private static void AppendBytesChar(char c, List<byte> sink)
    {
        if (c <= 0x7F)
        {
            sink.Add((byte)c);
            return;
        }

        Span<char> chars = [c];
        Span<byte> buf = stackalloc byte[4];
        var written = Encoding.UTF8.GetBytes(chars, buf);
        for (var i = 0; i < written; i++)
        {
            sink.Add(buf[i]);
        }
    }

    // ── escape sequences ──

    private bool HandleEscape(SourceLocation strStart, bool isBytes, StringBuilder? sb, List<byte>? bytes)
    {
        // Already at backslash.
        var escStart = CurrentLocation();
        Advance(); // consume '\'
        if (_pos >= _source.Length)
        {
            Error("CEL-0020", "trailing backslash in string literal", strStart);
            return false;
        }

        var c = _source[_pos];
        switch (c)
        {
            case 'a': EmitChar('\a', isBytes, sb, bytes); Advance(); return true;
            case 'b': EmitChar('\b', isBytes, sb, bytes); Advance(); return true;
            case 'f': EmitChar('\f', isBytes, sb, bytes); Advance(); return true;
            case 'n': EmitChar('\n', isBytes, sb, bytes); Advance(); return true;
            case 'r': EmitChar('\r', isBytes, sb, bytes); Advance(); return true;
            case 't': EmitChar('\t', isBytes, sb, bytes); Advance(); return true;
            case 'v': EmitChar('\v', isBytes, sb, bytes); Advance(); return true;
            case '\\': EmitChar('\\', isBytes, sb, bytes); Advance(); return true;
            case '\'': EmitChar('\'', isBytes, sb, bytes); Advance(); return true;
            case '"': EmitChar('"', isBytes, sb, bytes); Advance(); return true;
            case '?': EmitChar('?', isBytes, sb, bytes); Advance(); return true;
            case '`': EmitChar('`', isBytes, sb, bytes); Advance(); return true;
            case '0' or '1' or '2' or '3':
                return HandleOctalEscape(escStart, isBytes, sb, bytes);
            case 'x' or 'X':
                return HandleHexEscape(escStart, isBytes, sb, bytes, 2);
            case 'u':
                if (isBytes)
                {
                    Error("CEL-0021", @"\u escape is not allowed in bytes literal", escStart);
                    return false;
                }
                Advance();
                return HandleUnicodeEscape(escStart, sb!, 4);
            case 'U':
                if (isBytes)
                {
                    Error("CEL-0022", @"\U escape is not allowed in bytes literal", escStart);
                    return false;
                }
                Advance();
                return HandleUnicodeEscape(escStart, sb!, 8);
            default:
                Error("CEL-0023", $"invalid escape sequence: \\{c}", escStart);
                Advance();
                return true;
        }
    }

    private bool HandleOctalEscape(SourceLocation start, bool isBytes, StringBuilder? sb, List<byte>? bytes)
    {
        // Three octal digits expected.
        if (_pos + 2 >= _source.Length)
        {
            Error("CEL-0024", "octal escape requires 3 digits", start);
            return false;
        }
        var d1 = _source[_pos];
        var d2 = _source[_pos + 1];
        var d3 = _source[_pos + 2];
        if (!IsOctal(d1) || !IsOctal(d2) || !IsOctal(d3))
        {
            Error("CEL-0024", "octal escape requires 3 digits", start);
            return false;
        }
        var value = (d1 - '0') * 64 + (d2 - '0') * 8 + (d3 - '0');
        Advance(3);
        if (isBytes)
        {
            bytes!.Add((byte)value);
        }
        else
        {
            sb!.Append((char)value);
        }
        return true;
    }

    private bool HandleHexEscape(SourceLocation start, bool isBytes, StringBuilder? sb, List<byte>? bytes, int digits)
    {
        Advance(); // consume 'x'/'X'
        if (_pos + digits > _source.Length)
        {
            Error("CEL-0025", $"\\x escape requires {digits} hex digits", start);
            return false;
        }
        var span = _source.AsSpan(_pos, digits);
        if (!int.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            Error("CEL-0025", $"\\x escape requires {digits} hex digits", start);
            return false;
        }
        Advance(digits);
        if (isBytes)
        {
            bytes!.Add((byte)value);
        }
        else
        {
            sb!.Append((char)value);
        }
        return true;
    }

    private bool HandleUnicodeEscape(SourceLocation start, StringBuilder sb, int digits)
    {
        if (_pos + digits > _source.Length)
        {
            Error("CEL-0026", $"unicode escape requires {digits} hex digits", start);
            return false;
        }
        var span = _source.AsSpan(_pos, digits);
        if (!int.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
        {
            Error("CEL-0026", $"unicode escape requires {digits} hex digits", start);
            return false;
        }
        Advance(digits);
        if (codePoint is < 0 or > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
        {
            Error("CEL-0027", $"unicode escape value out of range: {codePoint:X}", start);
            return false;
        }
        sb.Append(char.ConvertFromUtf32(codePoint));
        return true;
    }

    private static void EmitChar(char c, bool isBytes, StringBuilder? sb, List<byte>? bytes)
    {
        if (isBytes)
        {
            bytes!.Add((byte)c);
        }
        else
        {
            sb!.Append(c);
        }
    }

    // ── operators ──

    private Token? ScanOperator(SourceLocation start)
    {
        var c = _source[_pos];
        switch (c)
        {
            case '+': Advance(); return new Token(TokenKind.Plus, "+", start with { Length = 1 });
            case '-': Advance(); return new Token(TokenKind.Minus, "-", start with { Length = 1 });
            case '*': Advance(); return new Token(TokenKind.Star, "*", start with { Length = 1 });
            case '/': Advance(); return new Token(TokenKind.Slash, "/", start with { Length = 1 });
            case '%': Advance(); return new Token(TokenKind.Percent, "%", start with { Length = 1 });
            case '?': Advance(); return new Token(TokenKind.Question, "?", start with { Length = 1 });
            case ':': Advance(); return new Token(TokenKind.Colon, ":", start with { Length = 1 });
            case ',': Advance(); return new Token(TokenKind.Comma, ",", start with { Length = 1 });
            case '.': Advance(); return new Token(TokenKind.Dot, ".", start with { Length = 1 });
            case '(': Advance(); return new Token(TokenKind.LParen, "(", start with { Length = 1 });
            case ')': Advance(); return new Token(TokenKind.RParen, ")", start with { Length = 1 });
            case '[': Advance(); return new Token(TokenKind.LBracket, "[", start with { Length = 1 });
            case ']': Advance(); return new Token(TokenKind.RBracket, "]", start with { Length = 1 });
            case '{': Advance(); return new Token(TokenKind.LBrace, "{", start with { Length = 1 });
            case '}': Advance(); return new Token(TokenKind.RBrace, "}", start with { Length = 1 });
            case '<':
                Advance();
                if (_pos < _source.Length && _source[_pos] == '=')
                {
                    Advance();
                    return new Token(TokenKind.LessEqual, "<=", start with { Length = 2 });
                }
                return new Token(TokenKind.Less, "<", start with { Length = 1 });
            case '>':
                Advance();
                if (_pos < _source.Length && _source[_pos] == '=')
                {
                    Advance();
                    return new Token(TokenKind.GreaterEqual, ">=", start with { Length = 2 });
                }
                return new Token(TokenKind.Greater, ">", start with { Length = 1 });
            case '=':
                Advance();
                if (_pos < _source.Length && _source[_pos] == '=')
                {
                    Advance();
                    return new Token(TokenKind.EqualEqual, "==", start with { Length = 2 });
                }
                Error("CEL-0030", "single '=' is not a valid operator (use '==')", start);
                return null;
            case '!':
                Advance();
                if (_pos < _source.Length && _source[_pos] == '=')
                {
                    Advance();
                    return new Token(TokenKind.BangEqual, "!=", start with { Length = 2 });
                }
                return new Token(TokenKind.Bang, "!", start with { Length = 1 });
            case '&':
                if (_pos + 1 < _source.Length && _source[_pos + 1] == '&')
                {
                    Advance(2);
                    return new Token(TokenKind.AmpAmp, "&&", start with { Length = 2 });
                }
                Error("CEL-0031", "'&' must be paired ('&&')", start);
                Advance();
                return null;
            case '|':
                if (_pos + 1 < _source.Length && _source[_pos + 1] == '|')
                {
                    Advance(2);
                    return new Token(TokenKind.PipePipe, "||", start with { Length = 2 });
                }
                Error("CEL-0032", "'|' must be paired ('||')", start);
                Advance();
                return null;
            default:
                Error("CEL-0033", $"unexpected character: '{c}' (U+{(int)c:X4})", start);
                Advance();
                return null;
        }
    }

    // ── helpers ──

    private SourceLocation CurrentLocation() => new(_line, _col, _pos);

    private void Advance(int n = 1)
    {
        for (var i = 0; i < n; i++)
        {
            if (_pos >= _source.Length)
            {
                return;
            }
            if (_source[_pos] == '\n')
            {
                AdvanceNewline();
            }
            else
            {
                _pos++;
                _col++;
            }
        }
    }

    private void AdvanceNewline()
    {
        _pos++;
        _line++;
        _col = 1;
    }

    private static bool IsIdentifierStart(char c) =>
        char.IsAsciiLetter(c) || c == '_';

    private static bool IsIdentifierContinue(char c) =>
        char.IsAsciiLetterOrDigit(c) || c == '_';

    private static bool IsHexDigit(char c) =>
        char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool IsOctal(char c) => c >= '0' && c <= '7';

    private void Error(string code, string message, SourceLocation location) =>
        _diagnostics.Error(code, message, location, _sourceName);
}
