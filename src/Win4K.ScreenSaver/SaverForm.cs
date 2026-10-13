using System.ComponentModel;
using Win4K.Core;
using Win4K.Core.Caching;
using Win4K.Core.Catalogs;
using Win4K.Core.Playback;

namespace Win4K.ScreenSaver;

public enum SaverMode
{
    /// <summary>Full screen, launched by the idle timer or /s. Any input dismisses it.</summary>
    FullScreen,
    /// <summary>A normal resizable desktop window (/w). Input never dismisses it.</summary>
    Window,
    /// <summary>The little preview inside Screen Saver Settings (/p).</summary>
    Preview,
}

/// <summary>
/// The playback surface: a window with a libmpv instance rendering into it.
/// </summary>
public sealed class SaverForm : Form
{
    private readonly SaverMode _mode;
    private readonly bool _showVideo;
    private readonly bool _shouldCache;
    private readonly IReadOnlyList<Clip>? _clips;
    private readonly CancellationTokenSource _shutdown = new();

    private MpvPlayer? _player;
    private PlaylistEngine? _playlist;
    private VideoCache? _cache;
    private DownloadQueue? _queue;
    private InputDismissWatcher? _dismiss;
    private System.Windows.Forms.Timer? _previewWatch;
    private bool _previewClosing;

    /// <param name="shouldCache">
    /// Only one window per session should populate the cache — otherwise a three-monitor
    /// setup downloads three copies of every clip. The /s path passes true for the primary
    /// display only.
    /// </param>
    public SaverForm(
        SaverMode mode,
        Rectangle? bounds = null,
        bool showVideo = true,
        IReadOnlyList<Clip>? clips = null,
        bool shouldCache = true)
    {
        _mode = mode;
        _showVideo = showVideo;
        _shouldCache = shouldCache;
        _clips = clips;

        SuspendLayout();

        // Black everywhere: any frame not covered by video (letterbox edges during a resize,
        // the moment before the first frame decodes) must be black, never the desktop.
        BackColor = Color.Black;
        DoubleBuffered = true;
        Text = "Win4KSS";
        KeyPreview = true;

        switch (mode)
        {
            case SaverMode.FullScreen:
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                Bounds = bounds ?? Screen.PrimaryScreen!.Bounds;
                ShowInTaskbar = false;
#if !DEBUG
                // TopMost only in Release: it makes attaching a debugger miserable.
                TopMost = true;
#endif
                break;

            case SaverMode.Preview:
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                break;

            case SaverMode.Window:
                FormBorderStyle = FormBorderStyle.Sizable;
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = bounds?.Size ?? new Size(1280, 720);
                ShowInTaskbar = true;
                break;
        }

        ResumeLayout(false);
    }

    /// <summary>Set by the /p path before the form is shown. Runtime-only, never designed.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IntPtr PreviewParent { get; set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Preview attaches in OnLoad, not here — see there.

        if (!_showVideo)
            return;

        try
        {
            // mpv needs a live HWND for its "wid" option, so this cannot happen in the
            // constructor — the handle does not exist yet.
            _player = new MpvPlayer();
            _player.Initialize(Handle);
        }
        catch (Exception ex)
        {
            Log.Error("Playback: mpv failed to initialise", ex);
            _player = null;
            ShowFatal(ex.Message);
            return;
        }

        var settings = AppSettings.Load();
        _playlist = new PlaylistEngine(_player);

        // Clips benched twice in a session are counted toward "unhealthy" in settings.json.
        _playlist.ClipFailedForSession += (_, clip) => ClipHealth.Shared.RecordSessionFailure(clip);

        if (settings.CacheEnabled)
        {
            _cache = new VideoCache(settings)
            {
                // The saver runs for hours on a settings snapshot; read pins live so a clip
                // kept offline in the browser meanwhile is never evicted by this process.
                PinnedKeys = PinnedKeySource.FromSettingsFile(settings),
            };
            var cache = _cache;

            // Cached clips play from disk; everything else streams from the origin. Cached
            // clips are also what the playlist falls back to when streaming keeps failing.
            _playlist.ResolveUrl = cache.Resolve;
            _playlist.IsAvailableOffline = clip => clip.IsLocal || cache.IsCached(clip.Url);

            if (_shouldCache)
            {
                // One slot at most for pins, and only if the browser is not running (it owns
                // the pin queue then): download-ahead is what keeps this playlist off the network.
                _queue = new DownloadQueue(cache, new DownloadQueueOptions { PinConcurrency = 1 });
                var queue = _queue;

                // Fetch the next clips while this one plays, so they are queued from disk.
                _playlist.DownloadAhead = queue;

                // Cache the clip that just started too (a no-op if it played from disk), on
                // a delay so the download does not compete with the stream being watched.
                _playlist.ClipStarted += (_, clip) => queue.EnqueuePlayed(clip);

                queue.Start();
            }
        }

        // Only start here if the caller already resolved clips (the /s path does, so every
        // monitor shares one manifest fetch). Otherwise OnShown resolves them asynchronously
        // — starting with an empty list first would just log a spurious "nothing to play".
        if (_clips is not null)
            _playlist.Start(_clips, settings);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Not in OnHandleCreated: WinForms finishes creating a top-level form's handle after
        // that callback and resets its parent/owner on the way, which silently moved the
        // preview back under the desktop. Measured: GetAncestor(GA_PARENT) was the preview
        // HWND at the end of OnHandleCreated and the desktop window by OnLoad. That stray
        // desktop child is why /p processes outlived Screen Saver Settings.
        if (_mode == SaverMode.Preview)
            StartPreviewWatch();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_mode == SaverMode.FullScreen)
        {
            Cursor.Hide();
            Activate();
        }

        // Input is ignored briefly after launch. Without this, a screen saver started from a
        // keyboard shortcut dies on the key release, and one started by the idle timer can die
        // on a stray mouse event — the failure cdima/aerial hit by exiting on any movement
        // over 5px with no grace period at all.
        if (_mode == SaverMode.FullScreen)
        {
            _dismiss = new InputDismissWatcher(LaunchContext.GraceMilliseconds);
            _dismiss.Dismissed += (_, _) => BeginInvoke(Close);
        }

        // Clips may not have been supplied (window mode resolves them itself).
        if (_showVideo && _clips is null && _playlist is not null)
        {
            try
            {
                var settings = AppSettings.Load();

                // Every source is resolved (local JSON plus Apple's cached manifest); the
                // rotation picks clips from it. Migrating v1 settings and merging in new
                // clips from auto-add sources needs the catalogue, so it happens here. Only
                // the primary window saves, so several video windows do not race to write.
                var catalogue = await ClipLibrary.LoadAsync(settings);
                if (_shouldCache)
                    settings = SettingsMigrator.PrepareAndSave(settings, catalogue);
                else
                    SettingsMigrator.Prepare(settings, catalogue);

                var cache = _cache;
                var selection = Rotation.Select(settings, catalogue,
                    cache is null ? null : clip => cache.IsCached(clip.Url));

                Log.Info(selection.Fallback == RotationFallback.None
                    ? $"Rotation: {selection.Clips.Count} of {catalogue.Count} clips"
                    : $"Rotation: empty, falling back to {selection.Fallback.ToString().ToLowerInvariant()} " +
                      $"({selection.Clips.Count} clips)");

                _playlist.Start(selection.Clips, settings);
            }
            catch (Exception ex)
            {
                Log.Error("Playback: could not resolve any clips", ex);
                ShowFatal("No videos available. Check your connection or add a local folder in settings.");
            }
        }
    }

    // ---- preview lifetime -------------------------------------------------------------

    private void StartPreviewWatch()
    {
        if (!NativeMethods.AttachToPreview(Handle, PreviewParent, this))
            Log.Warn($"Preview: could not attach to parent 0x{PreviewParent:X}");

        // Check once now (a parent that was already gone, or a failed attach) and then every
        // second — the parent dying does not reliably take this process down with it.
        CheckPreviewParent();
        if (_previewClosing)
            return;

        _previewWatch = new System.Windows.Forms.Timer { Interval = PreviewLifetime.PollIntervalMs };
        _previewWatch.Tick += (_, _) => CheckPreviewParent();
        _previewWatch.Start();
    }

    private void CheckPreviewParent()
    {
        if (_previewClosing || !IsHandleCreated)
            return;

        var reason = PreviewLifetime.ExitReason(
            PreviewParent,
            NativeMethods.IsWindow(PreviewParent),
            NativeMethods.ParentOf(Handle));

        if (reason is null)
            return;

        _previewClosing = true;
        _previewWatch?.Stop();
        Log.Info($"Preview: exiting, {reason} (parent 0x{PreviewParent:X})");

        // Deferred: this can run from inside OnLoad, before Application.Run has finished
        // showing the form.
        BeginInvoke(() =>
        {
            Close();
            Application.ExitThread();
        });
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        base.OnHandleDestroyed(e);

        // Our window was destroyed out from under us — normally because Windows tore down the
        // preview parent and its children. Nothing else will ever end this message loop.
        if (_mode == SaverMode.Preview && !RecreatingHandle)
        {
            _previewWatch?.Stop();
            Application.ExitThread();
        }
    }

    private void ShowFatal(string message)
    {
        // Never a MessageBox in full-screen mode: it would sit behind a topmost window, or
        // dismiss the saver the moment it is acknowledged.
        var label = new Label
        {
            Text = message,
            ForeColor = Color.Gainsboro,
            BackColor = Color.Black,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 11f),
        };
        Controls.Add(label);
    }

    // ---- input ------------------------------------------------------------------------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_mode != SaverMode.Preview && keyData == Keys.Escape)
        {
            Close();
            return true;
        }

        // 'n' skips to the next clip, in every mode — handy while picking videos.
        if (keyData == Keys.N)
        {
            _playlist?.Next();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Always record why the saver went away. A screen saver that vanishes for reasons
        // other than user input is the hardest kind of bug to chase after the fact, because
        // by definition nobody is watching when it happens.
        Log.Info($"Saver: closing, reason={e.CloseReason}, mode={_mode}");

        base.OnFormClosing(e);
        if (_mode == SaverMode.FullScreen)
            Cursor.Show();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Cancel in-flight downloads before tearing the player down. Partials are kept
            // and resume next session; disposing the queue also hands pin ownership back.
            _shutdown.Cancel();
            _queue?.Dispose();
            _cache?.Dispose();
            _dismiss?.Dispose();
            _previewWatch?.Dispose();
            _playlist?.Dispose();
            _player?.Dispose();
            _shutdown.Dispose();
        }
        base.Dispose(disposing);
    }

    [Browsable(false)]
    public MpvPlayer? Player => _player;

    [Browsable(false)]
    public PlaylistEngine? Playlist => _playlist;
}
