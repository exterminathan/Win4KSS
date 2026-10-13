using System.Collections.Concurrent;
using Win4K.Core.Media;

namespace Win4K.Tests.Fakes;

/// <summary>
/// Stands in for ffmpeg: records every command line and, unless told otherwise, "succeeds" by
/// writing <see cref="OutputBytes"/> bytes to the output path (ffmpeg's last argument).
/// </summary>
internal sealed class FakeFfmpeg : IProcessRunner
{
    public ConcurrentQueue<IReadOnlyList<string>> Calls { get; } = new();

    public int OutputBytes { get; set; } = 1000;

    /// <summary>Decides per call: return null to succeed normally, or a result to return without writing output.</summary>
    public Func<IReadOnlyList<string>, ProcessResult?>? Behaviour { get; set; }

    /// <summary>When set, every call waits on the token until cancelled (to test killing a running ffmpeg).</summary>
    public bool BlockUntilCancelled { get; set; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Enqueue(arguments);
        Started.TrySetResult();

        if (BlockUntilCancelled)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }

        if (Behaviour?.Invoke(arguments) is { } result)
            return result;

        await File.WriteAllBytesAsync(arguments[^1], new byte[OutputBytes], ct);
        return new ProcessResult(0, "", false);
    }

    /// <summary>The value after "-i" in a command line.</summary>
    public static string InputOf(IReadOnlyList<string> args) => args[IndexOf(args, "-i") + 1];

    public static int IndexOf(IReadOnlyList<string> args, string value)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == value)
                return i;
        }
        return -1;
    }
}
