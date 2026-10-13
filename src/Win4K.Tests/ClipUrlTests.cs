using Win4K.Core;

namespace Win4K.Tests;

/// <summary>
/// The archive-org streaming bug in one place: what string a clip hands to mpv.
/// </summary>
public class ClipUrlTests
{
    private const string Escaped = "https://archive.org/download/Fire7345s4kRes/Fire%2066%20-%2045s%20-%204k%20res.mp4";

    private static Clip Remote(string url) =>
        new("ia:x", "x", "Test", TimeOfDay.Unknown, new Uri(url), false);

    [Fact]
    public void Playable_url_keeps_percent_escapes()
    {
        Assert.Equal(Escaped, Remote(Escaped).PlayableUrl);
    }

    [Fact]
    public void Uri_ToString_is_the_trap_this_avoids()
    {
        // Documents why PlayableUrl exists: ToString() unescapes, and libcurl rejects the
        // resulting space with "URL using bad/illegal format" (mpv error -13).
        Assert.Contains(' ', new Uri(Escaped).ToString());
        Assert.DoesNotContain(' ', Remote(Escaped).PlayableUrl);
    }

    [Fact]
    public void Plain_urls_pass_through_unchanged()
    {
        const string apple = "https://sylvan.apple.com/Aerials/2x/Videos/SE_A016_C009_SDR_4K_HEVC.mov";
        Assert.Equal(apple, Remote(apple).PlayableUrl);
    }

    [Fact]
    public void Local_files_play_by_path_including_spaces()
    {
        var path = Path.Combine(Path.GetTempPath(), "My Clips", "beach 01.mp4");
        var clip = new Clip(path, "beach", "Local", TimeOfDay.Unknown, new Uri(path), IsLocal: true);

        Assert.Equal(path, clip.PlayableUrl);
    }

    [Fact]
    public async Task Every_shipped_archive_url_survives_as_a_space_free_string()
    {
        // 1006 of 1060 archive-org URLs contain %20; all of them must reach mpv escaped.
        var clips = await new RegistryCatalogSource("archive-org").GetClipsAsync();

        Assert.Contains(clips, c => c.PlayableUrl.Contains("%20"));
        Assert.All(clips, c => Assert.DoesNotContain(' ', c.PlayableUrl));
    }
}
