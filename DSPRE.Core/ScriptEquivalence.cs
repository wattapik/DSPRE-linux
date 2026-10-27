using System.Globalization;

namespace DSPRE.Core;

/// <summary>Checks that two plaintext script files behave the same: from each Script, both are run in
/// step, and every command, branch condition, movement list and End must match. How the code is split
/// into Functions does not matter: Jump, Call and Return only move between steps, so a Function's code
/// written inline compares equal to a Call or Jump to it. Used to prove a Lua file lifted from a script
/// compiles back to the same behaviour before it replaces the script.</summary>
public sealed class ScriptEquivalence
{
    private readonly Program left;
    private readonly Program right;
    private readonly HashSet<(State, State)> visited = [];
    private const int MaxStates = 500_000;
    private const int MaxStackDepth = 24;

    private ScriptEquivalence(ScriptTextDocument a, ScriptTextDocument b, ScriptLanguage language, IReadOnlyList<string>? textsA, IReadOnlyList<string>? textsB)
    {
        left = new Program(a, language, textsA);
        right = new Program(b, language, textsB);
    }

    /// <summary>Null when equivalent, otherwise a description of the first difference found. Given each
    /// file's text archive, messages are compared by the text they show rather than by number, so a file
    /// whose archive was rebuilt with the same texts in other places still compares equal.</summary>
    public static string? FindDifference(ScriptTextDocument a, ScriptTextDocument b, ScriptLanguage language,
        IReadOnlyList<string>? textsA = null, IReadOnlyList<string>? textsB = null)
    {
        if (a.Scripts.Count != b.Scripts.Count) return $"The files have {a.Scripts.Count} and {b.Scripts.Count} scripts.";
        var checker = new ScriptEquivalence(a, b, language, textsA, textsB);
        foreach (ScriptTextContainer script in a.Scripts)
        {
            string? difference = checker.Compare(checker.left.ScriptStart(script.Number), checker.right.ScriptStart(script.Number));
            if (difference is not null) return $"Script {script.Number}: {difference}";
        }
        return null;
    }

    private readonly record struct Position(bool IsScript, int Number, int Index);

    private sealed record State(Position Position, Stack? Stack)
    {
        public override string ToString() => $"{(Position.IsScript ? "Script" : "Function")} {Position.Number}, line {Position.Index + 1}";
    }

    /// <summary>The call stack as an immutable linked list, so states can be compared and hashed.</summary>
    private sealed record Stack(Position Return, Stack? Next, int Depth);

    private enum Kind { Command, Branch, End, ReturnToNothing, Missing }

    private sealed record Step(Kind Kind, string Text, State? Taken, State? Next);

    private string? Compare(State a, State b)
    {
        var work = new Stack<(State, State)>();
        work.Push((a, b));
        while (work.TryPop(out (State A, State B) pair))
        {
            if (!visited.Add(pair)) continue;
            if (visited.Count > MaxStates) return "too many paths to compare.";

            Step stepA = left.Advance(pair.A);
            Step stepB = right.Advance(pair.B);
            if (stepA.Kind != stepB.Kind || stepA.Text != stepB.Text)
            {
                return $"\"{Describe(stepA)}\" at {pair.A} differs from \"{Describe(stepB)}\" at {pair.B}.";
            }
            if (stepA.Kind == Kind.Missing) return $"{stepA.Text} at {pair.A}.";
            if (stepA.Next is not null) work.Push((stepA.Next, stepB.Next!));
            if (stepA.Taken is not null) work.Push((stepA.Taken, stepB.Taken!));
        }
        return null;
    }

    private static string Describe(Step step) => step.Kind switch
    {
        Kind.End => "End",
        Kind.ReturnToNothing => "Return with nothing to return to",
        _ => step.Text,
    };

    private sealed class Program
    {
        private readonly ScriptLanguage language;
        private readonly Dictionary<int, ScriptTextContainer> scripts;
        private readonly Dictionary<int, ScriptTextContainer> functions;
        private readonly Dictionary<int, ScriptTextAction> actions;

        private readonly IReadOnlyList<string>? texts;

        public Program(ScriptTextDocument document, ScriptLanguage language, IReadOnlyList<string>? texts)
        {
            this.language = language;
            this.texts = texts;
            scripts = document.Scripts.GroupBy(s => s.Number).ToDictionary(g => g.Key, g => g.First());
            functions = document.Functions.GroupBy(f => f.Number).ToDictionary(g => g.Key, g => g.First());
            actions = document.Actions.GroupBy(a => a.Number).ToDictionary(g => g.Key, g => g.First());
        }

        public State ScriptStart(int number) => new(new Position(true, number, 0), null);

        /// <summary>Runs through Jumps, Calls, Returns and UseScripts to the next step that does something.</summary>
        public Step Advance(State state)
        {
            for (int guard = 0; guard < 10_000; guard++)
            {
                Position position = state.Position;
                ScriptTextContainer? container = position.IsScript ? scripts.GetValueOrDefault(position.Number) : functions.GetValueOrDefault(position.Number);
                if (container is null) return new Step(Kind.Missing, $"{(position.IsScript ? "Script" : "Function")} {position.Number} does not exist", null, null);
                if (container.UseScript is int used)
                {
                    state = state with { Position = new Position(true, used, 0) };
                    continue;
                }

                if (position.Index >= container.Lines.Count)
                {
                    switch (container.Terminator)
                    {
                        case ScriptTextTerminator.End:
                            return new Step(Kind.End, "End", null, null);
                        case ScriptTextTerminator.Jump:
                            state = state with { Position = new Position(false, container.JumpTarget, 0) };
                            continue;
                        default:
                            if (state.Stack is null) return new Step(Kind.ReturnToNothing, "Return", null, null);
                            state = new State(state.Stack.Return, state.Stack.Next);
                            continue;
                    }
                }

                ScriptTextLine line = container.Lines[position.Index];
                State next = state with { Position = position with { Index = position.Index + 1 } };
                ScriptCommandInfo? info = language.FindCommand(line.Command);
                int? targetIndex = FunctionArgument(line);

                if (info?.CommandId == 0x1A && targetIndex is int callIndex)
                {
                    state = Call(state, next, Target(line, callIndex));
                    if (state.Stack!.Depth > MaxStackDepth) return new Step(Kind.Missing, "Calls nest too deeply to compare", null, null);
                    continue;
                }

                string text = Normalize(line, info, targetIndex);
                if (targetIndex is int index)
                {
                    // CallIf is a branch whose taken side also pushes a return point; JumpIf and the other
                    // conditional jumps (JumpIfObjID...) just go there. Both compare as the same branch.
                    int target = Target(line, index);
                    State taken = info?.CommandId == 0x1D
                        ? Call(state, next, target)
                        : state with { Position = new Position(false, target, 0) };
                    if (taken.Stack?.Depth > MaxStackDepth) return new Step(Kind.Missing, "Calls nest too deeply to compare", null, null);
                    return new Step(Kind.Branch, text, taken, next);
                }
                return new Step(Kind.Command, text, null, next);
            }
            return new Step(Kind.Missing, "Endless loop of jumps", null, null);
        }

        private static State Call(State state, State returnTo, int target) =>
            new(new Position(false, target, 0), new Stack(returnTo.Position, state.Stack, (state.Stack?.Depth ?? 0) + 1));

        private static int Target(ScriptTextLine line, int index)
        {
            ScriptTextDocument.TryParseFunctionRef(line.Args[index], out int target);
            return target;
        }

        private static int? FunctionArgument(ScriptTextLine line)
        {
            for (int i = 0; i < line.Args.Count; i++)
            {
                if (ScriptTextDocument.TryParseFunctionRef(line.Args[i], out _)) return i;
            }
            return null;
        }

        /// <summary>The line with numbers in one notation, names in one case, CallIf written as JumpIf,
        /// the Function target left out (it is compared by following it) and Actions spelled out.</summary>
        private string Normalize(ScriptTextLine line, ScriptCommandInfo? info, int? targetIndex)
        {
            string command = info?.CommandId == 0x1D ? language.JumpIf : info?.Name ?? line.Command;
            var parts = new List<string> { command.ToUpperInvariant() };
            for (int i = 0; i < line.Args.Count; i++)
            {
                if (i == targetIndex) continue;
                string arg = line.Args[i];
                if (ScriptTextDocument.TryParseActionRef(arg, out int action))
                {
                    parts.Add(actions.TryGetValue(action, out ScriptTextAction? steps)
                        ? "{" + string.Join(",", steps.Steps.Select(step => $"{NormalizeMovement(step.Movement)}*{NormalizeToken(step.Count ?? "1")}")) + "}"
                        : $"missing Action {action}");
                }
                else if (texts is not null && info is not null && ScriptLanguage.IsMessageParameter(info, i)
                         && long.TryParse(NormalizeToken(arg), out long entry) && entry >= 0 && entry < Math.Min(texts.Count, 0x4000))
                {
                    parts.Add("TEXT\"" + texts[(int)entry] + "\"");
                }
                else parts.Add(NormalizeToken(arg));
            }
            return string.Join(' ', parts);
        }

        private string NormalizeMovement(string movement) =>
            ushort.TryParse(movement, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort id) && language.Database.Movements.TryGetValue(id, out ScriptSubEntryInfo? info) && info.Name.Length > 0
                ? info.Name.ToUpperInvariant()
                : movement.ToUpperInvariant();

        private string NormalizeToken(string token)
        {
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex)) return hex.ToString(CultureInfo.InvariantCulture);
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number)) return number.ToString(CultureInfo.InvariantCulture);
            if (language.FindOperator(token) is int op) return language.ComparisonOperators[op];
            return token.ToUpperInvariant();
        }
    }
}
