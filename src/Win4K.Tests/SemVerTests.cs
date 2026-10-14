using Win4K.Core.Updates;

namespace Win4K.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("1.2.3.0", 1, 2, 3)] // .NET AssemblyVersion's 4th ("revision") component
    [InlineData("1.2.3.45", 1, 2, 3)]
    [InlineData("1.2.3+abc123", 1, 2, 3)] // build metadata never affects precedence
    public void Parses_the_numeric_parts(string text, int major, int minor, int patch)
    {
        Assert.True(SemVer.TryParse(text, out var version));
        Assert.Equal(new SemVer(major, minor, patch, null), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not-a-version")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.x.3")]
    [InlineData("-1.2.3")]
    public void Rejects_what_it_cannot_parse(string? text)
    {
        Assert.False(SemVer.TryParse(text, out _));
    }

    [Fact]
    public void The_pairing_the_spec_calls_out_compares_correctly()
    {
        Assert.True(SemVer.TryParse("1.1.0", out var older));
        Assert.True(SemVer.TryParse("1.10.0", out var newer));

        Assert.True(newer > older);
        Assert.True(older < newer);
        Assert.True(newer.CompareTo(older) > 0);
    }

    [Fact]
    public void Numeric_comparison_is_not_lexicographic()
    {
        // The pair that actually tells numeric and lexicographic comparison apart: "9" > "1" as
        // text (so a naive string compare would call 1.9.0 the newer one), but 9 < 10 as a number.
        Assert.True(SemVer.TryParse("1.9.0", out var nine));
        Assert.True(SemVer.TryParse("1.10.0", out var ten));

        Assert.True(ten > nine, "1.10.0 must compare as newer than 1.9.0 numerically");
        Assert.True(string.CompareOrdinal("1.9.0", "1.10.0") > 0, "the lexicographic trap this guards against");
    }

    [Theory]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.9.9", "2.0.0", false)]
    [InlineData("1.2.0", "1.1.9", true)]
    [InlineData("1.1.0", "1.1.0", false)]
    [InlineData("1.1.0", "1.1.0.0", false)] // assembly-version revision ignored
    public void Is_newer_than(string a, string b, bool expected)
    {
        Assert.True(SemVer.TryParse(a, out var va));
        Assert.True(SemVer.TryParse(b, out var vb));
        Assert.Equal(expected, va > vb);
    }

    [Fact]
    public void A_release_outranks_its_own_prerelease()
    {
        Assert.True(SemVer.TryParse("1.2.0", out var release));
        Assert.True(SemVer.TryParse("1.2.0-beta.1", out var prerelease));

        Assert.True(release > prerelease);
    }

    [Theory]
    [InlineData(1, 2, 3, null, "1.2.3")]
    [InlineData(1, 2, 3, "beta.1", "1.2.3-beta.1")]
    public void ToString_round_trips_what_it_shows(int major, int minor, int patch, string? pre, string expected)
    {
        Assert.Equal(expected, new SemVer(major, minor, patch, pre).ToString());
    }
}
