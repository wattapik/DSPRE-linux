using System.Text;

namespace DSPRE.LuaLanguageServer;

/// <summary>Re-indents Lua by its block structure. Only leading whitespace, trailing whitespace and runs of blank lines change;
/// the tokens of a line are left as written, so formatting can never change what a script compiles to.</summary>
internal static class LuaFormatter
{
    private static readonly HashSet<string> LeadingClosers = ["end", "until", "else", "elseif", ")", "}", "]"];

    public static string Format(string source, string indentUnit)
    {
        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        List<Token> tokens = LuaTokens.Tokenize(source.Replace("\r\n", "\n"));
        var indents = new int[lines.Length];
        var keep = new bool[lines.Length];
        foreach (Token token in tokens)
        {
            for (int line = token.Line + 1; line <= token.EndLine && line < lines.Length; line++) keep[line] = true;
        }

        var byLine = tokens.Where(t => t.Kind != TokenKind.Comment).GroupBy(t => t.Line).ToDictionary(g => g.Key, g => g.ToList());
        var stack = new List<int>();
        bool loopHeader = false;
        for (int line = 0; line < lines.Length; line++)
        {
            if (!byLine.TryGetValue(line, out List<Token>? row))
            {
                indents[line] = stack.Distinct().Count();
                continue;
            }

            int first = 0;
            bool reopen = false;
            while (first < row.Count && row[first].Kind is TokenKind.Keyword or TokenKind.Symbol && LeadingClosers.Contains(row[first].Text))
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                reopen |= row[first].Text is "else" or "elseif";
                first++;
            }
            indents[line] = stack.Distinct().Count();
            if (reopen) stack.Add(line);

            for (int i = first; i < row.Count; i++)
            {
                Token token = row[i];
                if (token.Kind is not (TokenKind.Keyword or TokenKind.Symbol)) continue;
                switch (token.Text)
                {
                    case "for" or "while":
                        loopHeader = true;
                        break;
                    case "do" when loopHeader:
                        loopHeader = false;
                        stack.Add(line);
                        break;
                    case "do" or "function" or "if" or "repeat" or "(" or "{" or "[":
                        stack.Add(line);
                        break;
                    case "end" or "until" or ")" or "}" or "]":
                        if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                        break;
                }
            }
        }

        var result = new StringBuilder();
        int blank = 0;
        for (int line = 0; line < lines.Length; line++)
        {
            string text = keep[line] ? lines[line] : lines[line].Trim();
            if (!keep[line] && text.Length == 0)
            {
                if (++blank > 1 || result.Length == 0) continue;
                result.Append('\n');
                continue;
            }
            blank = 0;
            if (!keep[line]) text = string.Concat(Enumerable.Repeat(indentUnit, indents[line])) + text;
            result.Append(text).Append('\n');
        }
        return result.ToString().TrimEnd('\n') + "\n";
    }
}
