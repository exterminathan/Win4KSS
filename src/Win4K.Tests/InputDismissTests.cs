using Win4K.ScreenSaver;

namespace Win4K.Tests;

/// <summary>
/// The dismissal rule is the single most delicate part of a screen saver, and it cannot be
/// exercised end-to-end from an automated session: a non-interactive desktop will not let a
/// process move the physical pointer, and GetCursorPos reports 0,0 there. So the decision
/// itself is a pure function and is tested here; the polling around it needs a human with a
/// real mouse.
///
/// The behaviour being protected: cdima/aerial exited on any movement over 5px with no grace
/// period, so a jittery pointer, a drifting trackpoint, or a stray event delivered as the
/// window appeared would kill the saver instantly.
/// </summary>
public class InputDismissTests
{
    private static readonly Point Origin = new(500, 400);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(10, 10)]     // exactly at the threshold is not past it
    [InlineData(-10, -10)]
    [InlineData(7, -9)]
    public void Small_jitter_does_not_dismiss(int dx, int dy)
    {
        var moved = new Point(Origin.X + dx, Origin.Y + dy);
        Assert.False(InputDismissWatcher.HasMovedEnough(Origin, moved));
    }

    [Theory]
    [InlineData(11, 0)]
    [InlineData(0, 11)]
    [InlineData(-11, 0)]
    [InlineData(0, -11)]
    [InlineData(200, 140)]
    [InlineData(-300, 250)]
    public void Deliberate_movement_dismisses(int dx, int dy)
    {
        var moved = new Point(Origin.X + dx, Origin.Y + dy);
        Assert.True(InputDismissWatcher.HasMovedEnough(Origin, moved));
    }

    [Fact]
    public void Threshold_is_larger_than_the_original_projects()
    {
        // cdima/aerial used 5px. 10 leaves room for pointer drift without feeling sticky.
        Assert.Equal(10, InputDismissWatcher.MovementThreshold);
    }

    [Fact]
    public void Threshold_is_configurable_per_call()
    {
        var moved = new Point(Origin.X + 20, Origin.Y);

        Assert.True(InputDismissWatcher.HasMovedEnough(Origin, moved, threshold: 10));
        Assert.False(InputDismissWatcher.HasMovedEnough(Origin, moved, threshold: 50));
    }

    [Fact]
    public void Manual_launch_gets_a_longer_grace_than_an_idle_launch()
    {
        // A saver started from a keyboard shortcut is racing the key release; an idle-timer
        // launch is not, so it can arm sooner.
        Assert.True(LaunchContext.ManualGraceMs > LaunchContext.SystemGraceMs);
        Assert.Equal(1000, LaunchContext.SystemGraceMs);
        Assert.Equal(1500, LaunchContext.ManualGraceMs);
    }
}
