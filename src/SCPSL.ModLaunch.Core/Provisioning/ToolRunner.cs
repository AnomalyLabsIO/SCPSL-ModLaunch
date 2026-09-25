using System.Diagnostics;

namespace SCPSL.ModLaunch.Core.Provisioning;

internal static class ToolRunner
{
    internal static async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        string workingDirectory, string dotnetRoot, Action<string> log, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_ROOT"] = dotnetRoot;
        start.Environment["DOTNET_ROOT_X64"] = dotnetRoot;
        start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        using var process = Process.Start(start) ?? throw new IOException($"Unable to start {executable}.");
        var output = new System.Text.StringBuilder();
        var gate = new object();
        async Task DrainAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (gate)
                {
                    if (output.Length > 32000) output.Remove(0, output.Length - 16000);
                    output.AppendLine(line);
                    log(line);
                }
            }
        }
        var stdout = DrainAsync(process.StandardOutput);
        var stderr = DrainAsync(process.StandardError);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(budget.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(executable)} exceeded its {timeout.TotalMinutes:g} minute time limit.");
        }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new IOException($"{Path.GetFileName(executable)} exited with code {process.ExitCode}.\n{output}");
        return output.ToString();
    }
}
