using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Win4K.Core;
using Win4K.Core.Caching;
using Win4K.Core.Media;
using Win4K.Core.Playback;

namespace Win4K.ScreenSaver.Browser;

/// <summary>
/// The clip browser (/c): a WebView2 showing the page from <see cref="UiAssets"/>, with a native
/// mpv panel laid over the detail pane's video rect. The page cannot play the clips itself —
/// WebView2 has neither Apple's TLS trust (<see cref="AppleTrust"/>) nor dependable HEVC —
/// so the panel is a sibling HWND above the WebView, positioned from the page's panelRect
/// messages. Everything else is <see cref="BrowserBridge"/>.
/// </summary>
internal sealed class BrowserForm : Form, IBrowserHost
{
    /// <summary>A second /c broadcasts this so the open browser comes to the front. See <see cref="Program"/>.</summary>
    public static readonly uint ActivateMessage = NativeMethods.RegisterWindowMessage("Win4KSS.Browser.Activate");

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(0x15, 0x18, 0x1c) };
    private readonly Panel _videoPanel = new() { BackColor = Color.Black, Visible = false };

    private AppSettings? _settings;
    private VideoCache? _cache;
    private DownloadQueue? _queue;
    private MediaPreviewService? _previews;
    private BrowserBridge? _bridge;
    private CoreWebView2Environment? _environment;

    private MpvPlayer? _player;
    private bool _playerFailed;
    private bool _hasClip;
    private bool _paused;
    private PanelRect _rect = PanelRect.Hidden;
    private bool _closing;

    public BrowserForm()
    {
        Text = "Win4KSS";
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(0x15, 0x18, 0x1c);
        MinimumSize = new Size(900, 600);

        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var size = new Size(Math.Min(area.Width, Math.Max(1200, area.Width * 4 / 5)),
                            Math.Min(area.Height, Math.Max(800, area.Height * 4 / 5)));
        Bounds = new Rectangle(area.Left + (area.Width - size.Width) / 2, area.Top + (area.Height - size.Height) / 2,
                               size.Width, size.Height);

        // Both are children of the form. Added WebView first, then the panel brought to the top
        // of the sibling z-order: the WebView2 control hosts the browser in its own child HWND,
        // and a sibling above it overlays that HWND.
        Controls.Add(_web);
        Controls.Add(_videoPanel);
        _videoPanel.BringToFront();

        _web.ZoomFactorChanged += (_, _) => ApplyPanel();
        DpiChanged += (_, _) => ApplyPanel();

        Load += (_, _) => _ = StartAsync();
        FormClosing += (_, _) => Shutdown();
    }

    // ---- start-up ----------------------------------------------------------------------------

    private async Task StartAsync()
    {
        try
        {
            _settings = AppSettings.Load();
            _cache = new VideoCache(_settings);
            _queue = new DownloadQueue(_cache);
            _previews = new MediaPreviewService(_settings, _cache);

            _bridge = new BrowserBridge(_settings, _cache, _queue, _previews, this, PostToPage, new BrowserBridgeOptions
            {
                Dispatch = RunOnUiThread,
            });

            // Takes pin ownership (its mutex) unless the saver already has it.
            _queue.Start();

            // The catalogue loads while WebView2 starts; the bridge sends it once the page says ready.
            var catalogue = _bridge.InitializeAsync();

            AppPaths.EnsureRoot();
            _environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebView2Directory);
            await _web.EnsureCoreWebView2Async(_environment);
            ConfigureWebView(_web.CoreWebView2);

            _web.CoreWebView2.Navigate(UiAssets.StartUrl);
            await catalogue;
        }
        catch (Exception ex)
        {
            Log.Error("Browser: start-up failed", ex);
            if (!_closing)
            {
                MessageBox.Show(this, "The clip browser could not start:\n\n" + ex.Message, "Win4KSS",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }
    }

    private void ConfigureWebView(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.IsStatusBarEnabled = false;
        settings.IsWebMessageEnabled = true;
        settings.AreHostObjectsAllowed = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
#if !DEBUG
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
#endif

        Log.Info($"Browser: WebView2 runtime {_environment?.BrowserVersionString}, " +
                 $"UI {(UiAssets.HasIndex ? $"embedded ({UiAssets.Files.Count} files)" : "not built — serving the fallback page")}");

        core.AddWebResourceRequestedFilter(UiAssets.Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;

        // Previews are plain files; WebView2 serves them straight from the folder. The page's
        // ?v= cache-busting query is ignored by the mapping (only the path is mapped).
        core.SetVirtualHostNameToFolderMapping(MediaPreviewService.VirtualHost, _previews!.Directory,
                                               CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += (_, e) => _bridge?.OnPageMessage(e.WebMessageAsJson);

        core.NavigationStarting += (_, e) =>
        {
            // Only our own origin is ever shown in this window.
            if (!e.Uri.StartsWith(UiAssets.Origin, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
                return;
            }
            _bridge?.PageUnloaded();
        };

        // Links with target=_blank (a licence, an archive.org item page) open in the real browser.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };

        core.ProcessFailed += (_, e) =>
            Log.Error($"Browser: WebView2 process failed: {e.ProcessFailedKind} ({e.Reason}, exit {e.ExitCode})");
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            var asset = UiAssets.Resolve(uri.PathAndQuery);
            e.Response = asset is null
                ? _environment!.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain")
                : _environment!.CreateWebResourceResponse(new MemoryStream(asset.Body), 200, "OK",
                    $"Content-Type: {asset.ContentType}\r\nCache-Control: no-cache");
        }
        catch (Exception ex)
        {
            Log.Error($"Browser: serving {e.Request.Uri} failed", ex);
        }
    }

    private static void OpenExternal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http"))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Browser: could not open {uri}: {ex.Message}");
        }
    }

    private void PostToPage(string json)
    {
        if (_closing || _web.CoreWebView2 is not { } core)
            return;
        try
        {
            core.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Log.Warn($"Browser: post to page failed: {ex.Message}");
        }
    }

    /// <summary>Service events and bridge timers arrive on background threads; this is the way back.</summary>
    private void RunOnUiThread(Action action)
    {
        if (_closing || IsDisposed || !IsHandleCreated)
            return;
        if (InvokeRequired)
            BeginInvoke(action);
        else
            action();
    }

    // ---- the native preview panel -------------------------------------------------------------

    public void ShowClip(Clip? clip, string? playUrl)
    {
        _hasClip = clip is not null && playUrl is not null;
        if (!_hasClip)
        {
            _player?.Stop();
            ApplyPanel();
            return;
        }

        var player = EnsurePlayer();
        if (player is null)
            return;

        Log.Info($"Browser: previewing {clip} from {(playUrl!.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "the network" : "the cache")}");
        _paused = false;
        player.SetProperty("pause", "no");
        player.Play(playUrl);
        ApplyPanel();
    }

    public void SetPanelRect(PanelRect rect)
    {
        _rect = rect;
        ApplyPanel();
    }

    /// <summary>
    /// Shows the panel over the page's video rect when there is a clip and the page says it is
    /// visible; otherwise hides it and pauses decoding (a hidden 4K decode is wasted GPU).
    /// </summary>
    private void ApplyPanel()
    {
        if (_closing)
            return;

        var device = _rect.ToDevicePixels(_web.ZoomFactor, _web.DeviceDpi);
        var show = _hasClip && _player is not null && !device.IsEmpty;

        if (show)
        {
            device.Offset(_web.Left, _web.Top);
            if (_videoPanel.Bounds != device)
                _videoPanel.Bounds = device;
            if (!_videoPanel.Visible)
            {
                _videoPanel.Visible = true;
                _videoPanel.BringToFront();
            }
            SetPaused(false);
        }
        else
        {
            if (_videoPanel.Visible)
                _videoPanel.Visible = false;
            SetPaused(true);
        }
    }

    private void SetPaused(bool paused)
    {
        if (_player is null || _paused == paused)
            return;
        _paused = paused;
        try
        {
            _player.SetProperty("pause", paused ? "yes" : "no");
        }
        catch (Exception ex)
        {
            Log.Warn($"Browser: could not {(paused ? "pause" : "resume")} the preview: {ex.Message}");
        }
    }

    private MpvPlayer? EnsurePlayer()
    {
        if (_player is not null || _playerFailed)
            return _player;

        try
        {
            // mpv needs a real HWND for its wid option; reading Handle creates it even while hidden.
            var handle = _videoPanel.Handle;

            var player = new MpvPlayer();
            player.Initialize(handle);

            // Looped so a short clip does not leave a black panel; muted like the saver.
            player.SetProperty("loop-file", "inf");
            _player = player;
            return _player;
        }
        catch (Exception ex)
        {
            // The browser is still useful without a live preview.
            _playerFailed = true;
            Log.Error("Browser: preview player failed to start", ex);
            return null;
        }
    }

    // ---- IBrowserHost: the rest --------------------------------------------------------------

    public bool StartNow() => SaverLauncher.StartNow();

    public ShortcutOptions ReadShortcuts() => ShortcutManager.ReadOptions();

    public bool ApplyShortcuts(ShortcutOptions options) => ShortcutManager.Apply(options);

    public string? PickFolder(string title, string? initialPath)
    {
        using var dialog = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            dialog.SelectedPath = initialPath;
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    public string? PickSaveFile(string title, string suggestedName, string filter)
    {
        using var dialog = new SaveFileDialog
        {
            Title = title, FileName = suggestedName, Filter = filter, OverwritePrompt = true, AddExtension = false,
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    public string? PickOpenFile(string title, string filter)
    {
        using var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Browser: could not open {path}", ex);
        }
    }

    // ---- single instance & shutdown ----------------------------------------------------------

    protected override void WndProc(ref Message m)
    {
        if (ActivateMessage != 0 && m.Msg == (int)ActivateMessage)
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
            NativeMethods.SetForegroundWindow(Handle);
            Log.Info("Browser: activated by a second /c");
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// Runs on FormClosing, while every HWND still exists: mpv must be torn down before its
    /// panel is destroyed, and pending settings written before the process goes.
    /// </summary>
    private void Shutdown()
    {
        if (_closing)
            return;
        _closing = true;
        Log.Info("Browser: closing");

        Try("bridge", () => _bridge?.Dispose());
        Try("preview player", () =>
        {
            // A live mpv attached to a destroyed HWND leaks the decoder and can wedge exit.
            _player?.Dispose();
            _player = null;
        });
        Try("previews", () => _previews?.Dispose());

        // Unfinished pins stay in queue.json with their partials; whoever runs next resumes them.
        Try("download queue", () => _queue?.Dispose());
        Try("cache", () => _cache?.Dispose());
        Try("webview", () => _web.Dispose());
    }

    private static void Try(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error($"Browser: shutting down the {what} failed", ex);
        }
    }
}
