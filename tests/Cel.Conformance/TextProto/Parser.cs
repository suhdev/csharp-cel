using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Cel.Conformance.TextProto;

/// <summary>
/// Hand-rolled parser for the protobuf text format subset used by cel-spec testdata. Only
/// supports the constructs that appear in those files: identifier-keyed fields with optional
/// colon, scalar values (int / double / quoted string / bool / enum-ident), nested
/// <c>{ ... }</c> messages, and bracketed <c>[ a, b, c ]</c> repeated values.
/// </summary>
internal sealed class TextProtoParser
{
    private readonly string _src;
    private int _pos;

    public TextProtoParser(string src) => _src = src;

    public static TextProtoMessage Parse(string source) => new TextProtoParser(source).ParseTopLevel();

    private TextProtoMessage ParseTopLevel()
    {
        SkipTrivia();
        var fields = new List<TextProtoField>();
        while (_pos < _src.Length)
        {
            fields.Add(ReadField());
            SkipTrivia();
        }
        return new TextProtoMessage(fields);
    }

    private TextProtoMessage ReadMessageBody(char close)
    {
        SkipTrivia();
        var fields = new List<TextProtoField>();
        while (_pos < _src.Length && _src[_pos] != close)
        {
            fields.Add(ReadField());
            SkipTrivia();
            // Allow optional ',' or ';' between fields.
            if (_pos < _src.Length && (_src[_pos] == ',' || _src[_pos] == ';'))
            {
                _pos++;
                SkipTrivia();
            }
        }
        if (_pos >= _src.Length)
        {
            throw new FormatException($"unterminated message; expected '{close}'");
        }
        _pos++; // consume close
        return new TextProtoMessage(fields);
    }

    private TextProtoField ReadField()
    {
        string name;
        // proto3 Any extension syntax: `[type.googleapis.com/foo.Bar]`
        if (_pos < _src.Length && _src[_pos] == '[')
        {
            _pos++;
            var start = _pos;
            while (_pos < _src.Length && _src[_pos] != ']')
            {
                _pos++;
            }
            if (_pos >= _src.Length)
            {
                throw new FormatException("unterminated bracketed field name");
            }
            name = "[" + _src[start.._pos] + "]";
            _pos++; // ]
        }
        else
        {
            name = ReadIdent();
        }
        SkipTrivia();
        // Optional colon between name and value (omitted before '{').
        if (_pos < _src.Length && _src[_pos] == ':')
        {
            _pos++;
            SkipTrivia();
        }
        var value = ReadValue();
        return new TextProtoField(name, value);
    }

    private TextProtoValue ReadValue()
    {
        SkipTrivia();
        if (_pos >= _src.Length)
        {
            throw new FormatException("unexpected end of input where value expected");
        }
        var c = _src[_pos];
        switch (c)
        {
            case '{': _pos++; return new TextProtoMessageValue(ReadMessageBody('}'));
            case '<': _pos++; return new TextProtoMessageValue(ReadMessageBody('>'));
            case '[': _pos++; return ReadListBody();
            case '"' or '\'': return new TextProtoString(ReadString(c));
        }
        if (char.IsAsciiDigit(c) || c == '-' || c == '+' || c == '.')
        {
            return ReadNumber();
        }
        if (char.IsAsciiLetter(c) || c == '_')
        {
            var ident = ReadIdent();
            return ident switch
            {
                "true" => TextProtoBool.True,
                "false" => TextProtoBool.False,
                _ => new TextProtoIdent(ident),
            };
        }
        throw new FormatException($"unexpected character '{c}' at offset {_pos}");
    }

    private TextProtoValue ReadListBody()
    {
        SkipTrivia();
        var items = new List<TextProtoValue>();
        while (_pos < _src.Length && _src[_pos] != ']')
        {
            items.Add(ReadValue());
            SkipTrivia();
            if (_pos < _src.Length && _src[_pos] == ',')
            {
                _pos++;
                SkipTrivia();
            }
        }
        if (_pos >= _src.Length)
        {
            throw new FormatException("unterminated list");
        }
        _pos++; // ]
        return new TextProtoListValue(items);
    }

    private string ReadIdent()
    {
        var start = _pos;
        while (_pos < _src.Length && (char.IsAsciiLetterOrDigit(_src[_pos]) || _src[_pos] == '_' || _src[_pos] == '.'))
        {
            _pos++;
        }
        if (start == _pos)
        {
            throw new FormatException($"expected identifier at offset {_pos}");
        }
        return _src[start.._pos];
    }

    private string ReadString(char quote)
    {
        _pos++; // consume opening quote
        var sb = new StringBuilder();
        while (_pos < _src.Length && _src[_pos] != quote)
        {
            var c = _src[_pos];
            if (c == '\\' && _pos + 1 < _src.Length)
            {
                _pos++;
                var esc = _src[_pos];
                switch (esc)
                {
                    case 'n': sb.Append('\n'); _pos++; break;
                    case 'r': sb.Append('\r'); _pos++; break;
                    case 't': sb.Append('\t'); _pos++; break;
                    case '\\': sb.Append('\\'); _pos++; break;
                    case '\'': sb.Append('\''); _pos++; break;
                    case '"': sb.Append('"'); _pos++; break;
                    case 'a': sb.Append('\a'); _pos++; break;
                    case 'b': sb.Append('\b'); _pos++; break;
                    case 'f': sb.Append('\f'); _pos++; break;
                    case 'v': sb.Append('\v'); _pos++; break;
                    case 'x' or 'X':
                        _pos++;
                        sb.Append((char)ReadHex(2));
                        break;
                    case 'u':
                        _pos++;
                        AppendUtf8Bytes(sb, ReadHex(4));
                        break;
                    case 'U':
                        _pos++;
                        AppendUtf8Bytes(sb, ReadHex(8));
                        break;
                    default:
                        if (char.IsAsciiDigit(esc))
                        {
                            // Octal: up to 3 digits
                            var val = 0;
                            var count = 0;
                            while (_pos < _src.Length && count < 3 && _src[_pos] is >= '0' and <= '7')
                            {
                                val = val * 8 + (_src[_pos] - '0');
                                _pos++;
                                count++;
                            }
                            sb.Append((char)val);
                        }
                        else
                        {
                            sb.Append(esc);
                            _pos++;
                        }
                        break;
                }
            }
            else
            {
                sb.Append(c);
                _pos++;
            }
        }
        if (_pos >= _src.Length)
        {
            throw new FormatException("unterminated string");
        }
        _pos++; // consume close quote
        // Implicit string concatenation: "foo" "bar" → "foobar"
        var result = sb.ToString();
        SkipTrivia();
        if (_pos < _src.Length && (_src[_pos] == '"' || _src[_pos] == '\''))
        {
            return result + ReadString(_src[_pos]);
        }
        return result;
    }

    /// <summary>
    /// Append the UTF-8 byte sequence for <paramref name="codepoint"/> as one char per byte.
    /// proto text format models strings as byte sequences, so <c>✌</c> contributes the
    /// three bytes <c>E2 9C 8C</c> rather than a single .NET char.
    /// </summary>
    private static void AppendUtf8Bytes(StringBuilder sb, int codepoint)
    {
        if (codepoint < 0 || codepoint > 0x10FFFF)
        {
            throw new FormatException($"unicode escape out of range: U+{codepoint:X}");
        }
        var s = char.ConvertFromUtf32(codepoint);
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        foreach (var b in bytes)
        {
            sb.Append((char)b);
        }
    }

    private int ReadHex(int digits)
    {
        if (_pos + digits > _src.Length)
        {
            throw new FormatException("truncated hex escape");
        }
        var slice = _src.AsSpan(_pos, digits);
        if (!int.TryParse(slice, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            throw new FormatException("malformed hex escape");
        }
        _pos += digits;
        return v;
    }

    private TextProtoValue ReadNumber()
    {
        var start = _pos;
        var negative = false;
        if (_src[_pos] is '+' or '-')
        {
            negative = _src[_pos] == '-';
            _pos++;
        }

        // Recognise `inf` / `nan` keywords (with optional sign).
        if (_pos < _src.Length && char.IsAsciiLetter(_src[_pos]))
        {
            var identStart = _pos;
            while (_pos < _src.Length && char.IsAsciiLetter(_src[_pos]))
            {
                _pos++;
            }
            var ident = _src[identStart.._pos].ToLowerInvariant();
            return ident switch
            {
                "inf" or "infinity" => new TextProtoDouble(negative ? double.NegativeInfinity : double.PositiveInfinity),
                "nan" => new TextProtoDouble(double.NaN),
                _ => throw new FormatException($"unexpected token in numeric context: {ident}"),
            };
        }
        var sawDot = false;
        var sawExp = false;
        while (_pos < _src.Length)
        {
            var c = _src[_pos];
            if (char.IsAsciiDigit(c)) { _pos++; }
            else if (c == '.' && !sawDot && !sawExp) { sawDot = true; _pos++; }
            else if ((c == 'e' || c == 'E') && !sawExp)
            {
                sawExp = true;
                _pos++;
                if (_pos < _src.Length && (_src[_pos] is '+' or '-')) { _pos++; }
            }
            else { break; }
        }

        var lexeme = _src[start.._pos];

        // Optional 'u' suffix indicates uint64.
        if (_pos < _src.Length && (_src[_pos] is 'u' or 'U') && !sawDot && !sawExp)
        {
            _pos++;
            if (ulong.TryParse(lexeme, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u))
            {
                return new TextProtoUint(u);
            }
        }

        if (sawDot || sawExp)
        {
            return new TextProtoDouble(double.Parse(lexeme, NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        if (long.TryParse(lexeme, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
        {
            return new TextProtoInt(i);
        }
        if (ulong.TryParse(lexeme, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u2))
        {
            return new TextProtoUint(u2);
        }
        throw new FormatException($"malformed number: {lexeme}");
    }

    private void SkipTrivia()
    {
        while (_pos < _src.Length)
        {
            var c = _src[_pos];
            if (c is ' ' or '\t' or '\r' or '\n' or '\f' or '\v')
            {
                _pos++;
            }
            else if (c == '#')
            {
                while (_pos < _src.Length && _src[_pos] != '\n') { _pos++; }
            }
            else
            {
                return;
            }
        }
    }
}
