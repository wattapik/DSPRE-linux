namespace DSPRE.Core;

public sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}
