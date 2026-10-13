using Win4K.Core;
using Win4K.Core.Playback;

namespace Win4K.Tests;

public class ClipHealthTests : IDisposable
{
    private readonly string _dir;
    private readonly string _settingsPath;

    public ClipHealthTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "win4k-health-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _settingsPath = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static Clip Make(string id) =>
        new(id, id, "Test", TimeOfDay.Unknown, new Uri($"https://example.test/{id}.mov"), false);

    /// <summary>A fresh ClipHealth is a fresh session, backed by the same settings file.</summary>
    private ClipHealth NewSession() =>
        new(() => AppSettings.Load(_settingsPath), s => s.Save(_settingsPath));

    [Fact]
    public void A_clip_is_counted_at_most_once_per_session()
    {
        var session = NewSession();

        Assert.Equal(1, session.RecordSessionFailure(Make("a")));
        Assert.Null(session.RecordSessionFailure(Make("a")));

        Assert.Equal(1, AppSettings.Load(_settingsPath).FailedClips["a"]);
    }

    [Fact]
    public void Three_separate_sessions_make_a_clip_unhealthy_and_it_persists()
    {
        for (var i = 0; i < ClipHealth.UnhealthyAfterSessions; i++)
        {
            Assert.False(ClipHealth.IsUnhealthy(AppSettings.Load(_settingsPath), "a"));
            NewSession().RecordSessionFailure(Make("a"));
        }

        var reloaded = AppSettings.Load(_settingsPath);
        Assert.Equal(3, reloaded.FailedClips["a"]);
        Assert.True(ClipHealth.IsUnhealthy(reloaded, "a"));
        Assert.False(ClipHealth.IsUnhealthy(reloaded, "b"));
    }

    [Fact]
    public void Recording_a_failure_preserves_other_settings()
    {
        new AppSettings { CacheBudgetGb = 42, Shuffle = false }.Save(_settingsPath);

        NewSession().RecordSessionFailure(Make("a"));

        var reloaded = AppSettings.Load(_settingsPath);
        Assert.Equal(42, reloaded.CacheBudgetGb);
        Assert.False(reloaded.Shuffle);
    }

    [Fact]
    public void Settings_without_failed_clips_load_with_an_empty_map()
    {
        // Every settings.json written before this field existed.
        File.WriteAllText(_settingsPath, """{ "version": 1, "shuffle": true }""");

        var settings = AppSettings.Load(_settingsPath);

        Assert.NotNull(settings.FailedClips);
        Assert.Empty(settings.FailedClips);
    }

    [Fact]
    public void The_playlist_reports_a_benched_clip_once()
    {
        // End to end with the engine: a clip that fails twice is reported once, and the
        // session store turns that into one persisted count.
        var mpv = new Fakes.FakeMpvPlayer { Fails = url => url.Contains("/a.mov") };
        var clock = new Fakes.ManualTimeProvider();
        using var engine = new PlaylistEngine(mpv, clock, autoTick: false);
        var session = NewSession();
        engine.ClipFailedForSession += (_, c) => session.RecordSessionFailure(c);

        engine.Start(new[] { "a", "b", "c", "d" }.Select(Make).ToList(),new AppSettings { Shuffle = false, UseTimeOfDay = false });
        mpv.Pump();
        clock.Advance(PlaylistEngine.RetryDelay);
        for (var i = 0; i < 6; i++)
            mpv.FinishCurrent();

        Assert.Equal(1, AppSettings.Load(_settingsPath).FailedClips["a"]);
    }
}
