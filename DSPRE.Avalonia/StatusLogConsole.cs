using DSPRE.Core;
using System.Text;

namespace DSPRE.Avalonia;

/// <summary>Stands in for Console.Error so the diagnostics written there throughout the app
/// ("[DSPRE] ... could not be read") reach the status log too, while still going to the real stderr.</summary>
internal sealed class StatusLogConsole : TextWriter
{
    private readonly StringBuilder line = new();
    private readonly TextWriter original;

    private StatusLogConsole(TextWriter original) => this.original = original;

    /// <summary>The real stderr, for writers that log to the status log themselves.</summary>
    public static TextWriter Original { get; private set; } = Console.Error;

    public override Encoding Encoding => original.Encoding;

    public static void Install()
    {
        if (Console.Error is StatusLogConsole) return;
        Original = Console.Error;
        Console.SetError(TextWriter.Synchronized(new StatusLogConsole(Original)));
    }

    public override void Write(char value)
    {
        original.Write(value);
        if (value == '\n') Flush();
        else if (value != '\r') line.Append(value);
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        foreach (char c in value) Write(c);
    }

    public override void WriteLine(string? value)
    {
        Write(value);
        Write('\n');
    }

    public override void Flush()
    {
        string text = line.ToString();
        line.Clear();
        original.Flush();
        if (text.Trim().Length == 0) return;

        const string prefix = "[DSPRE] ";
        text = text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;
        StatusLog.Warning("Diagnostics", text);
    }
}
