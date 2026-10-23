using System.Diagnostics;
using System.Globalization;

namespace SCPSL.ModLaunch.Core;

/// <summary>Logs elapsed wall-clock time even when a startup phase fails.</summary>
public sealed class StartupPhase : IDisposable
{
    private readonly Action<string>? log;
    private readonly string phase;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private bool disposed;

    private StartupPhase(Action<string>? log, string phase)
    {
        this.log = log;
        this.phase = phase;
    }

    public static StartupPhase Start(Action<string>? log, string phase) => new(log, phase);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        elapsed.Stop();
        log?.Invoke($"Startup phase {phase}: {elapsed.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms.");
    }
}
