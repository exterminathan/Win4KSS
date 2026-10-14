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

        // Wrong-time last; matching and unknown keep their (here: source) order together.
        Assert.Equal(["day1", "unknown1", "day2", "night1", "night2"], order.Select(c => c.Id));
    }

    [Fact]
    public void The_few_clips_labelled_for_now_do_not_open_every_session()
    {
        // The real catalogue: 4 of ~139 rotation clips say "Day". Promoting them made one of the
        // same four open every launch, however many times Start now was pressed.
        var clips = Enumerable.Range(0, 135).Select(i => Make("u" + i))
            .Concat(Enumerable.Range(0, 4).Select(i => Make("day" + i, TimeOfDay.Day)))
            .ToList();
        var settings = new AppSettings { Shuffle = true, UseTimeOfDay = true };

        var dayFirst = Enumerable.Range(0, 200)
            .Count(seed => PlaylistEngine.BuildOrder(clips, settings, Midday, new Random(seed))[0].TimeOfDay == TimeOfDay.Day);
        var distinctOpeners = Enumerable.Range(0, 200)
            .Select(seed => PlaylistEngine.BuildOrder(clips, settings, Midday, new Random(seed))[0].Id)
            .Distinct().Count();

        // Fair odds are 4/139 ≈ 3%; the old ordering made it 100%.
        Assert.True(dayFirst < 30, $"a Day clip opened {dayFirst} of 200 sessions");
        Assert.True(distinctOpeners > 50, $"only {distinctOpeners} different clips opened 200 sessions");
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

        Assert.Equal(["unknown1", "night1", "day1"], order.Select(c => c.Id));
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
