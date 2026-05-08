namespace Cel.Parsing;

/// <summary>Lexical token categories produced by <see cref="Lexer"/>.</summary>
public enum TokenKind
{
    // ── literals ──
    Identifier,
    IntLiteral,
    UintLiteral,
    DoubleLiteral,
    StringLiteral,
    BytesLiteral,
    True,
    False,
    Null,

    /// <summary>A reserved keyword used in error reporting (e.g. <c>let</c>).</summary>
    Reserved,

    // ── single-char operators / punctuation ──
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Bang,
    Question,
    Colon,
    Comma,
    Dot,
    LParen,
    RParen,
    LBracket,
    RBracket,
    LBrace,
    RBrace,

    // ── multi-char operators ──
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    EqualEqual,
    BangEqual,
    AmpAmp,
    PipePipe,

    /// <summary>The relational <c>in</c> keyword.</summary>
    In,

    EndOfFile,
}
