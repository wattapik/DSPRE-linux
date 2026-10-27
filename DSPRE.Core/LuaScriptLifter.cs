using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DSPRE.Core.Lua;

/// <summary>Turns a plaintext script file into Lua that <see cref="LuaScriptCompiler"/> compiles back to
/// the same behaviour. A Function used from only one place is written inline there; a comparison
/// followed by JumpIf/CallIf becomes an <c>if</c>, and branches that meet again at a shared Function
/// become if/elseif/else with that Function's code following it. Functions used from several places stay
/// global <c>function func_N()</c> declarations, called with <c>func_N()</c> (Call) or <c>return func_N()</c>
/// (Jump). Lines it cannot express are kept as <c>asm [[ ]]</c>.</summary>
public sealed partial class LuaScriptLifter
{
    private readonly ScriptTextDocument document;
    private readonly ScriptLanguage language;
    private readonly IReadOnlyList<string>? messages;
    private readonly Dictionary<int, ScriptTextContainer> functions;
    private readonly Dictionary<int, ScriptTextAction> actions;
    private readonly Dictionary<int, int> functionRefs = [];
    private readonly Dictionary<int, int> actionRefs = [];
    private readonly HashSet<int> pinned = [];
    private readonly HashSet<int> inlined = [];
    private readonly List<int> inlineLog = [];
    private readonly HashSet<int> emitted = [];
    private readonly List<int> inlineStack = [];

    private readonly IReadOnlyList<LuaTemplate> templates;
    private readonly LuaNames names;

    private LuaScriptLifter(ScriptTextDocument document, ScriptLanguage language, IReadOnlyList<string>? messages, IReadOnlyList<LuaTemplate>? templates, LuaNames? names)
    {
        this.names = names ?? LuaNames.None;
        this.document = document;
        this.language = language;
        this.messages = messages;
        this.templates = templates ?? [];
        functions = document.Functions.GroupBy(f => f.Number).ToDictionary(g => g.Key, g => g.First());
        actions = document.Actions.GroupBy(a => a.Number).ToDictionary(g => g.Key, g => g.First());
    }

    /// <param name="messages">The script's text archive: each message is written as its text,
    /// <c>Message("text")</c>, and saving numbers them again.</param>
    /// <param name="templates">global.lua's script templates (LuaScriptCompiler.FindTemplates): a script
    /// one of them makes is written as a call to it, <c>script(18, npc_message("Hello?"))</c>.</param>
    /// <param name="names">global.lua's variable and flag names, written in place of their numbers.</param>
    public static string Lift(ScriptTextDocument document, ScriptLanguage language, string? header = null, IReadOnlyList<string>? messages = null,
        IReadOnlyList<LuaTemplate>? templates = null, LuaNames? names = null) =>
        new LuaScriptLifter(document, language, messages, templates, names).Run(header);

    private enum FrameKind { Script, Function }

    private abstract record Stmt;
    private sealed record TextStmt(string Lua) : Stmt;
    private sealed record IfStmt(string Condition, List<Stmt> Then, List<Stmt>? Else) : Stmt;
    private sealed record EndStmt : Stmt;
    private sealed record ReturnStmt : Stmt;
    /// <summary><c>return func_N()</c>, a Jump; Count is how many original Jumps it stands for once
    /// branches ending in the same Jump have been merged.</summary>
    private sealed record TailJumpStmt(int Function, int Count) : Stmt;

    private string Run(string? header)
    {
        CountReferences();

        var scriptBodies = new Dictionary<int, List<Stmt>>();
        var templateCalls = new Dictionary<int, string>();
        foreach (ScriptTextContainer script in document.Scripts)
        {
            if (script.UseScript is not null) continue;
            if (MatchTemplate(script) is string call) templateCalls[script.Number] = call;
            else scriptBodies[script.Number] = LiftContainer(script, FrameKind.Script);
        }

        // Functions nobody can absorb (used from several places, or never) are lifted first, so a function
        // used once is still free to be inlined by whichever of them uses it.
        var functionBodies = new SortedDictionary<int, List<Stmt>>();
        foreach (int number in functions.Keys.Order().Where(n => functionRefs.GetValueOrDefault(n) != 1 || pinned.Contains(n)).Concat(functions.Keys.Order()))
        {
            if (inlined.Contains(number) || emitted.Contains(number)) continue;
            emitted.Add(number);
            if (functions[number].UseScript is null) functionBodies[number] = LiftContainer(functions[number], FrameKind.Function);
        }

        var lua = new StringBuilder();
        if (header is not null)
        {
            foreach (string line in header.Split('\n')) lua.Append("-- ").Append(line.TrimEnd()).Append('\n');
            lua.Append('\n');
        }

        bool sharedActions = false;
        foreach (ScriptTextAction action in document.Actions)
        {
            if (actionRefs.GetValueOrDefault(action.Number) == 1) continue;
            lua.Append("local ").Append(ActionName(action.Number)).Append(" = ").Append(RenderSteps(action)).Append('\n');
            sharedActions = true;
        }
        if (sharedActions) lua.Append('\n');

        foreach (ScriptTextContainer script in document.Scripts)
        {
            if (script.UseScript is int used)
            {
                lua.Append($"script({script.Number}, {used})\n\n");
                continue;
            }
            if (templateCalls.TryGetValue(script.Number, out string? templateCall))
            {
                lua.Append($"script({script.Number}, {templateCall})\n\n");
                continue;
            }
            lua.Append($"script({script.Number}, function()\n");
            RenderBody(lua, scriptBodies[script.Number], FrameKind.Script, 1);
            lua.Append("end)\n\n");
        }

        foreach (int number in emitted.Order())
        {
            ScriptTextContainer function = functions[number];
            if (function.UseScript is int used)
            {
                lua.Append($"{FunctionName(number)} = use_script({used})\n\n");
                continue;
            }
            lua.Append($"function {FunctionName(number)}()\n");
            RenderBody(lua, functionBodies[number], FrameKind.Function, 1);
            lua.Append("end\n\n");
        }

        return lua.ToString().TrimEnd() + "\n";
    }

    private static string FunctionName(int number) => $"func_{number}";
    private static string ActionName(int number) => $"action_{number}";

    #region Analysis

    private void CountReferences()
    {
        foreach (ScriptTextContainer container in document.Scripts.Concat(document.Functions))
        {
            if (container.UseScript is not null) continue;
            foreach (ScriptTextLine line in container.Lines)
            {
                bool structured = IsStructuredLine(line);
                foreach (string arg in line.Args)
                {
                    if (ScriptTextDocument.TryParseFunctionRef(arg, out int function))
                    {
                        functionRefs[function] = functionRefs.GetValueOrDefault(function) + 1;
                        if (!structured) pinned.Add(function);
                    }
                    else if (ScriptTextDocument.TryParseActionRef(arg, out int action))
                    {
                        // An asm line refers to its Action by name, so that Action is declared as a local.
                        actionRefs[action] = structured ? actionRefs.GetValueOrDefault(action) + 1 : int.MaxValue;
                    }
                }
            }
            if (container.Terminator == ScriptTextTerminator.Jump)
            {
                functionRefs[container.JumpTarget] = functionRefs.GetValueOrDefault(container.JumpTarget) + 1;
            }
        }
    }

    /// <summary>Whether a line's Function/Action references can be written as Lua values (otherwise the
    /// line goes out as asm and its Functions must keep their names).</summary>
    private bool IsStructuredLine(ScriptTextLine line)
    {
        ScriptCommandInfo? info = language.FindCommand(line.Command);
        if (info is null) return !line.Args.Any(arg => ScriptTextDocument.TryParseFunctionRef(arg, out _) || ScriptTextDocument.TryParseActionRef(arg, out _));
        if (info.ParameterSizes.Count > 0 && info.ParameterSizes[0] == 0xFF) return !line.Args.Any(arg => ScriptTextDocument.TryParseFunctionRef(arg, out _));
        if (line.Args.Count != info.ParameterSizes.Count) return false;
        for (int i = 0; i < line.Args.Count; i++)
        {
            ScriptParameterType? type = i < info.ParameterTypes.Count ? info.ParameterTypes[i] : null;
            bool isFunction = ScriptTextDocument.TryParseFunctionRef(line.Args[i], out int function);
            bool isAction = ScriptTextDocument.TryParseActionRef(line.Args[i], out int action);
            if (isFunction && (type != ScriptParameterType.Function || !functions.ContainsKey(function))) return false;
            if (isAction && (type != ScriptParameterType.Action || !actions.ContainsKey(action))) return false;
            if (type == ScriptParameterType.Function && !isFunction) return false;
            if (type == ScriptParameterType.Action && !isAction) return false;
            if (type == ScriptParameterType.ComparisonOperator && language.FindOperator(line.Args[i]) is null) return false;
        }
        return true;
    }

    private bool CanInline(int function) =>
        functions.TryGetValue(function, out ScriptTextContainer? container)
        && container.UseScript is null
        && functionRefs.GetValueOrDefault(function) == 1
        && !pinned.Contains(function)
        && !inlined.Contains(function)
        && !emitted.Contains(function)
        && !inlineStack.Contains(function);

    /// <summary>Lifts a Function's code to be written where it is jumped to, or null when it cannot be:
    /// in a script, code that Returns stays a separate Function, since a script's <c>return</c> is End.</summary>
    private List<Stmt>? TryInlineJump(int function, FrameKind frame, int knownRefs = 1)
    {
        if (functionRefs.GetValueOrDefault(function) != knownRefs) return null;
        functionRefs[function] = 1;
        bool allowed = CanInline(function);
        functionRefs[function] = knownRefs;
        if (!allowed) return null;

        int mark = inlineLog.Count;
        MarkInlined(function);
        inlineStack.Add(function);
        List<Stmt> body = LiftContainer(functions[function], frame);
        inlineStack.RemoveAt(inlineStack.Count - 1);
        if (frame == FrameKind.Script && Contains(body, stmt => stmt is ReturnStmt))
        {
            Rollback(mark);
            return null;
        }
        return body;
    }

    /// <summary>Lifts a Function's code to be written where it is called (Call/CallIf), or null when it
    /// leaves other than by its final Return: a Jump or an early Return relies on the call.</summary>
    private List<Stmt>? TryInlineCall(int function)
    {
        if (!CanInline(function)) return null;
        int mark = inlineLog.Count;
        MarkInlined(function);
        inlineStack.Add(function);
        List<Stmt> body = LiftContainer(functions[function], FrameKind.Function);
        inlineStack.RemoveAt(inlineStack.Count - 1);
        if (!StripTailReturns(body))
        {
            Rollback(mark);
            return null;
        }
        return body;
    }

    private void MarkInlined(int function)
    {
        inlined.Add(function);
        inlineLog.Add(function);
    }

    private void Rollback(int mark)
    {
        for (int i = inlineLog.Count - 1; i >= mark; i--) inlined.Remove(inlineLog[i]);
        inlineLog.RemoveRange(mark, inlineLog.Count - mark);
    }

    private static bool Contains(List<Stmt> statements, Func<Stmt, bool> predicate) =>
        statements.Any(stmt => predicate(stmt) || stmt is IfStmt ifStmt && (Contains(ifStmt.Then, predicate) || ifStmt.Else is not null && Contains(ifStmt.Else, predicate)));

    private static bool StripTailReturns(List<Stmt> statements)
    {
        if (Contains(statements, stmt => stmt is TailJumpStmt)) return false;
        if (statements.Count == 0) return true;
        if (Contains(statements.SkipLast(1).ToList(), stmt => stmt is ReturnStmt)) return false;
        switch (statements[^1])
        {
            case ReturnStmt:
                statements.RemoveAt(statements.Count - 1);
                return true;
            case IfStmt ifStmt:
                return StripTailReturns(ifStmt.Then) && (ifStmt.Else is null || StripTailReturns(ifStmt.Else));
            default:
                return true;
        }
    }

    #endregion

    #region Lifting

    private List<Stmt> LiftContainer(ScriptTextContainer container, FrameKind frame) => LiftLines(container, 0, frame);

    private List<Stmt> LiftLines(ScriptTextContainer container, int start, FrameKind frame)
    {
        var statements = new List<Stmt>();
        IReadOnlyList<ScriptTextLine> lines = container.Lines;
        for (int i = start; i < lines.Count; i++)
        {
            ScriptTextLine line = lines[i];
            string? fusedCondition = null;
            ScriptTextLine branch = line;
            if (i + 2 < lines.Count && IsBranch(lines[i + 2]) && FuseResultTest(line, lines[i + 1], lines[i + 2]) is string fusedResult)
            {
                fusedCondition = fusedResult;
                i += 2;
                branch = lines[i];
            }
            else if (i + 1 < lines.Count && IsBranch(lines[i + 1]) && FuseTest(line, lines[i + 1]) is string fused)
            {
                fusedCondition = fused;
                branch = lines[++i];
            }

            if (IsBranch(branch) && (fusedCondition is not null || IsStructuredLine(branch)))
            {
                int op = language.FindOperator(branch.Args[0])!.Value;
                ScriptTextDocument.TryParseFunctionRef(branch.Args[1], out int target);
                string condition = fusedCondition ?? $"condition({OperatorConstant(op)})";

                if (IsCallIf(branch))
                {
                    List<Stmt> body = TryInlineCall(target) ?? [new TextStmt($"{FunctionName(target)}()")];
                    statements.Add(new IfStmt(condition, body, null));
                    continue;
                }

                List<Stmt> then = TryInlineJump(target, frame) ?? [new TailJumpStmt(target, 1)];
                List<Stmt> rest = LiftLines(container, i + 1, frame);
                statements.Add(new IfStmt(condition, then, rest));
                Normalize(statements, frame);
                return statements;
            }

            if (fusedCondition is not null)
            {
                // The test fused with a branch that turned out not to be liftable: emit both lines as is.
                statements.Add(LiftLine(lines[i - 1]));
                statements.Add(LiftLine(branch));
                continue;
            }

            if (language.FindCommand(line.Command) is { } info && info.CommandId == 0x1A && line.Args.Count == 1
                && ScriptTextDocument.TryParseFunctionRef(line.Args[0], out int called) && functions.ContainsKey(called))
            {
                List<Stmt>? body = TryInlineCall(called);
                if (body is not null) statements.AddRange(body);
                else statements.Add(new TextStmt($"{FunctionName(called)}()"));
                continue;
            }

            statements.Add(LiftLine(line));
        }

        switch (container.Terminator)
        {
            case ScriptTextTerminator.End:
                statements.Add(new EndStmt());
                break;
            case ScriptTextTerminator.Return:
                statements.Add(new ReturnStmt());
                break;
            default:
                if (!functions.ContainsKey(container.JumpTarget))
                {
                    statements.Add(new TextStmt($"asm [[ {language.Jump} Function#{container.JumpTarget} ]]"));
                    break;
                }
                List<Stmt>? next = TryInlineJump(container.JumpTarget, frame);
                if (next is not null) statements.AddRange(next);
                else statements.Add(new TailJumpStmt(container.JumpTarget, 1));
                break;
        }
        return statements;
    }

    private bool IsBranch(ScriptTextLine line) =>
        language.FindCommand(line.Command) is { CommandId: 0x1C or 0x1D } && line.Args.Count == 2
        && language.FindOperator(line.Args[0]) is not null
        && ScriptTextDocument.TryParseFunctionRef(line.Args[1], out int target) && functions.ContainsKey(target);

    private bool IsCallIf(ScriptTextLine line) => language.FindCommand(line.Command) is { CommandId: 0x1D };

    private static readonly string[] LuaOperators = ["<", "==", ">", "<=", ">=", "~="];

    /// <summary>The Lua condition for a test line directly followed by JumpIf/CallIf, or null when the
    /// test is not one with a Lua form (the branch then reads <c>condition(OP)</c>).</summary>
    private string? FuseTest(ScriptTextLine test, ScriptTextLine branch)
    {
        ScriptCommandInfo? info = language.FindCommand(test.Command);
        int? op = language.FindOperator(branch.Args[0]);
        if (info is null || op is null || test.Args.Count != 2 && info.CommandId != 0x20) return null;
        switch (info.CommandId)
        {
            case 0x11 when test.Args.Count == 2 && IsNumber(test.Args[0]) && LuaValue(test.Args[1], null) is string value && IsPlainValue(test.Args[1]):
                return $"{VarExpression(test.Args[0])} {LuaOperators[op.Value]} {value}";
            case 0x12 when test.Args.Count == 2 && IsNumber(test.Args[0]) && IsNumber(test.Args[1]):
                return $"{VarExpression(test.Args[0])} {LuaOperators[op.Value]} {VarExpression(test.Args[1])}";
            case 0x20 when test.Args.Count == 1 && IsNumber(test.Args[0]) && op is ScriptLanguage.Equal:
                return FlagExpression(test.Args[0]);
            case 0x20 when test.Args.Count == 1 && IsNumber(test.Args[0]) && op is ScriptLanguage.Different:
                return $"not {FlagExpression(test.Args[0])}";
            default:
                return null;
        }
    }

    /// <summary>A command whose result goes in VAR_RESULT and is compared straight away, written as one
    /// condition: <c>CheckItem ITEM_POTION 1 0x800C / CompareVarValue 0x800C 1 / JumpIf EQUAL</c> becomes
    /// <c>CheckItem(ITEM_POTION, 1) == 1</c>, which compiles back to those same lines.</summary>
    private string? FuseResultTest(ScriptTextLine command, ScriptTextLine test, ScriptTextLine branch)
    {
        if (!IsResultLine(command, out ScriptCommandInfo? info) || !TryParseNumber(command.Args[^1], out long variable) || variable != ScriptLanguage.ResultVariable) return null;
        if (language.FindCommand(test.Command) is not { CommandId: 0x11 } || test.Args.Count != 2
            || !TryParseNumber(test.Args[0], out long tested) || tested != variable || !IsPlainValue(test.Args[1])) return null;
        if (language.FindOperator(branch.Args[0]) is not int op || LuaValue(test.Args[1], null) is not string value) return null;
        return $"{RenderCall(info, command, command.Args.Count - 1)} {LuaOperators[op]} {value}";
    }

    /// <summary>A command storing its result in a variable given as its last argument.</summary>
    private bool IsResultLine(ScriptTextLine line, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ScriptCommandInfo? info)
    {
        info = language.FindCommand(line.Command);
        return info is not null && language.HasResultParameter(info) && line.Args.Count == info.ParameterSizes.Count
               && IsNumber(line.Args[^1]) && IsStructuredLine(line) && ScriptLanguage.IsIdentifier(info.Name) && language.ResolveCommand(info.Name) == info;
    }

    /// <summary>Numbers and named constants compare as written; anything else (Overworld.N...) is left
    /// to a plain CompareVarValue line.</summary>
    private static bool IsPlainValue(string token) => IsNumber(token) || PassthroughConstant().IsMatch(token);

    private Stmt LiftLine(ScriptTextLine line)
    {
        ScriptCommandInfo? info = language.FindCommand(line.Command);
        if (!IsStructuredLine(line)) return new TextStmt($"asm [[ {RenderAsmLine(line)} ]]");

        if (info is not null && language.SetVar is not null && string.Equals(info.Name, language.SetVar, StringComparison.OrdinalIgnoreCase)
            && line.Args.Count == 2 && IsNumber(line.Args[0]) && IsNumber(line.Args[1]))
        {
            return new TextStmt($"{VarExpression(line.Args[0])} = {line.Args[1]}");
        }
        if (info?.CommandId is 0x1E or 0x1F && line.Args.Count == 1 && FlagName(line.Args[0]) is string flagName)
        {
            return new TextStmt($"{flagName} = {(info.CommandId == 0x1E ? "true" : "false")}");
        }

        // X = CheckItem(ITEM_POTION, 1): the result's variable written as the assignment's target.
        if (IsResultLine(line, out ScriptCommandInfo? resultInfo))
        {
            return new TextStmt($"{VarExpression(line.Args[^1])} = {RenderCall(resultInfo, line, line.Args.Count - 1)}");
        }

        return new TextStmt(RenderCall(info, line, line.Args.Count));
    }

    /// <summary>A command line as a Lua call, with its first <paramref name="count"/> arguments.</summary>
    private string RenderCall(ScriptCommandInfo? info, ScriptTextLine line, int count)
    {
        bool variableLayout = info is not null && info.ParameterSizes.Count > 0 && info.ParameterSizes[0] == 0xFF;
        var args = new List<string>();
        for (int i = 0; i < count; i++)
        {
            ScriptParameterType? type = info is not null && !variableLayout && i < info.ParameterTypes.Count ? info.ParameterTypes[i] : null;
            string arg = line.Args[i];
            if (type == ScriptParameterType.Function && ScriptTextDocument.TryParseFunctionRef(arg, out int function)) args.Add(FunctionName(function));
            else if (type == ScriptParameterType.Action && ScriptTextDocument.TryParseActionRef(arg, out int action)) args.Add(RenderActionArg(action));
            else if (MessageTextAt(info, line, i) is string text) args.Add(LuaMessageText.ToLuaLiteral(text));
            else if (IsFlagParameter(info, i) && FlagName(arg) is string flag) args.Add(flag);
            else if ((type == ScriptParameterType.Variable || IsNumber(arg) && TryParseNumber(arg, out long value) && value >= 0x4000) && VarName(arg) is string variable) args.Add(variable);
            else args.Add(LuaValue(arg, type) ?? $"sym({Quote(arg)})");
        }


        return info is not null && ScriptLanguage.IsIdentifier(info.Name) && language.ResolveCommand(info.Name) == info
            ? $"{info.Name}({string.Join(", ", args)})"
            : $"cmd({string.Join(", ", args.Prepend(Quote(info?.Name ?? line.Command)))})";
    }

    private string? VarName(string token) => TryParseNumber(token, out long id) && names.Variables.TryGetValue(id, out string? name) ? name : null;
    private string? FlagName(string token) => TryParseNumber(token, out long id) && names.Flags.TryGetValue(id, out string? name) ? name : null;
    private string VarExpression(string token) => VarName(token) ?? $"var[{token}]";
    private string FlagExpression(string token) => FlagName(token) ?? $"flag[{token}]";

    /// <summary>The flag commands, and any parameter the database describes as a flag ("u16: Flag").</summary>
    private static bool IsFlagParameter(ScriptCommandInfo? info, int index) =>
        info is not null && (info.CommandId is 0x1E or 0x1F or 0x20
                             || index < info.ParameterNames.Count && info.ParameterNames[index].Contains("Flag", StringComparison.OrdinalIgnoreCase));

    /// <summary>The archive text of argument <paramref name="index"/> when it is a message slot whose
    /// entry the archive has (not a variable, which a Flex slot can also be).</summary>
    private string? MessageTextAt(ScriptCommandInfo? info, ScriptTextLine line, int index) =>
        messages is not null && info is not null && index < line.Args.Count && info.ParameterSizes is not [0xFF, ..]
        && ScriptLanguage.IsMessageParameter(info, index)
        && TryParseNumber(line.Args[index], out long entry) && entry >= 0 && entry < Math.Min(messages.Count, 0x4000)
            ? messages[(int)entry]
            : null;

    /// <summary>A line's arguments with each message slot's text in place of its number, as a compiled
    /// <c>Message("Hi!")</c> writes them, or after its number as <c>Message(4, "Hi!")</c> does.</summary>
    private List<string> ArgsWithTexts(ScriptTextLine line, bool afterNumber)
    {
        ScriptCommandInfo? info = language.FindCommand(line.Command);
        var args = new List<string>();
        for (int i = 0; i < line.Args.Count; i++)
        {
            string? text = MessageTextAt(info, line, i);
            if (text is null || afterNumber) args.Add(line.Args[i]);
            if (text is not null) args.Add(LuaMessageText.ToInlineToken(text));
        }
        return args;
    }

    /// <summary>A call to the first template that makes exactly this script, e.g. <c>npc_message("Hello?")</c>,
    /// or null. Placeholder numbers in the template stand for any argument token, read
    /// back as the Lua for that token; placeholder texts take the message text of the line.</summary>
    private string? MatchTemplate(ScriptTextContainer script)
    {
        List<List<ScriptTextLine>> lines = script.Lines.Select(line => new List<ScriptTextLine> { line }).ToList();
        List<List<ScriptTextLine>> folded = FoldMessageRuns(script.Lines);
        foreach (LuaTemplate template in templates)
        {
            ScriptTextContainer pattern = template.Instance.Scripts[0];
            if (pattern.Terminator != script.Terminator || script.Terminator == ScriptTextTerminator.Jump) continue;
            if (MatchLines(template, pattern, lines) is string call) return call;
            if (folded.Count != lines.Count && MatchLines(template, pattern, folded) is string foldedCall) return foldedCall;
        }
        return null;
    }

    /// <summary>A call to <paramref name="template"/> making these lines, each entry one line or a run of
    /// messages shown one after another, or null.</summary>
    private string? MatchLines(LuaTemplate template, ScriptTextContainer pattern, List<List<ScriptTextLine>> lines)
    {
        if (pattern.Lines.Count != lines.Count) return null;
        var bound = new string?[template.TextParameters.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            bool matches = lines[i].Count == 1 ? MatchLine(template, pattern.Lines[i], lines[i][0], bound) : MatchRun(template, pattern.Lines[i], lines[i], bound);
            if (!matches) return null;
        }
        return bound.Any(value => value is null) ? null : $"{template.Name}({string.Join(", ", bound)})";
    }

    /// <summary>Runs of <c>Message a / WaitButton / Message b ...</c> grouped together, the way a list of
    /// messages, <c>Message({a, b})</c>, compiles.</summary>
    private List<List<ScriptTextLine>> FoldMessageRuns(IReadOnlyList<ScriptTextLine> lines)
    {
        bool IsMessage(ScriptTextLine line) => line.Command.Equals("Message", StringComparison.OrdinalIgnoreCase) && line.Args.Count == 1 && IsNumber(line.Args[0]);
        bool IsWait(ScriptTextLine line) => line.Command.Equals("WaitButton", StringComparison.OrdinalIgnoreCase) && line.Args.Count == 0;

        var groups = new List<List<ScriptTextLine>>();
        for (int i = 0; i < lines.Count; i++)
        {
            var group = new List<ScriptTextLine> { lines[i] };
            while (IsMessage(lines[i]) && i + 2 < lines.Count && IsWait(lines[i + 1]) && IsMessage(lines[i + 2]))
            {
                group.Add(lines[i + 2]);
                i += 2;
            }
            groups.Add(group);
        }
        return groups;
    }

    /// <summary>Matches a run of messages against a template's Message line whose number (and text) are
    /// parameters, binding them to lists: <c>{107, 108}</c>, <c>{"Hi!", "Bye!"}</c>.</summary>
    private bool MatchRun(LuaTemplate template, ScriptTextLine pattern, List<ScriptTextLine> run, string?[] bound)
    {
        if (!pattern.Command.Equals(run[0].Command, StringComparison.OrdinalIgnoreCase) || pattern.Args.Count is < 1 or > 2) return false;
        ScriptCommandInfo? runInfo = language.FindCommand(run[0].Command);
        if (pattern.Args is [string onlyText] && onlyText.StartsWith('"'))
        {
            // Message(texts): the run's texts as one list.
            int listParameter = Enumerable.Range(0, template.TextParameters.Count)
                .FirstOrDefault(p => onlyText == LuaMessageText.ToInlineToken(LuaScriptCompiler.TemplateText(p)), -1);
            var runTexts = run.Select(line => MessageTextAt(runInfo, line, 0)).ToList();
            return listParameter >= 0 && runTexts.All(text => text is not null)
                   && Bind(bound, listParameter, "{ " + string.Join(", ", runTexts.Select(text => LuaMessageText.ToLuaLiteral(text!))) + " }");
        }
        if (!TryParseNumber(pattern.Args[0], out long number)) return false;
        int numberParameter = (int)(number - LuaScriptCompiler.TemplateNumber);
        if (numberParameter < 0 || numberParameter >= template.TextParameters.Count || template.TextParameters[numberParameter]) return false;
        if (!Bind(bound, numberParameter, "{ " + string.Join(", ", run.Select(line => line.Args[0])) + " }")) return false;
        if (pattern.Args.Count == 1) return true;

        int textParameter = Enumerable.Range(0, template.TextParameters.Count)
            .FirstOrDefault(p => pattern.Args[1] == LuaMessageText.ToInlineToken(LuaScriptCompiler.TemplateText(p)), -1);
        if (textParameter < 0) return false;
        ScriptCommandInfo? info = language.FindCommand(run[0].Command);
        var texts = run.Select(line => MessageTextAt(info, line, 0)).ToList();
        return texts.All(text => text is not null)
               && Bind(bound, textParameter, "{ " + string.Join(", ", texts.Select(text => LuaMessageText.ToLuaLiteral(text!))) + " }");
    }

    private bool MatchLine(LuaTemplate template, ScriptTextLine pattern, ScriptTextLine line, string?[] bound)
    {
        if (!string.Equals(pattern.Command, line.Command, StringComparison.OrdinalIgnoreCase)) return false;

        IReadOnlyList<string> args = !pattern.Args.Any(arg => arg.StartsWith('"')) ? line.Args
            : ArgsWithTexts(line, afterNumber: pattern.Args.Count != line.Args.Count);
        if (pattern.Args.Count != args.Count) return false;

        for (int i = 0; i < args.Count; i++)
        {
            string expected = pattern.Args[i], actual = args[i];
            for (int p = 0; p < template.TextParameters.Count; p++)
            {
                if (expected == LuaMessageText.ToInlineToken(LuaScriptCompiler.TemplateText(p)))
                {
                    if (!actual.StartsWith('"')) return false;
                    string value = LuaMessageText.ToLuaLiteral(actual[1..^1].Replace("\\\"", "\""));
                    if (!Bind(bound, p, value)) return false;
                    goto next;
                }
            }

            Match reference = OverworldOrMove().Match(expected);
            string expectedNumber = reference.Success ? reference.Groups[2].Value : expected;
            if (TryParseNumber(expectedNumber, out long number) && number >= LuaScriptCompiler.TemplateNumber
                && number < LuaScriptCompiler.TemplateNumber + template.TextParameters.Count && !template.TextParameters[(int)(number - LuaScriptCompiler.TemplateNumber)])
            {
                int p = (int)(number - LuaScriptCompiler.TemplateNumber);
                if (reference.Success)
                {
                    Match actualReference = OverworldOrMove().Match(actual);
                    if (!actualReference.Success || actualReference.Groups[1].Value != reference.Groups[1].Value) return false;
                    actual = actualReference.Groups[2].Value;
                }
                if (!Bind(bound, p, LuaValue(actual, null) ?? $"sym({Quote(actual)})")) return false;
                continue;
            }

            if (ScriptTextDocument.TryParseActionRef(expected, out int patternAction))
            {
                if (!ScriptTextDocument.TryParseActionRef(actual, out int action) || !actions.TryGetValue(action, out ScriptTextAction? steps)) return false;
                ScriptTextAction wanted = template.Instance.Actions.First(a => a.Number == patternAction);
                if (ActionKey(wanted) != ActionKey(steps)) return false;
                continue;
            }

            if (!SameToken(expected, actual)) return false;
            next:;
        }
        return true;
    }

    private static bool Bind(string?[] bound, int parameter, string value)
    {
        if (bound[parameter] is not null && bound[parameter] != value) return false;
        bound[parameter] = value;
        return true;
    }

    private bool SameToken(string a, string b)
    {
        if (TryParseNumber(a, out long x) && TryParseNumber(b, out long y)) return x == y;
        if (language.FindOperator(a) is int opA && language.FindOperator(b) is int opB) return opA == opB;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private string ActionKey(ScriptTextAction action) => string.Join(',', action.Steps.Select(step =>
        $"{(language.FindMovement(step.Movement)?.Id ?? (ushort.TryParse(step.Movement, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort id) ? id : -1))}"
        + $"*{(step.Count is not null && TryParseNumber(step.Count, out long count) ? count : 1)}"));

    /// <summary>Writes Function and Action references in an asm line as {func_N}/{action_N}, which the
    /// compiler resolves to whatever numbers they get.</summary>
    private string RenderAsmLine(ScriptTextLine line)
    {
        IEnumerable<string> args = line.Args.Select(arg =>
            ScriptTextDocument.TryParseFunctionRef(arg, out int function) && functions.ContainsKey(function) ? $"{{{FunctionName(function)}}}"
            : ScriptTextDocument.TryParseActionRef(arg, out int action) && actions.ContainsKey(action) ? $"{{{ActionName(action)}}}"
            : arg);
        string text = string.Join(' ', args.Prepend(line.Command));
        return text.Contains("]]", StringComparison.Ordinal) ? throw new InvalidDataException($"Cannot write \"{text}\" in an asm block.") : text;
    }

    private string RenderActionArg(int action)
    {
        if (actionRefs.GetValueOrDefault(action) != 1) return ActionName(action);
        return RenderSteps(actions[action]);
    }

    private string RenderSteps(ScriptTextAction action)
    {
        var steps = new List<string>();
        foreach ((string movement, string? count) in action.Steps)
        {
            long repeat = count is not null && TryParseNumber(count, out long parsed) ? parsed : 1;
            ScriptSubEntryInfo? info = language.FindMovement(movement);
            string name;
            if (info is not null && language.ResolveMovement(info.Name) == info && ScriptLanguage.IsIdentifier(info.Name)) name = info.Name;
            else if (info is not null)
            {
                steps.Add($"step(0x{info.Id:X4}, {repeat})");
                continue;
            }
            else if (ushort.TryParse(movement, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort id))
            {
                steps.Add($"step(0x{id:X4}, {repeat})");
                continue;
            }
            else throw new InvalidDataException($"Action {action.Number} has an unknown movement \"{movement}\".");
            steps.Add(repeat == 1 ? name : $"{name}({repeat})");
        }
        return "{ " + string.Join(", ", steps) + " }";
    }

    /// <summary>A plaintext argument token as a Lua expression the compiler turns back into the same token,
    /// or null if there is none.</summary>
    private string? LuaValue(string token, ScriptParameterType? type)
    {
        if (IsNumber(token)) return token;
        Match reference = OverworldOrMove().Match(token);
        if (reference.Success) return $"{reference.Groups[1].Value}[{reference.Groups[2].Value}]";
        if (language.FindOperator(token) is int op) return OperatorConstant(op);
        if (language.Database.SpecialOverworlds.Values.Contains(token) || language.Database.OverworldDirections.Values.Contains(token))
        {
            return ScriptLanguage.IsIdentifier(token) ? token : null;
        }
        if (PassthroughConstant().IsMatch(token) && language.ResolveCommand(token) is null && language.ResolveMovement(token) is null) return token;
        if (token.StartsWith("CMD_", StringComparison.Ordinal) && language.ResolveCommand(token) is not null) return token;
        return null;
    }

    private string OperatorConstant(int op) => language.ComparisonOperators[op].Replace('/', '_');

    private static bool IsNumber(string token) => NumberToken().IsMatch(token);

    private static bool TryParseNumber(string token, out long value) =>
        token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    [GeneratedRegex(@"^(0[xX][0-9A-Fa-f]+|\d+)$")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"^(Overworld|Move)\.(\d+)$")]
    private static partial Regex OverworldOrMove();

    [GeneratedRegex(@"^(SEQ|SPECIES|ITEM|MOVE|TRAINER|CMD)_\w+$")]
    private static partial Regex PassthroughConstant();

    #endregion

    #region Structuring

    /// <summary>Tidies the if that ends <paramref name="statements"/>:
    /// - when both branches finish by jumping to the same Function, that jump moves after the if, and if
    ///   those were the Function's only uses its code follows the if directly (the join of an if/else);
    /// - when the then-branch never falls through, the else-branch's code follows the if instead of
    ///   nesting under else (a guard clause).</summary>
    private void Normalize(List<Stmt> statements, FrameKind frame)
    {
        if (statements.Count == 0 || statements[^1] is not IfStmt { Else: not null } ifStmt) return;

        Dictionary<int, int> thenJumps = TailJumps(ifStmt.Then);
        Dictionary<int, int> elseJumps = TailJumps(ifStmt.Else);
        int? join = thenJumps.Keys.Intersect(elseJumps.Keys).OrderByDescending(f => thenJumps[f] + elseJumps[f]).Cast<int?>().FirstOrDefault();
        if (join is int target)
        {
            int count = StripTailJumps(ifStmt.Then, target) + StripTailJumps(ifStmt.Else, target);
            List<Stmt>? inline = TryInlineJump(target, frame, count);
            if (inline is not null) statements.AddRange(inline);
            else statements.Add(new TailJumpStmt(target, count));
            if (statements[^1] is IfStmt) Normalize(statements, frame);
            return;
        }

        if (AlwaysLeaves(ifStmt.Then))
        {
            statements[^1] = ifStmt with { Else = null };
            statements.AddRange(ifStmt.Else);
        }
    }

    /// <summary>The Functions the paths through the end of a statement list jump to, with how many
    /// original Jumps each stands for.</summary>
    private static Dictionary<int, int> TailJumps(List<Stmt> statements)
    {
        var result = new Dictionary<int, int>();
        void Collect(List<Stmt> list)
        {
            if (list.Count == 0) return;
            switch (list[^1])
            {
                case TailJumpStmt jump:
                    result[jump.Function] = result.GetValueOrDefault(jump.Function) + jump.Count;
                    break;
                case IfStmt { Else: not null } nested:
                    Collect(nested.Then);
                    Collect(nested.Else);
                    break;
            }
        }
        Collect(statements);
        return result;
    }

    private static int StripTailJumps(List<Stmt> statements, int target)
    {
        if (statements.Count == 0) return 0;
        switch (statements[^1])
        {
            case TailJumpStmt jump when jump.Function == target:
                statements.RemoveAt(statements.Count - 1);
                return jump.Count;
            case IfStmt { Else: not null } nested:
                return StripTailJumps(nested.Then, target) + StripTailJumps(nested.Else, target);
            default:
                return 0;
        }
    }

    /// <summary>Whether every path through the end of the list leaves (End, Return or a Jump) instead
    /// of falling through to what follows.</summary>
    private static bool AlwaysLeaves(List<Stmt> statements) => statements.Count > 0 && statements[^1] switch
    {
        EndStmt or ReturnStmt or TailJumpStmt => true,
        IfStmt { Else: not null } nested => AlwaysLeaves(nested.Then) && AlwaysLeaves(nested.Else),
        _ => false,
    };

    #endregion

    #region Rendering

    private void RenderBody(StringBuilder lua, List<Stmt> body, FrameKind frame, int depth)
    {
        int count = body.Count;
        if (count > 0 && (frame == FrameKind.Script ? body[^1] is EndStmt : body[^1] is ReturnStmt)) count--;
        for (int i = 0; i < count; i++) RenderStmt(lua, body[i], frame, depth);
    }

    private void RenderBlock(StringBuilder lua, List<Stmt> body, FrameKind frame, int depth)
    {
        foreach (Stmt statement in body) RenderStmt(lua, statement, frame, depth);
    }

    private void RenderStmt(StringBuilder lua, Stmt statement, FrameKind frame, int depth)
    {
        string indent = new('\t', depth);
        switch (statement)
        {
            case TextStmt text:
                lua.Append(indent).Append(text.Lua).Append('\n');
                break;
            case EndStmt:
                lua.Append(indent).Append(language.End).Append("()\n");
                break;
            case ReturnStmt:
                lua.Append(indent).Append(frame == FrameKind.Function ? "return" : $"{language.Return}()").Append('\n');
                break;
            case TailJumpStmt jump:
                lua.Append(indent).Append("return ").Append(FunctionName(jump.Function)).Append("()\n");
                break;
            case IfStmt ifStmt:
            {
                lua.Append(indent).Append("if ").Append(ifStmt.Condition).Append(" then\n");
                RenderBlock(lua, ifStmt.Then, frame, depth + 1);
                List<Stmt>? rest = ifStmt.Else;
                while (rest is [IfStmt { } elseIf])
                {
                    lua.Append(indent).Append("elseif ").Append(elseIf.Condition).Append(" then\n");
                    RenderBlock(lua, elseIf.Then, frame, depth + 1);
                    rest = elseIf.Else;
                }
                if (rest is { Count: > 0 })
                {
                    lua.Append(indent).Append("else\n");
                    RenderBlock(lua, rest, frame, depth + 1);
                }
                lua.Append(indent).Append("end\n");
                break;
            }
        }
    }

    #endregion
}
