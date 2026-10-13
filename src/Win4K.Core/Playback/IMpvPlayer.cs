namespace Win4K.Core.Playback;

public enum LoadFileMode
{
    /// <summary>Clear mpv's playlist and play this file now.</summary>
    Replace,

    /// <summary>
    /// Append to mpv's playlist, and start playing it if mpv is idle. Plain "append" does not
    /// restart an idle player, which is how one instant failure used to black out the saver.
    /// </summary>
    AppendPlay,
}

public sealed class StartFileEventArgs(long playlistEntryId) : EventArgs
{
    public long PlaylistEntryId { get; } = playlistEntryId;
}

public sealed class EndFileEventArgs(MpvEndFileReasonKind reason, int error, long playlistEntryId = 0) : EventArgs
{
    public MpvEndFileReasonKind Reason { get; } = reason;
    public int Error { get; } = error;

    /// <summary>Which playlist entry ended, matching <see cref="IMpvPlayer.LoadFile"/>'s return.</summary>
    public long PlaylistEntryId { get; } = playlistEntryId;
}

public enum MpvEndFileReasonKind
{
    Eof = 0,
    Stopped = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5,
}

/// <summary>
/// The slice of mpv that <see cref="PlaylistEngine"/> drives. Extracted so the engine can be
/// tested against a fake that fails instantly, goes idle, and reports entry ids — the exact
/// conditions that used to stall the saver and that are impractical to provoke in real mpv.
///
/// Events may arrive on any thread (the real player raises them from its event pump).
/// </summary>
public interface IMpvPlayer
{
    bool IsAlive { get; }

    /// <summary>mpv began opening a playlist entry.</summary>
    event EventHandler<StartFileEventArgs>? StartFile;

    /// <summary>The entry most recently started has parsed its headers and is about to show video.</summary>
    event EventHandler? FileLoaded;

    /// <summary>A playlist entry finished, for any reason.</summary>
    event EventHandler<EndFileEventArgs>? EndFile;

    /// <summary>mpv ran out of things to play. Raised on the transition into idle only.</summary>
    event EventHandler? Idle;

    /// <summary>
    /// Hands a file to mpv and returns the playlist entry id it was given, or -1 if mpv
    /// refused the command or did not report an id.
    /// </summary>
    long LoadFile(string url, LoadFileMode mode);

    void Stop();

    /// <summary>Current position in seconds, or null when nothing is playing. Polled by the watchdog.</summary>
    double? PlaybackTime { get; }
}
