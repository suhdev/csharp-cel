using System.Collections.Immutable;
using DotnetCel.Diagnostics;
using DotnetCel.Parsing;

namespace DotnetCel.UnitTests.Parser;

public sealed class LexerTests
{
    private static (ImmutableArray<Token> Tokens, DiagnosticBag Diagnostics) Lex(string src)
    {
        var bag = new DiagnosticBag();
        var tokens = Lexer.Tokenize(src, bag);
        return (tokens, bag);
    }

    private static TokenKind[] Kinds(string src)
    {
        var (tokens, bag) = Lex(src);
        Assert.False(bag.HasErrors, $"lexer reported errors: {string.Join(", ", bag.Select(static d => d.Message))}");
        // Strip trailing EOF for readability.
        return [.. tokens.Take(tokens.Length - 1).Select(static t => t.Kind)];
    }

    [Fact]
    public void Empty_Source_Yields_Only_Eof()
    {
        var (tokens, bag) = Lex("");
        Assert.False(bag.HasErrors);
        Assert.Single(tokens);
        Assert.Equal(TokenKind.EndOfFile, tokens[0].Kind);
    }

    [Fact]
    public void Whitespace_And_Comments_Are_Skipped()
    {
        var (tokens, bag) = Lex("  \t // comment to eol\n  42 // trailing");
        Assert.False(bag.HasErrors);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].Kind);
        Assert.Equal((ulong)42, tokens[0].Value);
        Assert.Equal(TokenKind.EndOfFile, tokens[^1].Kind);
    }

    [Fact]
    public void Identifiers_And_Keywords()
    {
        var kinds = Kinds("foo true false null in _x x1 x_y");
        Assert.Equal(
        [
            TokenKind.Identifier,
            TokenKind.True,
            TokenKind.False,
            TokenKind.Null,
            TokenKind.In,
            TokenKind.Identifier,
            TokenKind.Identifier,
            TokenKind.Identifier,
        ], kinds);
    }

    [Theory]
    [InlineData("let")]
    [InlineData("import")]
    [InlineData("return")]
    public void Reserved_Keywords_Tokenize_As_Reserved(string word)
    {
        var (tokens, bag) = Lex(word);
        Assert.False(bag.HasErrors);
        Assert.Equal(TokenKind.Reserved, tokens[0].Kind);
    }

    [Theory]
    [InlineData("0", 0UL)]
    [InlineData("123", 123UL)]
    [InlineData("9223372036854775807", 9223372036854775807UL)]
    [InlineData("0xFF", 0xFFUL)]
    [InlineData("0xff", 0xFFUL)]
    public void Int_Literals(string src, ulong expected)
    {
        var (tokens, bag) = Lex(src);
        Assert.False(bag.HasErrors);
        Assert.Equal(TokenKind.IntLiteral, tokens[0].Kind);
        Assert.Equal(expected, tokens[0].Value);
    }

    [Theory]
    [InlineData("0u", 0UL)]
    [InlineData("42U", 42UL)]
    public void Uint_Literals(string src, ulong expected)
    {
        var (tokens, bag) = Lex(src);
        Assert.False(bag.HasErrors);
        Assert.Equal(TokenKind.UintLiteral, tokens[0].Kind);
        Assert.Equal(expected, tokens[0].Value);
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("1.5e2", 150.0)]
    [InlineData("1e-3", 0.001)]
    [InlineData(".5", 0.5)]
    public void Double_Literals(string src, double expected)
    {
        var (tokens, bag) = Lex(src);
        Assert.False(bag.HasErrors);
        Assert.Equal(TokenKind.DoubleLiteral, tokens[0].Kind);
        Assert.Equal(expected, (double)tokens[0].Value!);
    }

    [Fact]
    public void Integer_Overflow_Reports_Error()
    {
        var (_, bag) = Lex("99999999999999999999"); // > ulong.MaxValue
        Assert.True(bag.HasErrors);
    }

    [Theory]
    [InlineData("\"hello\"", "hello")]
    [InlineData("'hello'", "hello")]
    [InlineData("\"a\\nb\"", "a\nb")]
    [InlineData("\"tab\\there\"", "tab\there")]
    [InlineData("\"q\\\"q\"", "q\"q")]
    [InlineData("'apos\\'apos'", "apos'apos")]
    [InlineData("\"\\x41\\x42\"", "AB")]
    [InlineData("\"\\u00e9\"", "é")]
    [InlineData("\"\\U0001F600\"", "😀")]
    [InlineData("\"\\101\\102\"", "AB")] // octal
    public void String_Escapes(string src, string expected)
    {
        var (tokens, bag) = Lex(src);
        Assert.False(bag.HasErrors, string.Join(", ", bag.Select(static d => d.Message)));
        Assert.Equal(TokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal(expected, tokens[0].Value);
    }

    [Fact]
    public void Raw_Strings_Do_Not_Interpret_Escapes()
    {
        var (tokens, bag) = Lex(@"r""a\nb""");
        Assert.False(bag.HasErrors);
        Assert.Equal("a\\nb", tokens[0].Value);
    }

    [Fact]
    public void Triple_Quoted_Strings_Span_Lines()
    {
        var src = "\"\"\"line1\nline2\"\"\"";
        var (tokens, bag) = Lex(src);
        Assert.False(bag.HasErrors);
        Assert.Equal("line1\nline2", tokens[0].Value);
    }

    [Fact]
    public void Bytes_Literal_From_Ascii()
    {
        var (tokens, bag) = Lex("b\"abc\"");
        Assert.False(bag.HasErrors);
        Assert.Equal(TokenKind.BytesLiteral, tokens[0].Kind);
        var bytes = (ImmutableArray<byte>)tokens[0].Value!;
        Assert.Equal((byte[])[0x61, 0x62, 0x63], bytes.ToArray());
    }

    [Fact]
    public void Bytes_Literal_From_Hex_Escapes()
    {
        var (tokens, bag) = Lex(@"b""\xDE\xAD""");
        Assert.False(bag.HasErrors);
        var bytes = (ImmutableArray<byte>)tokens[0].Value!;
        Assert.Equal((byte[])[0xDE, 0xAD], bytes.ToArray());
    }

    [Fact]
    public void Unicode_Escape_Forbidden_In_Bytes()
    {
        // The \u and \U *escapes* are forbidden in byte literals (cel-go parser/unescape.go).
        // Raw non-ASCII characters are accepted and UTF-8 encoded.
        Assert.True(Lex(@"b""\U000000e9""").Diagnostics.HasErrors);
        Assert.False(Lex(@"b""é""").Diagnostics.HasErrors);
    }

    [Fact]
    public void Bytes_Literal_Encodes_Non_Ascii_As_Utf8()
    {
        var (tokens, bag) = Lex(@"b""é""");
        Assert.False(bag.HasErrors);
        var bytes = (ImmutableArray<byte>)tokens[0].Value!;
        Assert.Equal((byte[])[0xC3, 0xA9], bytes.ToArray());
    }

    [Fact]
    public void Unterminated_String_Reports_Error()
    {
        var (_, bag) = Lex("\"oops");
        Assert.True(bag.HasErrors);
    }

    [Fact]
    public void Newline_In_Single_Quoted_String_Is_Error()
    {
        var (_, bag) = Lex("\"a\nb\"");
        Assert.True(bag.HasErrors);
    }

    [Fact]
    public void All_Operators_Tokenize()
    {
        var kinds = Kinds("+ - * / % ! ? : , . ( ) [ ] { } < <= > >= == != && || in");
        Assert.Equal(
        [
            TokenKind.Plus, TokenKind.Minus, TokenKind.Star, TokenKind.Slash, TokenKind.Percent,
            TokenKind.Bang, TokenKind.Question, TokenKind.Colon, TokenKind.Comma, TokenKind.Dot,
            TokenKind.LParen, TokenKind.RParen,
            TokenKind.LBracket, TokenKind.RBracket,
            TokenKind.LBrace, TokenKind.RBrace,
            TokenKind.Less, TokenKind.LessEqual, TokenKind.Greater, TokenKind.GreaterEqual,
            TokenKind.EqualEqual, TokenKind.BangEqual,
            TokenKind.AmpAmp, TokenKind.PipePipe,
            TokenKind.In,
        ], kinds);
    }

    [Fact]
    public void Single_Equal_Is_Error()
    {
        var (_, bag) = Lex("x = 1");
        Assert.True(bag.HasErrors);
    }

    [Fact]
    public void Single_Pipe_Is_Error()
    {
        var (_, bag) = Lex("a | b");
        Assert.True(bag.HasErrors);
    }

    [Fact]
    public void Source_Locations_Track_Lines_And_Columns()
    {
        var (tokens, bag) = Lex("a\n  + b");
        Assert.False(bag.HasErrors);
        Assert.Equal(1, tokens[0].Location.Line);
        Assert.Equal(1, tokens[0].Location.Column);
        Assert.Equal(2, tokens[1].Location.Line);  // '+'
        Assert.Equal(3, tokens[1].Location.Column);
        Assert.Equal(2, tokens[2].Location.Line);  // 'b'
        Assert.Equal(5, tokens[2].Location.Column);
    }
}
