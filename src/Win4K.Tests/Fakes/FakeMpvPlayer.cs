using Win4K.Core.Playback;

namespace Win4K.Tests.Fakes;

/// <summary>
/// A model of the parts of mpv the playlist engine depends on: a playlist of entries with ids,
/// one current entry, append-play restarting from idle, replace clearing the playlist, and
/// events delivered asynchronously.
///
/// Nothing happens until <see cref="Pump"/>: commands only change the playlist, and the
/// "core" acts on it when pumped — so, as in real mpv, an entry that fails instantly does so
/// before the engine's end-file handler has had a chance to append anything.
/// </summary>
internal sealed class FakeMpvPlayer : IMpvPlayer
{
    public sealed record Load(string Url, LoadFileMode Mode, long Id);

    private sealed record Item(long Id, string Url);

    private readonly List<Item> _playlist = [];
    private readonly Queue<Action> _events = new();
    private Item? _current;
    private Item? _pendingStart;
    private long _nextId = 1;

    public bool IsAlive => true;

    /// <summary>Every loadfile, in order.</summary>
    public List<Load> Loads { get; } = [];

    /// <summary>URLs for which loading fails instantly with mpv error -13.</summary>
    public Func<string, bool> Fails { get; set; } = _ => false;

    /// <summary>URLs that start but never load and never end — a dead stream.</summary>
    public Func<string, bool> Hangs { get; set; } = _ => false;

    public double? PlaybackTime { get; set; }

    public string? CurrentUrl => _current?.Url;
    public long? CurrentId => _current?.Id;
    public int PlaylistCount => _playlist.Count;
    public bool IsIdle => _current is null && _pendingStart is null;

    public event EventHandler<StartFileEventArgs>? StartFile;
    public event EventHandler? FileLoaded;
    public event EventHandler<EndFileEventArgs>? EndFile;
    public event EventHandler? Idle;

    public long LoadFile(string url, LoadFileMode mode)
    {
        var item = new Item(_nextId++, url);
        Loads.Add(new Load(url, mode, item.Id));

        if (mode == LoadFileMode.Replace)
        {
            if (_current is { } old)
                Emit(() => EndFile?.Invoke(this, new EndFileEventArgs(MpvEndFileReasonKind.Stopped, 0, old.Id)));

            _playlist.Clear();
            _playlist.Add(item);
            _current = null;
            _pendingStart = item;
        }
        else
        {
            _playlist.Add(item);
            if (IsIdle)
                _pendingStart = item;
        }

        return item.Id;
    }

    public void Stop()
    {
        if (_current is { } old)
            Emit(() => EndFile?.Invoke(this, new EndFileEventArgs(MpvEndFileReasonKind.Stopped, 0, old.Id)));
        _playlist.Clear();
        _current = null;
        _pendingStart = null;
    }

    /// <summary>The current entry reaches its natural end.</summary>
    public void FinishCurrent()
    {
        var item = _current ?? throw new InvalidOperationException("nothing is playing");
        Emit(() => EndFile?.Invoke(this, new EndFileEventArgs(MpvEndFileReasonKind.Eof, 0, item.Id)));
        AdvanceFrom(item);
        Pump();
    }

    /// <summary>mpv drops everything and goes idle, for whatever reason.</summary>
    public void ForceIdle()
    {
        _playlist.Clear();
        _current = null;
        _pendingStart = null;
        Emit(() => Idle?.Invoke(this, EventArgs.Empty));
        Pump();
    }

    /// <summary>An idle event with the playlist untouched — mpv's stale startup report.</summary>
    public void ForceIdleWithoutClearing()
    {
        Emit(() => Idle?.Invoke(this, EventArgs.Empty));
        Pump();
    }

    /// <summary>Delivers an arbitrary event, e.g. one for an entry id the engine never issued.</summary>
    public void Inject(EndFileEventArgs e)
    {
        Emit(() => EndFile?.Invoke(this, e));
        Pump();
    }

    public void InjectStart(long entryId)
    {
        var item = _playlist.FirstOrDefault(i => i.Id == entryId);
        if (item is not null)
        {
            _current = item;
            _pendingStart = null;
        }
        Emit(() => StartFile?.Invoke(this, new StartFileEventArgs(entryId)));
        Pump();
    }

    /// <summary>Runs the core and delivers events until nothing is left to do.</summary>
    public void Pump()
    {
        for (var guard = 0; guard < 10_000; guard++)
        {
            if (_events.Count > 0)
            {
                _events.Dequeue()();
                continue;
            }

            if (_pendingStart is null)
                return;

            var item = _pendingStart;
            _pendingStart = null;
            _current = item;
            Emit(() => StartFile?.Invoke(this, new StartFileEventArgs(item.Id)));

            if (Fails(item.Url))
            {
                Emit(() => EndFile?.Invoke(this, new EndFileEventArgs(MpvEndFileReasonKind.Error, -13, item.Id)));
                AdvanceFrom(item);
            }
            else if (!Hangs(item.Url))
            {
                Emit(() => FileLoaded?.Invoke(this, EventArgs.Empty));
            }
        }

        throw new InvalidOperationException("fake mpv did not settle; the engine is busy-looping");
    }

    private void AdvanceFrom(Item item)
    {
        var index = _playlist.IndexOf(item);
        _current = null;

        if (index >= 0 && index + 1 < _playlist.Count)
        {
            _pendingStart = _playlist[index + 1];
        }
        else
        {
            _pendingStart = null;
            Emit(() => Idle?.Invoke(this, EventArgs.Empty));
        }
    }

    private void Emit(Action raise) => _events.Enqueue(raise);
}

/// <summary>A clock that only moves when told to. Timers are not used: tests call Tick().</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Records download-ahead requests.</summary>
internal sealed class RecordingDownloadAhead : IDownloadAhead
{
    public List<(IReadOnlyList<Win4K.Core.Clip> Upcoming, bool Streaming)> Requests { get; } = [];

    public void Request(IReadOnlyList<Win4K.Core.Clip> upcoming, bool currentIsStreaming) =>
        Requests.Add((upcoming, currentIsStreaming));
}
