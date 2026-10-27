using System.Globalization;
using System.Text;

namespace DSPRE.Core.Lua;

public sealed class LuaException(int line, string message)
    : Exception(line > GlobalLineBase ? $"global.lua, line {line - GlobalLineBase}: {message}" : line > 0 ? $"Line {line}: {message}" : message)
{
    /// <summary>global.lua is parsed with its line numbers moved up by this much, so an error anywhere
    /// in its helpers, even while compiling a script file that uses them, says which file it is in.</summary>
    public const int GlobalLineBase = 1_000_000;

    public int Line { get; } = line;
    public bool InGlobal => Line > GlobalLineBase;
}

public abstract record LuaExpr(int Line);
public sealed record LuaNil(int Line) : LuaExpr(Line);
public sealed record LuaBool(int Line, bool Value) : LuaExpr(Line);
public sealed record LuaNumber(int Line, double Value, string Lexeme) : LuaExpr(Line);
public sealed record LuaString(int Line, string Value, bool Long) : LuaExpr(Line);
public sealed record LuaName(int Line, string Name) : LuaExpr(Line);
public sealed record LuaIndex(int Line, LuaExpr Target, LuaExpr Key) : LuaExpr(Line);
public sealed record LuaCall(int Line, LuaExpr Function, IReadOnlyList<LuaExpr> Args) : LuaExpr(Line);
public sealed record LuaFunction(int Line, IReadOnlyList<string> Parameters, IReadOnlyList<LuaStmt> Body, string? Name) : LuaExpr(Line);
public sealed record LuaBinary(int Line, string Op, LuaExpr Left, LuaExpr Right) : LuaExpr(Line);
public sealed record LuaUnary(int Line, string Op, LuaExpr Operand) : LuaExpr(Line);
public sealed record LuaTableField(LuaExpr? Key, LuaExpr Value);
public sealed record LuaTable(int Line, IReadOnlyList<LuaTableField> Fields) : LuaExpr(Line);

public abstract record LuaStmt(int Line);
public sealed record LuaLocal(int Line, IReadOnlyList<string> Names, IReadOnlyList<LuaExpr> Values) : LuaStmt(Line);
public sealed record LuaAssign(int Line, IReadOnlyList<LuaExpr> Targets, IReadOnlyList<LuaExpr> Values) : LuaStmt(Line);
public sealed record LuaCallStmt(int Line, LuaCall Call) : LuaStmt(Line);
public sealed record LuaIf(int Line, IReadOnlyList<(LuaExpr Condition, IReadOnlyList<LuaStmt> Body)> Branches, IReadOnlyList<LuaStmt>? Else) : LuaStmt(Line);
public sealed record LuaWhile(int Line, LuaExpr Condition, IReadOnlyList<LuaStmt> Body) : LuaStmt(Line);
public sealed record LuaRepeat(int Line, IReadOnlyList<LuaStmt> Body, LuaExpr Condition) : LuaStmt(Line);
public sealed record LuaNumericFor(int Line, string Variable, LuaExpr Start, LuaExpr Stop, LuaExpr? Step, IReadOnlyList<LuaStmt> Body) : LuaStmt(Line);
public sealed record LuaGenericFor(int Line, IReadOnlyList<string> Names, IReadOnlyList<LuaExpr> Values, IReadOnlyList<LuaStmt> Body) : LuaStmt(Line);
public sealed record LuaDo(int Line, IReadOnlyList<LuaStmt> Body) : LuaStmt(Line);
public sealed record LuaReturn(int Line, IReadOnlyList<LuaExpr> Values) : LuaStmt(Line);
public sealed record LuaBreak(int Line) : LuaStmt(Line);
/// <summary><c>function a.b.c() end</c> or <c>local function f() end</c>.</summary>
public sealed record LuaFunctionDecl(int Line, IReadOnlyList<string> Path, LuaFunction Function, bool IsLocal) : LuaStmt(Line);

internal enum LuaTokenKind { Name, Number, String, Keyword, Symbol, Eof }

internal readonly record struct LuaToken(LuaTokenKind Kind, string Text, int Line, double Number = 0, bool LongString = false);

internal sealed class LuaLexer(string source, int firstLine = 1)
{
    private int position;
    private int line = firstLine;

    public List<LuaToken> Tokenize()
    {
        var tokens = new List<LuaToken>();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (position >= source.Length)
            {
                tokens.Add(new LuaToken(LuaTokenKind.Eof, "<eof>", line));
                return tokens;
            }
            tokens.Add(Next());
        }
    }

    private char Peek(int offset = 0) => position + offset < source.Length ? source[position + offset] : '\0';

    private void SkipWhitespaceAndComments()
    {
        while (position < source.Length)
        {
            char c = source[position];
            if (c == '\n') { line++; position++; }
            else if (char.IsWhiteSpace(c)) position++;
            else if (c == '-' && Peek(1) == '-')
            {
                position += 2;
                if (Peek() == '[' && LongBracketLevel() is int level) ReadLongBracket(level);
                else while (position < source.Length && source[position] != '\n') position++;
            }
            else return;
        }
    }

    /// <summary>The level of a long bracket ([[ or [==[) starting here, or null if there is none.</summary>
    private int? LongBracketLevel()
    {
        int i = position + 1, level = 0;
        while (i < source.Length && source[i] == '=') { level++; i++; }
        return i < source.Length && source[i] == '[' ? level : null;
    }

    private string ReadLongBracket(int level)
    {
        int startLine = line;
        position += level + 2;
        if (Peek() == '\r') position++;
        if (Peek() == '\n') { line++; position++; }
        string close = "]" + new string('=', level) + "]";
        int end = source.IndexOf(close, position, StringComparison.Ordinal);
        if (end < 0) throw new LuaException(startLine, "Unfinished long string or comment.");
        string text = source[position..end];
        line += text.Count(c => c == '\n');
        position = end + close.Length;
        return text;
    }

    private static readonly string[] Symbols =
        ["...", "..", "==", "~=", "<=", ">=", "//", "::", "<<", ">>", "+", "-", "*", "/", "%", "^", "#", "&", "~", "|", "<", ">", "=", "(", ")", "{", "}", "[", "]", ";", ":", ",", "."];

    private LuaToken Next()
    {
        char c = source[position];
        int startLine = line;
        if (char.IsLetter(c) || c == '_')
        {
            int start = position;
            while (position < source.Length && (char.IsLetterOrDigit(source[position]) || source[position] == '_')) position++;
            string word = source[start..position];
            return new LuaToken(ScriptLanguage.LuaKeywords.Contains(word) ? LuaTokenKind.Keyword : LuaTokenKind.Name, word, startLine);
        }
        if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(1)))) return ReadNumber();
        if (c == '"' || c == '\'') return ReadString(c);
        if (c == '[' && LongBracketLevel() is int level) return new LuaToken(LuaTokenKind.String, ReadLongBracket(level), startLine, LongString: true);

        foreach (string symbol in Symbols)
        {
            if (string.CompareOrdinal(source, position, symbol, 0, symbol.Length) == 0)
            {
                position += symbol.Length;
                return new LuaToken(LuaTokenKind.Symbol, symbol, startLine);
            }
        }
        throw new LuaException(startLine, $"Unexpected character '{c}'.");
    }

    private LuaToken ReadNumber()
    {
        int start = position;
        double value;
        if (Peek() == '0' && (Peek(1) == 'x' || Peek(1) == 'X'))
        {
            position += 2;
            while (position < source.Length && Uri.IsHexDigit(source[position])) position++;
            string digits = source[(start + 2)..position];
            if (digits.Length == 0) throw new LuaException(line, "Malformed hexadecimal number.");
            value = ulong.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        else
        {
            while (position < source.Length && (char.IsDigit(source[position]) || source[position] == '.')) position++;
            if (position < source.Length && (source[position] == 'e' || source[position] == 'E'))
            {
                position++;
                if (Peek() == '+' || Peek() == '-') position++;
                while (position < source.Length && char.IsDigit(source[position])) position++;
            }
            if (!double.TryParse(source[start..position], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                throw new LuaException(line, $"Malformed number \"{source[start..position]}\".");
            }
        }
        if (position < source.Length && (char.IsLetter(source[position]) || source[position] == '_'))
        {
            throw new LuaException(line, $"Malformed number near \"{source[start..(position + 1)]}\".");
        }
        return new LuaToken(LuaTokenKind.Number, source[start..position], line, value);
    }

    private LuaToken ReadString(char quote)
    {
        int startLine = line;
        position++;
        var builder = new StringBuilder();
        while (true)
        {
            if (position >= source.Length || source[position] == '\n') throw new LuaException(startLine, "Unfinished string.");
            char c = source[position++];
            if (c == quote) break;
            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }
            char escape = position < source.Length ? source[position++] : '\0';
            switch (escape)
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case '\\': builder.Append('\\'); break;
                case '"': builder.Append('"'); break;
                case '\'': builder.Append('\''); break;
                case 'f': builder.Append('\f'); break;
                case 'v': builder.Append('\v'); break;
                case 'a': builder.Append('\a'); break;
                case 'b': builder.Append('\b'); break;
                case '\n': builder.Append('\n'); line++; break;
                // Message text holds the game's own escape codes; any Lua does not know is kept as written.
                default: builder.Append('\\').Append(escape); break;
            }
        }
        return new LuaToken(LuaTokenKind.String, builder.ToString(), startLine);
    }
}

/// <summary>Parses Lua 5.4 source into a syntax tree. Covers the whole statement and expression grammar
/// the script compiler can use; goto, labels, methods (a:b()) and varargs are rejected with an error.</summary>
public sealed class LuaParser
{
    private readonly List<LuaToken> tokens;
    private int index;

    private LuaParser(string source, int firstLine) => tokens = new LuaLexer(source, firstLine).Tokenize();

    public static IReadOnlyList<LuaStmt> Parse(string source, int firstLine = 1)
    {
        var parser = new LuaParser(source, firstLine);
        List<LuaStmt> chunk = parser.Block();
        if (parser.Current.Kind != LuaTokenKind.Eof) throw parser.Error($"Unexpected \"{parser.Current.Text}\".");
        return chunk;
    }

    private LuaToken Current => tokens[index];
    private LuaToken Advance() => tokens[index++];
    private LuaException Error(string message) => new(Current.Line, message);

    private bool Check(string text) => Current.Kind is LuaTokenKind.Keyword or LuaTokenKind.Symbol && Current.Text == text;

    private bool Accept(string text)
    {
        if (!Check(text)) return false;
        index++;
        return true;
    }

    private void Expect(string text, string? closing = null, int openLine = 0)
    {
        if (Accept(text)) return;
        string context = closing is null ? "" : $" (to close \"{closing}\" at line {openLine})";
        throw Error($"Expected \"{text}\"{context} near \"{Current.Text}\".");
    }

    private string ExpectName()
    {
        if (Current.Kind != LuaTokenKind.Name) throw Error($"Expected a name near \"{Current.Text}\".");
        return Advance().Text;
    }

    // A closing bracket can never start a statement, so stopping there lets the enclosing construct
    // report the "end" it is missing instead of an unexpected bracket.
    private bool BlockEnds() => Current.Kind == LuaTokenKind.Eof || Check("end") || Check("else") || Check("elseif") || Check("until")
                                || Check(")") || Check("}") || Check("]");

    private List<LuaStmt> Block()
    {
        var statements = new List<LuaStmt>();
        while (!BlockEnds())
        {
            if (Accept(";")) continue;
            if (Check("return"))
            {
                int line = Advance().Line;
                var values = new List<LuaExpr>();
                if (!BlockEnds() && !Check(";")) values = ExpressionList();
                Accept(";");
                statements.Add(new LuaReturn(line, values));
                if (!BlockEnds()) throw Error("\"return\" must be the last statement of its block.");
                break;
            }
            statements.Add(Statement());
        }
        return statements;
    }

    private LuaStmt Statement()
    {
        int line = Current.Line;
        if (Accept("if"))
        {
            var branches = new List<(LuaExpr, IReadOnlyList<LuaStmt>)>();
            LuaExpr condition = Expression();
            Expect("then");
            branches.Add((condition, Block()));
            List<LuaStmt>? elseBlock = null;
            while (true)
            {
                if (Accept("elseif"))
                {
                    condition = Expression();
                    Expect("then");
                    branches.Add((condition, Block()));
                }
                else if (Accept("else"))
                {
                    elseBlock = Block();
                    Expect("end", "if", line);
                    break;
                }
                else
                {
                    Expect("end", "if", line);
                    break;
                }
            }
            return new LuaIf(line, branches, elseBlock);
        }
        if (Accept("while"))
        {
            LuaExpr condition = Expression();
            Expect("do");
            List<LuaStmt> body = Block();
            Expect("end", "while", line);
            return new LuaWhile(line, condition, body);
        }
        if (Accept("repeat"))
        {
            List<LuaStmt> body = Block();
            Expect("until", "repeat", line);
            return new LuaRepeat(line, body, Expression());
        }
        if (Accept("do"))
        {
            List<LuaStmt> body = Block();
            Expect("end", "do", line);
            return new LuaDo(line, body);
        }
        if (Accept("for"))
        {
            string first = ExpectName();
            if (Accept("="))
            {
                LuaExpr start = Expression();
                Expect(",");
                LuaExpr stop = Expression();
                LuaExpr? step = Accept(",") ? Expression() : null;
                Expect("do");
                List<LuaStmt> body = Block();
                Expect("end", "for", line);
                return new LuaNumericFor(line, first, start, stop, step, body);
            }
            var names = new List<string> { first };
            while (Accept(",")) names.Add(ExpectName());
            Expect("in");
            List<LuaExpr> values = ExpressionList();
            Expect("do");
            List<LuaStmt> loopBody = Block();
            Expect("end", "for", line);
            return new LuaGenericFor(line, names, values, loopBody);
        }
        if (Accept("function"))
        {
            var path = new List<string> { ExpectName() };
            while (Accept(".")) path.Add(ExpectName());
            if (Check(":")) throw Error("Methods (a:b) are not supported.");
            return new LuaFunctionDecl(line, path, FunctionBody(line, string.Join('.', path)), false);
        }
        if (Accept("local"))
        {
            if (Accept("function"))
            {
                string name = ExpectName();
                return new LuaFunctionDecl(line, [name], FunctionBody(line, name), true);
            }
            var names = new List<string> { ExpectName() };
            SkipAttribute();
            while (Accept(","))
            {
                names.Add(ExpectName());
                SkipAttribute();
            }
            List<LuaExpr> values = Accept("=") ? ExpressionList() : [];
            return new LuaLocal(line, names, values);
        }
        if (Accept("break")) return new LuaBreak(line);
        if (Check("goto") || Check("::")) throw Error("goto and labels are not supported; use if/while, or asm [[ ]] for raw jumps.");

        LuaExpr expression = SuffixedExpression();
        if (Check("=") || Check(","))
        {
            var targets = new List<LuaExpr> { expression };
            while (Accept(",")) targets.Add(SuffixedExpression());
            Expect("=");
            foreach (LuaExpr target in targets)
            {
                if (target is not (LuaName or LuaIndex)) throw new LuaException(target.Line, "Cannot assign to this expression.");
            }
            return new LuaAssign(line, targets, ExpressionList());
        }
        if (expression is LuaCall call) return new LuaCallStmt(line, call);
        throw new LuaException(line, "Syntax error: expected a statement (a call or an assignment).");
    }

    private void SkipAttribute()
    {
        if (!Accept("<")) return;
        ExpectName();
        Expect(">");
    }

    private LuaFunction FunctionBody(int line, string? name)
    {
        Expect("(");
        var parameters = new List<string>();
        if (!Check(")"))
        {
            do
            {
                if (Check("...")) throw Error("Varargs (...) are not supported.");
                parameters.Add(ExpectName());
            }
            while (Accept(","));
        }
        Expect(")");
        List<LuaStmt> body = Block();
        Expect("end", "function", line);
        return new LuaFunction(line, parameters, body, name);
    }

    private List<LuaExpr> ExpressionList()
    {
        var list = new List<LuaExpr> { Expression() };
        while (Accept(",")) list.Add(Expression());
        return list;
    }

    private static readonly Dictionary<string, (int Left, int Right)> BinaryPriority = new()
    {
        ["or"] = (1, 1), ["and"] = (2, 2),
        ["<"] = (3, 3), [">"] = (3, 3), ["<="] = (3, 3), [">="] = (3, 3), ["~="] = (3, 3), ["=="] = (3, 3),
        ["|"] = (4, 4), ["~"] = (5, 5), ["&"] = (6, 6), ["<<"] = (7, 7), [">>"] = (7, 7),
        [".."] = (9, 8), ["+"] = (10, 10), ["-"] = (10, 10),
        ["*"] = (11, 11), ["/"] = (11, 11), ["//"] = (11, 11), ["%"] = (11, 11),
        ["^"] = (14, 13),
    };
    private const int UnaryPriority = 12;

    private LuaExpr Expression(int limit = 0)
    {
        LuaExpr left;
        int line = Current.Line;
        if (Check("not") || Check("-") || Check("#") || Check("~"))
        {
            string op = Advance().Text;
            left = new LuaUnary(line, op, Expression(UnaryPriority));
        }
        else left = SimpleExpression();

        while (Current.Kind is LuaTokenKind.Keyword or LuaTokenKind.Symbol
               && BinaryPriority.TryGetValue(Current.Text, out (int Left, int Right) priority) && priority.Left > limit)
        {
            LuaToken op = Advance();
            left = new LuaBinary(op.Line, op.Text, left, Expression(priority.Right));
        }
        return left;
    }

    private LuaExpr SimpleExpression()
    {
        LuaToken token = Current;
        switch (token.Kind)
        {
            case LuaTokenKind.Number:
                index++;
                return new LuaNumber(token.Line, token.Number, token.Text);
            case LuaTokenKind.String:
                index++;
                return new LuaString(token.Line, token.Text, token.LongString);
        }
        if (Accept("nil")) return new LuaNil(token.Line);
        if (Accept("true")) return new LuaBool(token.Line, true);
        if (Accept("false")) return new LuaBool(token.Line, false);
        if (Check("...")) throw Error("Varargs (...) are not supported.");
        if (Accept("function")) return FunctionBody(token.Line, null);
        if (Check("{")) return Table();
        return SuffixedExpression();
    }

    private LuaExpr PrimaryExpression()
    {
        int line = Current.Line;
        if (Current.Kind == LuaTokenKind.Name) return new LuaName(line, Advance().Text);
        if (Accept("("))
        {
            LuaExpr inner = Expression();
            Expect(")", "(", line);
            return inner;
        }
        throw Error($"Unexpected \"{Current.Text}\".");
    }

    private LuaExpr SuffixedExpression()
    {
        LuaExpr expression = PrimaryExpression();
        while (true)
        {
            int line = Current.Line;
            if (Accept("."))
            {
                expression = new LuaIndex(line, expression, new LuaString(line, ExpectName(), false));
            }
            else if (Accept("["))
            {
                LuaExpr key = Expression();
                Expect("]", "[", line);
                expression = new LuaIndex(line, expression, key);
            }
            else if (Check(":")) throw Error("Methods (a:b) are not supported.");
            else if (Check("("))
            {
                index++;
                List<LuaExpr> args = Check(")") ? [] : ExpressionList();
                Expect(")", "(", line);
                expression = new LuaCall(line, expression, args);
            }
            else if (Current.Kind == LuaTokenKind.String)
            {
                LuaToken text = Advance();
                expression = new LuaCall(line, expression, [new LuaString(text.Line, text.Text, text.LongString)]);
            }
            else if (Check("{"))
            {
                expression = new LuaCall(line, expression, [Table()]);
            }
            else return expression;
        }
    }

    private LuaTable Table()
    {
        int line = Current.Line;
        Expect("{");
        var fields = new List<LuaTableField>();
        while (!Check("}"))
        {
            if (Check("["))
            {
                index++;
                LuaExpr key = Expression();
                Expect("]");
                Expect("=");
                fields.Add(new LuaTableField(key, Expression()));
            }
            else if (Current.Kind == LuaTokenKind.Name && tokens[index + 1].Kind == LuaTokenKind.Symbol && tokens[index + 1].Text == "=")
            {
                LuaToken name = Advance();
                index++;
                fields.Add(new LuaTableField(new LuaString(name.Line, name.Text, false), Expression()));
            }
            else fields.Add(new LuaTableField(null, Expression()));

            if (!Accept(",") && !Accept(";")) break;
        }
        Expect("}", "{", line);
        return new LuaTable(line, fields);
    }
}
