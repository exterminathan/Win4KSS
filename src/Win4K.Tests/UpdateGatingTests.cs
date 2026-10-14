using Win4K.ScreenSaver;

namespace Win4K.Tests;

/// <summary>
/// Update checks (a network call, a background timer, or a dialog about one) must never happen
/// outside the clip browser — see CLAUDE.md "Never during the saver". This tests the chokepoint
/// function directly, the same way <see cref="NativeDependenciesTests"/> tests
/// <see cref="Program.GateOnNatives"/> directly rather than trusting the call sites.
/// </summary>
public class UpdateGatingTests
{
    [Fact]
    public void Only_configure_allows_an_update_check()
    {
        Assert.True(Program.AllowsUpdateCheck(Program.Verb.Configure));
    }

    // Verbs are compared by name: Program.Verb is internal, and an internal type cannot appear
    // in the signature of a public xunit theory (same workaround as ArgumentParsingTests).
    [Theory]
    [InlineData("FullScreen")]      // /s
    [InlineData("Preview")]         // /p -- the Settings dialog's preview child
    [InlineData("Window")]          // /w -- the saver itself, windowed
    [InlineData("StartNow")]        // /now
    [InlineData("ExportSettings")]
    [InlineData("ImportSettings")]
    [InlineData("Unknown")]
    public void Every_other_verb_never_allows_an_update_check(string verbName)
    {
        Assert.False(Program.AllowsUpdateCheck(Enum.Parse<Program.Verb>(verbName)));
    }

    [Fact]
    public void Every_verb_is_covered_by_the_gate_one_way_or_the_other()
    {
        // Exhaustive: whatever verbs Program.Verb grows in the future, this fails loudly
        // instead of a new one silently defaulting to "allowed" or being forgotten entirely.
        foreach (var verb in Enum.GetValues<Program.Verb>())
        {
            var allowed = Program.AllowsUpdateCheck(verb);
            Assert.Equal(verb == Program.Verb.Configure, allowed);
        }
    }
}
