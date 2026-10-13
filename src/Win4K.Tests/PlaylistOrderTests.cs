using Win4K.Core;
using Win4K.Core.Playback;

namespace Win4K.Tests;

public class PlaylistOrderTests
{
    private static Clip Make(string id, TimeOfDay tod = TimeOfDay.Unknown, string category = "Cities") =>
        new(id, $"Clip {id}", category, tod, new Uri($"https://example.test/{id}.mov"), false);

    private static readonly DateTime Midday = new(2026, 8, 12, 13, 0, 0);
    private static readonly DateTime Midnight = new(2026, 8, 12, 23, 0, 0);

    [Fact]
    public void Empty_input_yields_empty_order()
    {
        Assert.Empty(PlaylistEngine.BuildOrder([], new AppSettings(), Midday));
    }

    [Fact]
    public void Every_clip_appears_exactly_once()
    {
        var clips = Enumerable.Range(0, 50).Select(i => Make(i.ToString())).ToList();
        var order = PlaylistEngine.BuildOrder(clips, new AppSettings(), Midday, new Random(1));

        Assert.Equal(clips.Count, order.Count);
        Assert.Equal(clips.Select(c => c.Id).OrderBy(x => x), order.Select(c => c.Id).OrderBy(x => x));
    }

    [Fact]
    public void Shuffle_actually_reorders()
    {
        var clips = Enumerable.Range(0, 40).Select(i => Make(i.ToString())).ToList();
        var settings = new AppSettings { Shuffle = true, UseTimeOfDay = false };

        var order = PlaylistEngine.BuildOrder(clips, settings, Midday, new Random(7));

        Assert.NotEqual(clips.Select(c => c.Id), order.Select(c => c.Id));
    }

    [Fact]
    public void Shuffle_off_preserves_source_order()
    {
        var clips = Enumerable.Range(0, 10).Select(i => Make(i.ToString())).ToList();
        var settings = new AppSettings { Shuffle = false, UseTimeOfDay = false };

        var order = PlaylistEngine.BuildOrder(clips, settings, Midday, new Random(7));

        Assert.Equal(clips.Select(c => c.Id), order.Select(c => c.Id));
    }

    [Fact]
    public void Daytime_sinks_clips_explicitly_marked_night()
    {
        var clips = new List<Clip>
        {
            Make("night1", TimeOfDay.Night),
            Make("day1", TimeOfDay.Day),
            Make("unknown1"),
            Make("night2", TimeOfDay.Night),
            Make("day2", TimeOfDay.Day),
        };

        var settings = new AppSettings { Shuffle = false, UseTimeOfDay = true };
        var order = PlaylistEngine.BuildOrder(clips, settings, Midday);

        // Matching first, unknown in the middle, wrong-time last.
        Assert.Equal(["day1", "day2", "unknown1", "night1", "night2"], order.Select(c => c.Id));
    }

    [Fact]
    public void Night_inverts_the_same_grouping()
    {
        var clips = new List<Clip>
        {
            Make("day1", TimeOfDay.Day),
            Make("unknown1"),
            Make("night1", TimeOfDay.Night),
        };

        var settings = new AppSettings { Shuffle = false, UseTimeOfDay = true };
        var order = PlaylistEngine.BuildOrder(clips, settings, Midnight);

        Assert.Equal(["night1", "unknown1", "day1"], order.Select(c => c.Id));
    }

    [Fact]
    public void Time_of_day_sort_is_stable_so_the_shuffle_survives_inside_each_group()
    {
        // The whole reason for using a stable sort: clips stay randomised within their
        // time-of-day group instead of collapsing back to manifest order.
        var clips = Enumerable.Range(0, 60).Select(i => Make(i.ToString())).ToList();
        var settings = new AppSettings { Shuffle = true, UseTimeOfDay = true };

        var order = PlaylistEngine.BuildOrder(clips, settings, Midday, new Random(3));

        // All are Unknown here, so grouping is a no-op and the shuffle must show through.
        Assert.NotEqual(clips.Select(c => c.Id), order.Select(c => c.Id));
        Assert.Equal(60, order.Count);
    }

    [Fact]
    public void Order_plays_every_clip_it_is_given()
    {
        // Settings v2: choosing clips is Rotation.Select's job (see RotationTests); the order
        // never drops any, and there is no selection list left to filter by.
        var clips = Enumerable.Range(0, 10).Select(i => Make(i.ToString())).ToList();
        var settings = new AppSettings { Shuffle = false, UseTimeOfDay = false };

        var order = PlaylistEngine.BuildOrder(clips, settings, Midday);

        Assert.Equal(clips.Select(c => c.Id), order.Select(c => c.Id));
    }

    [Theory]
    [InlineData(5, TimeOfDay.Night)]
    [InlineData(6, TimeOfDay.Day)]
    [InlineData(12, TimeOfDay.Day)]
    [InlineData(18, TimeOfDay.Day)]
    [InlineData(19, TimeOfDay.Night)]
    [InlineData(23, TimeOfDay.Night)]
    public void Day_runs_from_six_to_nineteen(int hour, TimeOfDay expected)
    {
        Assert.Equal(expected, ClipHeuristics.Now(new DateTime(2026, 8, 12, hour, 0, 0)));
    }
}
