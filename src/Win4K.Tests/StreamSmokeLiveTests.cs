using System.Diagnostics;
using Win4K.Core;
using Win4K.Core.Apple;
using Win4K.Core.Playback;
using Xunit.Abstractions;

namespace Win4K.Tests;

/// <summary>
/// The "as well as Apple" gate: real libmpv, headless (vo=null), streaming real clips from
/// each source straight from the origin — never the cache — and requiring every one to reach
/// file-loaded within <see cref="LoadBudget"/>. Inert unless WIN4K_LIVE_TESTS=1:
///
///     $env:WIN4K_LIVE_TESTS="1"; dotnet test --filter FullyQualifiedName~StreamSmokeLiveTests
///
/// Before the escaping fix every archive-org clip failed this with mpv error -13.
/// </summary>
public class StreamSmokeLiveTests(ITestOutputHelper output)
{
    private static bool LiveEnabled => Environment.GetEnvironmentVariable("WIN4K_LIVE_TESTS") == "1";

    private static readonly TimeSpan LoadBudget = TimeSpan.FromSeconds(15);
    /// <summary>5 by default; WIN4K_SMOKE_COUNT raises it for a wider one-off sweep.</summary>
    private static int ClipsPerSource =>
        int.TryParse(Environment.GetEnvironmentVariable("WIN4K_SMOKE_COUNT"), out var n) && n > 0 ? n : 5;

    [Theory]
    [InlineData("apple")]
    [InlineData("archive-org")]
    [InlineData("nasa")]
    public async Task Clips_stream_and_load_within_budget(string source)
    {
        if (!LiveEnabled) return;

        var clips = await PickAsync(source);
        Assert.NotEmpty(clips);

        if (source == "archive-org")
            Assert.Contains(clips, c => c.PlayableUrl.Contains("%20"));

        using var player = new MpvPlayer();
        player.InitializeHeadless();

        var failures = new List<string>();
        foreach (var clip in clips)
        {
            var (ok, detail, elapsed) = await LoadOnceAsync(player, clip.PlayableUrl);
            output.WriteLine($"[{source}] {(ok ? "OK  " : "FAIL")} {elapsed.TotalSeconds,5:0.0}s  {clip.Title}  {detail}");
            output.WriteLine($"         {clip.PlayableUrl}");
            if (!ok)
                failures.Add($"{clip.Title}: {detail}");
        }

        output.WriteLine($"mpv.log: {Path.Combine(AppPaths.Root, "mpv.log")}");
        Assert.True(failures.Count == 0,
            $"{failures.Count}/{clips.Count} {source} clips failed to load:\n  " + string.Join("\n  ", failures));
    }

    private static async Task<IReadOnlyList<Clip>> PickAsync(string source)
    {
        IReadOnlyList<Clip> all = source == "apple"
            ? await new AppleAerialSource(new AppleManifestClient(), () => VideoQuality.Uhd4KSdr).GetClipsAsync()
            : await new RegistryCatalogSource(source).GetClipsAsync();

        // A fixed seed: the same clips each run, so a regression is comparable run to run,
        // but spread across the catalogue rather than its first few entries.
        var rng = new Random(20261012);
        return all.Where(c => !c.IsLocal)
                  .OrderBy(_ => rng.Next())
                  .Take(ClipsPerSource)
                  .ToList();
    }

    /// <summary>Replace-loads one URL and waits for file-loaded, a failure, or the budget.</summary>
    private static async Task<(bool Ok, string Detail, TimeSpan Elapsed)> LoadOnceAsync(MpvPlayer player, string url)
    {
        var result = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        long entry = -2;
        long started = -1;

        // Events can beat LoadFile's return, so record them by entry id and judge whenever
        // either side moves.
        var loaded = new System.Collections.Concurrent.ConcurrentDictionary<long, bool>();
        var ended = new System.Collections.Concurrent.ConcurrentDictionary<long, string>();

        void Evaluate()
        {
            var id = Interlocked.Read(ref entry);
            if (loaded.ContainsKey(id))
                result.TrySetResult((true, "file-loaded"));
            else if (ended.TryGetValue(id, out var why))
                result.TrySetResult((false, why));
        }

        void OnStart(object? s, StartFileEventArgs e) => Interlocked.Exchange(ref started, e.PlaylistEntryId);

        void OnLoaded(object? s, EventArgs e)
        {
            loaded[Interlocked.Read(ref started)] = true;
            Evaluate();
        }

        void OnEnd(object? s, EndFileEventArgs e)
        {
            if (e.Reason == MpvEndFileReasonKind.Stopped) return;
            ended[e.PlaylistEntryId] = $"end-file {e.Reason}, error {e.Error}";
            Evaluate();
        }

        player.StartFile += OnStart;
        player.FileLoaded += OnLoaded;
        player.EndFile += OnEnd;
        var clock = Stopwatch.StartNew();
        try
        {
            Interlocked.Exchange(ref entry, player.LoadFile(url, LoadFileMode.Replace));
            if (Interlocked.Read(ref entry) < 0)
                return (false, "loadfile refused / no entry id", clock.Elapsed);
            Evaluate();

            var winner = await Task.WhenAny(result.Task, Task.Delay(LoadBudget));
            var (ok, detail) = winner == result.Task
                ? await result.Task
                : (false, $"no file-loaded within {LoadBudget.TotalSeconds:0}s");
            return (ok, detail, clock.Elapsed);
        }
        finally
        {
            player.StartFile -= OnStart;
            player.FileLoaded -= OnLoaded;
            player.EndFile -= OnEnd;
            player.Stop();
        }
    }
}
