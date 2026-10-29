using System.Globalization;
using System.Text;

namespace LumenStudio.Authoring.Script;

public sealed class ScriptException(string message, int line, int column)
    : Exception($"{line}:{column}: {message}")
{
    public int Line { get; } = line;
    public int Column { get; } = column;
}

internal enum TokenKind { Identifier, Number, String, Punctuator, Keyword, End }

internal readonly record struct Token(TokenKind Kind, string Text, double Number, int Line, int Column)
{
    public bool Is(string text) => Kind is TokenKind.Punctuator or TokenKind.Keyword && Text == text;
}

internal static class Lexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "var", "function", "if", "else", "while", "do", "for", "return", "break", "continue", "new", "true", "false",
        "null", "undefined", "this", "switch", "case", "default", "import", "typeof", "delete", "in",
    };

    // Longest first, so ">>>=" wins over ">>>" and ">>".
    private static readonly string[] Punctuators =
    [
        ">>>=", "===", "!==", ">>>", "<<=", ">>=", "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=",
        "&=", "|=", "^=", "<<", ">>", "{", "}", "(", ")", "[", "]", ";", ",", "<", ">", "+", "-", "*", "/", "%", "&", "|", "^",
        "!", "~", "?", ":", "=", ".",
    ];

    public static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        int index = 0, line = 1, lineStart = 0;
        while (true)
        {
            // Whitespace and comments.
            while (index < source.Length)
            {
                var c = source[index];
                if (c == '\n') { index++; line++; lineStart = index; }
                else if (char.IsWhiteSpace(c)) index++;
                else if (c == '/' && index + 1 < source.Length && source[index + 1] == '/')
                    while (index < source.Length && source[index] != '\n') index++;
                else if (c == '/' && index + 1 < source.Length && source[index + 1] == '*')
                {
                    var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    if (end < 0) throw new ScriptException("Unterminated comment.", line, index - lineStart + 1);
                    for (var i = index; i < end; i++) if (source[i] == '\n') { line++; lineStart = i + 1; }
                    index = end + 2;
                }
                else break;
            }
            var column = index - lineStart + 1;
            if (index >= source.Length) { tokens.Add(new(TokenKind.End, "", 0, line, column)); return tokens; }
            var ch = source[index];
            if (char.IsLetter(ch) || ch is '_' or '$')
            {
                var start = index;
                while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] is '_' or '$')) index++;
                var word = source[start..index];
                tokens.Add(new(Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Identifier, word, 0, line, column));
            }
            else if (char.IsAsciiDigit(ch) || ch == '.' && index + 1 < source.Length && char.IsAsciiDigit(source[index + 1]))
            {
                var start = index;
                double value;
                if (ch == '0' && index + 1 < source.Length && source[index + 1] is 'x' or 'X')
                {
                    index += 2;
                    while (index < source.Length && Uri.IsHexDigit(source[index])) index++;
                    value = Convert.ToInt64(source[(start + 2)..index], 16);
                }
                else
                {
                    while (index < source.Length && (char.IsAsciiDigit(source[index]) || source[index] == '.')) index++;
                    if (index < source.Length && source[index] is 'e' or 'E')
                    {
                        index++;
                        if (index < source.Length && source[index] is '+' or '-') index++;
                        while (index < source.Length && char.IsAsciiDigit(source[index])) index++;
                    }
                    if (!double.TryParse(source[start..index], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        throw new ScriptException($"Invalid number '{source[start..index]}'.", line, column);
                }
                tokens.Add(new(TokenKind.Number, source[start..index], value, line, column));
            }
            else if (ch is '"' or '\'')
            {
                var text = new StringBuilder();
                index++;
                while (true)
                {
                    if (index >= source.Length || source[index] == '\n') throw new ScriptException("Unterminated string.", line, column);
                    var c = source[index++];
                    if (c == ch) break;
                    if (c != '\\') { text.Append(c); continue; }
                    var escape = index < source.Length ? source[index++] : '\\';
                    switch (escape)
                    {
                        case 'n': text.Append('\n'); break;
                        case 'r': text.Append('\r'); break;
                        case 't': text.Append('\t'); break;
                        case 'u':
                            text.Append((char)Convert.ToInt32(source.Substring(index, 4), 16)); index += 4; break;
                        default: text.Append(escape); break;
                    }
                }
                tokens.Add(new(TokenKind.String, text.ToString(), 0, line, column));
            }
            else
            {
                var punctuator = Punctuators.FirstOrDefault(p => string.CompareOrdinal(source, index, p, 0, p.Length) == 0)
                    ?? throw new ScriptException($"Unexpected character '{ch}'.", line, column);
                index += punctuator.Length;
                tokens.Add(new(TokenKind.Punctuator, punctuator, 0, line, column));
            }
        }
    }
}

// --- Syntax tree ---

internal abstract record Node(int Line, int Column);

internal abstract record Expr(int Line, int Column) : Node(Line, Column);
internal sealed record NumberExpr(double Value, int Line, int Column) : Expr(Line, Column);
internal sealed record StringExpr(string Value, int Line, int Column) : Expr(Line, Column);
internal sealed record LiteralExpr(string Kind, int Line, int Column) : Expr(Line, Column); // true false null undefined
internal sealed record NameExpr(string Name, int Line, int Column) : Expr(Line, Column);
internal sealed record MemberExpr(Expr Target, string Name, int Line, int Column) : Expr(Line, Column);
internal sealed record IndexExpr(Expr Target, Expr Index, int Line, int Column) : Expr(Line, Column);
internal sealed record CallExpr(Expr Callee, IReadOnlyList<Expr> Arguments, int Line, int Column) : Expr(Line, Column);
internal sealed record NewExpr(Expr Constructor, IReadOnlyList<Expr> Arguments, int Line, int Column) : Expr(Line, Column);
internal sealed record UnaryExpr(string Operator, Expr Operand, int Line, int Column) : Expr(Line, Column);
internal sealed record UpdateExpr(string Operator, bool Prefix, Expr Target, int Line, int Column) : Expr(Line, Column);
internal sealed record BinaryExpr(string Operator, Expr Left, Expr Right, int Line, int Column) : Expr(Line, Column);
internal sealed record ConditionalExpr(Expr Test, Expr Then, Expr Else, int Line, int Column) : Expr(Line, Column);
internal sealed record AssignExpr(string Operator, Expr Target, Expr Value, int Line, int Column) : Expr(Line, Column);
internal sealed record ArrayExpr(IReadOnlyList<Expr> Items, int Line, int Column) : Expr(Line, Column);
internal sealed record ObjectExpr(IReadOnlyList<(string Key, Expr Value)> Properties, int Line, int Column) : Expr(Line, Column);
internal sealed record FunctionExpr(string? Name, IReadOnlyList<string> Parameters, IReadOnlyList<Stmt> Body, int Line, int Column) : Expr(Line, Column);

internal abstract record Stmt(int Line, int Column) : Node(Line, Column);
internal sealed record ExprStmt(Expr Expression, int Line, int Column) : Stmt(Line, Column);
internal sealed record VarStmt(IReadOnlyList<(string Name, Expr? Value)> Declarations, int Line, int Column) : Stmt(Line, Column);
internal sealed record FunctionStmt(FunctionExpr Function, int Line, int Column) : Stmt(Line, Column);
internal sealed record BlockStmt(IReadOnlyList<Stmt> Body, int Line, int Column) : Stmt(Line, Column);
internal sealed record IfStmt(Expr Test, Stmt Then, Stmt? Else, int Line, int Column) : Stmt(Line, Column);
internal sealed record WhileStmt(Expr Test, Stmt Body, bool TestAfter, int Line, int Column) : Stmt(Line, Column);
internal sealed record ForStmt(Stmt? Init, Expr? Test, Expr? Step, Stmt Body, int Line, int Column) : Stmt(Line, Column);
internal sealed record SwitchStmt(Expr Discriminant, IReadOnlyList<(Expr? Test, IReadOnlyList<Stmt> Body)> Cases, int Line, int Column) : Stmt(Line, Column);
internal sealed record ReturnStmt(Expr? Value, int Line, int Column) : Stmt(Line, Column);
internal sealed record JumpStmt(bool IsBreak, int Line, int Column) : Stmt(Line, Column);
internal sealed record EmptyStmt(int Line, int Column) : Stmt(Line, Column);

/// <summary>Recursive-descent parser for the ActionScript 2 subset LumenStudio compiles.</summary>
internal sealed class Parser(List<Token> tokens)
{
    private int _position;

    /// <summary><c>import a.b.C;</c> aliases: C → a.b.C.</summary>
    public Dictionary<string, string[]> Imports { get; } = new(StringComparer.Ordinal);

    private Token Current => tokens[_position];

    public List<Stmt> ParseProgram()
    {
        var statements = new List<Stmt>();
        while (Current.Kind != TokenKind.End) statements.Add(statement());
        return statements;
    }

    private Token next() => tokens[_position++];

    private bool accept(string text)
    {
        if (!Current.Is(text)) return false;
        _position++;
        return true;
    }

    private Token expect(string text)
    {
        if (!Current.Is(text)) throw error($"Expected '{text}' but found '{describe(Current)}'.");
        return next();
    }

    private string identifier()
    {
        if (Current.Kind != TokenKind.Identifier) throw error($"Expected a name but found '{describe(Current)}'.");
        return next().Text;
    }

    private ScriptException error(string message) => new(message, Current.Line, Current.Column);

    private static string describe(Token token) => token.Kind == TokenKind.End ? "end of script" : token.Text;

    private void semicolon()
    {
        // Lenient ASI: a missing semicolon before '}' or the end is accepted.
        if (accept(";") || Current.Is("}") || Current.Kind == TokenKind.End) return;
        throw error($"Expected ';' but found '{describe(Current)}'.");
    }

    private Stmt statement()
    {
        var token = Current;
        if (token.Kind == TokenKind.Keyword)
        {
            switch (token.Text)
            {
                case "var":
                {
                    var declaration = varDeclaration();
                    semicolon();
                    return declaration;
                }
                case "function" when tokens[_position + 1].Kind == TokenKind.Identifier:
                    next();
                    return new FunctionStmt(function(identifier(), token), token.Line, token.Column);
                case "if":
                {
                    next(); expect("(");
                    var test = expression(); expect(")");
                    var then = statement();
                    var otherwise = accept("else") ? statement() : null;
                    return new IfStmt(test, then, otherwise, token.Line, token.Column);
                }
                case "while":
                {
                    next(); expect("(");
                    var test = expression(); expect(")");
                    return new WhileStmt(test, statement(), false, token.Line, token.Column);
                }
                case "do":
                {
                    next();
                    var body = statement();
                    expect("while"); expect("(");
                    var test = expression(); expect(")");
                    semicolon();
                    return new WhileStmt(test, body, true, token.Line, token.Column);
                }
                case "for":
                {
                    next(); expect("(");
                    Stmt? init = null;
                    if (Current.Is("var")) init = varDeclaration();
                    else if (!Current.Is(";")) init = new ExprStmt(expression(), token.Line, token.Column);
                    expect(";");
                    var test = Current.Is(";") ? null : expression();
                    expect(";");
                    var step = Current.Is(")") ? null : expression();
                    expect(")");
                    return new ForStmt(init, test, step, statement(), token.Line, token.Column);
                }
                case "switch":
                {
                    next(); expect("(");
                    var discriminant = expression(); expect(")"); expect("{");
                    var cases = new List<(Expr?, IReadOnlyList<Stmt>)>();
                    while (!accept("}"))
                    {
                        Expr? test = null;
                        if (accept("case")) test = expression();
                        else expect("default");
                        expect(":");
                        var body = new List<Stmt>();
                        while (!Current.Is("case") && !Current.Is("default") && !Current.Is("}")) body.Add(statement());
                        cases.Add((test, body));
                    }
                    return new SwitchStmt(discriminant, cases, token.Line, token.Column);
                }
                case "return":
                {
                    next();
                    var value = Current.Is(";") || Current.Is("}") ? null : expression();
                    semicolon();
                    return new ReturnStmt(value, token.Line, token.Column);
                }
                case "break" or "continue":
                    next(); semicolon();
                    return new JumpStmt(token.Text == "break", token.Line, token.Column);
                case "import":
                {
                    next();
                    var path = new List<string> { identifier() };
                    while (accept(".")) path.Add(identifier());
                    semicolon();
                    Imports[path[^1]] = [.. path];
                    return new EmptyStmt(token.Line, token.Column);
                }
            }
        }
        if (accept("{")) return new BlockStmt(block(), token.Line, token.Column);
        if (accept(";")) return new EmptyStmt(token.Line, token.Column);
        var expressionStatement = new ExprStmt(expression(), token.Line, token.Column);
        semicolon();
        return expressionStatement;
    }

    private List<Stmt> block()
    {
        var body = new List<Stmt>();
        while (!accept("}"))
        {
            if (Current.Kind == TokenKind.End) throw error("Missing '}'.");
            body.Add(statement());
        }
        return body;
    }

    private VarStmt varDeclaration()
    {
        var token = expect("var");
        var declarations = new List<(string, Expr?)>();
        do
        {
            var name = identifier();
            if (accept(":")) identifier(); // AS2 type annotations are accepted and ignored
            declarations.Add((name, accept("=") ? assignment() : null));
        }
        while (accept(","));
        return new VarStmt(declarations, token.Line, token.Column);
    }

    private FunctionExpr function(string? name, Token at)
    {
        expect("(");
        var parameters = new List<string>();
        if (!Current.Is(")"))
        {
            do
            {
                parameters.Add(identifier());
                if (accept(":")) identifier();
            }
            while (accept(","));
        }
        expect(")");
        if (accept(":")) identifier(); // return type (Void, Number...)
        expect("{");
        return new FunctionExpr(name, parameters, block(), at.Line, at.Column);
    }

    private Expr expression() => assignment();

    private static readonly HashSet<string> AssignmentOperators = ["=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", ">>>="];

    private Expr assignment()
    {
        var left = conditional();
        if (Current.Kind == TokenKind.Punctuator && AssignmentOperators.Contains(Current.Text))
        {
            var op = next();
            if (left is not (NameExpr or MemberExpr or IndexExpr)) throw new ScriptException("Invalid assignment target.", op.Line, op.Column);
            return new AssignExpr(op.Text, left, assignment(), op.Line, op.Column);
        }
        return left;
    }

    private Expr conditional()
    {
        var test = binary(0);
        if (!Current.Is("?")) return test;
        var token = next();
        var then = assignment();
        expect(":");
        return new ConditionalExpr(test, then, assignment(), token.Line, token.Column);
    }

    private static readonly string[][] Precedence =
    [
        ["||"], ["&&"], ["|"], ["^"], ["&"], ["==", "!=", "===", "!=="], ["<", ">", "<=", ">="], ["<<", ">>", ">>>"], ["+", "-"], ["*", "/", "%"],
    ];

    private Expr binary(int level)
    {
        if (level == Precedence.Length) return unary();
        var left = binary(level + 1);
        while (Current.Kind == TokenKind.Punctuator && Precedence[level].Contains(Current.Text))
        {
            var op = next();
            left = new BinaryExpr(op.Text, left, binary(level + 1), op.Line, op.Column);
        }
        return left;
    }

    private Expr unary()
    {
        var token = Current;
        if (token.Kind == TokenKind.Punctuator && token.Text is "!" or "-" or "+" or "~")
        {
            next();
            return new UnaryExpr(token.Text, unary(), token.Line, token.Column);
        }
        if (token.Kind == TokenKind.Punctuator && token.Text is "++" or "--")
        {
            next();
            return new UpdateExpr(token.Text, true, unary(), token.Line, token.Column);
        }
        if (token.Is("typeof") || token.Is("delete"))
            throw error($"'{token.Text}' is not supported by Waddamburo's interpreter.");
        var operand = postfix();
        if (Current.Kind == TokenKind.Punctuator && Current.Text is "++" or "--")
        {
            var op = next();
            return new UpdateExpr(op.Text, false, operand, op.Line, op.Column);
        }
        return operand;
    }

    private Expr postfix()
    {
        Expr expression;
        if (Current.Is("new"))
        {
            var token = next();
            var constructor = memberChain(primary(), allowCalls: false);
            var arguments = Current.Is("(") ? argumentList() : [];
            expression = new NewExpr(constructor, arguments, token.Line, token.Column);
        }
        else expression = primary();
        return memberChain(expression, allowCalls: true);
    }

    private Expr memberChain(Expr target, bool allowCalls)
    {
        while (true)
        {
            var token = Current;
            if (accept(".")) target = new MemberExpr(target, Current.Kind is TokenKind.Identifier or TokenKind.Keyword ? next().Text : identifier(), token.Line, token.Column);
            else if (accept("[")) { var index = expression(); expect("]"); target = new IndexExpr(target, index, token.Line, token.Column); }
            else if (allowCalls && Current.Is("(")) target = new CallExpr(target, argumentList(), token.Line, token.Column);
            else return target;
        }
    }

    private List<Expr> argumentList()
    {
        expect("(");
        var arguments = new List<Expr>();
        if (!Current.Is(")"))
            do arguments.Add(assignment()); while (accept(","));
        expect(")");
        return arguments;
    }

    private Expr primary()
    {
        var token = next();
        switch (token.Kind)
        {
            case TokenKind.Number: return new NumberExpr(token.Number, token.Line, token.Column);
            case TokenKind.String: return new StringExpr(token.Text, token.Line, token.Column);
            case TokenKind.Identifier: return new NameExpr(token.Text, token.Line, token.Column);
            case TokenKind.Keyword when token.Text is "true" or "false" or "null" or "undefined":
                return new LiteralExpr(token.Text, token.Line, token.Column);
            case TokenKind.Keyword when token.Text == "this": return new NameExpr("this", token.Line, token.Column);
            case TokenKind.Keyword when token.Text == "function":
                return function(Current.Kind == TokenKind.Identifier ? identifier() : null, token);
            case TokenKind.Punctuator when token.Text == "(":
            {
                var inner = expression();
                expect(")");
                return inner;
            }
            case TokenKind.Punctuator when token.Text == "[":
            {
                var items = new List<Expr>();
                if (!Current.Is("]"))
                    do items.Add(assignment()); while (accept(","));
                expect("]");
                return new ArrayExpr(items, token.Line, token.Column);
            }
            case TokenKind.Punctuator when token.Text == "{":
            {
                var properties = new List<(string, Expr)>();
                if (!Current.Is("}"))
                {
                    do
                    {
                        var key = Current.Kind is TokenKind.Identifier or TokenKind.String or TokenKind.Keyword ? next().Text
                            : Current.Kind == TokenKind.Number ? next().Text : throw error("Expected a property name.");
                        expect(":");
                        properties.Add((key, assignment()));
                    }
                    while (accept(","));
                }
                expect("}");
                return new ObjectExpr(properties, token.Line, token.Column);
            }
        }
        _position--;
        throw error($"Unexpected '{describe(token)}'.");
    }
}
