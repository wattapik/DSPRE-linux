using DSPRE.Core;

namespace DSPRE.LuaLanguageServer;

internal enum TokenKind { Name, Number, String, Comment, Keyword, Symbol }

/// <summary>A token with the zero-based line and column it starts at and the line and column it ends at (exclusive). Unlike the
/// compiler's lexer this one keeps comments and positions and never throws: it is for editing text that is half written.</summary>
internal readonly record struct Token(TokenKind Kind, string Text, int Line, int Column, int EndLine, int EndColumn)
{
    public bool Is(string text) => Kind is TokenKind.Keyword or TokenKind.Symbol && Text == text;
}

internal static class LuaTokens
{
    private static readonly string[] Symbols =
        ["...", "..", "==", "~=", "<=", ">=", "//", "::", "<<", ">>", "+", "-", "*", "/", "%", "^", "#", "&", "~", "|", "<", ">", "=", "(", ")", "{", "}", "[", "]", ";", ":", ",", "."];

    public static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        int position = 0, line = 0, lineStart = 0;

        void NewLine(int at)
        {
            line++;
            lineStart = at + 1;
        }

        // Advances over text that may hold newlines, keeping line and lineStart current.
        void Skip(int to)
        {
            for (int i = position; i < to; i++)
            {
                if (source[i] == '\n') NewLine(i);
            }
            position = to;
        }

        while (position < source.Length)
        {
            char c = source[position];
            if (c == '\n')
            {
                NewLine(position);
                position++;
            }
            else if (char.IsWhiteSpace(c))
            {
                position++;
            }
            else
            {
                int startLine = line, startColumn = position - lineStart, start = position;
                TokenKind kind;
                if (c == '-' && Peek(source, position + 1) == '-')
                {
                    kind = TokenKind.Comment;
                    int after = position + 2;
                    if (LongBracketEnd(source, after) is int closing) Skip(closing);
                    else
                    {
                        int end = source.IndexOf('\n', after);
                        Skip(end < 0 ? source.Length : end);
                    }
                }
                else if (c is '"' or '\'')
                {
                    kind = TokenKind.String;
                    int i = position + 1;
                    while (i < source.Length && source[i] != c && source[i] != '\n')
                    {
                        if (source[i] == '\\' && i + 1 < source.Length) i++;
                        i++;
                    }
                    Skip(Math.Min(source.Length, i < source.Length && source[i] == c ? i + 1 : i));
                }
                else if (c == '[' && LongBracketEnd(source, position) is int longEnd)
                {
                    kind = TokenKind.String;
                    Skip(longEnd);
                }
                else if (char.IsLetter(c) || c == '_')
                {
                    int i = position;
                    while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                    position = i;
                    kind = ScriptLanguage.LuaKeywords.Contains(source[start..i]) ? TokenKind.Keyword : TokenKind.Name;
                }
                else if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(source, position + 1))))
                {
                    int i = position;
                    bool hex = c == '0' && Peek(source, i + 1) is 'x' or 'X';
                    i += hex ? 2 : 0;
                    while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '.' || (!hex && source[i] is '+' or '-' && source[i - 1] is 'e' or 'E'))) i++;
                    position = i;
                    kind = TokenKind.Number;
                }
                else
                {
                    string symbol = Symbols.FirstOrDefault(s => string.CompareOrdinal(source, position, s, 0, s.Length) == 0) ?? c.ToString();
                    position += symbol.Length;
                    kind = TokenKind.Symbol;
                }

                tokens.Add(new Token(kind, source[start..position], startLine, startColumn, line, position - lineStart));
            }
        }
        return tokens;
    }

    private static char Peek(string source, int index) => index < source.Length ? source[index] : '\0';

    /// <summary>The index after the long bracket ([[ ... ]] or [==[ ... ]==]) that starts at <paramref name="start"/>, or null if none
    /// starts there. An unfinished one runs to the end of the text.</summary>
    private static int? LongBracketEnd(string source, int start)
    {
        if (Peek(source, start) != '[') return null;
        int i = start + 1, level = 0;
        while (Peek(source, i) == '=') { level++; i++; }
        if (Peek(source, i) != '[') return null;
        string close = "]" + new string('=', level) + "]";
        int end = source.IndexOf(close, i + 1, StringComparison.Ordinal);
        return end < 0 ? source.Length : end + close.Length;
    }
}
