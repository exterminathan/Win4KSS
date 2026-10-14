using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Win4K.Core.Catalogs;

public sealed record MediaProbeResult(int Width, int Height, double? DurationSeconds, string? Codec);

/// <summary>Measures a remote video without downloading it. Optional everywhere it is used.</summary>
public interface IMediaProbe
{
    Task<MediaProbeResult?> ProbeAsync(string url, CancellationToken ct);
}

/// <summary>
/// ffprobe over HTTP: reads the container header only. Used when installed
/// (C:\ffmpeg\ffprobe.exe, C:\ffmpeg\bin\ffprobe.exe, or PATH) and skipped when not.
/// </summary>
public sealed class FfprobeMediaProbe(string ffprobePath) : IMediaProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public string Path { get; } = ffprobePath;

    /// <summary>A probe if ffprobe can be found, else null.</summary>
    public static FfprobeMediaProbe? TryCreate()
    {
        var found = Locate();
        return found is null ? null : new FfprobeMediaProbe(found);
    }

    public static string? Locate()
    {
        string[] fixedPaths = [Media.FfmpegLocator.BundledPath("ffprobe.exe"), @"C:\ffmpeg\ffprobe.exe", @"C:\ffmpeg\bin\ffprobe.exe"];
        foreach (var candidate in fixedPaths)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = System.IO.Path.Combine(dir.Trim('"'), "ffprobe.exe");
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

    public async Task<MediaProbeResult?> ProbeAsync(string url, CancellationToken ct)
    {
        var info = new ProcessStartInfo(Path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
                 {
                     "-v", "error", "-select_streams", "v:0",
                     "-show_entries", "stream=width,height,codec_name", "-show_entries", "format=duration",
                     "-of", "json", url,
                 })
        {
            info.ArgumentList.Add(arg);
        }

        // ffmpeg on this machine is a GnuTLS build with its own trust store; point it at the
        // same bundle mpv uses so Apple-hosted links verify too.
        if (url.StartsWith("https", StringComparison.OrdinalIgnoreCase) && CaBundle.Ensure() is { } bundle)
        {
            info.ArgumentList.Insert(0, bundle);
            info.ArgumentList.Insert(0, "-ca_file");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw;
            }

            return Parse(await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"ffprobe: {url} failed: {ex.Message}");
            return null;
        }
    }

    public static MediaProbeResult? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var stream = doc.RootElement.GetProperty("streams")[0];
            var width = stream.GetProperty("width").GetInt32();
            var height = stream.GetProperty("height").GetInt32();
            var codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() : null;

            double? duration = null;
            if (doc.RootElement.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var d) &&
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                duration = seconds;
            }

            return new MediaProbeResult(width, height, duration, codec);
        }
        catch
        {
            return null;
        }
    }
}
