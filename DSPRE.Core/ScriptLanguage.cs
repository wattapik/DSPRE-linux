using System.Text.RegularExpressions;

namespace DSPRE.Core;

/// <summary>The names a script command database gives the plaintext script language: commands and
/// movements by their database name and by a snake_case alias of it, plus the handful of commands the
/// Lua compiler and lifter build control flow from. Those are found by ID, which DP, Platinum and HGSS
/// share, except SetVar and friends, whose IDs differ in HGSS and are found by name instead.</summary>
public sealed partial class ScriptLanguage
{
    public ScriptCommandDatabase Database { get; }

    public string End { get; }
    public string Return { get; }
    public string Jump { get; }
    public string Call { get; }
    public string JumpIf { get; }
    public string CallIf { get; }
    public string CompareVarValue { get; }
    public string CompareVars { get; }
    public string CheckFlag { get; }
    public string SetFlag { get; }
    public string ClearFlag { get; }
    public string Movement { get; }
    public string? SetVar { get; }
    public string? CopyVar { get; }
    public string? AddVar { get; }
    public string? SubVar { get; }

    private readonly Dictionary<string, ScriptCommandInfo> commandsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScriptCommandInfo> commandsByAlias = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptSubEntryInfo> movementsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScriptSubEntryInfo> movementsByAlias = new(StringComparer.Ordinal);

    public IReadOnlyList<string> ComparisonOperators { get; }

    public ScriptLanguage(ScriptCommandDatabase database)
    {
        Database = database;
        foreach (ScriptCommandInfo command in database.Commands.Values)
        {
            if (command.Name.Length > 0) commandsByName.TryAdd(command.Name, command);
        }
        foreach (ScriptSubEntryInfo movement in database.Movements.Values)
        {
            if (movement.Name.Length > 0) movementsByName.TryAdd(movement.Name, movement);
        }

        // An alias that two names share (or that is itself some other command's real name) would make a
        // call ambiguous, so such aliases are left out and only the exact names work.
        var taken = new HashSet<string>(commandsByName.Keys.Concat(movementsByName.Keys), StringComparer.Ordinal);
        AddAliases(commandsByName.Values.Select(command => (command.Name, command)), commandsByAlias, taken);
        AddAliases(movementsByName.Values.Select(movement => (movement.Name, movement)), movementsByAlias, taken);

        End = NameOf(0x02);
        Return = NameOf(0x1B);
        Jump = NameOf(0x16);
        Call = NameOf(0x1A);
        JumpIf = NameOf(0x1C);
        CallIf = NameOf(0x1D);
        CompareVarValue = NameOf(0x11);
        CompareVars = NameOf(0x12);
        SetFlag = NameOf(0x1E);
        ClearFlag = NameOf(0x1F);
        CheckFlag = NameOf(0x20);
        Movement = NameOf(0x5E);
        SetVar = FindName("SetVar");
        CopyVar = FindName("SetVarFromVariable");
        AddVar = FindName("IncrementVar");
        SubVar = FindName("DecrementVar");

        ComparisonOperators = Enumerable.Range(0, 6)
            .Select(id => database.ComparisonOperators.TryGetValue((ushort)id, out string? name) ? name : DefaultOperators[id])
            .ToArray();
    }

    private static readonly string[] DefaultOperators = ["LESS", "EQUAL", "GREATER", "LESS/EQUAL", "GREATER/EQUAL", "DIFFERENT"];

    public const int Less = 0, Equal = 1, Greater = 2, LessEqual = 3, GreaterEqual = 4, Different = 5;

    /// <summary>The operator that holds exactly when <paramref name="op"/> does not. The condition register
    /// only ever holds LESS, EQUAL or GREATER, so each operator has an exact complement.</summary>
    public static int Negate(int op) => op switch
    {
        Less => GreaterEqual,
        GreaterEqual => Less,
        Equal => Different,
        Different => Equal,
        Greater => LessEqual,
        LessEqual => Greater,
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    /// <summary>The operator for the same comparison with its operands swapped.</summary>
    public static int Mirror(int op) => op switch
    {
        Less => Greater,
        Greater => Less,
        LessEqual => GreaterEqual,
        GreaterEqual => LessEqual,
        _ => op,
    };

    public int? FindOperator(string token)
    {
        for (int i = 0; i < ComparisonOperators.Count; i++)
        {
            if (string.Equals(ComparisonOperators[i], token, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return null;
    }

    private string NameOf(ushort id) =>
        Database.Commands.TryGetValue(id, out ScriptCommandInfo? info) && info.Name.Length > 0
            ? info.Name
            : throw new InvalidDataException($"The script command database has no name for command 0x{id:X4}.");

    private string? FindName(string name) => commandsByName.TryGetValue(name, out ScriptCommandInfo? info) ? info.Name : null;

    public ScriptCommandInfo? FindCommand(string name) =>
        commandsByName.TryGetValue(name, out ScriptCommandInfo? info) ? info : null;

    /// <summary>A command by its exact database name or its snake_case alias.</summary>
    public ScriptCommandInfo? ResolveCommand(string identifier) =>
        commandsByName.TryGetValue(identifier, out ScriptCommandInfo? info) && info.Name == identifier ? info
        : commandsByAlias.TryGetValue(identifier, out info) ? info
        : null;

    public ScriptSubEntryInfo? FindMovement(string name) =>
        movementsByName.TryGetValue(name, out ScriptSubEntryInfo? info) ? info : null;

    public ScriptSubEntryInfo? ResolveMovement(string identifier) =>
        movementsByName.TryGetValue(identifier, out ScriptSubEntryInfo? info) && info.Name == identifier ? info
        : movementsByAlias.TryGetValue(identifier, out info) ? info
        : null;

    /// <summary>The command or movement name (or alias) closest to a misspelt one, if any is close.</summary>
    public string? SuggestName(string name)
    {
        if (commandsByName.TryGetValue(name, out ScriptCommandInfo? command)) return command.Name;
        if (movementsByName.TryGetValue(name, out ScriptSubEntryInfo? movement)) return movement.Name;
        string lower = name.ToLowerInvariant();
        return commandsByName.Keys.Concat(commandsByAlias.Keys).Concat(movementsByName.Keys).Concat(movementsByAlias.Keys)
            .Select(candidate => (Name: candidate, Distance: EditDistance(lower, candidate.ToLowerInvariant())))
            .Where(candidate => candidate.Distance <= Math.Max(1, name.Length / 4))
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => candidate.Name)
            .FirstOrDefault();
    }

    private static int EditDistance(string a, string b)
    {
        int[] previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int[] row = new int[b.Length + 1];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                row[j] = Math.Min(Math.Min(row[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
            previous = row;
        }
        return previous[b.Length];
    }

    /// <summary>Whether a command's parameter is a message of the script's own text archive: a "Text
    /// Slot" or GenderMessage's "Male Text"/"Female Text". Commands reading another archive
    /// (MessageFromArchive, MessageUnown, Mystery Gift, and the menu options of AddMultiOption and
    /// CreateMultiTouchBox, whose numbers run past the map's archive in the games' own scripts) are left out.</summary>
    public static bool IsMessageParameter(ScriptCommandInfo info, int index) =>
        index < info.ParameterNames.Count
        && MessageParameterName().IsMatch(info.ParameterNames[index])
        && !info.Name.Contains("Unown", StringComparison.OrdinalIgnoreCase)
        && !info.Name.Contains("Multi", StringComparison.OrdinalIgnoreCase)
        && !info.ParameterNames.Any(name => name.Contains("Archive", StringComparison.OrdinalIgnoreCase) || name.Contains("Text File", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^(u8|u16|Flex): (Text Slot|Male Text|Female Text)$")]
    private static partial Regex MessageParameterName();

    /// <summary>The variable a command's result goes in when it is used as a value without one,
    /// <c>if YesNoBox() == 0</c>: VAR_RESULT, the variable the games use for this.</summary>
    public const long ResultVariable = 0x800C;

    private readonly Dictionary<ushort, bool> resultCommands = [];

    /// <summary>Whether a command stores its result in its last parameter, a variable, so it can be used
    /// as a value: <c>RESULT = CheckItem(ITEM_POTION, 1)</c>. Read from the database's description of
    /// it ("Stores 1 in Variable if...", "The Variable stores the form ID"), which tells such variables
    /// from ones the command only reads (SetVarFromVariable, CompareVars).</summary>
    public bool HasResultParameter(ScriptCommandInfo info)
    {
        lock (resultCommands)
        {
            if (resultCommands.TryGetValue(info.CommandId, out bool known)) return known;
            bool result = false;
            if (info.ParameterTypes.Count > 0 && info.ParameterTypes[^1] == ScriptParameterType.Variable
                && info.ParameterSizes is not [0xFF, ..] && info.ParameterNames.Count == info.ParameterTypes.Count)
            {
                string name = info.ParameterNames[^1].Split(": ", 2)[^1].Split('\n')[0].Trim();
                string escaped = Regex.Escape(name);
                result = name.Length > 0 && !name.Contains("Countdown", StringComparison.OrdinalIgnoreCase)
                         && (Regex.IsMatch(info.Description, $@"\b(stor|sav|return|put|writ|set)\w*\b.*\b(in|into|to)\s+(the\s+)?{escaped}\b", RegexOptions.IgnoreCase)
                             || Regex.IsMatch(info.Description, $@"\b{escaped}\s+(stores|holds|contains|receives|will\s+(store|hold|contain))\b", RegexOptions.IgnoreCase));
            }
            resultCommands[info.CommandId] = result;
            return result;
        }
    }

    public bool IsEnd(string name) => string.Equals(name, End, StringComparison.OrdinalIgnoreCase);
    public bool IsReturn(string name) => string.Equals(name, Return, StringComparison.OrdinalIgnoreCase);
    public bool IsJump(string name) => string.Equals(name, Jump, StringComparison.OrdinalIgnoreCase);

    public static bool IsIdentifier(string text) => IdentifierPattern().IsMatch(text) && !LuaKeywords.Contains(text);

    /// <summary>LockAll → lock_all, AddOW → add_ow, WalkOnSpotNorth4 → walk_on_spot_north4. End and other
    /// names that turn into a Lua keyword get "_script" or "_" appended, since a keyword cannot be called.</summary>
    public static string ToSnakeCase(string name)
    {
        string snake = SnakeBoundary().Replace(name, "_").ToLowerInvariant();
        if (snake == "end") return "end_script";
        return LuaKeywords.Contains(snake) ? snake + "_" : snake;
    }

    private static void AddAliases<T>(IEnumerable<(string Name, T Entry)> entries, Dictionary<string, T> aliases, HashSet<string> taken)
    {
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, T entry) in entries)
        {
            if (!IdentifierPattern().IsMatch(name)) continue;
            string alias = ToSnakeCase(name);
            if (alias == name) continue;
            if (taken.Contains(alias) || !aliases.TryAdd(alias, entry)) duplicates.Add(alias);
        }
        foreach (string alias in duplicates) aliases.Remove(alias);
        taken.UnionWith(aliases.Keys);
    }

    public static readonly HashSet<string> LuaKeywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in",
        "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
    };

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex SnakeBoundary();
}
