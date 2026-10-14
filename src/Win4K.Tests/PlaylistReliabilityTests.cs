using Win4K.Core;
using Win4K.Core.Playback;
using Win4K.Tests.Fakes;

namespace Win4K.Tests;

/// <summary>
/// The playlist under the conditions that used to black out the saver: entries failing in
/// about a millisecond, mpv going idle, end-file events that do not belong to the current
/// clip, and streams that simply stop. Driven through <see cref="FakeMpvPlayer"/> with a
/// manual clock, so nothing here sleeps.
/// </summary>
public class PlaylistReliabilityTests : IDisposable
{
    private readonly FakeMpvPlayer _mpv = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly PlaylistEngine _engine;

    private static readonly AppSettings InOrder = new() { Shuffle = false, UseTimeOfDay = false };

    public PlaylistReliabilityTests()
    {
        _engine = new PlaylistEngine(_mpv, _clock, autoTick: false);
    }

    public void Dispose()
    {
        _engine.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Clip Make(string id) =>
        new(id, $"Clip {id}", "Test", TimeOfDay.Unknown, new Uri($"https://example.test/{id}.mov"), false);

    private static List<Clip> Clips(params string[] ids) => ids.Select(Make).ToList();

    private void Start(List<Clip> clips)
    {
        _engine.Start(clips, InOrder);
        _mpv.Pump();
    }

    private static string UrlOf(string id) => $"https://example.test/{id}.mov";

    // ---- queueing ---------------------------------------------------------------------------

    [Fact]
    public void Keeps_two_entries_queued_behind_the_current_one()
    {
        Start(Clips("a", "b", "c", "d", "e"));

        Assert.Equal(3, _mpv.Loads.Count);
        Assert.Equal(LoadFileMode.Replace, _mpv.Loads[0].Mode);
        Assert.All(_mpv.Loads.Skip(1), l => Assert.Equal(LoadFileMode.AppendPlay, l.Mode));
        Assert.Equal("a", _engine.Current?.Id);
    }

    [Fact]
    public void A_natural_end_advances_and_tops_the_queue_back_up()
    {
        Start(Clips("a", "b", "c", "d", "e"));

        _mpv.FinishCurrent();

        Assert.Equal("b", _engine.Current?.Id);
        Assert.Equal(UrlOf("d"), _mpv.Loads[^1].Url);
        Assert.Equal(4, _mpv.Loads.Count);
    }

    [Fact]
    public void Clip_started_fires_only_once_a_clip_has_actually_loaded()
    {
        var started = new List<string>();
        _engine.ClipStarted += (_, c) => started.Add(c.Id);
        _mpv.Fails = url => url == UrlOf("a");

        Start(Clips("a", "b", "c"));

        Assert.Equal(["b"], started);
    }

    // ---- the black-screen bug ---------------------------------------------------------------

    [Fact]
    public void An_instant_failure_moves_straight_on_to_the_next_clip()
    {
        // The original stall: the first entry failed in ~1 ms, before anything was appended
        // behind it, and mpv sat idle until someone dismissed the saver.
        _mpv.Fails = url => url == UrlOf("a");

        Start(Clips("a", "b", "c", "d", "e"));

        Assert.Equal("b", _engine.Current?.Id);
        Assert.Equal(UrlOf("b"), _mpv.CurrentUrl);
        Assert.False(_mpv.IsIdle);
    }

    [Fact]
    public void Several_instant_failures_in_a_row_still_end_up_playing()
    {
        _mpv.Fails = url => url is "https://example.test/a.mov" or "https://example.test/b.mov";

        Start(Clips("a", "b", "c", "d", "e"));

        Assert.Equal("c", _engine.Current?.Id);
        Assert.False(_mpv.IsIdle);
    }

    [Fact]
    public void Idle_while_running_restarts_playback_immediately()
    {
        Start(Clips("a", "b", "c", "d", "e"));
        var loadsBefore = _mpv.Loads.Count;

        _mpv.ForceIdle();

        Assert.False(_mpv.IsIdle);
        Assert.NotNull(_engine.Current);
        Assert.Contains(_mpv.Loads.Skip(loadsBefore), l => l.Mode == LoadFileMode.Replace);
    }

    [Fact]
    public void Idle_reported_before_the_first_clip_starts_is_ignored()
    {
        // mpv reports idle at startup; that stale event can land after Start's replace.
        _engine.Start(Clips("a", "b", "c"), InOrder);
        var loads = _mpv.Loads.Count;

        // Delivered before the core has started anything.
        _mpv.ForceIdleWithoutClearing();

        Assert.Equal(loads, _mpv.Loads.Count);
    }

    [Fact]
    public void Repeated_stalls_back_off_instead_of_spinning()
    {
        // Everything fails: the network is gone and nothing is cached.
        _mpv.Fails = _ => true;
        Start(Clips("a", "b", "c", "d", "e", "f"));

        var loadsAfterFirstBurst = _mpv.Loads.Count;
        Assert.True(_mpv.IsIdle, "with every entry failing, mpv should have drained to idle");

        // Not yet due.
        _engine.Tick();
        Assert.Equal(loadsAfterFirstBurst, _mpv.Loads.Count);

        // The network comes back; once the backoff elapses, playback resumes.
        _mpv.Fails = _ => false;
        _clock.Advance(TimeSpan.FromSeconds(31));
        _engine.Tick();
        _mpv.Pump();

        Assert.True(_mpv.Loads.Count > loadsAfterFirstBurst);
        Assert.NotNull(_engine.Current);
        Assert.False(_mpv.IsIdle);
        Assert.Equal(0, _engine.ConsecutiveFailures);
    }

    // ---- entry ids ------------------------------------------------------------------------------

    [Fact]
    public void End_file_for_an_unknown_entry_is_ignored()
    {
        Start(Clips("a", "b", "c", "d"));
        var loads = _mpv.Loads.Count;

        _mpv.Inject(new EndFileEventArgs(MpvEndFileReasonKind.Eof, 0, playlistEntryId: 999));
        _mpv.Inject(new EndFileEventArgs(MpvEndFileReasonKind.Error, -13, playlistEntryId: 998));

        Assert.Equal("a", _engine.Current?.Id);
        Assert.Equal(loads, _mpv.Loads.Count);
        Assert.Equal(0, _engine.ConsecutiveFailures);
    }

    [Fact]
    public void The_stopped_end_of_a_skipped_clip_does_not_move_the_playlist_again()
    {
        // The old engine advanced its index on every end-file it saw, so a skip followed by
        // the replaced clip's "stopped" event could drift it one place out of step.
        Start(Clips("a", "b", "c", "d", "e", "f"));

        _engine.Next();
        _mpv.Pump();

        Assert.Equal("d", _engine.Current?.Id);
        Assert.Equal(UrlOf("d"), _mpv.CurrentUrl);
    }

    [Fact]
    public void A_failure_is_charged_to_the_entry_that_failed_not_the_current_clip()
    {
        Start(Clips("a", "b", "c", "d"));
        var b = _mpv.Loads.Single(l => l.Url == UrlOf("b"));

        _mpv.Inject(new EndFileEventArgs(MpvEndFileReasonKind.Error, -13, b.Id));

        Assert.Equal("a", _engine.Current?.Id);
        Assert.False(_engine.IsBenched(Make("a")));
        Assert.Equal(1, _engine.ConsecutiveFailures);
    }

    [Fact]
    public void Starting_an_entry_further_down_the_queue_resynchronises()
    {
        Start(Clips("a", "b", "c", "d"));
        var c = _mpv.Loads.Single(l => l.Url == UrlOf("c"));

        _mpv.InjectStart(c.Id);

        Assert.Equal("c", _engine.Current?.Id);
    }

    // ---- watchdog -------------------------------------------------------------------------------

    [Fact]
    public void Watchdog_skips_a_stream_that_never_loads()
    {
        _mpv.Hangs = url => url == UrlOf("a");
        Start(Clips("a", "b", "c", "d"));

        _clock.Advance(TimeSpan.FromSeconds(19));
        _engine.Tick();
        Assert.Equal(UrlOf("a"), _mpv.CurrentUrl);

        _clock.Advance(TimeSpan.FromSeconds(2));
        _engine.Tick();
        _mpv.Pump();

        Assert.NotEqual(UrlOf("a"), _mpv.CurrentUrl);
        Assert.NotNull(_engine.Current);
        Assert.Equal(LoadFileMode.Replace, _mpv.Loads.Last(l => l.Mode == LoadFileMode.Replace).Mode);
    }

    [Fact]
    public void Watchdog_skips_playback_that_freezes()
    {
        Start(Clips("a", "b", "c", "d"));

        _mpv.PlaybackTime = 1.0;
        _engine.Tick();
        _clock.Advance(TimeSpan.FromSeconds(15));
        _mpv.PlaybackTime = 16.0;
        _engine.Tick();

        // Progressing: no skip, however long it runs.
        Assert.Equal("a", _engine.Current?.Id);

        // Now frozen on 16.0 for over 20 s.
        _clock.Advance(TimeSpan.FromSeconds(21));
        _engine.Tick();
        _mpv.Pump();

        Assert.NotEqual("a", _engine.Current?.Id);
    }

    [Fact]
    public void Watchdog_leaves_steadily_playing_clips_alone()
    {
        Start(Clips("a", "b", "c"));
        var loads = _mpv.Loads.Count;

        for (var i = 0; i < 30; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(5));
            _mpv.PlaybackTime = i * 5.0;
            _engine.Tick();
        }

        Assert.Equal("a", _engine.Current?.Id);
        Assert.Equal(loads, _mpv.Loads.Count);
    }

    // ---- failure policy -------------------------------------------------------------------------

    [Fact]
    public void A_failed_clip_is_retried_once_after_the_retry_delay()
    {
        var attemptsAtA = 0;
        _mpv.Fails = url => url == UrlOf("a") && attemptsAtA++ == 0;

        Start(Clips("a", "b", "c", "d", "e"));
        Assert.Equal("b", _engine.Current?.Id);

        // Retries do not jump the queue before they are due...
        _mpv.FinishCurrent();
        Assert.DoesNotContain(_mpv.Loads.Skip(1), l => l.Url == UrlOf("a"));

        // ...and are queued next once they are.
        _clock.Advance(PlaylistEngine.RetryDelay);
        _mpv.FinishCurrent();

        Assert.Equal(2, _mpv.Loads.Count(l => l.Url == UrlOf("a")));
        Assert.False(_engine.IsBenched(Make("a")));
    }

    [Fact]
    public void A_second_failure_benches_the_clip_for_the_session()
    {
        var benched = new List<string>();
        _engine.ClipFailedForSession += (_, c) => benched.Add(c.Id);
        _mpv.Fails = url => url == UrlOf("a");

        Start(Clips("a", "b", "c", "d", "e"));
        _clock.Advance(PlaylistEngine.RetryDelay);
        _mpv.FinishCurrent();   // b ends; a (retry) is queued and fails again
        _mpv.FinishCurrent();
        _mpv.FinishCurrent();

        Assert.True(_engine.IsBenched(Make("a")));
        Assert.Equal(["a"], benched);

        // And it is skipped on the next lap of the rotation.
        var loadsOfA = _mpv.Loads.Count(l => l.Url == UrlOf("a"));
        for (var i = 0; i < 6; i++)
            _mpv.FinishCurrent();
        Assert.Equal(loadsOfA, _mpv.Loads.Count(l => l.Url == UrlOf("a")));
    }

    [Fact]
    public void Three_consecutive_failures_switch_to_cached_only_rotation()
    {
        var cached = new HashSet<string> { "e", "f" };
        _engine.IsAvailableOffline = c => cached.Contains(c.Id);
        _engine.ResolveUrl = c => cached.Contains(c.Id) ? $@"C:\cache\{c.Id}.mov" : c.PlayableUrl;
        _mpv.Fails = url => url.StartsWith("https://");

        Start(Clips("a", "b", "c", "d", "e", "f", "g"));

        Assert.True(_engine.IsCachedOnly);
        Assert.All(_engine.Order, c => Assert.Contains(c.Id, cached));
        Assert.NotNull(_engine.Current);
        Assert.Contains(_engine.Current!.Id, cached);
        Assert.StartsWith(@"C:\cache\", _mpv.CurrentUrl);

        // And only cached clips are queued from here on.
        var lastReplace = _mpv.Loads.FindLastIndex(l => l.Mode == LoadFileMode.Replace);
        Assert.All(_mpv.Loads.Skip(lastReplace), l => Assert.StartsWith(@"C:\cache\", l.Url));
    }

    private void LeanOnCache(HashSet<string> cached)
    {
        _engine.IsAvailableOffline = c => cached.Contains(c.Id);
        _engine.ResolveUrl = c => cached.Contains(c.Id) ? $@"C:\cache\{c.Id}.mov" : c.PlayableUrl;
        _mpv.Fails = url => url.StartsWith("https://");
        Start(Clips("a", "b", "c", "d", "e", "f", "g"));
        Assert.True(_engine.IsCachedOnly);
    }

    /// <summary>Moves the clock on as if the clip on screen kept playing, so the watchdog stays quiet.</summary>
    private void PlayFor(TimeSpan span)
    {
        _mpv.PlaybackTime = (_mpv.PlaybackTime ?? 0) + span.TotalSeconds;
        _clock.Advance(span);
        _engine.Tick();
        _mpv.Pump();
    }

    [Fact]
    public void Leaning_on_the_cache_lasts_only_for_the_cooldown_then_every_rotation_clip_is_a_candidate()
    {
        LeanOnCache(["e", "f"]);
        _mpv.Fails = _ => false; // the network is back

        PlayFor(PlaylistEngine.CachedOnlyCooldown);

        Assert.False(_engine.IsCachedOnly);
        Assert.Equal(7, _engine.Order.Count);

        var before = _mpv.Loads.Count;
        for (var i = 0; i < 3; i++)
            _mpv.FinishCurrent();
        Assert.Contains(_mpv.Loads.Skip(before), l => l.Url.StartsWith("https://"));
    }

    [Fact]
    public void If_the_network_is_still_down_the_next_cooldown_is_longer()
    {
        LeanOnCache(["e", "f"]);

        PlayFor(PlaylistEngine.CachedOnlyCooldown);
        Assert.False(_engine.IsCachedOnly);

        for (var i = 0; i < 6 && !_engine.IsCachedOnly; i++)
            _mpv.FinishCurrent();
        Assert.True(_engine.IsCachedOnly);

        PlayFor(PlaylistEngine.CachedOnlyCooldown);
        Assert.True(_engine.IsCachedOnly);

        PlayFor(PlaylistEngine.CachedOnlyCooldown);
        Assert.False(_engine.IsCachedOnly);
    }

    [Fact]
    public void While_leaning_on_the_cache_download_ahead_fetches_uncached_rotation_clips()
    {
        var ahead = new RecordingDownloadAhead();
        _engine.DownloadAhead = ahead;
        var cached = new HashSet<string> { "e", "f" };

        LeanOnCache(cached);

        var latest = ahead.Requests[^1];
        Assert.NotEmpty(latest.Upcoming);
        Assert.All(latest.Upcoming, c => Assert.DoesNotContain(c.Id, cached));
    }

    [Fact]
    public void A_clip_that_lands_on_disk_joins_the_cached_rotation()
    {
        var cached = new HashSet<string> { "e", "f" };
        LeanOnCache(cached);

        cached.Add("g");
        for (var i = 0; i < 4; i++)
            _mpv.FinishCurrent();

        Assert.Contains(_engine.Order, c => c.Id == "g");
        Assert.Contains(_mpv.Loads, l => l.Url == @"C:\cache\g.mov");
    }

    [Fact]
    public void A_success_resets_the_consecutive_failure_count()
    {
        _engine.IsAvailableOffline = c => c.Id == "z";
        _mpv.Fails = url => url == UrlOf("a") || url == UrlOf("b");

        Start(Clips("a", "b", "c", "d", "e", "z"));

        Assert.Equal(0, _engine.ConsecutiveFailures);
        Assert.False(_engine.IsCachedOnly);
        Assert.Equal("c", _engine.Current?.Id);
    }

    [Fact]
    public void Without_anything_cached_failures_keep_streaming_rather_than_going_dark()
    {
        _mpv.Fails = url => url != UrlOf("d");

        Start(Clips("a", "b", "c", "d", "e"));

        Assert.False(_engine.IsCachedOnly);
    }

    // ---- escaping and download-ahead ------------------------------------------------------------

    [Fact]
    public void Default_resolver_hands_mpv_the_escaped_url()
    {
        var clip = new Clip("ia:x", "LA 01", "Test", TimeOfDay.Unknown,
                            new Uri("https://archive.org/download/LA01/LA%2001%20-%204k.mp4"), false);

        Start([clip]);

        Assert.Equal("https://archive.org/download/LA01/LA%2001%20-%204k.mp4", _mpv.Loads[0].Url);
    }

    [Fact]
    public void Download_ahead_is_offered_the_clips_beyond_the_mpv_queue()
    {
        var ahead = new RecordingDownloadAhead();
        _engine.DownloadAhead = ahead;

        Start(Clips("a", "b", "c", "d", "e", "f"));

        // a, b, c are already in mpv; d and e are next to be queued.
        var latest = ahead.Requests[^1];
        Assert.Equal(["d", "e"], latest.Upcoming.Select(c => c.Id));
        Assert.True(latest.Streaming);

        _mpv.FinishCurrent();
        Assert.Equal(["e", "f"], ahead.Requests[^1].Upcoming.Select(c => c.Id));
    }

    [Fact]
    public void Clips_downloaded_ahead_are_queued_from_disk()
    {
        var onDisk = new HashSet<string>();
        _engine.ResolveUrl = c => onDisk.Contains(c.Id) ? $@"C:\cache\{c.Id}.mov" : c.PlayableUrl;
        _engine.DownloadAhead = new InstantDownloadAhead(onDisk);

        Start(Clips("a", "b", "c", "d", "e", "f"));
        _mpv.FinishCurrent();

        // d was requested while a played, so it is queued as a local file.
        Assert.Equal(@"C:\cache\d.mov", _mpv.Loads[^1].Url);
    }

    private sealed class InstantDownloadAhead(HashSet<string> onDisk) : IDownloadAhead
    {
        public void Request(IReadOnlyList<Clip> upcoming, bool currentIsStreaming)
        {
            foreach (var clip in upcoming)
                onDisk.Add(clip.Id);
        }
    }
}
