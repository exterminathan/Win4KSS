using System.Runtime.InteropServices;
using System.Text;

namespace Win4K.Core.Playback;

/// <summary>
/// A libmpv instance rendering into an existing Win32 window.
///
/// This replaces the Windows Media Player ActiveX control that cdima/aerial used, which is
/// deprecated, absent on many Windows 10/11 installs, and cannot decode the HEVC Main 10
/// that Apple's 4K aerials are encoded in.
///
/// Threading: mpv's event queue must be pumped from one thread, so a dedicated background
/// thread owns <see cref="MpvInterop.mpv_wait_event"/> and raises events from there.
/// Consumers on a UI thread must marshal (see SaverForm).
/// </summary>
public sealed class MpvPlayer : IMpvPlayer, IDisposable
{
    private readonly object _gate = new();
    private IntPtr _ctx;
    private Thread? _eventThread;
    private volatile bool _disposed;
    private bool _idle;

    // Rate limit for mpv errors copied into win4k.log; touched only by the event thread.
    private const int ForwardedLogLimit = 30;
    private long _forwardWindowStart = long.MinValue / 2;
    private int _forwardCount;
    private int _forwardSuppressed;

    /// <summary>Property-observation reply id for "idle-active".</summary>
    private const ulong IdleActiveObserver = 1;

    /// <summary>Raised when mpv starts opening a playlist entry.</summary>
    public event EventHandler<StartFileEventArgs>? StartFile;

    /// <summary>Raised when a file finishes, for any reason. The playlist engine drives off this.</summary>
    public event EventHandler<EndFileEventArgs>? EndFile;

    /// <summary>Raised once a file's headers are parsed and video is about to appear.</summary>
    public event EventHandler? FileLoaded;

    /// <summary>Raised on the transition into idle: mpv has nothing left to play.</summary>
    public event EventHandler? Idle;

    public bool IsAlive => _ctx != IntPtr.Zero && !_disposed;

    /// <summary>
    /// Creates and initialises the player against a host window handle.
    /// </summary>
    /// <param name="windowHandle">HWND to render into; passed to mpv's "wid" option.</param>
    /// <param name="hardwareDecoding">false forces software decode, for diagnosing driver issues.</param>
    public void Initialize(IntPtr windowHandle, bool hardwareDecoding = true) =>
        InitializeCore(windowHandle, "gpu-next", hardwareDecoding);

    /// <summary>
    /// Initialises with no window and vo=null: demux and decode only. Used by the live stream
    /// smoke test, which needs to know a URL loads, not what it looks like.
    /// </summary>
    public void InitializeHeadless() => InitializeCore(IntPtr.Zero, "null", hardwareDecoding: false);

    private void InitializeCore(IntPtr windowHandle, string videoOutput, bool hardwareDecoding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        MpvLoader.EnsureLoadable();

        _ctx = MpvInterop.mpv_create();
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create() failed — libmpv-2.dll loaded but returned no context.");

        // ---- options that MUST be set before mpv_initialize --------------------------
        if (windowHandle != IntPtr.Zero)
            SetOption("wid", windowHandle.ToInt64().ToString());

        // Never read the user's mpv.conf or input.conf. A screen saver inheriting someone's
        // player configuration is a support nightmare and a way to get unexpected key bindings.
        SetOption("config", "no");
        SetOption("load-scripts", "no");
        SetOption("terminal", "no");

        // mpv's own log. Without it a failed load is just an error code with no cause.
        // curl=v is permanent: the network layer is where streams fail, and at plain "warn"
        // the reason (e.g. "URL using bad/illegal format") was only visible by luck. Set
        // WIN4K_MPV_LOG=1 to raise everything to full debug output.
        var verbose = Environment.GetEnvironmentVariable("WIN4K_MPV_LOG") == "1";
        try
        {
            AppPaths.EnsureRoot();
            SetOption("log-file", Path.Combine(AppPaths.Root, "mpv.log"));
            SetOption("msg-level", verbose ? "all=debug" : "all=warn,curl=v");
        }
        catch (Exception ex)
        {
            Log.Warn($"mpv: could not enable log file: {ex.Message}");
        }

        // ---- rendering ---------------------------------------------------------------
        // gpu-next is the modern renderer and handles PQ tone-mapping properly, which matters
        // if the user opts into the 4K HDR variants.
        SetOption("vo", videoOutput);
        SetOption("hwdec", hardwareDecoding ? "auto-safe" : "no");
        if (videoOutput != "null")
            SetOption("profile", "gpu-hq");

        // panscan=1.0 gives cover-fit: fill the window and crop the overflow, rather than
        // letterboxing. cdima/aerial computed this by hand (CalculateVideoFillSize); mpv does
        // it natively and keeps it correct across resizes and DPI changes.
        SetOption("panscan", "1.0");
        SetOption("keepaspect", "yes");
        SetOption("video-unscaled", "no");

        // ---- playback ----------------------------------------------------------------
        // Aerials are silent. Not opening an audio device avoids stealing the default output
        // and avoids a pointless exclusive-mode grab.
        SetOption("audio", "no");
        SetOption("mute", "yes");

        // keep-open=no lets files end so END_FILE fires and the playlist advances.
        SetOption("keep-open", "no");
        SetOption("idle", "yes");
        SetOption("prefetch-playlist", "yes");

        // ---- input and UI ------------------------------------------------------------
        // mpv must not draw anything or consume input; the host form owns all of that.
        SetOption("osc", "no");
        SetOption("osd-level", "0");
        SetOption("input-default-bindings", "no");
        SetOption("input-vo-keyboard", "no");
        SetOption("input-cursor", "no");
        SetOption("cursor-autohide", "no");

        // ---- network -----------------------------------------------------------------
        // Streaming a ~12 Mbps 4K file: a large demuxer cache smooths over CDN hiccups.
        SetOption("cache", "yes");
        SetOption("demuxer-max-bytes", "192MiB");
        SetOption("demuxer-readahead-secs", "20");
        SetOption("user-agent", "Win4KScreensavers/1.0");

        // sylvan.apple.com chains to Apple Root CA, which is in neither the Windows root
        // store nor libcurl's default bundle, so every stream failed TLS verification.
        // Point mpv at a bundle of "everything Windows trusts, plus Apple's root" rather
        // than reaching for tls-verify=no, which would disable checking altogether.
        var caBundle = CaBundle.Ensure();
        if (caBundle is not null)
        {
            SetOption("tls-ca-file", caBundle);
            SetOption("tls-verify", "yes");
        }

        // Never shell out to yt-dlp. These are direct video URLs, so the hook can only ever
        // fail — and when it does it costs about a second per clip spawning a subprocess,
        // which is both slow and a strange thing for a screen saver to be doing.
        SetOption("ytdl", "no");

        var rc = MpvInterop.mpv_initialize(_ctx);
        if (rc < 0)
        {
            var message = MpvInterop.ErrorText(rc);
            MpvInterop.mpv_terminate_destroy(_ctx);
            _ctx = IntPtr.Zero;
            throw new InvalidOperationException($"mpv_initialize() failed: {message}");
        }

        // Errors also go to win4k.log, so a failed load explains itself next to the playlist
        // line that triggered it instead of only in mpv.log.
        rc = MpvInterop.mpv_request_log_messages(_ctx, "error");
        if (rc < 0)
            Log.Warn($"mpv: could not subscribe to log messages: {MpvInterop.ErrorText(rc)}");

        // The supported way to learn mpv has run dry; MPV_EVENT_IDLE is deprecated.
        rc = MpvInterop.mpv_observe_property(_ctx, IdleActiveObserver, "idle-active", MpvFormat.Flag);
        if (rc < 0)
            Log.Warn($"mpv: could not observe idle-active: {MpvInterop.ErrorText(rc)}");

        StartEventLoop();
        Log.Info(windowHandle != IntPtr.Zero
            ? $"mpv initialised (client API {ApiVersionString}) on HWND 0x{windowHandle.ToInt64():X}"
            : $"mpv initialised (client API {ApiVersionString}) headless, vo={videoOutput}");
    }

    public static string ApiVersionString
    {
        get
        {
            var v = MpvInterop.mpv_client_api_version();
            return $"{v >> 16}.{v & 0xFFFF}";
        }
    }

    // ---- playlist ---------------------------------------------------------------------

    /// <summary>Replaces whatever is playing with <paramref name="url"/>.</summary>
    public void Play(string url) => LoadFile(url, LoadFileMode.Replace);

    /// <summary>
    /// Hands a file to mpv. Appending while the current clip plays is what makes transitions
    /// gapless — with prefetch-playlist mpv opens and decodes the next file while the current
    /// one is still on screen, instead of the ~1s black gap cdima/aerial had from polling
    /// playState at 1 Hz and only then setting the next URL.
    /// </summary>
    /// <returns>mpv's playlist entry id for the new entry, or -1.</returns>
    public long LoadFile(string url, LoadFileMode mode)
    {
        if (!IsAlive) return -1;

        var flag = mode == LoadFileMode.Replace ? "replace" : "append-play";
        var id = CommandReturningEntryId("loadfile", url, flag);
        Log.Info($"mpv: loadfile {flag} -> entry {id}: {url}");
        return id;
    }

    public void Stop() => Command("stop");

    public double? PlaybackTime =>
        double.TryParse(GetPropertyString("playback-time"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var t)
            ? t
            : null;

    // ---- primitives -------------------------------------------------------------------

    public void SetOption(string name, string value)
    {
        var rc = MpvInterop.mpv_set_option_string(_ctx, name, value);
        if (rc < 0)
        {
            // A rejected option is worth knowing about but is rarely fatal — an mpv build
            // without gpu-next, say, should still play with the default vo.
            Log.Warn($"mpv: option '{name}={value}' rejected: {MpvInterop.ErrorText(rc)}");
        }
    }

    public void SetProperty(string name, string value)
    {
        if (!IsAlive) return;
        var rc = MpvInterop.mpv_set_property_string(_ctx, name, value);
        if (rc < 0)
            Log.Warn($"mpv: property '{name}={value}' rejected: {MpvInterop.ErrorText(rc)}");
    }

    public string? GetPropertyString(string name)
    {
        if (!IsAlive) return null;

        var ptr = MpvInterop.mpv_get_property_string(_ctx, name);
        if (ptr == IntPtr.Zero) return null;

        try
        {
            return MpvInterop.PtrToStringUtf8(ptr);
        }
        finally
        {
            // mpv allocates the string; we must hand it back or leak.
            MpvInterop.mpv_free(ptr);
        }
    }

    /// <summary>
    /// Sends a command using the argv form, so arguments are never re-parsed and paths
    /// containing spaces or quotes need no escaping.
    /// </summary>
    public void Command(params string[] args)
    {
        if (!IsAlive) return;

        WithArgv(args, argv =>
        {
            var rc = MpvInterop.mpv_command(_ctx, argv);
            if (rc < 0)
                Log.Warn($"mpv: command [{string.Join(' ', args)}] failed: {MpvInterop.ErrorText(rc)}");
        });
    }

    /// <summary>
    /// Runs a command and pulls "playlist_entry_id" out of its result map (loadfile has
    /// returned one since mpv 0.36). -1 when the command failed or no id was reported.
    /// </summary>
    private long CommandReturningEntryId(params string[] args)
    {
        long id = -1;
        WithArgv(args, argv =>
        {
            var rc = MpvInterop.mpv_command_ret(_ctx, argv, out var node);
            if (rc < 0)
            {
                Log.Warn($"mpv: command [{string.Join(' ', args)}] failed: {MpvInterop.ErrorText(rc)}");
                return;
            }

            try
            {
                id = ReadInt64FromMap(node, "playlist_entry_id") ?? -1;
            }
            finally
            {
                MpvInterop.mpv_free_node_contents(ref node);
            }
        });
        return id;
    }

    private static long? ReadInt64FromMap(MpvNode node, string key)
    {
        if (node.Format != MpvFormat.NodeMap || node.U == IntPtr.Zero)
            return null;

        var list = Marshal.PtrToStructure<MpvNodeList>(node.U);
        var nodeSize = Marshal.SizeOf<MpvNode>();

        for (var i = 0; i < list.Num; i++)
        {
            var name = MpvInterop.PtrToStringUtf8(Marshal.ReadIntPtr(list.Keys, i * IntPtr.Size));
            if (name != key)
                continue;

            var value = Marshal.PtrToStructure<MpvNode>(list.Values + i * nodeSize);
            return value.Format == MpvFormat.Int64 ? value.U.ToInt64() : null;
        }

        return null;
    }

    private static void WithArgv(string[] args, Action<IntPtr[]> use)
    {
        var pointers = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++)
                pointers[i] = Utf8ToHGlobal(args[i]);

            pointers[^1] = IntPtr.Zero; // argv must be null-terminated
            use(pointers);
        }
        finally
        {
            foreach (var p in pointers)
            {
                if (p != IntPtr.Zero)
                    Marshal.FreeHGlobal(p);
            }
        }
    }

    private static IntPtr Utf8ToHGlobal(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return ptr;
    }

    // ---- event loop ---------------------------------------------------------------------

    private void StartEventLoop()
    {
        _eventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "mpv-events",
        };
        _eventThread.Start();
    }

    private void EventLoop()
    {
        while (!_disposed && _ctx != IntPtr.Zero)
        {
            IntPtr eventPtr;
            try
            {
                // Blocks up to a second, then loops so shutdown is responsive.
                eventPtr = MpvInterop.mpv_wait_event(_ctx, 1.0);
            }
            catch (Exception ex)
            {
                Log.Error("mpv: event loop threw", ex);
                return;
            }

            if (eventPtr == IntPtr.Zero)
                continue;

            var evt = Marshal.PtrToStructure<MpvEvent>(eventPtr);

            switch (evt.EventId)
            {
                case MpvEventId.None:
                    break;

                case MpvEventId.Shutdown:
                    Log.Info("mpv: shutdown event");
                    return;

                case MpvEventId.StartFile:
                {
                    long entryId = 0;
                    if (evt.Data != IntPtr.Zero)
                        entryId = Marshal.PtrToStructure<MpvEventStartFile>(evt.Data).PlaylistEntryId;

                    _idle = false;
                    SafeRaise(() => StartFile?.Invoke(this, new StartFileEventArgs(entryId)));
                    break;
                }

                case MpvEventId.FileLoaded:
                    SafeRaise(() => FileLoaded?.Invoke(this, EventArgs.Empty));
                    break;

                case MpvEventId.EndFile:
                {
                    var reason = MpvEndFileReasonKind.Eof;
                    var error = 0;
                    long entryId = 0;

                    if (evt.Data != IntPtr.Zero)
                    {
                        var end = Marshal.PtrToStructure<MpvEventEndFile>(evt.Data);
                        reason = (MpvEndFileReasonKind)end.Reason;
                        error = end.Error;
                        entryId = end.PlaylistEntryId;
                    }

                    if (reason == MpvEndFileReasonKind.Error)
                        Log.Warn($"mpv: entry {entryId} ended with error {MpvInterop.ErrorText(error)}");

                    SafeRaise(() => EndFile?.Invoke(this, new EndFileEventArgs(reason, error, entryId)));
                    break;
                }

                case MpvEventId.Idle:
                    EnterIdle();
                    break;

                case MpvEventId.PropertyChange when evt.ReplyUserdata == IdleActiveObserver && evt.Data != IntPtr.Zero:
                {
                    var property = Marshal.PtrToStructure<MpvEventProperty>(evt.Data);
                    if (property.Format == MpvFormat.Flag && property.Data != IntPtr.Zero)
                    {
                        if (Marshal.ReadInt32(property.Data) != 0)
                            EnterIdle();
                        else
                            _idle = false;
                    }
                    break;
                }

                case MpvEventId.LogMessage when evt.Data != IntPtr.Zero:
                {
                    var message = Marshal.PtrToStructure<MpvEventLogMessage>(evt.Data);
                    var prefix = MpvInterop.PtrToStringUtf8(message.Prefix);
                    var text = MpvInterop.PtrToStringUtf8(message.Text)?.TrimEnd();
                    if (!string.IsNullOrEmpty(text))
                        ForwardLog(prefix, text);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Copies an mpv error into win4k.log, at most <see cref="ForwardedLogLimit"/> lines a
    /// minute: a damaged clip can produce a decoder error per frame, and win4k.log is capped
    /// at 2 MB, so an unthrottled flood would erase the history that explains it. mpv.log
    /// still has everything.
    /// </summary>
    private void ForwardLog(string? prefix, string text)
    {
        var now = Environment.TickCount64;
        if (now - _forwardWindowStart > 60_000)
        {
            if (_forwardSuppressed > 0)
                Log.Warn($"mpv: {_forwardSuppressed} further error lines suppressed (see mpv.log)");
            _forwardWindowStart = now;
            _forwardCount = 0;
            _forwardSuppressed = 0;
        }

        if (++_forwardCount <= ForwardedLogLimit)
            Log.Warn($"mpv[{prefix}]: {text}");
        else
            _forwardSuppressed++;
    }

    /// <summary>The deprecated event and the property can both report idle; raise once per transition.</summary>
    private void EnterIdle()
    {
        if (_idle) return;
        _idle = true;
        SafeRaise(() => Idle?.Invoke(this, EventArgs.Empty));
    }

    private static void SafeRaise(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            // A throwing handler must not kill the event pump, or playback silently stops
            // advancing and the screen freezes on one frame.
            Log.Error("mpv: event handler threw", ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        var ctx = _ctx;
        _ctx = IntPtr.Zero;

        if (ctx != IntPtr.Zero)
        {
            try
            {
                // Nudges mpv_wait_event so the pump notices _disposed instead of blocking
                // for its full timeout while we wait to join.
                MpvInterop.mpv_wakeup(ctx);
            }
            catch { /* shutting down anyway */ }
        }

        try
        {
            _eventThread?.Join(TimeSpan.FromSeconds(2));
        }
        catch { /* best effort */ }

        if (ctx != IntPtr.Zero)
        {
            try
            {
                MpvInterop.mpv_terminate_destroy(ctx);
            }
            catch (Exception ex)
            {
                Log.Error("mpv: terminate failed", ex);
            }
        }
    }
}
