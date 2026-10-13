using System.Diagnostics;
using Win4K.Core;

namespace Win4K.ScreenSaver;

/// <summary>
/// Decides when real user input should dismiss the screen saver.
///
/// This is the single most delicate part of a screen saver and the easiest to get wrong.
/// cdima/aerial exited on any mouse movement greater than 5px with no grace period at all,
/// which meant a jittery mouse, a trackpoint drifting, or a stray event delivered as the
/// window appeared would kill it instantly. Started from a keyboard shortcut it would die on
/// the key release before anything was ever drawn.
///
/// The rules here:
///   * ignore everything for a grace period after launch (longer when launched by hand),
///   * require the pointer to move more than a threshold from where it started, not merely
///     to report an event, since Windows delivers spurious WM_MOUSEMOVE at the same
///     coordinates when a window appears under the cursor,
///   * treat any key or button as intent immediately once the grace period is over.
///
/// Polls rather than installing a low-level hook: a global WH_MOUSE_LL hook from a screen
/// saver is exactly the shape antivirus heuristics dislike, and polling at 60ms is plenty.
/// </summary>
public sealed class InputDismissWatcher : IDisposable
{
    /// <summary>Cumulative pixels the pointer must travel before we treat it as intent.</summary>
    public const int MovementThreshold = 10;

    private const int PollIntervalMs = 60;

    private readonly System.Windows.Forms.Timer _timer;
    private readonly Stopwatch _since = Stopwatch.StartNew();
    private readonly int _graceMs;

    private Point? _origin;
    private bool _fired;

    public event EventHandler? Dismissed;

    public InputDismissWatcher(int graceMilliseconds)
    {
        _graceMs = graceMilliseconds;

        _timer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _timer.Tick += OnTick;
        _timer.Start();

        Log.Info($"Input: dismissal armed after {graceMilliseconds}ms");
    }

    public bool InGracePeriod => _since.ElapsedMilliseconds < _graceMs;

    /// <summary>Called by the form for key and button events.</summary>
    public void NotifyExplicitInput()
    {
        if (InGracePeriod)
            return;

        Fire("key or button");
    }

    /// <summary>
    /// Whether a pointer at <paramref name="current"/> has moved far enough from
    /// <paramref name="origin"/> to count as intent.
    ///
    /// Pulled out as a pure function because the surrounding behaviour cannot be tested
    /// without a real desktop — an automated session cannot move the physical cursor, and
    /// GetCursorPos reports 0,0 there — so this is the part that gets unit tests.
    /// </summary>
    public static bool HasMovedEnough(Point origin, Point current, int threshold = MovementThreshold) =>
        Math.Abs(current.X - origin.X) > threshold || Math.Abs(current.Y - origin.Y) > threshold;

    /// <summary>Set WIN4K_INPUT_DEBUG=1 to log the pointer poll. Off by default; it is chatty.</summary>
    private static readonly bool DebugPolling = Environment.GetEnvironmentVariable("WIN4K_INPUT_DEBUG") == "1";

    private int _ticks;

    private void OnTick(object? sender, EventArgs e)
    {
        if (_fired)
            return;

        var position = NativeMethods.CursorPosition;

        if (DebugPolling && ++_ticks % 50 == 0)
        {
            Log.Info($"Input: tick {_ticks} pos={position.X},{position.Y} origin=" +
                     $"{(_origin is null ? "unset" : $"{_origin.Value.X},{_origin.Value.Y}")} grace={InGracePeriod}");
        }

        // Latch the origin at the end of the grace period, not at construction: the pointer
        // may still be settling from whatever gesture launched us.
        if (InGracePeriod)
        {
            _origin = position;
            return;
        }

        _origin ??= position;

        if (HasMovedEnough(_origin.Value, position))
        {
            var dx = Math.Abs(position.X - _origin.Value.X);
            var dy = Math.Abs(position.Y - _origin.Value.Y);
            Fire($"pointer moved {dx}x{dy}px");
        }
    }

    private void Fire(string reason)
    {
        if (_fired) return;
        _fired = true;

        _timer.Stop();
        Log.Info($"Input: dismissing ({reason})");
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _timer.Tick -= OnTick;
        _timer.Stop();
        _timer.Dispose();
    }
}
