using System.Diagnostics;

namespace DSPRE.Core;

public interface IToolRunner
{
    Task<ToolResult> RunAsync(
        string executablePath,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessToolRunner : IToolRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    public async Task<ToolResult> RunAsync(
        string executablePath,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(DefaultTimeout);

        try
        {
            process.Start();

            // Drain both streams concurrently so a verbose tool cannot block on a full pipe.
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);

            return new ToolResult(
                process.ExitCode,
                await outputTask,
                await errorTask);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            string reason = cancellationToken.IsCancellationRequested
                ? "The external tool operation was cancelled."
                : $"The external tool exceeded the {DefaultTimeout.TotalSeconds:0}-second timeout.";
            return new ToolResult(-1, string.Empty, reason);
        }
    }
}
