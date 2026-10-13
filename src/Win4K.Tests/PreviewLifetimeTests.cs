using Win4K.ScreenSaver;

namespace Win4K.Tests;

/// <summary>
/// /p processes used to outlive the Screen Saver Settings thumbnail they were drawn into —
/// several were found alive with parent HWNDs for which IsWindow was false. The form now
/// polls <see cref="PreviewLifetime.ExitReason"/>; these pin down when it must give up.
/// </summary>
public class PreviewLifetimeTests
{
    private static readonly IntPtr Parent = new(0x1234);

    [Fact]
    public void Keeps_running_while_attached_to_a_live_parent()
    {
        Assert.Null(PreviewLifetime.ExitReason(Parent, parentIsWindow: true, actualParent: Parent));
    }

    [Fact]
    public void Exits_when_the_parent_window_is_gone()
    {
        // The leak: parent destroyed, our window somehow still parented to the dead HWND.
        Assert.NotNull(PreviewLifetime.ExitReason(Parent, parentIsWindow: false, actualParent: Parent));
    }

    [Fact]
    public void Exits_when_reparented_away_or_never_attached()
    {
        var desktop = new IntPtr(0x10010);
        Assert.NotNull(PreviewLifetime.ExitReason(Parent, parentIsWindow: true, actualParent: desktop));
        Assert.NotNull(PreviewLifetime.ExitReason(Parent, parentIsWindow: true, actualParent: IntPtr.Zero));
    }

    [Fact]
    public void Exits_without_a_parent_at_all()
    {
        Assert.NotNull(PreviewLifetime.ExitReason(IntPtr.Zero, parentIsWindow: false, actualParent: IntPtr.Zero));
    }

    [Fact]
    public void Polls_about_once_a_second()
    {
        Assert.InRange(PreviewLifetime.PollIntervalMs, 250, 2000);
    }
}
