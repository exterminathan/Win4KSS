using System.Diagnostics;
using System.Text;

namespace Win4K.Core.Media;

/// <param name="ExitCode">The process's exit code; -1 if it was killed for running too long.</param>
/// <param name="StdErr">The tail of standard error (ffmpeg reports everything there).</param>
/// <param name="TimedOut">Killed because it outlived the timeout.</param>
public sealed record ProcessResult(int ExitCode, string StdErr, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>Runs a tool to completion. The seam that lets the preview service be tested without ffmpeg.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> and waits for it. A process that outlives
    /// <paramref name="timeout"/> is killed (with its children) and reported as
    /// <see cref="ProcessResult.TimedOut"/>; cancelling <paramref name="ct"/> kills it and throws.
    /// </summary>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// The real runner: no window, below-normal priority (previews must never make the browser
/// stutter), stderr captured, and <see cref="Process.Kill(bool)"/> on the entire tree when
/// cancelled or timed out — an orphaned ffmpeg decoding 4K HEVC would pin the CPU for minutes.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int MaxStdErrChars = 4096;

    /// <summary>Raised with the process id once a process has started. For tests and diagnostics.</summary>
    public event Action<int>? Started;

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = false,
        };
        foreach (var arg in arguments)
            info.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = info };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (stderr)
            {
                stderr.AppendLine(e.Data);
                if (stderr.Length > MaxStdErrChars * 2)
                    stderr.Remove(0, stderr.Length - MaxStdErrChars);
            }
        };
        process.OutputDataReceived += (_, _) => { };

        process.Start();
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception)
        {
            // Exited already, or not permitted; neither matters.
        }
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        Started?.Invoke(process.Id);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested)
                throw;
            return new ProcessResult(-1, Tail(stderr), TimedOut: true);
        }

        // WaitForExitAsync returns after the redirected streams hit EOF, so stderr is complete.
        return new ProcessResult(process.ExitCode, Tail(stderr), TimedOut: false);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    private static string Tail(StringBuilder sb)
    {
        lock (sb)
        {
            var text = sb.ToString().Trim();
            return text.Length <= MaxStdErrChars ? text : text[^MaxStdErrChars..];
        }
    }
}

/// <summary>Finds ffmpeg: the configured path, the bundled copy, then C:\ffmpeg (and its bin\), then PATH.</summary>
public static class FfmpegLocator
{
    public const string DefaultPath = @"C:\ffmpeg\ffmpeg.exe";

    /// <summary>The copy shipped with Win4KSS, in ffmpeg\ beside the exe (scripts/fetch-ffmpeg.ps1).</summary>
    public static string BundledPath(string exeName = "ffmpeg.exe") => Path.Combine(AppContext.BaseDirectory, "ffmpeg", exeName);

    /// <returns>A path that exists, or null when ffmpeg is not installed.</returns>
    public static string? Locate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var trimmed = configured.Trim().Trim('"');
            if (File.Exists(trimmed))
                return trimmed;
            Log.Warn($"Previews: configured ffmpeg '{trimmed}' does not exist; searching the usual places");
        }

        foreach (var candidate in new[] { BundledPath(), DefaultPath, @"C:\ffmpeg\bin\ffmpeg.exe" })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry; skip it.
            }
        }
        return null;
    }
}
