using Win4K.ScreenSaver;

namespace Win4K.Tests;

/// <summary>The Start now marker: when it was written, and the clip "Play now" asked for.</summary>
public class LaunchContextTests
{
    private static readonly DateTime When = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_plain_start_now_names_no_clip()
    {
        Assert.Null(LaunchContext.ParseStartWith(LaunchContext.Format(When, null)));
    }

    [Fact]
    public void Play_now_round_trips_the_clip_key()
    {
        const string key = "ia:pixabay-18534/video 18534_large.mp4";
        Assert.Equal(key, LaunchContext.ParseStartWith(LaunchContext.Format(When, key)));
    }

    [Fact]
    public void A_marker_from_an_older_version_still_reads_as_no_clip()
    {
        Assert.Null(LaunchContext.ParseStartWith(When.ToString("o")));
    }
}
