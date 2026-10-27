using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DSPRE.Core.Lua;

public sealed record LuaCompileResult(ScriptTextDocument Document, IReadOnlyList<string> Warnings);

/// <summary>A global.lua helper that makes a whole script, <c>function name(params) return function() ...
/// end end</c>, compiled with placeholder arguments so scripts it would produce can be recognised.
/// <see cref="Instance"/> has those placeholders where the arguments go: numbers from
/// <see cref="LuaScriptCompiler.TemplateNumber"/> up, and texts <c>"\u0001P0\u0001"</c>...</summary>
/// <summary>The variable and flag names global.lua gives: <c>STARTER = var[0x4000]</c>, <c>MET_RIVAL = flag[372]</c>.</summary>
public sealed record LuaNames(IReadOnlyDictionary<long, string> Variables, IReadOnlyDictionary<long, string> Flags)
{
    public static readonly LuaNames None = new(new Dictionary<long, string>(), new Dictionary<long, string>());
}

public sealed record LuaTemplate(string Name, IReadOnlyList<bool> TextParameters, ScriptTextDocument Instance);

/// <summary>Compiles a Lua script file into the plaintext script format (<see cref="ScriptTextDocument"/>).
///
/// Lua runs at compile time, so locals, arithmetic, numeric for loops (unrolled) and <c>local function</c>
/// helpers (inlined at each call) are all worked out while compiling. Only what touches game state
/// becomes game code: a database command call (<c>LockAll()</c>, <c>lock_all()</c>), a condition on a
/// variable or flag (<c>if var[0x4000] == 1 then</c>, <c>while not flag[12] do</c>), an assignment to one
/// (<c>var[0x4000] = 2</c>), and a call to a global <c>function name()</c>, which becomes a script
/// Function: <c>name()</c> is a Call and <c>return name()</c> a Jump. An <c>if</c> whose condition is known
/// at compile time just picks its branch. Anything the database or this language cannot express is
/// written as raw script lines with <c>asm [[ ... ]]</c>.</summary>
public sealed partial class LuaScriptCompiler
{
    private readonly ScriptLanguage language;
    private readonly List<string> warnings = [];
    private readonly Dictionary<string, object?> globals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> builtins = new(StringComparer.Ordinal);
    private readonly SortedDictionary<int, ScriptDefinition> scripts = [];
    private readonly Queue<GameFunction> pendingFunctions = new();
    private readonly List<IfWatch> watches = [];

    private Block? current;
    private Frame frame = new(FrameKind.TopLevel, false, null);

    private LuaScriptCompiler(ScriptLanguage language)
    {
        this.language = language;
        RegisterBuiltins();
    }

    /// <param name="global">The project's global.lua, run first: its global functions are helpers every
    /// script file can call, and its global values constants they can use.</param>
    public static LuaCompileResult Compile(string source, ScriptLanguage language, string? global = null)
    {
        var compiler = new LuaScriptCompiler(language);
        compiler.RunGlobal(global);
        IReadOnlyList<LuaStmt> chunk = LuaParser.Parse(source);
        var top = new Scope(null);
        compiler.ExecBlock(chunk, top);
        ScriptTextDocument document = compiler.Finish();
        return new LuaCompileResult(document, compiler.warnings);
    }

    private bool runningGlobal;

    private void RunGlobal(string? global)
    {
        if (string.IsNullOrWhiteSpace(global)) return;
        runningGlobal = true;
        try
        {
            ExecBlock(LuaParser.Parse(global, LuaException.GlobalLineBase + 1), new Scope(null));
        }
        finally
        {
            runningGlobal = false;
        }
    }

    private static bool TextsFillMessageSlots(ScriptTextDocument instance, ScriptLanguage language, int parameters)
    {
        foreach (ScriptTextLine line in instance.Scripts[0].Lines)
        {
            if (language.FindCommand(line.Command) is not { } info || info.ParameterSizes is [0xFF, ..]) continue;
            int parameter = 0;
            for (int i = 0; i < line.Args.Count; i++)
            {
                string arg = line.Args[i];
                bool slot = ScriptLanguage.IsMessageParameter(info, parameter);
                bool text = arg.StartsWith('"');
                // A text right after a message number is that message's text (Message(n, text)).
                if (text && i > 0 && ScriptLanguage.IsMessageParameter(info, parameter - 1) && !line.Args[i - 1].StartsWith('"')) continue;
                if (text && !slot) return false;
                if (!text && slot && long.TryParse(arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(arg[2..], 16).ToString() : arg, out long value)
                    && value >= TemplateNumber && value < TemplateNumber + parameters
                    && (i + 1 >= line.Args.Count || !line.Args[i + 1].StartsWith('"')))
                {
                    return false;
                }
                parameter++;
            }
        }
        return true;
    }

    /// <summary>The global names global.lua gives variables and flags; the first name wins where one is
    /// named twice.</summary>
    public static LuaNames FindNames(string? global, ScriptLanguage language)
    {
        if (string.IsNullOrWhiteSpace(global)) return LuaNames.None;
        var compiler = new LuaScriptCompiler(language);
        try
        {
            compiler.RunGlobal(global);
        }
        catch (LuaException)
        {
            return LuaNames.None;
        }
        var variables = new Dictionary<long, string>();
        var flags = new Dictionary<long, string>();
        foreach ((string name, object? value) in compiler.globals)
        {
            if (value is VarRef var) variables.TryAdd(var.Id, name);
            else if (value is FlagRef { Flag: double flag }) flags.TryAdd((long)flag, name);
        }
        return new LuaNames(variables, flags);
    }

    /// <summary>The names global.lua gives script files in a table called SCRIPT_NAMES:
    /// <c>SCRIPT_NAMES = { [12] = "Rival intro" }</c> (or <c>SCRIPT_NAMES[12] = "Rival intro"</c> after
    /// <c>SCRIPT_NAMES = {}</c>).</summary>
    public static Dictionary<int, string> FindScriptNames(string? global, ScriptLanguage language) =>
        FindNameTable(global, language, "SCRIPT_NAMES");

    /// <summary>The names level.lua gives level scripts in a table called LEVEL_SCRIPT_NAMES.</summary>
    public static Dictionary<int, string> FindLevelScriptNames(string? levelLua, ScriptLanguage language) =>
        FindNameTable(levelLua, language, "LEVEL_SCRIPT_NAMES");

    public static Dictionary<int, string> FindNameTable(string? source, ScriptLanguage language, string table)
    {
        var names = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(source)) return names;
        var compiler = new LuaScriptCompiler(language);
        try
        {
            compiler.RunGlobal(source);
        }
        catch (LuaException)
        {
            return names;
        }

        if (compiler.globals.TryGetValue(table, out object? value) && value is LuaTableValue entries)
        {
            foreach ((object key, object? name) in entries.Entries)
            {
                if (key is double id && id >= 0 && id == Math.Floor(id) && name is string text && text.Length > 0) names[(int)id] = text;
            }
        }
        return names;
    }

    public const int TemplateNumber = 0x700000;
    public static string TemplateText(int parameter) => $"\u0001P{parameter}\u0001";

    /// <summary>The script templates in global.lua: helpers that, called with arguments, return a script
    /// body compiling to a single Script without Functions. Each is tried with numbers for its parameters,
    /// then with texts for some of them, until one compiles.</summary>
    public static List<LuaTemplate> FindTemplates(string? global, ScriptLanguage language)
    {
        var templates = new List<LuaTemplate>();
        if (string.IsNullOrWhiteSpace(global)) return templates;
        var probe = new LuaScriptCompiler(language);
        probe.RunGlobal(global);

        foreach ((string name, object? value) in probe.globals)
        {
            if (value is not Closure { Function.Parameters.Count: <= 4 } helper) continue;
            int count = helper.Function.Parameters.Count;
            foreach (int mask in Enumerable.Range(0, 1 << count).OrderBy(m => System.Numerics.BitOperations.PopCount((uint)m)))
            {
                var compiler = new LuaScriptCompiler(language);
                compiler.RunGlobal(global);
                var args = Enumerable.Range(0, count)
                    .Select(i => (mask & (1 << i)) != 0 ? (object?)TemplateText(i) : (double)(TemplateNumber + i))
                    .ToList();
                try
                {
                    if (compiler.CallClosure((Closure)compiler.globals[name]!, args, 0) is not Closure { Function.Parameters.Count: 0 } body) break;
                    compiler.scripts[1] = new ScriptDefinition(1, 0, body, null);
                    ScriptTextDocument instance = compiler.Finish();
                    if (instance.Functions.Count != 0 || instance.Scripts is not [{ UseScript: null }]) break;
                    // The right mix puts texts exactly where messages go: a number that compiles in a message
                    // slot is a message parameter that should take the message's text.
                    if (!TextsFillMessageSlots(instance, language, count)) continue;
                    templates.Add(new LuaTemplate(name, Enumerable.Range(0, count).Select(i => (mask & (1 << i)) != 0).ToList(), instance));
                    break;
                }
                catch (LuaException)
                {
                }
            }
        }
        return templates;
    }

    #region Values

    private sealed class Scope(Scope? parent)
    {
        private readonly Dictionary<string, object?> values = new(StringComparer.Ordinal);
        public Scope? Parent { get; } = parent;

        public void Declare(string name, object? value) => values[name] = value;

        public bool TryFind(string name, out Scope owner)
        {
            for (Scope? scope = this; scope is not null; scope = scope.Parent)
            {
                if (scope.values.ContainsKey(name))
                {
                    owner = scope;
                    return true;
                }
            }
            owner = null!;
            return false;
        }

        public object? Get(string name) => values[name];
        public void Set(string name, object? value) => values[name] = value;
    }

    private sealed class LuaTableValue
    {
        public Dictionary<object, object?> Entries { get; } = [];
        public List<object> Order { get; } = [];

        public object? Get(object key) => Entries.TryGetValue(NormalizeKey(key), out object? value) ? value : null;

        public void Set(object key, object? value)
        {
            key = NormalizeKey(key);
            if (value is null)
            {
                if (Entries.Remove(key)) Order.Remove(key);
                return;
            }
            if (!Entries.ContainsKey(key)) Order.Add(key);
            Entries[key] = value;
        }

        public int Length
        {
            get
            {
                int n = 0;
                while (Entries.ContainsKey((double)(n + 1))) n++;
                return n;
            }
        }

        private static object NormalizeKey(object key) => key is int i ? (double)i : key;
    }

    private sealed record Closure(LuaFunction Function, Scope Env);

    private sealed record Builtin(string Name, Func<int, IReadOnlyList<object?>, object?> Invoke);

    /// <summary>var[...], flag[...], Overworld[...] and the like: indexable at compile time.</summary>
    private sealed record Indexer(string Name, Func<int, object?, object?> Get, Action<int, object?, object?>? Set);

    private sealed record CommandValue(ScriptCommandInfo Info);

    private sealed record MovementValue(string Name, long Count);

    /// <summary>A variable. FromCommand marks a command's result used as a value,
    /// <c>if CheckItem(ITEM_POTION, 1) then</c>, which as a condition means "not 0".</summary>
    private sealed record VarRef(long Id, bool FromCommand = false);

    /// <summary>A command's result used in a condition, <c>if not CheckItem(ITEM_POTION, 1) and YesNoBox() == 0</c>:
    /// the command is emitted with the test of its result rather than when the condition is read, so each
    /// result is tested straight after its command and <c>and</c>/<c>or</c> skip the commands they skip.</summary>
    private sealed record PendingResult(Line Command, VarRef Result);

    private sealed record FlagRef(object Flag);

    /// <summary>var[x] + n or var[x] - n, which can only be assigned back to var[x].</summary>
    private sealed record VarArithmetic(VarRef Var, bool Add, object Amount);

    /// <summary>A token passed through to the script as written: SEQ_SE_CONFIRM, Overworld.3, EQUAL...</summary>
    private sealed record SymbolValue(string Text);

    private sealed record ConditionValue(Condition Condition);

    private sealed class GameFunction(string name, int line)
    {
        public string Name { get; } = name;
        public int Line { get; } = line;
        public Closure? Body { get; init; }
        public int? UseScript { get; init; }
        public Block? Entry { get; set; }
    }

    private sealed record ScriptDefinition(int Number, int Line, Closure? Body, int? UseScript);

    private sealed record IteratorValue(List<(object? Key, object? Value)> Items);

    #endregion

    #region Control flow graph

    private abstract record Condition;
    private sealed record LeafCondition(IReadOnlyList<Line> Tests, int Op) : Condition;
    private sealed record NotCondition(Condition Inner) : Condition;
    private sealed record AndCondition(Condition Left, Condition Right) : Condition;
    private sealed record OrCondition(Condition Left, Condition Right) : Condition;

    private sealed record Line(string Command, IReadOnlyList<object> Args);

    private sealed record ActionArg(IReadOnlyList<(string Movement, string? Count)> Steps);

    private abstract record Terminator;
    private sealed record EndTerminator : Terminator;
    private sealed record ReturnTerminator : Terminator;
    private sealed record GotoTerminator(Block Target) : Terminator;

    private sealed class Block
    {
        public List<Line> Lines { get; } = [];
        public Terminator? Terminator { get; set; }
    }

    private enum FrameKind { TopLevel, Script, Function, Macro }
    private enum Flow { Normal, Break, Return }

    private sealed class Frame(FrameKind kind, bool inGame, Frame? caller)
    {
        public FrameKind Kind { get; } = kind;
        public bool InGame { get; } = inGame;
        public Frame? Caller { get; } = caller;
        public int BranchDepth { get; set; }
        public List<LoopInfo> Loops { get; } = [];
        public Block? MacroExit { get; set; }
        public object? ReturnValue { get; set; }
        public bool WarnedUnreachable { get; set; }
    }

    private sealed record LoopInfo(Block? GameExit, int BranchDepth);

    /// <summary>Watches the then-body of an if while it compiles, to learn whether it leaves through a
    /// Return, Jump, break or helper return: such a body cannot be a CallIf'd Function, whose Return
    /// would come back to the wrong place.</summary>
    private sealed class IfWatch(Frame frame, int loopCount)
    {
        public Frame Frame { get; } = frame;
        public int LoopCount { get; } = loopCount;
        public bool Escaped { get; set; }
    }

    #endregion

    #region Builtins

    private void RegisterBuiltins()
    {
        builtins["script"] = new Builtin("script", DefineScript);
        builtins["use_script"] = new Builtin("use_script", (line, args) =>
            new GameFunction($"use_script({ToInteger(Arg(args, 0, line), line)})", line) { UseScript = (int)ToInteger(Arg(args, 0, line), line) });
        builtins["var"] = new Indexer("var", (line, key) => ToVar(key, line), AssignVar);
        builtins["flag"] = new Indexer("flag", (line, key) => new FlagRef(ToFlagArgument(key, line)), AssignFlag);
        builtins["condition"] = new Builtin("condition", (line, args) =>
            new ConditionValue(new LeafCondition([], ToOperator(Arg(args, 0, line), line))));
        builtins["sym"] = new Builtin("sym", (line, args) => new SymbolValue(ToStringArg(Arg(args, 0, line), line)));
        builtins["step"] = new Builtin("step", (line, args) =>
        {
            long id = ToInteger(Arg(args, 0, line), line);
            string name = language.Database.Movements.TryGetValue((ushort)id, out ScriptSubEntryInfo? info) && info.Name.Length > 0 ? info.Name : $"{id:X4}";
            return new MovementValue(name, args.Count > 1 ? ToInteger(args[1], line) : 1);
        });
        builtins["cmd"] = new Builtin("cmd", RawCommand);
        builtins["has_command"] = new Builtin("has_command", (line, args) => language.FindCommand(ToStringArg(Arg(args, 0, line), line)) is not null);
        builtins["asm"] = new Builtin("asm", (line, args) =>
        {
            EmitAsm(ToStringArg(Arg(args, 0, line), line), line);
            return null;
        });

        var overworld = new Indexer("Overworld", (line, key) => new SymbolValue($"Overworld.{ToInteger(key, line)}"), null);
        builtins["Overworld"] = overworld;
        builtins["ow"] = overworld;
        builtins["Move"] = new Indexer("Move", (line, key) => new SymbolValue($"Move.{ToInteger(key, line)}"), null);

        for (int op = 0; op < language.ComparisonOperators.Count; op++)
        {
            string name = language.ComparisonOperators[op];
            builtins[name.Replace('/', '_')] = new SymbolValue(name);
        }
        foreach (string name in language.Database.SpecialOverworlds.Values.Concat(language.Database.OverworldDirections.Values))
        {
            if (ScriptLanguage.IsIdentifier(name)) builtins.TryAdd(name, new SymbolValue(name));
        }

        builtins["print"] = new Builtin("print", (line, args) =>
        {
            warnings.Add($"Line {line}: {string.Join('\t', args.Select(ToDisplayString))}");
            return null;
        });
        builtins["error"] = new Builtin("error", (line, args) => throw new LuaException(line, ToDisplayString(args.Count > 0 ? args[0] : null)));
        builtins["tostring"] = new Builtin("tostring", (line, args) => ToDisplayString(Arg(args, 0, line)));
        builtins["tonumber"] = new Builtin("tonumber", (line, args) => Arg(args, 0, line) switch
        {
            double d => d,
            string s when TryParseNumber(s, out double d) => d,
            _ => null,
        });
        builtins["type"] = new Builtin("type", (line, args) => Arg(args, 0, line) switch
        {
            null => "nil",
            bool => "boolean",
            double => "number",
            string => "string",
            LuaTableValue => "table",
            Closure or Builtin => "function",
            _ => "userdata",
        });
        builtins["ipairs"] = new Builtin("ipairs", (line, args) =>
        {
            LuaTableValue table = Arg(args, 0, line) as LuaTableValue ?? throw new LuaException(line, "ipairs expects a table.");
            return new IteratorValue(Enumerable.Range(1, table.Length).Select(i => ((object?)(double)i, table.Get((double)i))).ToList());
        });
        builtins["pairs"] = new Builtin("pairs", (line, args) =>
        {
            LuaTableValue table = Arg(args, 0, line) as LuaTableValue ?? throw new LuaException(line, "pairs expects a table.");
            return new IteratorValue(table.Order.Select(key => ((object?)key, table.Entries[key])).ToList());
        });

        var math = new LuaTableValue();
        math.Set("floor", new Builtin("math.floor", (line, args) => Math.Floor(ToNumber(Arg(args, 0, line), line))));
        math.Set("ceil", new Builtin("math.ceil", (line, args) => Math.Ceiling(ToNumber(Arg(args, 0, line), line))));
        math.Set("abs", new Builtin("math.abs", (line, args) => Math.Abs(ToNumber(Arg(args, 0, line), line))));
        math.Set("max", new Builtin("math.max", (line, args) => args.Select(arg => ToNumber(arg, line)).Max()));
        math.Set("min", new Builtin("math.min", (line, args) => args.Select(arg => ToNumber(arg, line)).Min()));
        builtins["math"] = math;
    }

    private static object? Arg(IReadOnlyList<object?> args, int index, int line) =>
        index < args.Count ? args[index] : throw new LuaException(line, $"Missing argument {index + 1}.");

    private object? DefineScript(int line, IReadOnlyList<object?> args)
    {
        if (runningGlobal) throw new LuaException(line, "global.lua holds helpers for every script file and cannot define scripts itself.");
        if (frame.Kind != FrameKind.TopLevel)
        {
            throw new LuaException(line, "script() can only be used at the top level of the file.");
        }
        int number = (int)ToInteger(Arg(args, 0, line), line);
        if (number < 1) throw new LuaException(line, "Script numbers start at 1.");
        if (scripts.ContainsKey(number)) throw new LuaException(line, $"Script {number} is defined twice.");
        // script(n, "name", body): the name is only a label for the editors and changes nothing in the script.
        int bodyIndex = Arg(args, 1, line) is string ? 2 : 1;
        ScriptDefinition definition = Arg(args, bodyIndex, line) switch
        {
            Closure closure when closure.Function.Parameters.Count == 0 => new ScriptDefinition(number, line, closure, null),
            double used => new ScriptDefinition(number, line, null, (int)ToInteger(used, line)),
            _ => throw new LuaException(line, "script(n, body) expects a function with no parameters, or the number of a script to share code with."),
        };
        scripts[number] = definition;
        return null;
    }

    private object? RawCommand(int line, IReadOnlyList<object?> args)
    {
        string name = Arg(args, 0, line) switch
        {
            string text => text,
            double id when language.Database.Commands.TryGetValue((ushort)id, out ScriptCommandInfo? info) && info.Name.Length > 0 => info.Name,
            double id => $"0x{(long)id:X4}",
            CommandValue command => command.Info.Name,
            _ => throw new LuaException(line, "cmd() expects a command name or ID first."),
        };
        ScriptCommandInfo? known = language.FindCommand(name);
        if (known is not null) return EmitCommand(known, args.Skip(1).ToList(), line);
        EmitLine(new Line(name, args.Skip(1).Select(arg => ToToken(arg, null, line)).ToList()), line);
        return null;
    }

    #endregion

    #region Statements

    private Flow ExecBlock(IReadOnlyList<LuaStmt> statements, Scope scope)
    {
        foreach (LuaStmt statement in statements)
        {
            Flow flow = Exec(statement, scope);
            if (flow != Flow.Normal) return flow;
        }
        return Flow.Normal;
    }

    private Flow Exec(LuaStmt statement, Scope scope)
    {
        switch (statement)
        {
            case LuaLocal local:
            {
                List<object?> values = local.Values.Select(value => Eval(value, scope)).ToList();
                if (values.OfType<VarArithmetic>().Any()) throw new LuaException(local.Line, "var[x] + n can only be assigned back to var[x] (it compiles to IncrementVar/DecrementVar).");
                for (int i = 0; i < local.Names.Count; i++) scope.Declare(local.Names[i], i < values.Count ? values[i] : null);
                return Flow.Normal;
            }
            case LuaAssign { Targets: [LuaExpr target], Values: [LuaCall call] } assign
                when ResultTarget(target, scope) is VarRef output && Eval(call.Function, scope) is CommandValue command
                     && language.HasResultParameter(command.Info):
            {
                // X = CheckItem(...) has the command store its result in X itself.
                List<object?> args = call.Args.Select(arg => Eval(arg, scope)).ToList();
                if (LeavesOutResult(command.Info, args)) EmitCommand(command.Info, [.. args, output], assign.Line);
                else AssignTo(target, InvokeCommand(command.Info, args, call.Line), scope);
                return Flow.Normal;
            }
            case LuaAssign assign:
            {
                List<object?> values = assign.Values.Select(value => Eval(value, scope)).ToList();
                for (int i = 0; i < assign.Targets.Count; i++) AssignTo(assign.Targets[i], i < values.Count ? values[i] : null, scope);
                return Flow.Normal;
            }
            case LuaCallStmt call:
            {
                // A template helper (npc_message("Hi")) hands back the script body it stands for; used as a statement inside a body, that
                // body is what the author means to run here, so it is run rather than thrown away.
                bool userHelper = call.Call.Function is LuaName helper && Lookup(helper.Name, scope, helper.Line) is Closure;
                object? result = Eval(call.Call, scope);
                if (userHelper && result is Closure { Function.Parameters.Count: 0 } body) CallClosure(body, [], call.Line);
                return Flow.Normal;
            }
            case LuaFunctionDecl declaration:
                DeclareFunction(declaration, scope);
                return Flow.Normal;
            case LuaDo block:
                return ExecBlock(block.Body, new Scope(scope));
            case LuaIf conditional:
                return CompileIf(conditional, 0, scope);
            case LuaWhile loop:
                return CompileWhile(loop, scope);
            case LuaRepeat loop:
                return CompileRepeat(loop, scope);
            case LuaNumericFor loop:
                return CompileNumericFor(loop, scope);
            case LuaGenericFor loop:
                return CompileGenericFor(loop, scope);
            case LuaReturn ret:
                return CompileReturn(ret, scope);
            case LuaBreak brk:
                return CompileBreak(brk);
            default:
                throw new LuaException(statement.Line, "Unsupported statement.");
        }
    }

    private void DeclareFunction(LuaFunctionDecl declaration, Scope scope)
    {
        var closure = new Closure(declaration.Function, scope);
        if (declaration.IsLocal)
        {
            scope.Declare(declaration.Path[0], closure);
            return;
        }

        // Script Functions belong to one script file, so global.lua's functions are all helpers.
        if (runningGlobal && declaration.Path.Count == 1)
        {
            globals[declaration.Path[0]] = closure;
            return;
        }

        if (declaration.Path.Count == 1)
        {
            if (frame.Kind != FrameKind.TopLevel || frame.Caller is not null || scope.Parent is not null)
            {
                throw new LuaException(declaration.Line, $"Script functions (global \"function {declaration.Path[0]}\") must be declared at the top level; use \"local function\" for a helper.");
            }
            if (declaration.Function.Parameters.Count > 0)
            {
                throw new LuaException(declaration.Line, $"Script function {declaration.Path[0]} cannot take parameters; a \"local function\" helper can, and is inlined where it is called.");
            }
            globals[declaration.Path[0]] = new GameFunction(declaration.Path[0], declaration.Line) { Body = closure };
            return;
        }

        object? target = Lookup(declaration.Path[0], scope, declaration.Line);
        for (int i = 1; i < declaration.Path.Count - 1; i++) target = IndexValue(target, declaration.Path[i], declaration.Line);
        if (target is not LuaTableValue table) throw new LuaException(declaration.Line, $"{string.Join('.', declaration.Path.SkipLast(1))} is not a table.");
        table.Set(declaration.Path[^1], closure);
    }

    private void AssignTo(LuaExpr target, object? value, Scope scope)
    {
        switch (target)
        {
            case LuaName name:
                // A name holding var[...] or flag[...] is that variable's or flag's name, so assigning to it
                // writes the variable or flag (global.lua names them for every script file this way).
                object? existing = scope.TryFind(name.Name, out Scope owner) ? owner.Get(name.Name) : globals.GetValueOrDefault(name.Name);
                if (existing is VarRef aliased && !runningGlobal)
                {
                    AssignVar(name.Line, aliased, value);
                    break;
                }
                if (existing is FlagRef namedFlag && !runningGlobal)
                {
                    AssignFlag(name.Line, namedFlag.Flag, value);
                    break;
                }
                if (value is VarArithmetic) throw new LuaException(name.Line, "var[x] + n can only be assigned back to var[x] (it compiles to IncrementVar/DecrementVar).");
                if (owner is not null) owner.Set(name.Name, value);
                else if (builtins.ContainsKey(name.Name) || language.ResolveCommand(name.Name) is not null)
                {
                    throw new LuaException(name.Line, $"{name.Name} is a built-in name; declare a local to use it as a variable.");
                }
                else globals[name.Name] = value;
                break;
            case LuaIndex index:
                object? container = Eval(index.Target, scope);
                object? key = Eval(index.Key, scope);
                switch (container)
                {
                    case LuaTableValue table:
                        table.Set(key ?? throw new LuaException(index.Line, "Table index is nil."), value);
                        break;
                    case Indexer { Set: not null } indexer:
                        indexer.Set(index.Line, key, value);
                        break;
                    default:
                        throw new LuaException(index.Line, "Cannot assign into this value.");
                }
                break;
        }
    }

    /// <summary>The variable an assignment writes, when its target is var[x] or a variable's name.</summary>
    private VarRef? ResultTarget(LuaExpr target, Scope scope)
    {
        if (!frame.InGame || runningGlobal) return null;
        switch (target)
        {
            case LuaName name:
                object? existing = scope.TryFind(name.Name, out Scope owner) ? owner.Get(name.Name) : globals.GetValueOrDefault(name.Name);
                return existing as VarRef;
            case LuaIndex { Target: LuaName { Name: "var" } } index when Lookup("var", scope, index.Line) is Indexer { Name: "var" }:
                return ToVar(Eval(index.Key, scope), index.Line);
            default:
                return null;
        }
    }

    private void AssignVar(int line, object? key, object? value)
    {
        VarRef target = ToVar(key, line);
        switch (value)
        {
            case VarArithmetic arithmetic when arithmetic.Var.Id == target.Id:
            {
                string command = (arithmetic.Add ? language.AddVar : language.SubVar)
                                 ?? throw new LuaException(line, "The script command database has no IncrementVar/DecrementVar command.");
                EmitCommand(language.FindCommand(command)!, [target, arithmetic.Amount], line);
                break;
            }
            case VarArithmetic:
                throw new LuaException(line, "var[x] + n can only be assigned back to var[x] (it compiles to IncrementVar/DecrementVar).");
            case VarRef source:
                EmitCommand(language.FindCommand(language.CopyVar ?? throw new LuaException(line, "The script command database has no SetVarFromVariable command."))!, [target, source], line);
                break;
            default:
                EmitCommand(language.FindCommand(language.SetVar ?? throw new LuaException(line, "The script command database has no SetVar command."))!, [target, value], line);
                break;
        }
    }

    private void AssignFlag(int line, object? key, object? value)
    {
        object flag = ToFlagArgument(key, line);
        string command = value switch
        {
            true => language.SetFlag,
            false => language.ClearFlag,
            _ => throw new LuaException(line, "flag[x] can only be set to true or false."),
        };
        EmitCommand(language.FindCommand(command)!, [flag], line);
    }

    private Flow CompileIf(LuaIf statement, int branchIndex, Scope scope)
    {
        if (branchIndex == statement.Branches.Count)
        {
            return statement.Else is null ? Flow.Normal : ExecBlock(statement.Else, new Scope(scope));
        }

        (LuaExpr conditionExpr, IReadOnlyList<LuaStmt> body) = statement.Branches[branchIndex];
        object condition = EvalCondition(conditionExpr, scope);
        if (condition is bool constant)
        {
            return constant ? ExecBlock(body, new Scope(scope)) : CompileIf(statement, branchIndex + 1, scope);
        }

        Condition test = (Condition)condition;
        Block start = RequireCurrent(conditionExpr.Line);
        bool hasRest = branchIndex + 1 < statement.Branches.Count || statement.Else is not null;

        frame.BranchDepth++;
        var watch = new IfWatch(frame, frame.Loops.Count);
        watches.Add(watch);
        var thenBlock = new Block();
        current = thenBlock;
        ExecBlock(body, new Scope(scope));
        Block? thenEnd = current;
        watches.Remove(watch);

        current = start;
        if (!hasRest && thenEnd is not null && !watch.Escaped && test is LeafCondition leaf)
        {
            // "if c then f() end" calls f directly rather than a Function that only calls f.
            Block target = thenBlock;
            if (thenEnd == thenBlock && thenBlock.Lines is [{ Args: [Block called] } call] && call.Command == language.Call) target = called;
            else thenEnd.Terminator = new ReturnTerminator();
            foreach (Line line in leaf.Tests) start.Lines.Add(line);
            start.Lines.Add(new Line(language.CallIf, [OperatorToken(leaf.Op), target]));
        }
        else if (thenEnd is null)
        {
            JumpIfTrue(test, thenBlock, conditionExpr.Line);
            if (hasRest) CompileIf(statement, branchIndex + 1, scope);
        }
        else
        {
            var join = new Block();
            thenEnd.Terminator = new GotoTerminator(join);
            JumpIfTrue(test, thenBlock, conditionExpr.Line);
            if (hasRest) CompileIf(statement, branchIndex + 1, scope);
            if (current is not null) current.Terminator = new GotoTerminator(join);
            current = join;
        }
        frame.BranchDepth--;
        return Flow.Normal;
    }

    private Flow CompileWhile(LuaWhile loop, Scope scope)
    {
        // The condition is evaluated inside the loop's first block, so a command in it,
        // while YesNoBox() == 1 do, runs on every pass rather than once before the loop.
        Block start = RequireCurrent(loop.Line);
        var header = new Block();
        var exit = new Block();
        start.Terminator = new GotoTerminator(header);
        current = header;
        object condition = EvalCondition(loop.Condition, scope);
        if (condition is false)
        {
            RequireCurrent(loop.Line).Terminator = new GotoTerminator(exit);
            current = exit;
            return Flow.Normal;
        }
        if (condition is Condition test) JumpIfFalse(test, exit, loop.Condition.Line);

        frame.BranchDepth++;
        frame.Loops.Add(new LoopInfo(exit, frame.BranchDepth));
        ExecBlock(loop.Body, new Scope(scope));
        frame.Loops.RemoveAt(frame.Loops.Count - 1);
        frame.BranchDepth--;

        if (current is not null) current.Terminator = new GotoTerminator(header);
        current = exit;
        return Flow.Normal;
    }

    private Flow CompileRepeat(LuaRepeat loop, Scope scope)
    {
        Block start = RequireCurrent(loop.Line);
        var body = new Block();
        var exit = new Block();
        start.Terminator = new GotoTerminator(body);
        current = body;

        frame.BranchDepth++;
        frame.Loops.Add(new LoopInfo(exit, frame.BranchDepth));
        var bodyScope = new Scope(scope);
        ExecBlock(loop.Body, bodyScope);
        if (current is not null)
        {
            object condition = EvalCondition(loop.Condition, bodyScope);
            if (condition is Condition test) JumpIfFalse(test, body, loop.Condition.Line);
            current!.Terminator = new GotoTerminator(condition is false ? body : exit);
        }
        frame.Loops.RemoveAt(frame.Loops.Count - 1);
        frame.BranchDepth--;

        current = exit;
        return Flow.Normal;
    }

    private const int MaxUnrolledIterations = 10000;

    private Flow CompileNumericFor(LuaNumericFor loop, Scope scope)
    {
        double start = ToNumber(Eval(loop.Start, scope), loop.Line);
        double stop = ToNumber(Eval(loop.Stop, scope), loop.Line);
        double step = loop.Step is null ? 1 : ToNumber(Eval(loop.Step, scope), loop.Line);
        if (step == 0) throw new LuaException(loop.Line, "for loop step is zero.");

        frame.Loops.Add(new LoopInfo(null, frame.BranchDepth));
        try
        {
            int iterations = 0;
            for (double i = start; step > 0 ? i <= stop : i >= stop; i += step)
            {
                if (++iterations > MaxUnrolledIterations) throw new LuaException(loop.Line, "for loop runs too many times to unroll.");
                var body = new Scope(scope);
                body.Declare(loop.Variable, i);
                Flow flow = ExecBlock(loop.Body, body);
                if (flow == Flow.Break) break;
                if (flow == Flow.Return) return flow;
            }
        }
        finally
        {
            frame.Loops.RemoveAt(frame.Loops.Count - 1);
        }
        return Flow.Normal;
    }

    private Flow CompileGenericFor(LuaGenericFor loop, Scope scope)
    {
        if (loop.Values.Count != 1 || Eval(loop.Values[0], scope) is not IteratorValue iterator)
        {
            throw new LuaException(loop.Line, "Generic for loops must use ipairs(t) or pairs(t).");
        }

        frame.Loops.Add(new LoopInfo(null, frame.BranchDepth));
        try
        {
            foreach ((object? key, object? value) in iterator.Items)
            {
                var body = new Scope(scope);
                body.Declare(loop.Names[0], key);
                if (loop.Names.Count > 1) body.Declare(loop.Names[1], value);
                Flow flow = ExecBlock(loop.Body, body);
                if (flow == Flow.Break) break;
                if (flow == Flow.Return) return flow;
            }
        }
        finally
        {
            frame.Loops.RemoveAt(frame.Loops.Count - 1);
        }
        return Flow.Normal;
    }

    private Flow CompileReturn(LuaReturn statement, Scope scope)
    {
        if (statement.Values.Count == 1 && statement.Values[0] is LuaCall call && frame.InGame
            && Eval(call.Function, scope) is GameFunction tailTarget)
        {
            if (call.Args.Count > 0) throw new LuaException(call.Line, $"Script function {tailTarget.Name} takes no arguments.");
            MarkEscape(_ => true);
            Terminate(new GotoTerminator(EntryOf(tailTarget)), statement.Line);
            return Flow.Normal;
        }

        switch (frame.Kind)
        {
            case FrameKind.Macro when frame.BranchDepth > 0:
                if (statement.Values.Count > 0) throw new LuaException(statement.Line, "A helper cannot return a value from inside a game-time if or loop.");
                frame.MacroExit ??= new Block();
                MarkEscape(watch => watch.Frame == frame);
                Terminate(new GotoTerminator(frame.MacroExit), statement.Line);
                return Flow.Normal;
            case FrameKind.Macro:
            case FrameKind.TopLevel:
                frame.ReturnValue = statement.Values.Count > 0 ? Eval(statement.Values[0], scope) : null;
                return Flow.Return;
            case FrameKind.Script:
                foreach (LuaExpr value in statement.Values) Eval(value, scope);
                Terminate(new EndTerminator(), statement.Line);
                return Flow.Normal;
            default:
                foreach (LuaExpr value in statement.Values) Eval(value, scope);
                MarkEscape(_ => true);
                Terminate(new ReturnTerminator(), statement.Line);
                return Flow.Normal;
        }
    }

    private Flow CompileBreak(LuaBreak statement)
    {
        if (frame.Loops.Count == 0) throw new LuaException(statement.Line, "break outside a loop.");
        LoopInfo loop = frame.Loops[^1];
        if (loop.GameExit is not null)
        {
            int loopIndex = frame.Loops.Count - 1;
            MarkEscape(watch => watch.Frame == frame && watch.LoopCount <= loopIndex);
            Terminate(new GotoTerminator(loop.GameExit), statement.Line);
            return Flow.Normal;
        }
        if (frame.BranchDepth > loop.BranchDepth)
        {
            throw new LuaException(statement.Line, "Cannot break out of an unrolled for loop from inside a game-time if or loop.");
        }
        return Flow.Break;
    }

    private void MarkEscape(Func<IfWatch, bool> applies)
    {
        foreach (IfWatch watch in watches)
        {
            if (applies(watch)) watch.Escaped = true;
        }
    }

    #endregion

    #region Expressions

    private object? Lookup(string name, Scope scope, int line)
    {
        if (scope.TryFind(name, out Scope owner)) return owner.Get(name);
        if (globals.TryGetValue(name, out object? global)) return global;
        if (builtins.TryGetValue(name, out object? builtin)) return builtin;
        if (language.ResolveCommand(name) is ScriptCommandInfo command) return new CommandValue(command);
        if (language.ResolveMovement(name) is ScriptSubEntryInfo movement) return new MovementValue(movement.Name, 1);
        if (PassthroughConstant().IsMatch(name)) return new SymbolValue(name);
        string? suggestion = language.SuggestName(name);
        throw new LuaException(line, $"Unknown name \"{name}\": not a local, script function, database command or movement." + (suggestion is null ? "" : $" Did you mean {suggestion}?"));
    }

    private object? Eval(LuaExpr expression, Scope scope)
    {
        switch (expression)
        {
            case LuaNil: return null;
            case LuaBool b: return b.Value;
            case LuaNumber n: return n.Value;
            case LuaString s: return s.Value;
            case LuaName name: return Lookup(name.Name, scope, name.Line);
            case LuaIndex index: return IndexValue(Eval(index.Target, scope), Eval(index.Key, scope), index.Line);
            case LuaCall call: return EvalCall(call, scope);
            case LuaFunction function: return new Closure(function, scope);
            case LuaTable table: return BuildTable(table, scope);
            case LuaUnary unary: return EvalUnary(unary, scope);
            case LuaBinary binary: return EvalBinary(binary, scope);
            default: throw new LuaException(expression.Line, "Unsupported expression.");
        }
    }

    private LuaTableValue BuildTable(LuaTable table, Scope scope)
    {
        var result = new LuaTableValue();
        int position = 1;
        foreach (LuaTableField field in table.Fields)
        {
            object? value = Eval(field.Value, scope);
            if (field.Key is null) result.Set((double)position++, value);
            else result.Set(Eval(field.Key, scope) ?? throw new LuaException(table.Line, "Table key is nil."), value);
        }
        return result;
    }

    private object? IndexValue(object? target, object? key, int line) => target switch
    {
        LuaTableValue table => table.Get(key ?? throw new LuaException(line, "Table index is nil.")),
        Indexer indexer => indexer.Get(line, key),
        _ => throw new LuaException(line, $"Cannot index a {DescribeValue(target)}."),
    };

    private object? EvalCall(LuaCall call, Scope scope)
    {
        object? function = Eval(call.Function, scope);
        List<object?> args = call.Args.Select(arg => Eval(arg, scope)).ToList();
        switch (function)
        {
            case Closure closure:
                return CallClosure(closure, args, call.Line);
            case Builtin builtin:
                asmScopes.Push(scope);
                try
                {
                    return builtin.Invoke(call.Line, args);
                }
                finally
                {
                    asmScopes.Pop();
                }
            case Indexer indexer when args.Count == 1:
                return indexer.Get(call.Line, args[0]);
            case CommandValue command:
                return InvokeCommand(command.Info, args, call.Line);
            case MovementValue movement:
                if (args.Count != 1) throw new LuaException(call.Line, $"Movement {movement.Name} takes one repeat count, e.g. {movement.Name}(3).");
                return movement with { Count = ToInteger(args[0], call.Line) };
            case SymbolValue symbol when call.Function is LuaName:
            {
                string? suggestion = language.SuggestName(symbol.Text);
                throw new LuaException(call.Line, $"{symbol.Text} is not a command in this ROM's script command database." + (suggestion is null ? "" : $" Did you mean {suggestion}?"));
            }
            case GameFunction gameFunction:
                if (args.Count > 0) throw new LuaException(call.Line, $"Script function {gameFunction.Name} takes no arguments.");
                EmitLine(new Line(language.Call, [EntryOf(gameFunction)]), call.Line);
                return null;
            default:
                throw new LuaException(call.Line, $"Attempt to call a {DescribeValue(function)}.");
        }
    }

    /// <summary>A command call. One that stores a result, called without the variable for it, stores it
    /// in VAR_RESULT and is that variable as a value: <c>if YesNoBox() == 0 then</c>.</summary>
    private object? InvokeCommand(ScriptCommandInfo info, IReadOnlyList<object?> args, int line)
    {
        if (!LeavesOutResult(info, args)) return EmitCommand(info, args, line);
        if (readingCondition && !args.Any(arg => arg is LuaTableValue))
        {
            Line? command = null;
            EmitCommand(info, [.. args, new VarRef(ScriptLanguage.ResultVariable)], line, built => command = built);
            return new PendingResult(command!, new VarRef(ScriptLanguage.ResultVariable, FromCommand: true));
        }
        EmitCommand(info, [.. args, new VarRef(ScriptLanguage.ResultVariable)], line);
        return new VarRef(ScriptLanguage.ResultVariable, FromCommand: true);
    }

    /// <summary>Whether a call to a result-storing command gives every argument but the result's
    /// variable; message texts after message numbers do not count as arguments.</summary>
    private bool LeavesOutResult(ScriptCommandInfo info, IReadOnlyList<object?> args)
    {
        if (!language.HasResultParameter(info)) return false;
        int count = 0;
        for (int i = 0; i < args.Count; i++)
        {
            bool text = args[i] is string or null && count > 0 && ScriptLanguage.IsMessageParameter(info, count - 1) && (i == 0 || args[i - 1] is not (string or null));
            if (!text) count++;
        }
        return count == info.ParameterSizes.Count - 1;
    }

    /// <summary>Set while an if/while/until condition is read: commands used as values in it become
    /// <see cref="PendingResult"/>s.</summary>
    private bool readingCondition;

    private object? CallClosure(Closure closure, IReadOnlyList<object?> args, int line)
    {
        // A helper's own statements run as usual even when it is called from a condition.
        bool wasReadingCondition = readingCondition;
        readingCondition = false;
        try
        {
            return CallClosureBody(closure, args, line);
        }
        finally
        {
            readingCondition = wasReadingCondition;
        }
    }

    private object? CallClosureBody(Closure closure, IReadOnlyList<object?> args, int line)
    {
        var scope = new Scope(closure.Env);
        for (int i = 0; i < closure.Function.Parameters.Count; i++) scope.Declare(closure.Function.Parameters[i], i < args.Count ? args[i] : null);

        Frame caller = frame;
        if (Depth(caller) > 200) throw new LuaException(line, "Helpers call each other too deeply (runaway recursion?).");
        frame = new Frame(FrameKind.Macro, caller.InGame, caller);
        try
        {
            ExecBlock(closure.Function.Body, scope);
            if (frame.MacroExit is not null)
            {
                if (current is not null) current.Terminator = new GotoTerminator(frame.MacroExit);
                current = frame.MacroExit;
            }
            return frame.ReturnValue;
        }
        finally
        {
            frame = caller;
        }
    }

    private static int Depth(Frame frame)
    {
        int depth = 0;
        for (Frame? f = frame; f is not null; f = f.Caller) depth++;
        return depth;
    }

    private object? EvalUnary(LuaUnary unary, Scope scope)
    {
        object? operand = Eval(unary.Operand, scope);
        switch (unary.Op)
        {
            case "not":
                return IsGameCondition(operand) ? new ConditionValue(Negated(ToCondition(operand, unary.Line))) : !IsTruthy(operand);
            case "-":
                return -ToNumber(operand, unary.Line);
            case "#":
                return operand switch
                {
                    string s => (double)s.Length,
                    LuaTableValue t => (double)t.Length,
                    _ => throw new LuaException(unary.Line, $"Cannot take the length of a {DescribeValue(operand)}."),
                };
            case "~":
                return (double)~ToInteger(operand, unary.Line);
            default:
                throw new LuaException(unary.Line, $"Unsupported operator {unary.Op}.");
        }
    }

    private object? EvalBinary(LuaBinary binary, Scope scope)
    {
        int line = binary.Line;
        if (binary.Op is "and" or "or")
        {
            object? left = Eval(binary.Left, scope);
            if (IsGameCondition(left))
            {
                object? rightValue = Eval(binary.Right, scope);
                Condition l = ToCondition(left, line);
                if (!IsGameCondition(rightValue))
                {
                    bool truthy = IsTruthy(rightValue);
                    if (binary.Op == "and") return truthy ? new ConditionValue(l) : false;
                    return truthy ? true : new ConditionValue(l);
                }
                Condition r = ToCondition(rightValue, line);
                return new ConditionValue(binary.Op == "and" ? new AndCondition(l, r) : new OrCondition(l, r));
            }
            if (binary.Op == "and") return IsTruthy(left) ? Eval(binary.Right, scope) : left;
            return IsTruthy(left) ? left : Eval(binary.Right, scope);
        }

        object? a = Eval(binary.Left, scope);
        object? b = Eval(binary.Right, scope);
        switch (binary.Op)
        {
            case "==": case "~=": case "<": case ">": case "<=": case ">=":
                return Compare(binary.Op, a, b, line);
            case "+" when a is VarRef v: return new VarArithmetic(v, true, RequireAmount(b, line));
            case "-" when a is VarRef v: return new VarArithmetic(v, false, RequireAmount(b, line));
            case "+": return ToNumber(a, line) + ToNumber(b, line);
            case "-": return ToNumber(a, line) - ToNumber(b, line);
            case "*": return ToNumber(a, line) * ToNumber(b, line);
            case "/": return ToNumber(a, line) / ToNumber(b, line);
            case "//": return Math.Floor(ToNumber(a, line) / ToNumber(b, line));
            case "%":
            {
                double x = ToNumber(a, line), y = ToNumber(b, line);
                return x - Math.Floor(x / y) * y;
            }
            case "^": return Math.Pow(ToNumber(a, line), ToNumber(b, line));
            case "..": return ToConcatString(a, line) + ToConcatString(b, line);
            case "&": return (double)(ToInteger(a, line) & ToInteger(b, line));
            case "|": return (double)(ToInteger(a, line) | ToInteger(b, line));
            case "~": return (double)(ToInteger(a, line) ^ ToInteger(b, line));
            case "<<": return (double)(ToInteger(a, line) << (int)ToInteger(b, line));
            case ">>": return (double)(ToInteger(a, line) >> (int)ToInteger(b, line));
            default: throw new LuaException(line, $"Unsupported operator {binary.Op}.");
        }
    }

    private static object RequireAmount(object? value, int line) =>
        value is double or VarRef ? value : throw new LuaException(line, "var[x] + n needs a number or a var[...] for n.");

    private static readonly Dictionary<string, int> OperatorOf = new()
    {
        ["=="] = ScriptLanguage.Equal, ["~="] = ScriptLanguage.Different,
        ["<"] = ScriptLanguage.Less, [">"] = ScriptLanguage.Greater,
        ["<="] = ScriptLanguage.LessEqual, [">="] = ScriptLanguage.GreaterEqual,
    };

    private object Compare(string op, object? a, object? b, int line)
    {
        if (a is FlagRef || b is FlagRef)
        {
            (FlagRef flag, object? other) = a is FlagRef fa ? (fa, b) : ((FlagRef)b!, a);
            if (op is not ("==" or "~=") || other is not bool expected) throw new LuaException(line, "A flag can only be compared with true or false; write flag[x] or not flag[x].");
            Condition test = FlagCondition(flag);
            return new ConditionValue(expected == (op == "==") ? test : Negated(test));
        }

        if (a is PendingResult && b is PendingResult)
        {
            throw new LuaException(line, "Two commands' results cannot be compared directly, since both go in the same variable; store one first: X = Command(...).");
        }
        var commands = new List<Line>();
        if (a is PendingResult pendingA) (commands, a) = ([pendingA.Command], pendingA.Result);
        if (b is PendingResult pendingB) (commands, b) = ([pendingB.Command], pendingB.Result);

        if (a is VarRef || b is VarRef)
        {
            int compareOp = OperatorOf[op];
            if (a is not VarRef)
            {
                (a, b) = (b, a);
                compareOp = ScriptLanguage.Mirror(compareOp);
            }
            var left = (VarRef)a!;
            Line test = b is VarRef right
                ? new Line(language.CompareVars, [VarToken(left), VarToken(right)])
                : new Line(language.CompareVarValue, [VarToken(left), ToToken(b, ScriptParameterType.Integer, line)]);
            return new ConditionValue(new LeafCondition([.. commands, test], compareOp));
        }

        if (IsGameCondition(a) || IsGameCondition(b)) throw new LuaException(line, "Conditions cannot be compared; combine them with and/or/not.");

        if (op is "==" or "~=")
        {
            bool equal = LuaEquals(a, b);
            return op == "==" ? equal : !equal;
        }
        int order = a switch
        {
            double x when b is double y => x.CompareTo(y),
            string x when b is string y => string.CompareOrdinal(x, y),
            _ => throw new LuaException(line, $"Cannot compare a {DescribeValue(a)} with a {DescribeValue(b)}."),
        };
        return op switch { "<" => order < 0, ">" => order > 0, "<=" => order <= 0, _ => order >= 0 };
    }

    private static bool LuaEquals(object? a, object? b) => a switch
    {
        null => b is null,
        double x => b is double y && x == y,
        string x => b is string y && x == y,
        bool x => b is bool y && x == y,
        SymbolValue x => b is SymbolValue y && x.Text == y.Text,
        VarRef x => b is VarRef y && x.Id == y.Id,
        MovementValue x => b is MovementValue y && x == y,
        _ => ReferenceEquals(a, b),
    };

    private Condition FlagCondition(FlagRef flag) =>
        new LeafCondition([new Line(language.CheckFlag, [ToToken(flag.Flag, ScriptParameterType.Flex, 0)])], ScriptLanguage.Equal);

    private static bool IsGameCondition(object? value) => value is ConditionValue or FlagRef or VarRef { FromCommand: true } or PendingResult;

    private Condition ToCondition(object? value, int line) => value switch
    {
        ConditionValue condition => condition.Condition,
        FlagRef flag => FlagCondition(flag),
        VarRef { FromCommand: true } result => new LeafCondition(
            [new Line(language.CompareVarValue, [VarToken(result), "0"])], ScriptLanguage.Different),
        PendingResult pending => new LeafCondition(
            [pending.Command, new Line(language.CompareVarValue, [VarToken(pending.Result), "0"])], ScriptLanguage.Different),
        _ => throw new LuaException(line, "Expected a condition."),
    };

    /// <summary>A compile-time bool, or a game-time <see cref="Condition"/>.</summary>
    private object EvalCondition(LuaExpr expression, Scope scope)
    {
        bool wasReadingCondition = readingCondition;
        readingCondition = true;
        object? value;
        try
        {
            value = Eval(expression, scope);
        }
        finally
        {
            readingCondition = wasReadingCondition;
        }
        if (IsGameCondition(value)) return ToCondition(value, expression.Line);
        if (value is VarRef) throw new LuaException(expression.Line, "A variable is not a condition by itself; compare it, e.g. var[0x4000] ~= 0.");
        return IsTruthy(value);
    }

    private static Condition Negated(Condition condition) => condition switch
    {
        LeafCondition leaf => leaf with { Op = ScriptLanguage.Negate(leaf.Op) },
        NotCondition not => not.Inner,
        _ => new NotCondition(condition),
    };

    private static bool IsTruthy(object? value) => value is not (null or false);

    #endregion

    #region Emission

    private Block RequireCurrent(int line)
    {
        if (!frame.InGame) throw new LuaException(line, "Game code (commands, var/flag conditions) can only appear inside a script or script function.");
        if (current is not null) return current;
        if (!frame.WarnedUnreachable)
        {
            warnings.Add($"Line {line}: unreachable code after End, return or a jump is dropped.");
            frame.WarnedUnreachable = true;
        }
        current = new Block();
        return current;
    }

    private void EmitLine(Line line, int sourceLine) => RequireCurrent(sourceLine).Lines.Add(line);

    private void Terminate(Terminator terminator, int line)
    {
        RequireCurrent(line).Terminator = terminator;
        current = null;
    }

    /// <param name="sink">Takes the command's line instead of it being emitted, for a command a condition
    /// tests, which is emitted with the test (see <see cref="PendingResult"/>).</param>
    private object? EmitCommand(ScriptCommandInfo info, IReadOnlyList<object?> args, int line, Action<Line>? sink = null)
    {
        if (language.IsEnd(info.Name) && args.Count == 0)
        {
            Terminate(new EndTerminator(), line);
            return null;
        }
        if (language.IsReturn(info.Name) && args.Count == 0)
        {
            MarkEscape(_ => true);
            Terminate(new ReturnTerminator(), line);
            return null;
        }
        if (language.IsJump(info.Name) && args.Count == 1)
        {
            MarkEscape(_ => true);
            Terminate(new GotoTerminator(ToFunctionTarget(args[0], line)), line);
            return null;
        }

        if (ExpandMessageLists(info, args, line)) return null;

        bool variableLayout = info.ParameterSizes.Count > 0 && info.ParameterSizes[0] == 0xFF;

        // A message slot's number can be followed by the message's text, Message(4, "Hi!") or
        // GenderMessage(4, "Hi, sir!", 5, "Hi, ma'am!"): the text goes in the script line after the number,
        // and saving sets it in the script's text archive (ScriptMessageText.Collapse).
        var texts = new Dictionary<int, string?>();
        if (!variableLayout && args.Count > info.ParameterSizes.Count)
        {
            var remaining = new List<object?>();
            for (int i = 0; i < args.Count; i++)
            {
                int parameter = remaining.Count - 1;
                if (args[i] is string or null && parameter >= 0 && ScriptLanguage.IsMessageParameter(info, parameter) && !texts.ContainsKey(parameter))
                {
                    // nil leaves the message's text as it is.
                    texts[parameter] = args[i] as string;
                    continue;
                }
                remaining.Add(args[i]);
            }
            args = remaining;
        }

        if (!variableLayout && args.Count != info.ParameterSizes.Count)
        {
            string names = info.ParameterNames.Count > 0 ? $" ({string.Join(", ", info.ParameterNames)})" : "";
            throw new LuaException(line, $"{info.Name} takes {info.ParameterSizes.Count} argument(s){names}, got {args.Count}.");
        }

        var tokens = new List<object>();
        for (int i = 0; i < args.Count; i++)
        {
            ScriptParameterType? type = !variableLayout && i < info.ParameterTypes.Count ? info.ParameterTypes[i] : null;
            if (args[i] is string && i > 0 && args[i - 1] is not string && !texts.ContainsKey(i - 1)
                && ScriptLanguage.IsMessageParameter(info, i) && ScriptLanguage.IsMessageParameter(info, i - 1))
            {
                // "GenderMessage 12 \"...\"" would read as message 12's text.
                throw new LuaException(line, $"{info.Name}: give its messages all as texts, or all as numbers (optionally each followed by its text).");
            }
            tokens.Add(type switch
            {
                ScriptParameterType.Function => ToFunctionTarget(args[i], line),
                ScriptParameterType.Action => ToAction(args[i], line),
                _ => ToToken(args[i], type, line),
            });
            if (texts.TryGetValue(i, out string? messageText) && messageText is not null) tokens.Add(ToToken(messageText, null, line));
        }
        if (sink is not null) sink(new Line(info.Name, tokens));
        else EmitLine(new Line(info.Name, tokens), line);
        return null;
    }

    /// <summary>A list of messages where one is expected, <c>Message({107, 108, 109})</c> or
    /// <c>npc_message({107, 108}, {"Hi!", "Bye!"})</c>, shows each in turn: the command once per message,
    /// with a WaitButton between them, texts paired up by position. Returns false when no argument is a list.</summary>
    private bool ExpandMessageLists(ScriptCommandInfo info, IReadOnlyList<object?> args, int line)
    {
        if (!args.Any(arg => arg is LuaTableValue)) return false;
        bool hasMessageSlot = Enumerable.Range(0, info.ParameterNames.Count).Any(i => ScriptLanguage.IsMessageParameter(info, i));
        if (!hasMessageSlot || info.ParameterTypes.Contains(ScriptParameterType.Action)) return false;

        int count = args.OfType<LuaTableValue>().First().Length;
        if (count == 0) throw new LuaException(line, $"{info.Name} was given an empty list of messages.");
        if (args.OfType<LuaTableValue>().Any(table => table.Length != count))
        {
            throw new LuaException(line, $"{info.Name}'s lists of messages and texts must be the same length.");
        }
        ScriptCommandInfo wait = language.FindCommand("WaitButton")
                                 ?? throw new LuaException(line, "The script command database has no WaitButton command to show messages one after another.");

        for (int i = 1; i <= count; i++)
        {
            if (i > 1) EmitCommand(wait, [], line);
            EmitCommand(info, args.Select(arg => arg is LuaTableValue table ? table.Get((double)i) : arg).ToList(), line);
        }
        return true;
    }

    private Block ToFunctionTarget(object? value, int line) => value switch
    {
        GameFunction function => EntryOf(function),
        Closure { Function.Parameters.Count: 0 } closure => EntryOf(new GameFunction(closure.Function.Name ?? $"function at line {closure.Function.Line}", closure.Function.Line) { Body = closure }),
        _ => throw new LuaException(line, $"Expected a script function, got a {DescribeValue(value)}."),
    };

    private ActionArg ToAction(object? value, int line)
    {
        if (value is MovementValue single) value = new LuaTableValue { Entries = { [1d] = single }, Order = { 1d } };
        if (value is not LuaTableValue table) throw new LuaException(line, $"Expected a list of movements such as {{ WalkNorth8(2), FaceSouth }}, got a {DescribeValue(value)}.");
        var steps = new List<(string, string?)>();
        for (int i = 1; i <= table.Length; i++)
        {
            if (table.Get((double)i) is not MovementValue movement) throw new LuaException(line, $"Item {i} of the movement list is not a movement.");
            steps.Add((movement.Name, $"0x{movement.Count:X}"));
        }
        if (table.Entries.Count != table.Length) throw new LuaException(line, "A movement list cannot have named fields.");
        return new ActionArg(steps);
    }

    private Block EntryOf(GameFunction function)
    {
        if (function.Entry is null)
        {
            function.Entry = new Block();
            referencedFunctions.Add(function);
            pendingFunctions.Enqueue(function);
        }
        return function.Entry;
    }

    private string OperatorToken(int op) => language.ComparisonOperators[op];

    private void JumpIfTrue(Condition condition, Block target, int line)
    {
        switch (condition)
        {
            case LeafCondition leaf:
                foreach (Line test in leaf.Tests) EmitLine(test, line);
                EmitLine(new Line(language.JumpIf, [OperatorToken(leaf.Op), target]), line);
                break;
            case NotCondition not:
                JumpIfFalse(not.Inner, target, line);
                break;
            case OrCondition or:
                JumpIfTrue(or.Left, target, line);
                JumpIfTrue(or.Right, target, line);
                break;
            case AndCondition and:
            {
                var skip = new Block();
                JumpIfFalse(and.Left, skip, line);
                JumpIfTrue(and.Right, target, line);
                RequireCurrent(line).Terminator = new GotoTerminator(skip);
                current = skip;
                break;
            }
        }
    }

    private void JumpIfFalse(Condition condition, Block target, int line)
    {
        switch (condition)
        {
            case LeafCondition leaf:
                foreach (Line test in leaf.Tests) EmitLine(test, line);
                EmitLine(new Line(language.JumpIf, [OperatorToken(ScriptLanguage.Negate(leaf.Op)), target]), line);
                break;
            case NotCondition not:
                JumpIfTrue(not.Inner, target, line);
                break;
            case AndCondition and:
                JumpIfFalse(and.Left, target, line);
                JumpIfFalse(and.Right, target, line);
                break;
            case OrCondition or:
            {
                var proceed = new Block();
                JumpIfTrue(or.Left, proceed, line);
                JumpIfFalse(or.Right, target, line);
                RequireCurrent(line).Terminator = new GotoTerminator(proceed);
                current = proceed;
                break;
            }
        }
    }

    /// <summary>Raw script lines. <c>{name}</c> inside them is replaced by that Lua value: a script
    /// function becomes its Function#N, a movement list its Action#N, a var its number.</summary>
    private void EmitAsm(string text, int line)
    {
        Scope? scope = null;
        foreach (string rawLine in text.Split('\n'))
        {
            string stripped = rawLine.Trim();
            int comment = stripped.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) stripped = stripped[..comment].TrimEnd();
            if (stripped.Length == 0) continue;
            if (Regex.IsMatch(stripped, @"\b(Function|Action)#\d+", RegexOptions.IgnoreCase))
            {
                throw new LuaException(line, $"asm line \"{stripped}\" names a Function or Action by number; numbers are assigned when compiling, so write {{name}} with a Lua name instead.");
            }

            string[] parts = ScriptTextDocument.Tokenize(stripped);
            var args = new List<object>();
            foreach (string part in parts.Skip(1))
            {
                Match whole = Regex.Match(part, @"^\{([A-Za-z_][A-Za-z0-9_]*)\}$");
                if (whole.Success)
                {
                    object? value = LookupForAsm(whole.Groups[1].Value, ref scope, line);
                    args.Add(value switch
                    {
                        GameFunction or Closure => ToFunctionTarget(value, line),
                        LuaTableValue or MovementValue => ToAction(value, line),
                        _ => ToToken(value, null, line),
                    });
                }
                else args.Add(part);
            }

            string command = parts[0];
            if (language.IsEnd(command) && args.Count == 0) Terminate(new EndTerminator(), line);
            else if (language.IsReturn(command) && args.Count == 0)
            {
                MarkEscape(_ => true);
                Terminate(new ReturnTerminator(), line);
            }
            else if (language.IsJump(command) && args.Count == 1 && args[0] is Block target)
            {
                MarkEscape(_ => true);
                Terminate(new GotoTerminator(target), line);
            }
            else EmitLine(new Line(command, args), line);
        }
    }

    /// <summary>Builtins get no scope, so asm resolves {name} through the scope of the call it came from.</summary>
    private object? LookupForAsm(string name, ref Scope? scope, int line)
    {
        scope ??= asmScopes.Count > 0 ? asmScopes.Peek() : new Scope(null);
        return Lookup(name, scope, line);
    }

    private readonly Stack<Scope> asmScopes = new();

    private string ToToken(object? value, ScriptParameterType? type, int line)
    {
        switch (value)
        {
            case double number:
            {
                if (number != Math.Floor(number)) throw new LuaException(line, $"{number.ToString(CultureInfo.InvariantCulture)} is not a whole number.");
                long integer = (long)number;
                return type == ScriptParameterType.Variable || integer >= 0x4000 ? $"0x{integer:X}" : integer.ToString(CultureInfo.InvariantCulture);
            }
            case VarRef var: return VarToken(var);
            case FlagRef flag: return ToToken(flag.Flag, type, line);
            case SymbolValue symbol: return symbol.Text;
            case bool flag: return flag ? "1" : "0";
            case string text: return LuaMessageText.ToInlineToken(text);
            case CommandValue command when command.Info.Name.StartsWith("CMD_", StringComparison.Ordinal): return command.Info.Name;
            default: throw new LuaException(line, $"A {DescribeValue(value)} cannot be a command argument.");
        }
    }

    private static string VarToken(VarRef var) => $"0x{var.Id:X}";

    private static VarRef ToVar(object? key, int line) => key switch
    {
        double number when number == Math.Floor(number) && number >= 0 => new VarRef((long)number),
        VarRef var => var,
        _ => throw new LuaException(line, "var[...] expects a variable number such as var[0x4000]."),
    };

    private static object ToFlagArgument(object? key, int line) => key switch
    {
        double number when number == Math.Floor(number) && number >= 0 => number,
        VarRef var => var,
        _ => throw new LuaException(line, "flag[...] expects a flag number."),
    };

    private int ToOperator(object? value, int line) => value switch
    {
        SymbolValue symbol when language.FindOperator(symbol.Text) is int op => op,
        double number when number is >= 0 and <= 5 => (int)number,
        _ => throw new LuaException(line, "condition(...) expects EQUAL, DIFFERENT, LESS, GREATER, LESS_EQUAL or GREATER_EQUAL."),
    };

    private static double ToNumber(object? value, int line) => value switch
    {
        double number => number,
        string text when TryParseNumber(text, out double parsed) => parsed,
        _ => throw new LuaException(line, $"Expected a number, got a {DescribeValue(value)}."),
    };

    private static long ToInteger(object? value, int line)
    {
        double number = ToNumber(value, line);
        if (number != Math.Floor(number)) throw new LuaException(line, $"Expected a whole number, got {number.ToString(CultureInfo.InvariantCulture)}.");
        return (long)number;
    }

    private static string ToStringArg(object? value, int line) =>
        value as string ?? throw new LuaException(line, $"Expected a string, got a {DescribeValue(value)}.");

    private static string ToConcatString(object? value, int line) => value switch
    {
        string text => text,
        double number => FormatNumber(number),
        _ => throw new LuaException(line, $"Cannot concatenate a {DescribeValue(value)}."),
    };

    private static bool TryParseNumber(string text, out double value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
        {
            value = hex;
            return true;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string FormatNumber(double number) =>
        number == Math.Floor(number) && Math.Abs(number) < 1e15 ? ((long)number).ToString(CultureInfo.InvariantCulture) : number.ToString(CultureInfo.InvariantCulture);

    private static string ToDisplayString(object? value) => value switch
    {
        null => "nil",
        bool b => b ? "true" : "false",
        double d => FormatNumber(d),
        string s => s,
        SymbolValue symbol => symbol.Text,
        VarRef var => $"var[0x{var.Id:X}]",
        _ => DescribeValue(value),
    };

    private static string DescribeValue(object? value) => value switch
    {
        null => "nil value",
        bool => "boolean",
        double => "number",
        string => "string",
        LuaTableValue => "table",
        Closure or Builtin => "function",
        CommandValue command => $"command ({command.Info.Name})",
        MovementValue movement => $"movement ({movement.Name})",
        VarRef => "variable",
        FlagRef => "flag",
        ConditionValue => "condition",
        GameFunction function => $"script function ({function.Name})",
        PendingResult => "command result in a condition (compare it directly, e.g. Command(...) == 1, or store it first: X = Command(...))",
        SymbolValue symbol => $"constant ({symbol.Text})",
        Indexer indexer => indexer.Name,
        _ => value.GetType().Name,
    };

    [GeneratedRegex(@"^(SEQ|SPECIES|ITEM|MOVE|TRAINER|CMD)_\w+$")]
    private static partial Regex PassthroughConstant();

    #endregion

    #region Layout

    private ScriptTextDocument Finish()
    {
        var scriptEntries = new List<(int Number, Block? Entry, int? UseScript)>();
        int expected = 1;
        foreach ((int number, ScriptDefinition definition) in scripts)
        {
            if (number != expected) throw new LuaException(definition.Line, $"Script {expected} is missing: scripts must be numbered 1, 2, 3... without gaps.");
            expected++;
            if (definition.UseScript is int used)
            {
                if (!scripts.ContainsKey(used) || scripts[used].UseScript is not null)
                {
                    throw new LuaException(definition.Line, $"Script {number} shares the code of script {used}, which must exist and have its own body.");
                }
                scriptEntries.Add((number, null, used));
                continue;
            }
            var entry = new Block();
            current = entry;
            frame = new Frame(FrameKind.Script, true, null);
            ExecBlock(definition.Body!.Function.Body, new Scope(definition.Body.Env));
            if (current is not null) current.Terminator = new EndTerminator();
            scriptEntries.Add((number, entry, null));
        }

        while (pendingFunctions.TryDequeue(out GameFunction? function))
        {
            if (function.UseScript is int used)
            {
                if (!scripts.ContainsKey(used)) throw new LuaException(function.Line, $"use_script({used}): there is no script {used}.");
                continue;
            }
            current = function.Entry;
            frame = new Frame(FrameKind.Function, true, null);
            ExecBlock(function.Body!.Function.Body, new Scope(function.Body.Env));
            if (current is not null) current.Terminator = new ReturnTerminator();
        }

        foreach ((string name, object? value) in globals)
        {
            if (value is GameFunction { Entry: null } unused) warnings.Add($"Line {unused.Line}: function {name} is never used and is left out.");
        }

        return new Layout(scriptEntries, CollectUseScripts()).Build();
    }

    private Dictionary<Block, int> CollectUseScripts() =>
        referencedFunctions.Where(function => function.UseScript is not null).ToDictionary(function => function.Entry!, function => function.UseScript!.Value);

    private readonly List<GameFunction> referencedFunctions = [];

    /// <summary>Turns the blocks into numbered Scripts, Functions and Actions: a block reached only by
    /// falling through from one place is appended to it, everything jumped or called to becomes a
    /// Function, identical Functions and Actions are merged, and empty jump-only blocks are skipped.</summary>
    private sealed class Layout(List<(int Number, Block? Entry, int? UseScript)> scripts, Dictionary<Block, int> useScriptBlocks)
    {
        private readonly Dictionary<Block, int> gotoPredecessors = [];
        private readonly HashSet<Block> referenced = [];
        private readonly HashSet<Block> reachable = [];

        public ScriptTextDocument Build()
        {
            foreach ((_, Block? entry, _) in scripts)
            {
                if (entry is not null) Visit(entry);
            }

            // A script's own entry cannot be jumped to by number, so a script whose start is also a jump
            // or call target becomes a Jump to a Function holding that code.
            var scriptHeads = new Dictionary<int, Block>();
            foreach ((int number, Block? entry, _) in scripts)
            {
                if (entry is null) continue;
                Block head = Resolve(entry);
                if (referenced.Contains(head) || gotoPredecessors.GetValueOrDefault(head) > 0 || scriptHeads.ContainsValue(head))
                {
                    var trampoline = new Block { Terminator = new GotoTerminator(head) };
                    gotoPredecessors[head] = gotoPredecessors.GetValueOrDefault(head) + 1;
                    reachable.Add(trampoline);
                    head = trampoline;
                }
                scriptHeads[number] = head;
            }

            var containers = new Dictionary<Block, Container>();
            var order = new List<Block>();
            var pending = new Queue<Block>();
            var queued = new HashSet<Block>();
            void Enqueue(Block block)
            {
                if (queued.Add(block)) pending.Enqueue(block);
            }

            foreach (Block head in scriptHeads.Values) Enqueue(head);
            while (pending.TryDequeue(out Block? head))
            {
                Container container = BuildContainer(head, Enqueue);
                containers[head] = container;
                order.Add(head);
            }

            Dictionary<Block, Block> merged = MergeDuplicates(containers, order, scriptHeads.Values.ToHashSet());

            var functionNumbers = new Dictionary<Block, int>();
            var actionNumbers = new Dictionary<string, int>();
            var actions = new List<ScriptTextAction>();
            var document = new ScriptTextDocument();

            int FunctionNumber(Block block)
            {
                block = Canonical(merged, block);
                if (!functionNumbers.TryGetValue(block, out int number))
                {
                    number = functionNumbers.Count + 1;
                    functionNumbers[block] = number;
                }
                return number;
            }

            string ArgToken(object arg) => arg switch
            {
                Block block => $"Function#{FunctionNumber(Resolve(block))}",
                ActionArg action => $"Action#{ActionNumber(action)}",
                _ => (string)arg,
            };

            int ActionNumber(ActionArg action)
            {
                string key = string.Join('\n', action.Steps.Select(step => $"{step.Movement} {step.Count}"));
                if (!actionNumbers.TryGetValue(key, out int number))
                {
                    number = actionNumbers.Count + 1;
                    actionNumbers[key] = number;
                    actions.Add(new ScriptTextAction { Number = number, Steps = action.Steps.ToList() });
                }
                return number;
            }

            ScriptTextContainer Render(int number, Container container) => new()
            {
                Number = number,
                UseScript = container.UseScript,
                Lines = container.Lines.Select(line => new ScriptTextLine(line.Command, line.Args.Select(ArgToken).ToList())).ToList(),
                Terminator = container.Terminator switch
                {
                    EndTerminator => ScriptTextTerminator.End,
                    ReturnTerminator => ScriptTextTerminator.Return,
                    _ => ScriptTextTerminator.Jump,
                },
                JumpTarget = container.Terminator is GotoTerminator jump ? FunctionNumber(jump.Target) : 0,
            };

            foreach ((int number, _, int? used) in scripts)
            {
                document.Scripts.Add(used is int shared
                    ? new ScriptTextContainer { Number = number, UseScript = shared }
                    : Render(number, containers[scriptHeads[number]]));
            }

            var renderedFunctions = new Dictionary<int, ScriptTextContainer>();
            for (int i = 0; i < functionNumbers.Count; i++)
            {
                (Block block, int number) = functionNumbers.ElementAt(i);
                renderedFunctions[number] = Render(number, containers[block]);
            }
            document.Functions.AddRange(renderedFunctions.OrderBy(pair => pair.Key).Select(pair => pair.Value));
            document.Actions.AddRange(actions);
            return document;
        }

        private sealed class Container
        {
            public List<Line> Lines { get; } = [];
            public Terminator Terminator { get; set; } = new EndTerminator();
            public int? UseScript { get; init; }
        }

        private void Visit(Block start)
        {
            var stack = new Stack<Block>();
            stack.Push(start);
            while (stack.TryPop(out Block? block))
            {
                block = Resolve(block);
                if (!reachable.Add(block)) continue;
                foreach (Line line in block.Lines)
                {
                    foreach (object arg in line.Args)
                    {
                        if (arg is not Block target) continue;
                        target = Resolve(target);
                        referenced.Add(target);
                        stack.Push(target);
                    }
                }
                if (block.Terminator is GotoTerminator jump)
                {
                    Block target = Resolve(jump.Target);
                    gotoPredecessors[target] = gotoPredecessors.GetValueOrDefault(target) + 1;
                    stack.Push(target);
                }
            }
        }

        /// <summary>Skips empty blocks that only fall through to another.</summary>
        private static Block Resolve(Block block)
        {
            var seen = new HashSet<Block>();
            while (block.Lines.Count == 0 && block.Terminator is GotoTerminator jump && seen.Add(block)) block = jump.Target;
            return block;
        }

        private bool IsHead(Block block) =>
            referenced.Contains(block) || gotoPredecessors.GetValueOrDefault(block) != 1 || useScriptBlocks.ContainsKey(block);

        private Container BuildContainer(Block head, Action<Block> enqueue)
        {
            if (useScriptBlocks.TryGetValue(head, out int used)) return new Container { UseScript = used };

            var container = new Container();
            Block block = head;
            var chain = new HashSet<Block>();
            while (true)
            {
                chain.Add(block);
                foreach (Line line in block.Lines)
                {
                    container.Lines.Add(line);
                    foreach (object arg in line.Args)
                    {
                        if (arg is Block target) enqueue(Resolve(target));
                    }
                }
                if (block.Terminator is GotoTerminator jump)
                {
                    Block next = Resolve(jump.Target);
                    if (!IsHead(next) && !chain.Contains(next))
                    {
                        block = next;
                        continue;
                    }
                    container.Terminator = new GotoTerminator(next);
                    enqueue(next);
                }
                else container.Terminator = block.Terminator ?? new EndTerminator();
                return container;
            }
        }

        private static Block Canonical(Dictionary<Block, Block> merged, Block block)
        {
            while (merged.TryGetValue(block, out Block? target)) block = target;
            return block;
        }

        /// <summary>Merges Functions with identical code, repeating until nothing changes since merging
        /// two targets can make their callers identical too.</summary>
        private static Dictionary<Block, Block> MergeDuplicates(Dictionary<Block, Container> containers, List<Block> order, HashSet<Block> scriptHeads)
        {
            var merged = new Dictionary<Block, Block>();
            var ids = new Dictionary<Block, int>();
            for (int i = 0; i < order.Count; i++) ids[order[i]] = i;

            bool changed = true;
            while (changed)
            {
                changed = false;
                var seen = new Dictionary<string, Block>();
                foreach (Block head in order)
                {
                    if (scriptHeads.Contains(head) || merged.ContainsKey(head)) continue;
                    string key = Key(containers[head], block => ids[Canonical(merged, Resolve(block))]);
                    if (seen.TryGetValue(key, out Block? existing))
                    {
                        merged[head] = existing;
                        changed = true;
                    }
                    else seen[key] = head;
                }
            }
            return merged;
        }

        private static string Key(Container container, Func<Block, int> id)
        {
            var builder = new StringBuilder();
            if (container.UseScript is int used) return $"use {used}";
            foreach (Line line in container.Lines)
            {
                builder.Append(line.Command);
                foreach (object arg in line.Args)
                {
                    builder.Append(' ').Append(arg switch
                    {
                        Block block => $"F{id(block)}",
                        ActionArg action => "A[" + string.Join(',', action.Steps.Select(step => $"{step.Movement} {step.Count}")) + "]",
                        _ => (string)arg,
                    });
                }
                builder.Append('\n');
            }
            builder.Append(container.Terminator switch
            {
                GotoTerminator jump => $"jump F{id(jump.Target)}",
                ReturnTerminator => "return",
                _ => "end",
            });
            return builder.ToString();
        }
    }

    #endregion
}
