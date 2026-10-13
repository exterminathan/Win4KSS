using System.Text;
using Win4K.Core;

namespace Win4K.Tests;

/// <summary>The settings export/import file (<see cref="SettingsPortability"/>): pure logic, no app-data root touched.</summary>
public class SettingsPortabilityTests : IDisposable
{
    private static readonly DateTime When = new(2026, 10, 13, 12, 30, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "win4k-portable-" + Guid.NewGuid().ToString("N")[..8]);

    public SettingsPortabilityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static ClipInfo Clip(string key, string source, string title) =>
        new() { Key = key, SourceId = source, CatalogId = source, Title = title, Urls = new ClipUrls(Uhd4KSdr: "https://x.example/" + Uri.EscapeDataString(key)) };

    private static readonly List<ClipInfo> Catalogue =
    [
        Clip("apple:B", "apple", "Hawaii Coastline"),
        Clip("apple:A", "apple", "Alaska Dawn"),
        Clip("ia:Fire/Fire 66 - 4k.mp4", "archive-org", "Fire 66 \"Blaze\" & <more>"),
        Clip("nasa:earth", "nasa", "Earth \u00e9toile \u2605"),
        Clip("amazon:content-1", "aerial-manifests", "Amazon Reef"),
    ];

    private static AppSettings Settings() => new()
    {
        Quality = VideoQuality.Uhd4KHdr,
        MultiMonitorMode = MultiMonitorMode.SameOnEach,
        Shuffle = false,
        UseTimeOfDay = false,
        CacheEnabled = true,
        CacheBudgetGb = 42.5,
        CacheDirectory = @"D:\Cache",
        LocalFolders = [@"D:\Videos", @"E:\More"],
        FfmpegPath = @"C:\ffmpeg\ffmpeg.exe",
        AutoAddSources = new(StringComparer.Ordinal) { "apple", "nasa" },
        RotationKeys = new(StringComparer.Ordinal) { "apple:B", "apple:A", "ia:Fire/Fire 66 - 4k.mp4", "nasa:earth", "apple:GONE" },
        KeptOffline = new(StringComparer.Ordinal) { "apple:A", "nasa:earth" },
        KnownKeys = new(StringComparer.Ordinal) { "apple:KNOWN-ONLY-HERE" },
        FailedClips = new Dictionary<string, int>(StringComparer.Ordinal) { ["apple:B"] = 5 },
    };

    private static string Export(AppSettings s, DateTime? when = null, PortableShortcuts? shortcuts = null) =>
        SettingsPortability.Write(SettingsPortability.Build(s, Catalogue, shortcuts ?? new PortableShortcuts(true, false, "CTRL+ALT+S"), "1.2.3.4", when ?? When));

    private static PortableSettings Parse(string text)
    {
        Assert.True(SettingsPortability.TryParse(text, out var parsed, out var error), error);
        return parsed!;
    }

    // ---- round trip and format ----------------------------------------------------------------

    [Fact]
    public void A_round_trip_keeps_every_choice()
    {
        var original = Settings();
        var file = Parse(Export(original));

        Assert.Equal(1, file.FormatVersion);
        Assert.Equal("1.2.3.4", file.AppVersion);
        Assert.Equal(When, file.ExportedUtc);

        var target = new AppSettings();
        SettingsPortability.Apply(target, file, includeMachine: true);

        Assert.Equal(VideoQuality.Uhd4KHdr, target.Quality);
        Assert.Equal(MultiMonitorMode.SameOnEach, target.MultiMonitorMode);
        Assert.False(target.Shuffle);
        Assert.False(target.UseTimeOfDay);
        Assert.Equal(42.5, target.CacheBudgetGb);
        Assert.Equal(original.AutoAddSources.Order(), target.AutoAddSources.Order());
        Assert.Equal(original.RotationKeys.Order(), target.RotationKeys.Order());
        Assert.Equal(original.KeptOffline.Order(), PortableSettings.KeysOf(file.KeptOffline).Order());
        Assert.Equal(new PortableShortcuts(true, false, "CTRL+ALT+S"), file.Shortcuts);
        Assert.Equal(@"D:\Cache", target.CacheDirectory);
        Assert.Equal([@"D:\Videos", @"E:\More"], target.LocalFolders);
        Assert.Equal(@"C:\ffmpeg\ffmpeg.exe", target.FfmpegPath);
    }

    [Fact]
    public void Clips_are_grouped_by_source_with_catalogue_titles_one_line_each()
    {
        var text = Export(Settings());

        // One line per clip, key and title together, nothing else on it.
        var clipLines = text.Split('\n').Where(l => l.Contains("\"key\":")).ToList();
        Assert.Equal(5 + 2, clipLines.Count); // rotation + kept offline
        Assert.All(clipLines, l =>
        {
            Assert.Contains("\"title\":", l);
            Assert.Matches(@"^\s+\{ ""key"": "".*"", ""title"": "".*"" \},?$", l);
        });
        Assert.Contains("{ \"key\": \"apple:A\", \"title\": \"Alaska Dawn\" }", text);

        var parsed = Parse(text);
        // Sources in the app's own order; within a source, by title (a missing title is its key).
        Assert.Equal(Rotation.SourceIds.Where(id => id is "apple" or "archive-org" or "nasa"), parsed.Rotation!.Select(g => g.Source));
        Assert.Equal(["apple:A", "apple:GONE", "apple:B"], parsed.Rotation![0].Clips.Select(c => c.Key));
    }

    [Fact]
    public void Titles_with_quotes_symbols_and_unicode_stay_readable_and_round_trip()
    {
        var text = Export(Settings());
        Assert.Contains("Earth \u00e9toile \u2605", text); // not \u00e9 escapes
        Assert.Contains("Fire 66 \\\"Blaze\\\" & <more>", text);

        var parsed = Parse(text);
        Assert.Contains(parsed.Rotation!.SelectMany(g => g.Clips), c => c.Title == "Fire 66 \"Blaze\" & <more>");
    }

    [Fact]
    public void A_clip_missing_from_the_catalogue_uses_its_key_as_the_title_and_a_source_from_its_namespace()
    {
        var parsed = Parse(Export(Settings()));
        var gone = parsed.Rotation!.SelectMany(g => g.Clips).Single(c => c.Key == "apple:GONE");
        Assert.Equal("apple:GONE", gone.Title);
        Assert.Equal("apple", parsed.Rotation!.Single(g => g.Clips.Contains(gone)).Source);

        var s = new AppSettings { RotationKeys = new(StringComparer.Ordinal) { "ia:X/y.mp4", "local:C:\\v\\a.mp4", "weird" } };
        var groups = SettingsPortability.Build(s, [], null, null, When).Rotation!;
        Assert.Equal(["archive-org", "local", "other"], groups.Select(g => g.Source));
    }

    [Fact]
    public void The_same_settings_give_the_same_bytes_apart_from_the_export_date()
    {
        var a = Export(Settings(), When);
        var b = Export(Settings(), When);
        Assert.Equal(a, b);

        var later = Export(Settings(), When.AddDays(30));
        Assert.NotEqual(a, later);
        Assert.Equal(a.Replace("2026-10-13T12:30:00Z", "X"), later.Replace("2026-11-12T12:30:00Z", "X"));

        // Independent of the order the sets happen to enumerate in.
        var shuffled = Settings();
        shuffled.RotationKeys = new(shuffled.RotationKeys.Reverse(), StringComparer.Ordinal);
        Assert.Equal(a, Export(shuffled, When));
    }

    [Fact]
    public void The_file_is_pretty_utf8_without_a_bom_with_the_documented_sections_in_order()
    {
        var path = Path.Combine(_dir, "out" + SettingsPortability.FileExtension);
        SettingsPortability.WriteFile(SettingsPortability.Build(Settings(), Catalogue, null, "1.0", When), path);

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]); // no BOM
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain('\r', text);

        var order = new[] { "\"about\"", "\"playback\"", "\"cache\"", "\"sources\"", "\"machine\"", "\"keptOffline\"", "\"rotation\"" };
        var positions = order.Select(o => text.IndexOf("\n  " + o, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);

        Assert.Contains("\"format\": \"win4kss-settings\"", text);
        Assert.Contains("\"formatVersion\": 1", text);
        Assert.Contains("\"howToImport\"", text);
        Assert.Contains("\"exported\": \"2026-10-13T12:30:00Z\"", text);
        Assert.DoesNotContain("shortcuts", text); // none given, so no section
    }

    [Fact]
    public void Known_keys_and_failed_clips_are_never_exported()
    {
        var text = Export(Settings());
        Assert.DoesNotContain("KNOWN-ONLY-HERE", text);
        Assert.DoesNotContain("knownKeys", text);
        Assert.DoesNotContain("failedClips", text);
    }

    // ---- import ---------------------------------------------------------------------------------

    [Fact]
    public void The_machine_section_is_skipped_unless_asked_for()
    {
        var file = Parse(Export(Settings()));
        var target = new AppSettings { CacheDirectory = @"C:\Mine", LocalFolders = [@"C:\MyVideos"], FfmpegPath = null };

        var skipped = SettingsPortability.Apply(target, file, includeMachine: false);
        Assert.Equal(@"C:\Mine", target.CacheDirectory);
        Assert.Equal([@"C:\MyVideos"], target.LocalFolders);
        Assert.Null(target.FfmpegPath);
        Assert.False(skipped.CacheDirectoryChanged || skipped.LocalFoldersChanged || skipped.FfmpegPathChanged);
        Assert.Equal(42.5, target.CacheBudgetGb); // the rest still applies

        var applied = SettingsPortability.Apply(target, file, includeMachine: true);
        Assert.Equal(@"D:\Cache", target.CacheDirectory);
        Assert.True(applied.CacheDirectoryChanged && applied.LocalFoldersChanged && applied.FfmpegPathChanged);
    }

    [Fact]
    public void Unknown_sections_members_comments_and_trailing_commas_are_tolerated()
    {
        var text = """
        {
          // hand edited
          "about": { "format": "Win4KSS-Settings", "formatVersion": 7, "exported": "not a date", "newField": [1,2], },
          "playback": { "quality": "Hd1080Sdr", "shuffle": "yes please", "futureKnob": 3, "multiMonitorMode": "Bogus" },
          "cache": { "budgetGb": "lots" },
          "sources": { "autoAdd": ["apple", "no-such-source", 5] },
          "holograms": { "enabled": true },
          "rotation": { "apple": [ { "key": "apple:A", "title": "Alaska", "rating": 5 }, "apple:B", { "title": "no key" }, 7 ], "future": "ignored" },
        }
        """;
        var file = Parse(text);

        Assert.Equal(7, file.FormatVersion);
        Assert.Null(file.ExportedUtc);
        Assert.Equal(VideoQuality.Hd1080Sdr, file.Quality);
        Assert.Null(file.Shuffle);              // wrong type: absent
        Assert.Null(file.MultiMonitorMode);     // not a member
        Assert.Null(file.CacheBudgetGb);
        Assert.Null(file.Shortcuts);
        Assert.Null(file.Machine);
        Assert.Null(file.KeptOffline);
        Assert.Equal(["apple:A", "apple:B"], PortableSettings.KeysOf(file.Rotation));

        var target = new AppSettings { Shuffle = true, CacheBudgetGb = 9, KeptOffline = new(StringComparer.Ordinal) { "apple:A" } };
        var applied = SettingsPortability.Apply(target, file, includeMachine: false);
        Assert.Equal(VideoQuality.Hd1080Sdr, target.Quality);
        Assert.True(target.Shuffle);                                  // untouched
        Assert.Equal(9, target.CacheBudgetGb);
        Assert.Equal(new[] { "apple" }, target.AutoAddSources);       // unknown ids dropped
        Assert.Null(applied.PinKeys);                                 // no section: pins left alone
        Assert.Contains("apple:A", target.KeptOffline);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{}")]
    [InlineData("{\"about\":{}}")]
    [InlineData("{\"about\":{\"format\":\"something-else\",\"formatVersion\":1}}")]
    [InlineData("{\"about\":{\"format\":\"win4kss-settings\"}}")]
    [InlineData("{\"about\":{\"format\":\"win4kss-settings\",\"formatVersion\":0}}")]
    [InlineData("{\"about\":{\"format\":\"win4kss-settings\",\"formatVersion\":\"1\"}}")]
    [InlineData("{\"version\":2,\"quality\":\"Auto\",\"rotationKeys\":[\"apple:A\"]}")] // a raw settings.json
    public void Anything_that_is_not_this_format_is_rejected_with_a_message(string text)
    {
        Assert.False(SettingsPortability.TryParse(text, out var parsed, out var error));
        Assert.Null(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_missing_or_unreadable_file_is_reported_not_thrown()
    {
        Assert.False(SettingsPortability.TryParseFile(Path.Combine(_dir, "nope.json"), out _, out var error));
        Assert.Contains("does not exist", error);

        var path = Path.Combine(_dir, "ok.win4kss.json");
        SettingsPortability.WriteFile(SettingsPortability.Build(Settings(), Catalogue, null, null, When), path);
        Assert.True(SettingsPortability.TryParseFile(path, out var parsed, out _));
        Assert.NotNull(parsed);

        // A BOM (an editor added one) is fine.
        File.WriteAllText(path, File.ReadAllText(path), new UTF8Encoding(true));
        Assert.True(SettingsPortability.TryParseFile(path, out _, out _));
    }

    [Fact]
    public void Keys_the_catalogue_does_not_list_stay_in_the_rotation_and_are_counted()
    {
        var file = Parse(Export(Settings()));
        var target = new AppSettings { RotationKeys = new(StringComparer.Ordinal) { "apple:OLD" } };

        var plan = SettingsPortability.Plan(target, file, Catalogue, null);
        Assert.Equal(1, plan.RotationBefore);
        Assert.Equal(5, plan.RotationAfter);
        Assert.Equal(5, plan.RotationAdded);
        Assert.Equal(1, plan.RotationRemoved);
        Assert.Equal(1, plan.RotationNotInCatalogue); // apple:GONE
        Assert.Equal(0, plan.PinsBefore);
        Assert.Equal(2, plan.PinsAfter);
        Assert.Equal(0, plan.PinsNotInCatalogue);

        var applied = SettingsPortability.Apply(target, file, includeMachine: false);
        Assert.Contains("apple:GONE", target.RotationKeys);
        Assert.DoesNotContain("apple:OLD", target.RotationKeys);
        Assert.Equal(["apple:OLD"], applied.RotationRemoved);
        Assert.Equal(5, applied.RotationAdded.Count);
        Assert.Equal(["apple:A", "nasa:earth"], applied.PinKeys!.Order());
    }

    [Fact]
    public void Import_never_touches_known_keys_or_failed_clips()
    {
        var file = Parse(Export(Settings()));
        var target = new AppSettings
        {
            KnownKeys = new(StringComparer.Ordinal) { "apple:MINE" },
            FailedClips = new Dictionary<string, int>(StringComparer.Ordinal) { ["apple:X"] = 3 },
        };

        SettingsPortability.Apply(target, file, includeMachine: true);

        Assert.Equal(["apple:MINE"], target.KnownKeys);
        Assert.Equal(3, target.FailedClips["apple:X"]);
        Assert.Single(target.FailedClips);
    }

    [Fact]
    public void An_empty_section_clears_but_an_absent_one_leaves_alone()
    {
        var target = new AppSettings
        {
            RotationKeys = new(StringComparer.Ordinal) { "apple:A" },
            AutoAddSources = new(StringComparer.Ordinal) { "apple" },
        };

        SettingsPortability.Apply(target, Parse("{\"about\":{\"format\":\"win4kss-settings\",\"formatVersion\":1}}"), false);
        Assert.Single(target.RotationKeys);
        Assert.Single(target.AutoAddSources);

        SettingsPortability.Apply(target, Parse("{\"about\":{\"format\":\"win4kss-settings\",\"formatVersion\":1},\"rotation\":{},\"sources\":{\"autoAdd\":[]}}"), false);
        Assert.Empty(target.RotationKeys);
        Assert.Empty(target.AutoAddSources);
    }

    [Fact]
    public void The_plan_lists_each_setting_that_would_change_and_only_those()
    {
        var file = Parse(Export(Settings()));
        var current = new AppSettings
        {
            Quality = VideoQuality.Uhd4KHdr, // same
            MultiMonitorMode = MultiMonitorMode.MainOnly,
            Shuffle = false,                 // same
            CacheBudgetGb = 20,
        };

        var plan = SettingsPortability.Plan(current, file, Catalogue, new PortableShortcuts(false, false, ""));

        Assert.Contains("Multi-monitor: MainOnly → SameOnEach", plan.Changes);
        Assert.Contains("Time of day: on → off", plan.Changes);
        Assert.Contains("Cache limit: 20 → 42.5 GB", plan.Changes);
        Assert.Contains("Desktop shortcut: off → on", plan.Changes);
        Assert.Contains(plan.Changes, c => c.StartsWith("Hotkey: none → CTRL+ALT+S"));
        Assert.Contains(plan.Changes, c => c.StartsWith("Auto-add sources:"));
        Assert.DoesNotContain(plan.Changes, c => c.StartsWith("Quality") || c.StartsWith("Shuffle"));

        Assert.True(plan.HasMachine);
        Assert.Equal(3, plan.MachineChanges.Count);
        Assert.Contains("Local folders: 0 → 2", plan.MachineChanges);
    }

    [Fact]
    public void Backing_up_copies_settings_json_and_replaces_the_previous_backup()
    {
        var settings = Path.Combine(_dir, "settings.json");
        File.WriteAllText(settings, "first");
        Assert.True(SettingsPortability.BackUp(settings));
        File.WriteAllText(settings, "second");
        Assert.True(SettingsPortability.BackUp(settings));

        Assert.Equal("second", File.ReadAllText(Path.Combine(_dir, SettingsPortability.BackupFileName)));
        File.Delete(settings);
        Assert.False(SettingsPortability.BackUp(settings));
    }
}
