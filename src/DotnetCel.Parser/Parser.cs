using System.Collections.Immutable;
using DotnetCel.Ast;
using DotnetCel.Diagnostics;

namespace DotnetCel.Parsing;

/// <summary>
/// Hand-rolled recursive-descent / Pratt parser for the CEL grammar. Produces an AST that
/// matches the shape of cel-go: every operator becomes a <see cref="CallExpr"/> against the
/// canonical names in <see cref="Operators"/>; macros (<c>has</c>, <c>all</c>, <c>exists</c>,
/// <c>exists_one</c>, <c>map</c>, <c>filter</c>) are expanded inline to <see cref="ComprehensionExpr"/>
/// or test-only <see cref="SelectExpr"/>.
/// </summary>
public sealed class Parser
{
    private const int MaxRecursionDepth = 250;

    private readonly ImmutableArray<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private readonly ExprBuilder _builder;
    private readonly IdGenerator _ids;
    private readonly SourceInfoBuilder _sourceInfo;
    private readonly string? _sourceName;
    private readonly ImmutableArray<CelMacro> _macros;
    private int _pos;
    private int _depth;

    private Parser(
        ImmutableArray<Token> tokens,
        DiagnosticBag diagnostics,
        string? sourceName,
        string? source,
        ImmutableArray<CelMacro> macros)
    {
        _tokens = tokens;
        _diagnostics = diagnostics;
        _sourceName = sourceName;
        _sourceInfo = new SourceInfoBuilder { Source = source };
        _ids = new IdGenerator();
        _builder = new ExprBuilder(_ids, _sourceInfo);
        _macros = macros;
    }

    /// <summary>
    /// Parse a CEL expression. Returns a <see cref="ParseResult"/> containing the AST (when
    /// recoverable), the source-info side-table, and all diagnostics from both lex and parse.
    /// </summary>
    public static ParseResult Parse(string source, string? sourceName = null) =>
        Parse(source, sourceName, ImmutableArray<CelMacro>.Empty);

    /// <summary>
    /// Parse with extension-supplied macros. Macros are consulted after the parser's hardcoded
    /// set (<c>has</c>, <c>all</c>, <c>exists</c>, <c>exists_one</c>, <c>map</c>, <c>filter</c>);
    /// for namespaced macros (<see cref="CelMacro.IsReceiverStyle"/> = false), the receiver
    /// chain is flattened to a dotted name and matched against <see cref="CelMacro.Name"/>.
    /// </summary>
    public static ParseResult Parse(string source, string? sourceName, ImmutableArray<CelMacro> macros)
    {
        ArgumentNullException.ThrowIfNull(source);
        var diagnostics = new DiagnosticBag();
        var tokens = Lexer.Tokenize(source, diagnostics, sourceName);
        var parser = new Parser(tokens, diagnostics, sourceName, source, macros);
        var expr = parser.ParseTopLevel();
        return new ParseResult(expr, parser._sourceInfo.Build(), [.. diagnostics]);
    }

    // ── token helpers ──

    private Token Current => _tokens[_pos];
    private Token Peek(int offset = 1) =>
        _tokens[Math.Min(_pos + offset, _tokens.Length - 1)];

    private bool Check(TokenKind kind) => Current.Kind == kind;

    private bool Match(TokenKind kind)
    {
        if (!Check(kind))
        {
            return false;
        }
        _pos++;
        return true;
    }

    private Token Consume() => _tokens[_pos++];

    private Token Expect(TokenKind kind, string what)
    {
        if (Check(kind))
        {
            return Consume();
        }
        Error("CEL-1000", $"expected {what}, got '{Current.Lexeme}'", Current.Location);
        return Current;
    }

    private void Error(string code, string message, SourceLocation location) =>
        _diagnostics.Error(code, message, location, _sourceName);

    private Expr ErrorPlaceholder(SourceLocation loc) =>
        _builder.Identifier("@error@", loc);

    private IDisposable EnterRecursion(SourceLocation loc)
    {
        if (++_depth > MaxRecursionDepth)
        {
            Error("CEL-1099", "expression nests too deeply", loc);
            throw new RecursionLimitExceeded();
        }
        return new RecursionGuard(this);
    }

    private sealed class RecursionGuard(Parser p) : IDisposable
    {
        public void Dispose() => p._depth--;
    }

    private sealed class RecursionLimitExceeded : Exception;

    // ── grammar ──

    private Expr? ParseTopLevel()
    {
        try
        {
            var e = ParseExpression();
            if (!Check(TokenKind.EndOfFile))
            {
                Error("CEL-1001", $"unexpected token after expression: '{Current.Lexeme}'", Current.Location);
            }
            return e;
        }
        catch (RecursionLimitExceeded)
        {
            return null;
        }
    }

    // expr : conditionalOr ('?' conditionalOr ':' expr)? ;
    private Expr ParseExpression()
    {
        using var _ = EnterRecursion(Current.Location);
        var cond = ParseLogicalOr();
        if (!Check(TokenKind.Question))
        {
            return cond;
        }
        var loc = Current.Location;
        Consume(); // '?'
        var thenE = ParseLogicalOr();
        Expect(TokenKind.Colon, "':'");
        var elseE = ParseExpression();
        return _builder.Call(null, Operators.Conditional, [cond, thenE, elseE], loc);
    }

    // conditionalOr : conditionalAnd ('||' conditionalAnd)* ;
    private Expr ParseLogicalOr()
    {
        var left = ParseLogicalAnd();
        while (Check(TokenKind.PipePipe))
        {
            var loc = Current.Location;
            Consume();
            var right = ParseLogicalAnd();
            left = _builder.Call(null, Operators.LogicalOr, [left, right], loc);
        }
        return left;
    }

    // conditionalAnd : relation ('&&' relation)* ;
    private Expr ParseLogicalAnd()
    {
        var left = ParseRelation();
        while (Check(TokenKind.AmpAmp))
        {
            var loc = Current.Location;
            Consume();
            var right = ParseRelation();
            left = _builder.Call(null, Operators.LogicalAnd, [left, right], loc);
        }
        return left;
    }

    // relation : addition (relop addition)* ;
    private Expr ParseRelation()
    {
        var left = ParseAdditive();
        while (true)
        {
            var op = RelationalOperator();
            if (op is null)
            {
                return left;
            }
            var loc = Current.Location;
            Consume();
            var right = ParseAdditive();
            left = _builder.Call(null, op, [left, right], loc);
        }
    }

    private string? RelationalOperator() => Current.Kind switch
    {
        TokenKind.Less => Operators.Less,
        TokenKind.LessEqual => Operators.LessEqual,
        TokenKind.Greater => Operators.Greater,
        TokenKind.GreaterEqual => Operators.GreaterEqual,
        TokenKind.EqualEqual => Operators.Equal,
        TokenKind.BangEqual => Operators.NotEqual,
        TokenKind.In => Operators.In,
        _ => null,
    };

    // addition : multiplication (('+'|'-') multiplication)* ;
    private Expr ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Check(TokenKind.Plus) || Check(TokenKind.Minus))
        {
            var op = Check(TokenKind.Plus) ? Operators.Add : Operators.Subtract;
            var loc = Current.Location;
            Consume();
            var right = ParseMultiplicative();
            left = _builder.Call(null, op, [left, right], loc);
        }
        return left;
    }

    // multiplication : unary (('*'|'/'|'%') unary)* ;
    private Expr ParseMultiplicative()
    {
        var left = ParseUnary();
        while (true)
        {
            string op;
            if (Check(TokenKind.Star)) { op = Operators.Multiply; }
            else if (Check(TokenKind.Slash)) { op = Operators.Divide; }
            else if (Check(TokenKind.Percent)) { op = Operators.Modulo; }
            else { return left; }
            var loc = Current.Location;
            Consume();
            var right = ParseUnary();
            left = _builder.Call(null, op, [left, right], loc);
        }
    }

    // unary : '!'+ member | '-'+ member | member ;
    private Expr ParseUnary()
    {
        if (Check(TokenKind.Bang))
        {
            var loc = Current.Location;
            Consume();
            var operand = ParseUnary();
            return _builder.Call(null, Operators.LogicalNot, [operand], loc);
        }
        if (Check(TokenKind.Minus))
        {
            var loc = Current.Location;
            Consume();
            // Special case: '-' immediately followed by an integer or double literal folds into
            // a negative literal. This is required so that -9223372036854775808 (long.MinValue)
            // can be expressed.
            if (Check(TokenKind.IntLiteral))
            {
                var t = Consume();
                var magnitude = (ulong)t.Value!;
                if (magnitude > unchecked((ulong)long.MaxValue) + 1UL)
                {
                    Error("CEL-1002", $"integer literal out of range: -{magnitude}", t.Location);
                    return ErrorPlaceholder(loc);
                }
                long value = magnitude == unchecked((ulong)long.MaxValue) + 1UL
                    ? long.MinValue
                    : -(long)magnitude;
                return _builder.Constant(new IntConstant(value), loc);
            }
            if (Check(TokenKind.DoubleLiteral))
            {
                var t = Consume();
                return _builder.Constant(new DoubleConstant(-(double)t.Value!), loc);
            }
            var operand = ParseUnary();
            return _builder.Call(null, Operators.Negate, [operand], loc);
        }
        return ParseMember(ParsePrimary());
    }

    // member : primary
    //        | member '.' '?'? IDENT ('(' args ')')?     # SelectOrCall
    //        | member '[' '?'? expr ']'                   # Index
    //        | member '{' fieldInits '}'                  # CreateMessage
    //        ;
    private Expr ParseMember(Expr operand)
    {
        while (true)
        {
            if (Check(TokenKind.Dot))
            {
                var loc = Current.Location;
                Consume();
                var optional = Match(TokenKind.Question);
                if (!Check(TokenKind.Identifier))
                {
                    Error("CEL-1003", "expected identifier after '.'", Current.Location);
                    return ErrorPlaceholder(loc);
                }
                var ident = Consume();
                if (Check(TokenKind.LParen))
                {
                    var args = ParseArgumentList();
                    operand = MakeReceiverCall(operand, ident.Lexeme, args, loc, optional);
                }
                else if (optional)
                {
                    operand = _builder.Call(
                        null,
                        Operators.OptSelect,
                        [operand, _builder.Constant(new StringConstant(ident.Lexeme), ident.Location)],
                        loc);
                }
                else
                {
                    operand = _builder.Select(operand, ident.Lexeme, loc);
                }
            }
            else if (Check(TokenKind.LBracket))
            {
                var loc = Current.Location;
                Consume();
                var optional = Match(TokenKind.Question);
                var index = ParseExpression();
                Expect(TokenKind.RBracket, "']'");
                operand = _builder.Call(
                    null,
                    optional ? Operators.OptIndex : Operators.Index,
                    [operand, index],
                    loc);
            }
            else if (Check(TokenKind.LBrace))
            {
                var typeName = TryFlattenSelectChain(operand);
                if (typeName is null)
                {
                    return operand;
                }
                operand = ParseStructLiteral(typeName, operand);
            }
            else
            {
                return operand;
            }
        }
    }

    /// <summary>
    /// Attempt to flatten a chain of <see cref="SelectExpr"/>/<see cref="IdentifierExpr"/> nodes
    /// into a dotted type name suitable for a struct literal. Returns null if the chain contains
    /// any non-identifier expression.
    /// </summary>
    private static string? TryFlattenSelectChain(Expr e)
    {
        var parts = new Stack<string>();
        var cur = e;
        while (true)
        {
            switch (cur)
            {
                case SelectExpr s when !s.TestOnly:
                    parts.Push(s.Field);
                    cur = s.Operand;
                    break;
                case IdentifierExpr ident:
                    parts.Push(ident.Name);
                    return string.Join('.', parts);
                default:
                    return null;
            }
        }
    }

    // primary
    //   : '.'? IDENT ('(' args ')')?     # IdentOrGlobalCall
    //   | '(' e=expr ')'                 # Nested
    //   | '[' (optExpr (',' optExpr)*)? ','? ']'  # CreateList
    //   | '{' (mapEntry (',' mapEntry)*)? ','? '}' # CreateMap
    //   | literal                        # ConstantLiteral
    //   ;
    private Expr ParsePrimary()
    {
        using var _ = EnterRecursion(Current.Location);
        var loc = Current.Location;

        switch (Current.Kind)
        {
            case TokenKind.LParen:
            {
                Consume();
                var e = ParseExpression();
                Expect(TokenKind.RParen, "')'");
                return e;
            }
            case TokenKind.LBracket:
                return ParseListLiteral();
            case TokenKind.LBrace:
                return ParseMapLiteral();
            case TokenKind.IntLiteral:
            {
                var t = Consume();
                var magnitude = (ulong)t.Value!;
                if (magnitude > (ulong)long.MaxValue)
                {
                    Error("CEL-1004", $"integer literal out of range: {magnitude}", t.Location);
                    return ErrorPlaceholder(loc);
                }
                return _builder.Constant(new IntConstant((long)magnitude), loc);
            }
            case TokenKind.UintLiteral:
            {
                var t = Consume();
                return _builder.Constant(new UintConstant((ulong)t.Value!), loc);
            }
            case TokenKind.DoubleLiteral:
            {
                var t = Consume();
                return _builder.Constant(new DoubleConstant((double)t.Value!), loc);
            }
            case TokenKind.StringLiteral:
            {
                var t = Consume();
                return _builder.Constant(new StringConstant((string)t.Value!), loc);
            }
            case TokenKind.BytesLiteral:
            {
                var t = Consume();
                return _builder.Constant(new BytesConstant((ImmutableArray<byte>)t.Value!), loc);
            }
            case TokenKind.True:
                Consume();
                return _builder.Constant(new BoolConstant(true), loc);
            case TokenKind.False:
                Consume();
                return _builder.Constant(new BoolConstant(false), loc);
            case TokenKind.Null:
                Consume();
                return _builder.Constant(NullConstant.Instance, loc);
            case TokenKind.Dot:
            {
                Consume();
                if (!Check(TokenKind.Identifier))
                {
                    Error("CEL-1005", "expected identifier after leading '.'", Current.Location);
                    return ErrorPlaceholder(loc);
                }
                var ident = Consume();
                if (Check(TokenKind.LParen))
                {
                    var args = ParseArgumentList();
                    return MakeGlobalCall("." + ident.Lexeme, args, loc);
                }
                // Leading-dot identifier is encoded by prefixing the name with '.', mirroring
                // cel-go's convention. The checker treats this as an absolute reference, bypassing
                // container-based qualification.
                return _builder.Identifier("." + ident.Lexeme, loc);
            }
            case TokenKind.Identifier:
            {
                var ident = Consume();
                if (Check(TokenKind.LParen))
                {
                    var args = ParseArgumentList();
                    return MakeGlobalCall(ident.Lexeme, args, loc);
                }
                return _builder.Identifier(ident.Lexeme, loc);
            }
            case TokenKind.Reserved:
                Error("CEL-1006", $"reserved keyword: '{Current.Lexeme}'", Current.Location);
                Consume();
                return ErrorPlaceholder(loc);
            case TokenKind.EndOfFile:
                Error("CEL-1007", "unexpected end of input", Current.Location);
                return ErrorPlaceholder(loc);
            default:
                Error("CEL-1008", $"unexpected token: '{Current.Lexeme}'", Current.Location);
                Consume();
                return ErrorPlaceholder(loc);
        }
    }

    // listInit : '[' (optExpr (',' optExpr)*)? ','? ']' ;
    private Expr ParseListLiteral()
    {
        var loc = Current.Location;
        Consume(); // '['
        var elements = ImmutableArray.CreateBuilder<Expr>();
        var optionalIndices = ImmutableArray.CreateBuilder<int>();
        var index = 0;
        while (!Check(TokenKind.RBracket) && !Check(TokenKind.EndOfFile))
        {
            var optional = Match(TokenKind.Question);
            elements.Add(ParseExpression());
            if (optional)
            {
                optionalIndices.Add(index);
            }
            index++;
            if (!Match(TokenKind.Comma))
            {
                break;
            }
        }
        Expect(TokenKind.RBracket, "']'");
        return _builder.List(elements.ToImmutable(), optionalIndices.ToImmutable(), loc);
    }

    // mapInit : '{' (mapEntry (',' mapEntry)*)? ','? '}' ;
    private Expr ParseMapLiteral()
    {
        var loc = Current.Location;
        Consume(); // '{'
        var entries = ImmutableArray.CreateBuilder<MapEntry>();
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.EndOfFile))
        {
            var entryLoc = Current.Location;
            var optional = Match(TokenKind.Question);
            var key = ParseExpression();
            Expect(TokenKind.Colon, "':'");
            var value = ParseExpression();
            entries.Add(_builder.NewMapEntry(key, value, optional, entryLoc));
            if (!Match(TokenKind.Comma))
            {
                break;
            }
        }
        Expect(TokenKind.RBrace, "'}'");
        return _builder.Map(entries.ToImmutable(), loc);
    }

    // structInit : '{' (fieldInit (',' fieldInit)*)? ','? '}' ;  (after 'TypeName')
    private Expr ParseStructLiteral(string typeName, Expr placeholder)
    {
        var loc = Current.Location;
        Consume(); // '{'
        var fields = ImmutableArray.CreateBuilder<StructField>();
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.EndOfFile))
        {
            var fieldLoc = Current.Location;
            var optional = Match(TokenKind.Question);
            if (!Check(TokenKind.Identifier))
            {
                Error("CEL-1009", "expected field name", Current.Location);
                break;
            }
            var name = Consume().Lexeme;
            Expect(TokenKind.Colon, "':'");
            var value = ParseExpression();
            fields.Add(_builder.NewStructField(name, value, optional, fieldLoc));
            if (!Match(TokenKind.Comma))
            {
                break;
            }
        }
        Expect(TokenKind.RBrace, "'}'");
        _ = placeholder; // the placeholder Expr we unwrapped; ids stay valid in side-table.
        return _builder.Struct(typeName, fields.ToImmutable(), loc);
    }

    private ImmutableArray<Expr> ParseArgumentList()
    {
        Expect(TokenKind.LParen, "'('");
        if (Match(TokenKind.RParen))
        {
            return [];
        }
        var args = ImmutableArray.CreateBuilder<Expr>();
        while (true)
        {
            args.Add(ParseExpression());
            if (!Match(TokenKind.Comma))
            {
                break;
            }
        }
        Expect(TokenKind.RParen, "')'");
        return args.ToImmutable();
    }

    // ── call construction (with macro expansion) ──

    private Expr MakeGlobalCall(string function, ImmutableArray<Expr> args, SourceLocation loc)
    {
        var macro = TryExpandMacro(receiver: null, function, args, loc);
        return macro ?? _builder.Call(null, function, args, loc);
    }

    private Expr MakeReceiverCall(Expr receiver, string function, ImmutableArray<Expr> args, SourceLocation loc, bool optional)
    {
        if (optional)
        {
            // Optional method dispatch is not part of the public CEL syntax; treat as error.
            Error("CEL-1010", "optional receiver dispatch ('.?name(...)') is not supported", loc);
        }
        var macro = TryExpandMacro(receiver, function, args, loc);
        return macro ?? _builder.Call(receiver, function, args, loc);
    }

    private Expr? TryExpandMacro(Expr? receiver, string function, ImmutableArray<Expr> args, SourceLocation loc)
    {
        // has(e.f) — only as a global call.
        if (receiver is null && function == Macros.Has && args.Length == 1)
        {
            return ExpandHas(args[0], loc);
        }

        // Hardcoded receiver-style macros. `exists_one` is accepted under both spellings
        // because the v1 spec used `exists_one` while cel-go and the macros2 tests use
        // `existsOne`.
        if (receiver is not null)
        {
            var isExistsOne = function == Macros.ExistsOne || function == "existsOne";
            var builtin = function switch
            {
                Macros.All when args.Length == 2 => ExpandQuantifier(receiver, args, loc, exists: false),
                Macros.All when args.Length == 3 => ExpandQuantifier2(receiver, args, loc, exists: false),
                Macros.Exists when args.Length == 2 => ExpandQuantifier(receiver, args, loc, exists: true),
                Macros.Exists when args.Length == 3 => ExpandQuantifier2(receiver, args, loc, exists: true),
                _ when isExistsOne && args.Length == 2 => ExpandExistsOne(receiver, args, loc),
                _ when isExistsOne && args.Length == 3 => ExpandExistsOne2(receiver, args, loc),
                Macros.Map when args.Length == 2 => ExpandMap(receiver, args, filter: null, loc),
                Macros.Map when args.Length == 3 => ExpandMap(receiver, [args[0], args[2]], filter: args[1], loc),
                Macros.Filter when args.Length == 2 => ExpandFilter(receiver, args, loc),
                "transformList" when args.Length == 3 => ExpandTransformList2(receiver, args[0], args[1], filter: null, args[2], loc),
                "transformList" when args.Length == 4 => ExpandTransformList2(receiver, args[0], args[1], filter: args[2], args[3], loc),
                _ => null,
            };
            if (builtin is not null)
            {
                return builtin;
            }
        }

        return TryExpandExtensionMacro(receiver, function, args, loc);
    }

    private Expr? TryExpandExtensionMacro(Expr? receiver, string function, ImmutableArray<Expr> args, SourceLocation loc)
    {
        if (_macros.IsDefaultOrEmpty)
        {
            return null;
        }
        // Pre-flatten the receiver chain for namespaced-macro matching (e.g. cel.bind).
        var qualified = receiver is null ? null : TryFlattenIdentChain(receiver);
        var qualifiedName = qualified is null ? null : $"{qualified}.{function}";

        foreach (var macro in _macros)
        {
            if (macro.Arity >= 0 && macro.Arity != args.Length)
            {
                continue;
            }
            var matches = macro.IsReceiverStyle
                ? receiver is not null && string.Equals(macro.Name, function, StringComparison.Ordinal)
                : qualifiedName is not null && string.Equals(macro.Name, qualifiedName, StringComparison.Ordinal);
            if (!matches)
            {
                continue;
            }
            var ctx = new MacroExpansionContext(_ids, _sourceInfo, _diagnostics, loc);
            var expanded = macro.Expand(ctx, receiver, args);
            if (expanded is not null)
            {
                return expanded;
            }
        }
        return null;
    }

    /// <summary>
    /// Flatten a SelectExpr / IdentifierExpr chain to a dotted name for namespaced-macro
    /// matching. Returns null when the chain contains anything else.
    /// </summary>
    private static string? TryFlattenIdentChain(Expr e)
    {
        var parts = new Stack<string>();
        var cur = e;
        while (cur is SelectExpr s && !s.TestOnly)
        {
            parts.Push(s.Field);
            cur = s.Operand;
        }
        if (cur is IdentifierExpr ident)
        {
            var name = ident.Name;
            if (name.Length > 0 && name[0] == '.')
            {
                name = name[1..];
            }
            parts.Push(name);
            return string.Join('.', parts);
        }
        return null;
    }

    private Expr ExpandHas(Expr arg, SourceLocation loc)
    {
        if (arg is SelectExpr s && !s.TestOnly)
        {
            return _builder.Select(s.Operand, s.Field, loc, testOnly: true);
        }
        Error("CEL-1011", "argument to 'has' must be a field selection", loc);
        return ErrorPlaceholder(loc);
    }

    private Expr ExpandQuantifier(Expr receiver, ImmutableArray<Expr> args, SourceLocation loc, bool exists)
    {
        if (args[0] is not IdentifierExpr iter)
        {
            Error("CEL-1012", $"first argument of '{(exists ? "exists" : "all")}' must be a simple identifier", loc);
            return ErrorPlaceholder(loc);
        }
        var pred = args[1];

        var accuVar = Operators.AccumulatorName;
        var initBool = !exists;
        var accuInit = _builder.Constant(new BoolConstant(initBool), loc);
        var accuRef = _builder.Identifier(accuVar, loc);

        Expr loopCondArg = exists
            ? _builder.Call(null, Operators.LogicalNot, [_builder.Identifier(accuVar, loc)], loc)
            : _builder.Identifier(accuVar, loc);
        var loopCondition = _builder.Call(null, Operators.NotStrictlyFalse, [loopCondArg], loc);

        var combineOp = exists ? Operators.LogicalOr : Operators.LogicalAnd;
        var loopStep = _builder.Call(null, combineOp,
            [_builder.Identifier(accuVar, loc), pred],
            loc);

        return _builder.Comprehension(iter.Name, receiver, accuVar, accuInit, loopCondition, loopStep, accuRef, loc);
    }

    private Expr ExpandExistsOne(Expr receiver, ImmutableArray<Expr> args, SourceLocation loc)
    {
        if (args[0] is not IdentifierExpr iter)
        {
            Error("CEL-1013", "first argument of 'exists_one' must be a simple identifier", loc);
            return ErrorPlaceholder(loc);
        }
        var pred = args[1];

        var accuVar = Operators.AccumulatorName;
        var accuInit = _builder.Constant(new IntConstant(0), loc);
        var loopCondition = _builder.Constant(new BoolConstant(true), loc);

        var oneLit = _builder.Constant(new IntConstant(1), loc);
        var increment = _builder.Call(null, Operators.Add,
            [_builder.Identifier(accuVar, loc), oneLit],
            loc);
        var loopStep = _builder.Call(null, Operators.Conditional,
            [pred, increment, _builder.Identifier(accuVar, loc)],
            loc);

        var resultEqOne = _builder.Call(null, Operators.Equal,
            [_builder.Identifier(accuVar, loc), _builder.Constant(new IntConstant(1), loc)],
            loc);

        return _builder.Comprehension(iter.Name, receiver, accuVar, accuInit, loopCondition, loopStep, resultEqOne, loc);
    }

    private Expr ExpandMap(Expr receiver, ImmutableArray<Expr> args, Expr? filter, SourceLocation loc)
    {
        if (args[0] is not IdentifierExpr iter)
        {
            Error("CEL-1014", "first argument of 'map' must be a simple identifier", loc);
            return ErrorPlaceholder(loc);
        }
        var transform = args[1];
        var accuVar = Operators.AccumulatorName;
        var accuInit = _builder.List([], [], loc);
        var loopCondition = _builder.Constant(new BoolConstant(true), loc);

        var append = _builder.Call(null, Operators.Add,
            [_builder.Identifier(accuVar, loc),
             _builder.List([transform], [], loc)],
            loc);
        var loopStep = filter is null
            ? append
            : _builder.Call(null, Operators.Conditional,
                [filter, append, _builder.Identifier(accuVar, loc)],
                loc);

        return _builder.Comprehension(iter.Name, receiver, accuVar, accuInit, loopCondition, loopStep,
            _builder.Identifier(accuVar, loc), loc);
    }

    /// <summary>
    /// Two-iterator quantifier (<c>m.all(k, v, p)</c>, <c>list.exists(i, v, p)</c>): same shape
    /// as the single-iterator form but with <c>iter_var2</c> populated. For lists, the first
    /// var is the element index and the second is the element; for maps, key + value.
    /// </summary>
    private Expr ExpandQuantifier2(Expr receiver, ImmutableArray<Expr> args, SourceLocation loc, bool exists)
    {
        if (args[0] is not IdentifierExpr iter1 || args[1] is not IdentifierExpr iter2)
        {
            Error("CEL-1020", $"first two arguments of '{(exists ? "exists" : "all")}' must be identifiers", loc);
            return ErrorPlaceholder(loc);
        }
        var pred = args[2];
        var accuVar = Operators.AccumulatorName;
        var accuInit = _builder.Constant(new BoolConstant(!exists), loc);
        var accuRef = _builder.Identifier(accuVar, loc);

        Expr loopCondArg = exists
            ? _builder.Call(null, Operators.LogicalNot, [_builder.Identifier(accuVar, loc)], loc)
            : _builder.Identifier(accuVar, loc);
        var loopCondition = _builder.Call(null, Operators.NotStrictlyFalse, [loopCondArg], loc);

        var combineOp = exists ? Operators.LogicalOr : Operators.LogicalAnd;
        var loopStep = _builder.Call(null, combineOp,
            [_builder.Identifier(accuVar, loc), pred],
            loc);

        return _builder.Comprehension(
            iter1.Name, receiver, accuVar, accuInit, loopCondition, loopStep, accuRef, loc,
            iterVar2: iter2.Name);
    }

    /// <summary>
    /// Two-iterator <c>transformList</c>: <c>list.transformList(i, v, t)</c> appends
    /// <c>t</c> to a fresh list at each element, with <c>i</c> bound to the index and <c>v</c>
    /// to the element. The 4-arg form <c>list.transformList(i, v, filter, t)</c> wraps the
    /// append in a conditional.
    /// </summary>
    private Expr ExpandTransformList2(Expr receiver, Expr iterArg1, Expr iterArg2, Expr? filter, Expr transform, SourceLocation loc)
    {
        if (iterArg1 is not IdentifierExpr iter1 || iterArg2 is not IdentifierExpr iter2)
        {
            Error("CEL-1022", "first two arguments of 'transformList' must be identifiers", loc);
            return ErrorPlaceholder(loc);
        }
        var accuVar = Operators.AccumulatorName;
        var accuInit = _builder.List([], [], loc);
        var loopCondition = _builder.Constant(new BoolConstant(true), loc);
        var append = _builder.Call(null, Operators.Add,
            [_builder.Identifier(accuVar, loc),
             _builder.List([transform], [], loc)],
            loc);
        var loopStep = filter is null
            ? append
            : _builder.Call(null, Operators.Conditional,
                [filter, append, _builder.Identifier(accuVar, loc)],
                loc);
        return _builder.Comprehension(
            iter1.Name, receiver, accuVar, accuInit, loopCondition, loopStep,
            _builder.Identifier(accuVar, loc), loc,
            iterVar2: iter2.Name);
    }

    /// <summary>Two-iterator <c>exists_one</c> — same expansion as the single-iter form but with iter_var2.</summary>
    private Expr ExpandExistsOne2(Expr receiver, ImmutableArray<Expr> args, SourceLocation loc)
    {
        if (args[0] is not IdentifierExpr iter1 || args[1] is not IdentifierExpr iter2)
        {
            Error("CEL-1021", "first two arguments of 'exists_one' must be identifiers", loc);
            return ErrorPlaceholder(loc);
        }
        var pred = args[2];
        var accuVar = Operators.AccumulatorName;
        var accuInit = _builder.Constant(new IntConstant(0), loc);
        var loopCondition = _builder.Constant(new BoolConstant(true), loc);
        var oneLit = _builder.Constant(new IntConstant(1), loc);
        var increment = _builder.Call(null, Operators.Add,
            [_builder.Identifier(accuVar, loc), oneLit], loc);
        var loopStep = _builder.Call(null, Operators.Conditional,
            [pred, increment, _builder.Identifier(accuVar, loc)], loc);
        var resultEqOne = _builder.Call(null, Operators.Equal,
            [_builder.Identifier(accuVar, loc), _builder.Constant(new IntConstant(1), loc)], loc);
        return _builder.Comprehension(
            iter1.Name, receiver, accuVar, accuInit, loopCondition, loopStep, resultEqOne, loc,
            iterVar2: iter2.Name);
    }

    private Expr ExpandFilter(Expr receiver, ImmutableArray<Expr> args, SourceLocation loc)
    {
        if (args[0] is not IdentifierExpr iter)
        {
            Error("CEL-1015", "first argument of 'filter' must be a simple identifier", loc);
            return ErrorPlaceholder(loc);
        }
        var pred = args[1];
        var accuVar = Operators.AccumulatorName;
        var accuInit = _builder.List([], [], loc);
        var loopCondition = _builder.Constant(new BoolConstant(true), loc);

        var append = _builder.Call(null, Operators.Add,
            [_builder.Identifier(accuVar, loc),
             _builder.List([_builder.Identifier(iter.Name, loc)], [], loc)],
            loc);
        var loopStep = _builder.Call(null, Operators.Conditional,
            [pred, append, _builder.Identifier(accuVar, loc)],
            loc);

        return _builder.Comprehension(iter.Name, receiver, accuVar, accuInit, loopCondition, loopStep,
            _builder.Identifier(accuVar, loc), loc);
    }
}
