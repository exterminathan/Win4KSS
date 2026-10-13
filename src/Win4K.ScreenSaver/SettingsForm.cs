using Win4K.Core;
using Win4K.Core.Caching;
using Win4K.Core.Catalogs;
using Win4K.Core.Playback;

namespace Win4K.ScreenSaver;

/// <summary>
/// The configuration dialog (/c). Laid out in code rather than the designer so the whole
/// thing is reviewable in one file and there is no .resx to keep in step.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings = AppSettings.Load();
    private VideoCache? _cache;
    private DownloadQueue? _queue;
    private PinManager? _pins;
    private IReadOnlyList<Clip> _clips = [];
    private IReadOnlyList<ClipInfo> _library = [];
    private Dictionary<string, ClipInfo> _infoByKey = new(StringComparer.Ordinal);
    private bool _prepared;
    private CancellationTokenSource _work = new();

    // Sources
    private readonly ComboBox _sourceMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ListBox _folders = new() { Height = 110 };
    private readonly CheckedListBox _catalogs = new() { Height = 110, CheckOnClick = true };

    // Clips
    private readonly TreeView _clipTree = new() { CheckBoxes = true, HideSelection = false };
    private readonly Label _clipSummary = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    // Live preview: a second mpv instance rendering into a panel beside the tree, so you can
    // see what a clip actually is before deciding whether to keep it in the rotation.
    private readonly Panel _previewHost = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly CheckBox _livePreview = new() { Text = "Live preview", AutoSize = true, Checked = true };
    private readonly Label _previewCaption = new()
    {
        Dock = DockStyle.Bottom,
        Height = 34,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = SystemColors.GrayText,
        Text = "Select a clip to preview it.",
    };
    private MpvPlayer? _preview;
    private bool _previewFailed;

    // Quality / display
    private readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox _multiMonitor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly CheckBox _shuffle = new() { Text = "Shuffle the play order", AutoSize = true };
    private readonly CheckBox _timeOfDay = new() { Text = "Prefer clips matching the time of day", AutoSize = true };

    // Cache
    private readonly CheckBox _cacheEnabled = new() { Text = "Keep watched clips on disk", AutoSize = true };
    private readonly TextBox _cacheFolder = new() { Width = 340, ReadOnly = true };
    private readonly NumericUpDown _budget = new() { Minimum = 1, Maximum = 500, DecimalPlaces = 0, Width = 80 };
    private readonly Label _cacheStats = new() { AutoSize = true };

    // Trigger
    private readonly CheckBox _desktopShortcut = new() { Text = "Desktop shortcut", AutoSize = true };
    private readonly CheckBox _startMenuShortcut = new() { Text = "Start Menu shortcut", AutoSize = true };
    private readonly TextBox _hotkey = new() { Width = 180, ReadOnly = true, Text = ShortcutManager.DefaultHotkey };

    private readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    public SettingsForm()
    {
        Text = "Win4KSS — Settings";
        ClientSize = new Size(720, 560);
        MinimumSize = new Size(700, 540);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildSourcesTab());
        tabs.TabPages.Add(BuildClipsTab());
        tabs.TabPages.Add(BuildPlaybackTab());
        tabs.TabPages.Add(BuildCacheTab());
        tabs.TabPages.Add(BuildTriggerTab());
        tabs.TabPages.Add(BuildAboutTab());

        Controls.Add(tabs);
        Controls.Add(BuildButtonBar());

        Load += async (_, _) =>
        {
            LoadSettings();
            await ReloadClipsAsync();
        };

        FormClosed += (_, _) =>
        {
            _work.Cancel();

            // Tear the preview player down explicitly. Leaving a live mpv instance attached
            // to a destroyed HWND leaks the decoder and can wedge process exit.
            _preview?.Dispose();
            _preview = null;

            // Unfinished pins stay in queue.json with their partials; the saver (or the next
            // time this opens) resumes them.
            _queue?.Dispose();
            _cache?.Dispose();
        };
    }

    // ---- layout helpers -------------------------------------------------------------------

    private static TabPage Page(string title) => new(title) { Padding = new Padding(12), UseVisualStyleBackColor = true };

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        Margin = new Padding(0, 10, 0, 4),
    };

    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(650, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 0, 0, 8),
    };

    private static FlowLayoutPanel Column() => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 2, 0, 2),
        };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Button Btn(string text, EventHandler onClick, int width = 130)
    {
        var button = new Button { Text = text, Width = width, Height = 28, Margin = new Padding(0, 0, 8, 0) };
        button.Click += onClick;
        return button;
    }

    // ---- tabs -----------------------------------------------------------------------------

    private TabPage BuildSourcesTab()
    {
        var page = Page("Sources");
        var column = Column();

        column.Controls.Add(Heading("Where clips come from"));
        column.Controls.Add(_sourceMode);
        column.Controls.Add(Hint("Apple's aerials stream from sylvan.apple.com. Local folders play files already on this machine."));

        column.Controls.Add(Heading("Local folders"));
        column.Controls.Add(_folders);
        column.Controls.Add(Row(
            Btn("Add folder…", (_, _) => AddFolder()),
            Btn("Remove", (_, _) => RemoveFolder())));

        column.Controls.Add(Heading("Extra catalogues"));
        column.Controls.Add(Hint(
            "Harvested collections that ship with the app. They exist so there is still something to " +
            "play if Apple stops hosting the aerials, which is exactly how the original Aerial project died."));
        column.Controls.Add(_catalogs);

        page.Controls.Add(column);
        return page;
    }

    private TabPage BuildClipsTab()
    {
        var page = Page("Clips");

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        bottom.Controls.Add(Btn("Select all", (_, _) => SetAllChecked(true)));
        bottom.Controls.Add(Btn("Select none", (_, _) => SetAllChecked(false)));
        bottom.Controls.Add(Btn("Refresh", async (_, _) => await ReloadClipsAsync(force: true)));
        bottom.Controls.Add(_livePreview);
        bottom.Controls.Add(_clipSummary);

        _clipTree.Dock = DockStyle.Fill;
        _clipTree.AfterCheck += OnTreeChecked;
        _clipTree.AfterSelect += OnClipSelected;

        // Tree on the left, preview on the right. A splitter rather than fixed widths so a
        // long clip name is still readable on a small display.
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 300,
            FixedPanel = FixedPanel.None,
        };
        split.Panel1.Controls.Add(_clipTree);
        split.Panel2.Controls.Add(_previewHost);
        split.Panel2.Controls.Add(_previewCaption);

        _livePreview.CheckedChanged += (_, _) =>
        {
            if (_livePreview.Checked)
                PreviewSelected();
            else
                StopPreview();
        };

        page.Controls.Add(split);
        page.Controls.Add(bottom);
        page.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 });
        return page;
    }

    // ---- live preview -----------------------------------------------------------------------

    private void OnClipSelected(object? sender, TreeViewEventArgs e) => PreviewSelected();

    private void PreviewSelected()
    {
        if (!_livePreview.Checked || _previewFailed)
            return;

        if (_clipTree.SelectedNode is not { Tag: string id })
            return;

        var clip = _clips.FirstOrDefault(c => c.Id == id);
        if (clip is null)
            return;

        var player = EnsurePreviewPlayer();
        if (player is null)
            return;

        // Play from the cache when we have it, so flicking through the list does not pull
        // hundreds of megabytes per click.
        var url = _cache?.Resolve(clip) ?? clip.PlayableUrl;
        var cached = !url.StartsWith("http", StringComparison.OrdinalIgnoreCase);

        _previewCaption.Text = $"  {clip.Title} — {clip.Category}{(cached ? "   (cached)" : "   (streaming)")}";
        player.Play(url);
    }

    private MpvPlayer? EnsurePreviewPlayer()
    {
        if (_preview is not null)
            return _preview;

        try
        {
            // The panel must have a real HWND before mpv can be told to render into it.
            if (!_previewHost.IsHandleCreated)
                _previewHost.CreateControl();

            var player = new MpvPlayer();
            player.Initialize(_previewHost.Handle);

            // Preview is incidental: loop the clip so a short one does not leave a black
            // panel, and keep it muted like the main player.
            player.SetProperty("loop-file", "inf");

            _preview = player;
            return _preview;
        }
        catch (Exception ex)
        {
            // A failed preview must not take the settings dialog down with it — everything
            // else on this form still works without it.
            _previewFailed = true;
            _previewCaption.Text = "  Preview unavailable: " + ex.Message;
            Log.Error("Settings: preview player failed to start", ex);
            return null;
        }
    }

    private void StopPreview()
    {
        _preview?.Stop();
        _previewCaption.Text = "Preview stopped.";
    }

    private TabPage BuildPlaybackTab()
    {
        var page = Page("Playback");
        var column = Column();

        column.Controls.Add(Heading("Quality"));
        column.Controls.Add(_quality);
        column.Controls.Add(Hint(
            "Apple publishes each aerial at 4K and 1080p, in SDR and HDR. 4K clips are around 350 MB each. " +
            "HDR is PQ-encoded and gets tone-mapped on an SDR display, so SDR is the safer default."));

        column.Controls.Add(Heading("Multiple displays"));
        column.Controls.Add(_multiMonitor);
        column.Controls.Add(Hint("Every display is always covered — the choice is only which of them decode video."));

        column.Controls.Add(Heading("Order"));
        column.Controls.Add(_shuffle);
        column.Controls.Add(_timeOfDay);
        column.Controls.Add(Hint(
            "Time of day has little effect on Apple's 4K catalogue: only 4 of its 114 clips are labelled " +
            "day or night. The 1080p manifest used to mark every clip, but the 4K one dropped the field."));

        page.Controls.Add(column);
        return page;
    }

    private TabPage BuildCacheTab()
    {
        var page = Page("Cache");
        var column = Column();

        column.Controls.Add(Heading("Disk cache"));
        column.Controls.Add(_cacheEnabled);
        column.Controls.Add(Hint(
            "A clip streams the first time and is saved in the background, so later showings play from disk. " +
            "Downloading starts 10 seconds in so it does not compete with the clip you are watching."));

        column.Controls.Add(Row(new Label { Text = "Folder:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) },
                                _cacheFolder,
                                Btn("Change…", (_, _) => ChangeCacheFolder(), 90)));

        column.Controls.Add(Row(new Label { Text = "Budget (GB):", AutoSize = true, Margin = new Padding(0, 6, 6, 0) },
                                _budget));
        column.Controls.Add(Hint(
            "When the cache exceeds its budget the least recently played clips are deleted. " +
            "The full 4K catalogue is about 40 GB, so this is what stops it quietly filling the drive."));

        column.Controls.Add(_cacheStats);
        column.Controls.Add(Row(
            Btn("Open folder", (_, _) => OpenCacheFolder()),
            Btn("Purge cache", (_, _) => PurgeCache()),
            Btn("Keep selected offline…", async (_, _) => await KeepSelectedOfflineAsync(), 170)));
        column.Controls.Add(Hint(
            "Clips kept offline are downloaded now and never evicted. They count against the budget; " +
            "the rolling cache of watched clips gets whatever is left."));

        page.Controls.Add(column);
        return page;
    }

    private TabPage BuildTriggerTab()
    {
        var page = Page("Start now");
        var column = Column();

        column.Controls.Add(Heading("Start the screen saver on demand"));
        column.Controls.Add(Hint(
            "Useful when you are getting up to leave and do not want to wait out the idle timer. " +
            "The shortcut asks Windows to start the configured screen saver, which is the only way " +
            "the \"On resume, display logon screen\" setting is honoured — running the saver directly does not lock."));

        column.Controls.Add(Row(Btn("Start now", (_, _) => StartNow(), 120)));

        column.Controls.Add(Heading("Shortcuts"));
        column.Controls.Add(Hint(
            "The hotkey is stored on the shortcut itself and bound by Explorer, so nothing runs in the " +
            "background. It only works for shortcuts on the Desktop or in the Start Menu."));
        column.Controls.Add(_desktopShortcut);
        column.Controls.Add(_startMenuShortcut);

        column.Controls.Add(Row(new Label { Text = "Hotkey:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) },
                                _hotkey,
                                Btn("Clear", (_, _) => _hotkey.Text = "", 70)));
        column.Controls.Add(Hint("Click the box and press a combination. At least one of Ctrl, Alt or Shift is required."));

        _hotkey.KeyDown += (_, e) =>
        {
            var value = ShortcutManager.ToShortcutHotkey(e.KeyData);
            if (value is not null)
                _hotkey.Text = value;

            e.SuppressKeyPress = true;
            e.Handled = true;
        };

        page.Controls.Add(column);
        return page;
    }

    private TabPage BuildAboutTab()
    {
        var page = Page("About");
        var column = Column();

        string mpvVersion;
        try
        {
            MpvLoader.Register();
            mpvVersion = MpvPlayer.ApiVersionString;
        }
        catch (Exception ex)
        {
            mpvVersion = "not loaded — " + ex.Message;
        }

        column.Controls.Add(Heading("Win4KSS"));
        column.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(650, 0),
            Text = $"""
                    4K aerial screen saver for Windows.

                    libmpv client API : {mpvVersion}
                    Settings file     : {AppPaths.SettingsFile}
                    Log file          : {AppPaths.LogFile}
                    mpv log           : {Path.Combine(AppPaths.Root, "mpv.log")}
                    Cache             : {_settings.ResolvedCacheDirectory}
                    """,
        });

        column.Controls.Add(Row(
            Btn("Open log folder", (_, _) => OpenFolder(AppPaths.Root), 140)));

        column.Controls.Add(Hint(
            "Apple's aerial CDN presents a certificate chaining to Apple Root CA, which Windows does not " +
            "trust out of the box. Rather than disabling verification, this app pins Apple's real root for " +
            "its own downloads and hands mpv a certificate bundle of every Windows-trusted root plus Apple's."));

        page.Controls.Add(column);
        return page;
    }

    private Control BuildButtonBar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(10, 8, 10, 8),
        };

        var ok = Btn("OK", (_, _) => { SaveSettings(); Close(); }, 100);
        var cancel = Btn("Cancel", (_, _) => Close(), 100);

        bar.Controls.Add(cancel);
        bar.Controls.Add(ok);
        bar.Controls.Add(_status);

        AcceptButton = ok;
        CancelButton = cancel;
        return bar;
    }

    // ---- load / save -------------------------------------------------------------------------

    private void LoadSettings()
    {
        _sourceMode.Items.AddRange(["Apple aerials", "Local folders", "Apple aerials and local folders"]);

        _quality.Items.AddRange([
            "Automatic (match this display)",
            "4K SDR — about 350 MB per clip",
            "4K HDR — about 350 MB per clip",
            "1080p SDR",
            "1080p HDR",
            "1080p H.264 (most compatible)",
        ]);
        _quality.SelectedIndex = (int)_settings.Quality;

        _multiMonitor.Items.AddRange([
            "Video on the main display only",
            "Same video on every display",
            "A different video on each display",
            "One video spanning all displays",
        ]);
        _multiMonitor.SelectedIndex = _settings.MultiMonitorMode switch
        {
            MultiMonitorMode.SameOnEach => 1,
            MultiMonitorMode.DifferentVideos => 2,
            MultiMonitorMode.SpanAll => 3,
            _ => 0,
        };

        _shuffle.Checked = _settings.Shuffle;
        _timeOfDay.Checked = _settings.UseTimeOfDay;

        _cacheEnabled.Checked = _settings.CacheEnabled;
        _cacheFolder.Text = _settings.ResolvedCacheDirectory;
        _budget.Value = (decimal)Math.Clamp(_settings.CacheBudgetGb, 1, 500);

        foreach (var folder in _settings.LocalFolders)
            _folders.Items.Add(folder);

        foreach (var source in SourceRegistry.Available())
            _catalogs.Items.Add($"{source.DisplayName}  ({source.ClipCount:N0} clips)");
        SyncSourceChecks();

        _desktopShortcut.Checked = ShortcutManager.DesktopShortcutExists;
        _startMenuShortcut.Checked = ShortcutManager.StartMenuShortcutExists;
        _hotkey.Text = ShortcutManager.ReadHotkey(ShortcutManager.DesktopPath)
                       ?? ShortcutManager.ReadHotkey(ShortcutManager.StartMenuPath)
                       ?? ShortcutManager.DefaultHotkey;

        _cache = new VideoCache(_settings);
        _queue = new DownloadQueue(_cache);
        _pins = new PinManager(_settings, _cache, _queue);

        // Both events fire on background threads.
        _cache.Changed += (_, _) => PostCacheStats();
        _queue.JobChanged += (_, _) => PostCacheStats();
        _queue.Progress += (_, _) => PostCacheStats();
        _queue.Start();

        UpdateCacheStats();
    }

    private void PostCacheStats()
    {
        if (!IsHandleCreated || IsDisposed)
            return;
        try
        {
            BeginInvoke(UpdateCacheStats);
        }
        catch (InvalidOperationException)
        {
            // Closing.
        }
    }

    private void SaveSettings()
    {
        _settings.Quality = (VideoQuality)_quality.SelectedIndex;
        _settings.MultiMonitorMode = _multiMonitor.SelectedIndex switch
        {
            1 => MultiMonitorMode.SameOnEach,
            2 => MultiMonitorMode.DifferentVideos,
            3 => MultiMonitorMode.SpanAll,
            _ => MultiMonitorMode.MainOnly,
        };

        _settings.Shuffle = _shuffle.Checked;
        _settings.UseTimeOfDay = _timeOfDay.Checked;
        _settings.CacheEnabled = _cacheEnabled.Checked;
        _settings.CacheDirectory = _cacheFolder.Text;
        _settings.CacheBudgetGb = (double)_budget.Value;

        _settings.LocalFolders = _folders.Items.Cast<string>().ToList();

        SaveRotation();

        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save settings:\n\n" + ex.Message, "Win4KSS",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        ApplyShortcuts();
    }

    private void ApplyShortcuts()
    {
        var hotkey = string.IsNullOrWhiteSpace(_hotkey.Text) ? null : _hotkey.Text;

        // Shortcuts must target an .exe, never the installed .scr — see ShortcutManager.
        if (_desktopShortcut.Checked || _startMenuShortcut.Checked)
            ShortcutManager.EnsureCompanionExe();

        if (_desktopShortcut.Checked)
            ShortcutManager.Create(ShortcutManager.DesktopPath, hotkey);
        else
            ShortcutManager.Remove(ShortcutManager.DesktopPath);

        if (_startMenuShortcut.Checked)
            ShortcutManager.Create(ShortcutManager.StartMenuPath, hotkey);
        else
            ShortcutManager.Remove(ShortcutManager.StartMenuPath);
    }

    // ---- clips -------------------------------------------------------------------------------

    private async Task ReloadClipsAsync(bool force = false)
    {
        _work.Cancel();
        _work = new CancellationTokenSource();

        _status.Text = "Loading clips…";
        _clipTree.Enabled = false;

        try
        {
            // Read the pending folders and quality, not the saved ones, so Refresh reflects
            // what is on screen rather than what was last written to disk.
            var probe = new AppSettings
            {
                Quality = (VideoQuality)Math.Max(0, _quality.SelectedIndex),
                LocalFolders = _folders.Items.Cast<string>().ToList(),
                ManifestUrlOverride = _settings.ManifestUrlOverride,
            };

            _library = await ClipLibrary.LoadAsync(probe, _work.Token);
            _infoByKey = _library.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            // Settings v2 needs the catalogue: migrate v1 and merge in new clips once, on the
            // first load. Saved with everything else on OK.
            if (!_prepared && _library.Count > 0)
            {
                _prepared = true;
                if (SettingsMigrator.Prepare(_settings, _library))
                    SyncSourceChecks();
            }

            // The tree lists the clips of the sources ticked on the Sources tab.
            var sources = CheckedSources();
            _clips = ClipResolver.ToClips(_library.Where(c => Rotation.SourcesOf(c).Overlaps(sources)), probe.Quality);
            BuildTree();
            _status.Text = "";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh.
        }
        catch (Exception ex)
        {
            _status.Text = "Could not load clips";
            Log.Error("Settings: clip load failed", ex);
        }
        finally
        {
            _clipTree.Enabled = true;
        }
    }

    private void BuildTree()
    {
        _clipTree.BeginUpdate();
        _clipTree.Nodes.Clear();

        foreach (var group in _clips.GroupBy(c => c.Category).OrderBy(g => g.Key))
        {
            var parent = new TreeNode(group.Key);

            foreach (var clip in group.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase))
            {
                var node = new TreeNode(clip.Title) { Tag = clip.Id, Checked = DefaultChecked(clip.Id) };
                parent.Nodes.Add(node);
            }

            parent.Checked = parent.Nodes.Cast<TreeNode>().All(n => n.Checked);
            _clipTree.Nodes.Add(parent);
        }

        // Expanded by default: with four categories the collapsed view hides everything the
        // tab exists for, and a collapsed node's children are not reachable by keyboard or
        // accessibility tooling either.
        _clipTree.ExpandAll();
        if (_clipTree.Nodes.Count > 0)
            _clipTree.TopNode = _clipTree.Nodes[0];

        _clipTree.EndUpdate();
        UpdateClipSummary();
    }

    private bool _updatingChecks;

    private void OnTreeChecked(object? sender, TreeViewEventArgs e)
    {
        // Guard against the cascade re-entering this handler for every child we tick.
        if (_updatingChecks || e.Node is null)
            return;

        _updatingChecks = true;
        try
        {
            if (e.Node.Nodes.Count > 0)
            {
                foreach (TreeNode child in e.Node.Nodes)
                    child.Checked = e.Node.Checked;
            }
            else if (e.Node.Parent is { } parent)
            {
                parent.Checked = parent.Nodes.Cast<TreeNode>().All(n => n.Checked);
            }
        }
        finally
        {
            _updatingChecks = false;
            UpdateClipSummary();
        }
    }

    private void SetAllChecked(bool value)
    {
        _updatingChecks = true;
        try
        {
            foreach (TreeNode parent in _clipTree.Nodes)
            {
                parent.Checked = value;
                foreach (TreeNode child in parent.Nodes)
                    child.Checked = value;
            }
        }
        finally
        {
            _updatingChecks = false;
            UpdateClipSummary();
        }
    }

    private List<string> CollectCheckedClipIds()
    {
        var ids = new List<string>();

        foreach (TreeNode parent in _clipTree.Nodes)
        {
            foreach (TreeNode child in parent.Nodes)
            {
                if (child.Checked && child.Tag is string id)
                    ids.Add(id);
            }
        }

        return ids;
    }

    // ---- settings v2 mapping (this dialog predates the per-clip rotation; the clip browser
    // replaces it). Ticked sources = AutoAddSources; ticked clips = RotationKeys.

    /// <summary>Ticks the source controls from <see cref="AppSettings.AutoAddSources"/>.</summary>
    private void SyncSourceChecks()
    {
        var apple = _settings.AutoAddSources.Contains(AppleAerialSource.SourceId);
        var local = _settings.AutoAddSources.Contains(Rotation.LocalSourceId);
        _sourceMode.SelectedIndex = apple && local ? 2 : local ? 1 : 0;

        var available = SourceRegistry.Available();
        for (var i = 0; i < _catalogs.Items.Count && i < available.Count; i++)
            _catalogs.SetItemChecked(i, _settings.AutoAddSources.Contains(available[i].Id));
    }

    /// <summary>The sources ticked on the Sources tab, as rotation source ids.</summary>
    private HashSet<string> CheckedSources()
    {
        var sources = new HashSet<string>(StringComparer.Ordinal);
        if (_sourceMode.SelectedIndex is 0 or 2 or -1)
            sources.Add(AppleAerialSource.SourceId);
        if (_sourceMode.SelectedIndex is 1 or 2)
            sources.Add(Rotation.LocalSourceId);

        var available = SourceRegistry.Available();
        for (var i = 0; i < _catalogs.Items.Count && i < available.Count; i++)
        {
            if (_catalogs.GetItemChecked(i))
                sources.Add(available[i].Id);
        }
        return sources;
    }

    /// <summary>In the rotation, or from a source just ticked here (whose clips all join).</summary>
    private bool DefaultChecked(string key) =>
        _settings.RotationKeys.Contains(key) ||
        (_infoByKey.TryGetValue(key, out var info) && !Rotation.SourcesOf(info).Overlaps(_settings.AutoAddSources));

    /// <summary>
    /// Ticked sources become <see cref="AppSettings.AutoAddSources"/>. Clips of unticked sources
    /// leave the rotation; clips in the tree follow their tick; clips of a newly ticked source
    /// not yet in the tree (no Refresh since) all join.
    /// </summary>
    private void SaveRotation()
    {
        var sources = CheckedSources();
        var before = new HashSet<string>(_settings.AutoAddSources, StringComparer.Ordinal);
        foreach (var id in Rotation.SourceIds)
            Rotation.SetAutoAdd(_settings, id, sources.Contains(id));

        if (_library.Count == 0)
            return; // the catalogue never loaded; leave the rotation alone

        var shown = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (TreeNode parent in _clipTree.Nodes)
        {
            foreach (TreeNode child in parent.Nodes)
            {
                if (child.Tag is string id)
                    shown[id] = child.Checked;
            }
        }

        foreach (var info in _library)
        {
            var of = Rotation.SourcesOf(info);
            if (!of.Overlaps(sources))
                Rotation.SetInRotation(_settings, [info.Key], false);
            else if (shown.TryGetValue(info.Key, out var ticked))
                Rotation.SetInRotation(_settings, [info.Key], ticked);
            else if (!of.Overlaps(before))
                Rotation.SetInRotation(_settings, [info.Key], true);
        }
    }

    private void UpdateClipSummary()
    {
        var checkedCount = _clipTree.Nodes.Cast<TreeNode>()
                                          .SelectMany(p => p.Nodes.Cast<TreeNode>())
                                          .Count(n => n.Checked);

        _clipSummary.Text = $"   {checkedCount:N0} of {_clips.Count:N0} clips selected";
    }

    // ---- actions -----------------------------------------------------------------------------

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose a folder of video files" };
        if (dialog.ShowDialog(this) == DialogResult.OK && !_folders.Items.Contains(dialog.SelectedPath))
            _folders.Items.Add(dialog.SelectedPath);
    }

    private void RemoveFolder()
    {
        if (_folders.SelectedIndex >= 0)
            _folders.Items.RemoveAt(_folders.SelectedIndex);
    }

    private void ChangeCacheFolder()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _cacheFolder.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _cacheFolder.Text = dialog.SelectedPath;
    }

    private void OpenCacheFolder() => OpenFolder(_cacheFolder.Text);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Settings: could not open {path}", ex);
        }
    }

    private void PurgeCache()
    {
        if (_cache is null)
            return;

        var stats = _cache.Stats();
        var message = $"Delete {VideoCache.FormatSize(stats.RollingBytes)} of cached video?";
        if (stats.PinnedFiles > 0)
            message += $"\n\n{stats.PinnedFiles:N0} clips kept offline ({VideoCache.FormatSize(stats.PinnedBytes)}) are not touched.";

        if (MessageBox.Show(this, message, "Purge cache", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            != DialogResult.Yes)
            return;

        _cache.PurgeUnpinned();
        UpdateCacheStats();
    }

    /// <summary>
    /// "Keep offline: all filtered" for the old dialog: pins every clip ticked on the Clips
    /// tab, refusing (with an offer to raise the limit) if they would not fit the budget.
    /// </summary>
    private async Task KeepSelectedOfflineAsync()
    {
        if (_pins is null || _clips.Count == 0)
            return;

        var keys = CollectCheckedClipIds();
        if (keys.Count == 0)
            return;

        _status.Text = "Reading the catalogue…";
        try
        {
            _pins.SetCatalog(await ClipLibrary.LoadAsync(_settings, _work.Token));
        }
        catch (Exception ex)
        {
            Log.Error("Settings: catalogue load for pinning failed", ex);
            _status.Text = "Could not read the catalogue";
            return;
        }
        _status.Text = "";

        var check = _pins.CanPin(keys);
        if (check.Added.Count == 0)
        {
            MessageBox.Show(this, "Every selected clip is already kept offline (or is a local file).", "Keep offline",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!check.Ok)
        {
            var raise = MessageBox.Show(this,
                $"Keeping {check.Added.Count:N0} more clips offline needs {VideoCache.FormatSize(check.RequiredBytes)} " +
                $"for all kept clips, over the {VideoCache.FormatSize(check.BudgetBytes)} limit.\n\n" +
                $"Raise the limit to {check.RequiredGb} GB?",
                "Keep offline", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (raise != DialogResult.Yes)
                return;

            _settings.CacheBudgetGb = check.RequiredGb;
            _budget.Value = Math.Clamp(check.RequiredGb, _budget.Minimum, _budget.Maximum);
        }
        else if (MessageBox.Show(this,
                     $"Download and keep {check.Added.Count:N0} clips offline " +
                     $"({VideoCache.FormatSize(check.RequiredBytes)} kept in all)?",
                     "Keep offline", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        var result = _pins.Pin(keys);
        if (!result.Ok)
        {
            MessageBox.Show(this, $"Still does not fit: {VideoCache.FormatSize(result.RequiredBytes)} needed.",
                "Keep offline", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateCacheStats();
    }

    private void UpdateCacheStats()
    {
        if (_cache is null)
            return;

        var stats = _cache.Stats();
        var pending = _queue?.Snapshot().Where(s => s.Priority == DownloadPriority.Pin).ToList() ?? [];
        var active = pending.FirstOrDefault(s => s.State == DownloadState.Downloading);

        _cacheStats.Text =
            $"Kept offline: {VideoCache.FormatSize(stats.PinnedBytes)} ({stats.PinnedFiles:N0})    " +
            $"Rolling: {VideoCache.FormatSize(stats.RollingBytes)} ({stats.RollingFiles:N0})    " +
            $"Free on drive: {VideoCache.FormatSize(stats.FreeDiskBytes)}" +
            (pending.Count > 0
                ? $"\n{pending.Count:N0} to download" +
                  (active is { Fraction: { } f } ? $" — {active.Title} {f:P0}" : "") +
                  (_queue is { OwnsPins: false } ? " (paused: another Win4KSS window is downloading)" : "")
                : "");
    }

    private void StartNow()
    {
        LaunchContext.MarkManualLaunch();

        if (!NativeMethods.StartConfiguredScreenSaver())
        {
            MessageBox.Show(this, "Windows did not accept the request to start the screen saver.",
                "Start now", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
