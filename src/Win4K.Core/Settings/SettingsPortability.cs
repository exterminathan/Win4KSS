using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Win4K.Core.Catalogs;

namespace Win4K.Core;

/// <summary>One clip in an export. Only <see cref="Key"/> is used on import; <see cref="Title"/> is for the human reading the file.</summary>
public sealed record PortableClip(string Key, string Title);

/// <summary>The clips of one source, as grouped in the file. <see cref="Source"/> is a rotation source id, or "other".</summary>
public sealed record PortableGroup(string Source, IReadOnlyList<PortableClip> Clips);

/// <summary>The walk-away shortcuts. Null members were absent from the file. Hotkey is in .lnk form ("CTRL+ALT+S"); "" means none.</summary>
public sealed record PortableShortcuts(bool? Desktop, bool? StartMenu, string? Hotkey);

/// <summary>
/// Values that only make sense on the machine they were exported from. Import skips them unless
/// asked. Null means "the default" (no custom cache folder, ffmpeg searched for).
/// </summary>
public sealed record PortableMachine(string? CacheDirectory, IReadOnlyList<string> LocalFolders, string? FfmpegPath);

/// <summary>
/// A settings export in memory. A null section or value means the file did not say, and import
/// leaves that setting alone; so a file written by an older or newer build still imports.
/// </summary>
public sealed class PortableSettings
{
    public int FormatVersion { get; init; } = SettingsPortability.FormatVersion;
    public string? AppVersion { get; init; }
    public DateTime? ExportedUtc { get; init; }

    public VideoQuality? Quality { get; init; }
    public MultiMonitorMode? MultiMonitorMode { get; init; }
    public bool? Shuffle { get; init; }
    public bool? UseTimeOfDay { get; init; }

    public bool? CacheEnabled { get; init; }
    public double? CacheBudgetGb { get; init; }

    public IReadOnlyList<string>? AutoAddSources { get; init; }
    public PortableShortcuts? Shortcuts { get; init; }
    public PortableMachine? Machine { get; init; }

    public IReadOnlyList<PortableGroup>? Rotation { get; init; }
    public IReadOnlyList<PortableGroup>? KeptOffline { get; init; }

    public static IReadOnlyList<string> KeysOf(IReadOnlyList<PortableGroup>? groups) =>
        groups is null
            ? []
            : groups.SelectMany(g => g.Clips).Select(c => c.Key).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>What importing a file would change, for the confirmation dialog. Computed without changing anything.</summary>
public sealed record ImportPlan(
    int RotationBefore, int RotationAfter, int RotationAdded, int RotationRemoved, int RotationNotInCatalogue,
    int PinsBefore, int PinsAfter, int PinsNotInCatalogue,
    IReadOnlyList<string> Changes,
    bool HasMachine,
    IReadOnlyList<string> MachineChanges);

/// <summary>What <see cref="SettingsPortability.Apply"/> did to the settings object, so the caller can do the side effects.</summary>
public sealed class ImportApplied
{
    public bool QualityChanged { get; init; }
    public bool BudgetLowered { get; init; }
    public bool CacheDirectoryChanged { get; init; }
    public bool FfmpegPathChanged { get; init; }
    public bool LocalFoldersChanged { get; init; }
    public IReadOnlyList<string> RotationAdded { get; init; } = [];
    public IReadOnlyList<string> RotationRemoved { get; init; } = [];

    /// <summary>The kept-offline keys the file lists (null: it has no such section). Not applied: pins go through PinManager.</summary>
    public IReadOnlyList<string>? PinKeys { get; init; }
}

/// <summary>
/// Moving a setup to another machine with one readable file (<c>*.win4kss.json</c>): the user's
/// choices only. A new machine rebuilds the catalogues itself, so none are exported.
///
/// <para><b>Format.</b> UTF-8 without a BOM, pretty-printed, stable key order, LF line endings:
/// <c>about</c>, <c>playback</c>, <c>cache</c>, <c>sources</c>, <c>shortcuts</c>, <c>machine</c>,
/// <c>keptOffline</c>, <c>rotation</c> (the long one last). The two clip lists are grouped by source
/// and every clip is ONE line, <c>{ "key": "…", "title": "…" }</c>, so a 1,400-clip rotation stays
/// scannable and diffs line by line. Written by hand: System.Text.Json's indenting would spend
/// four lines on every clip. Parsing uses System.Text.Json and tolerates unknown or missing
/// sections, comments and trailing commas.</para>
///
/// <para><b>Not exported.</b> <see cref="AppSettings.KnownKeys"/>: it is the new machine's own record
/// of which clips it has seen, and importing the old one would hide clips that are new there from
/// auto-add. <see cref="AppSettings.FailedClips"/>: health is an observation about this machine's
/// network, disk and decoder; carrying it over would bench clips (3 failures = unhealthy) on a
/// machine where they may work, and it re-learns within a few sessions anyway. The retired v1 fields
/// and <see cref="AppSettings.ManifestUrlOverride"/> (a debugging aid) are not choices worth moving.</para>
///
/// <para><b>Machine section.</b> Cache folder, local folders and the ffmpeg path are paths on the
/// old machine. They are written, clearly marked, and import skips them unless asked.</para>
/// </summary>
public static class SettingsPortability
{
    public const string FormatName = "win4kss-settings";
    public const int FormatVersion = 1;
    public const string FileExtension = ".win4kss.json";
    public const string BackupFileName = "settings.before-import.json";

    /// <summary>Keys the catalogue does not know are grouped under this when their source cannot be told.</summary>
    public const string OtherSource = "other";

    private const string What =
        "Win4KSS screen saver settings: your choices only (no videos, no catalogues). A new machine rebuilds the catalogues itself.";
    private const string HowToImport =
        "In Win4KSS open Settings > About > Import settings..., or run: Win4KSS.exe --import-settings \"<this file>\"";
    private const string MachineNote =
        "These values are specific to the machine this file was exported from. Import skips them unless you tick the option to apply them.";

    private static readonly JsonSerializerOptions StringOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ---- export ----------------------------------------------------------------------------------

    /// <summary>
    /// The export model for <paramref name="settings"/>. Titles come from <paramref name="catalogue"/>,
    /// falling back to the key; clips are grouped by source and sorted by title, so the same settings
    /// always produce the same file.
    /// </summary>
    public static PortableSettings Build(AppSettings settings, IEnumerable<ClipInfo> catalogue, PortableShortcuts? shortcuts,
                                         string? appVersion, DateTime exportedUtc)
    {
        var byKey = new Dictionary<string, ClipInfo>(StringComparer.Ordinal);
        foreach (var clip in catalogue)
            byKey.TryAdd(clip.Key, clip);

        return new PortableSettings
        {
            FormatVersion = FormatVersion,
            AppVersion = appVersion,
            ExportedUtc = exportedUtc,
            Quality = settings.Quality,
            MultiMonitorMode = settings.MultiMonitorMode,
            Shuffle = settings.Shuffle,
            UseTimeOfDay = settings.UseTimeOfDay,
            CacheEnabled = settings.CacheEnabled,
            CacheBudgetGb = settings.CacheBudgetGb,
            AutoAddSources = SortSources(settings.AutoAddSources),
            Shortcuts = shortcuts,
            Machine = new PortableMachine(
                string.IsNullOrWhiteSpace(settings.CacheDirectory) ? null : settings.CacheDirectory,
                settings.LocalFolders.ToList(),
                string.IsNullOrWhiteSpace(settings.FfmpegPath) ? null : settings.FfmpegPath),
            KeptOffline = Group(settings.KeptOffline, byKey),
            Rotation = Group(settings.RotationKeys, byKey),
        };
    }

    private static List<string> SortSources(IEnumerable<string> ids)
    {
        var order = Rotation.SourceIds.ToList();
        return ids.Distinct(StringComparer.Ordinal)
                  .OrderBy(id => order.IndexOf(id) is var i and >= 0 ? i : int.MaxValue)
                  .ThenBy(id => id, StringComparer.Ordinal)
                  .ToList();
    }

    private static List<PortableGroup> Group(IEnumerable<string> keys, Dictionary<string, ClipInfo> byKey)
    {
        var order = Rotation.SourceIds.ToList();
        return keys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.Ordinal)
            .Select(k => (Source: SourceOfKey(k, byKey), Clip: new PortableClip(k, byKey.TryGetValue(k, out var info) && !string.IsNullOrWhiteSpace(info.Title) ? info.Title : k)))
            .GroupBy(x => x.Source, StringComparer.Ordinal)
            .OrderBy(g => order.IndexOf(g.Key) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PortableGroup(g.Key, g.Select(x => x.Clip)
                .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Key, StringComparer.Ordinal)
                .ToList()))
            .ToList();
    }

    /// <summary>The rotation source a key belongs to: the catalogue's answer if it has the clip, else the key's namespace.</summary>
    public static string SourceOfKey(string key, IReadOnlyDictionary<string, ClipInfo> byKey)
    {
        if (byKey.TryGetValue(key, out var info))
            return Rotation.SourceOf(info);

        return ClipKey.NamespaceOf(key) switch
        {
            ClipKey.Apple => AppleAerialSource.SourceId,
            ClipKey.Archive => "archive-org",
            ClipKey.Nasa => "nasa",
            ClipKey.Amazon or ClipKey.Jetson or ClipKey.Robin => Rotation.AerialManifestsId,
            ClipKey.Local => Rotation.LocalSourceId,
            _ => OtherSource,
        };
    }

    /// <summary>The file's text: pretty, one line per clip, UTF-8 without a BOM when written (<see cref="WriteFile"/>).</summary>
    public static string Write(PortableSettings model)
    {
        var sb = new StringBuilder(64 * 1024);
        var rotationKeys = PortableSettings.KeysOf(model.Rotation).Count;
        var pinKeys = PortableSettings.KeysOf(model.KeptOffline).Count;

        sb.Append("{\n");
        sb.Append("  \"about\": {\n");
        Line(sb, 4, "what", What);
        Line(sb, 4, "app", "Win4KSS");
        Line(sb, 4, "appVersion", model.AppVersion ?? "");
        Line(sb, 4, "exported", (model.ExportedUtc ?? DateTime.UnixEpoch).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        Line(sb, 4, "format", FormatName);
        Raw(sb, 4, "formatVersion", model.FormatVersion.ToString(CultureInfo.InvariantCulture));
        Line(sb, 4, "summary", $"{rotationKeys} clips in the rotation, {pinKeys} kept offline. Informational: import reads the sections below, not this line.");
        Line(sb, 4, "howToImport", HowToImport, last: true);
        sb.Append("  },\n");

        sb.Append("  \"playback\": {\n");
        Line(sb, 4, "quality", (model.Quality ?? VideoQuality.Auto).ToString());
        Line(sb, 4, "multiMonitorMode", (model.MultiMonitorMode ?? MultiMonitorMode.MainOnly).ToString());
        Raw(sb, 4, "shuffle", Bool(model.Shuffle ?? true));
        Raw(sb, 4, "timeOfDay", Bool(model.UseTimeOfDay ?? true), last: true);
        sb.Append("  },\n");

        sb.Append("  \"cache\": {\n");
        Raw(sb, 4, "enabled", Bool(model.CacheEnabled ?? true));
        Raw(sb, 4, "budgetGb", Number(model.CacheBudgetGb ?? 20), last: true);
        sb.Append("  },\n");

        sb.Append("  \"sources\": {\n");
        sb.Append("    \"autoAdd\": ").Append(StringArray(model.AutoAddSources ?? [])).Append('\n');
        sb.Append("  },\n");

        if (model.Shortcuts is { } sc)
        {
            sb.Append("  \"shortcuts\": {\n");
            Raw(sb, 4, "desktop", Bool(sc.Desktop ?? false));
            Raw(sb, 4, "startMenu", Bool(sc.StartMenu ?? false));
            Raw(sb, 4, "hotkey", Str(sc.Hotkey ?? ""), last: true); // "" = no hotkey
            sb.Append("  },\n");
        }

        var machine = model.Machine ?? new PortableMachine(null, [], null);
        sb.Append("  \"machine\": {\n");
        Line(sb, 4, "note", MachineNote);
        Raw(sb, 4, "cacheDirectory", machine.CacheDirectory is null ? "null" : Str(machine.CacheDirectory));
        sb.Append("    \"localFolders\": ").Append(StringArray(machine.LocalFolders)).Append(",\n");
        Raw(sb, 4, "ffmpegPath", machine.FfmpegPath is null ? "null" : Str(machine.FfmpegPath), last: true);
        sb.Append("  },\n");

        ClipSection(sb, "keptOffline", model.KeptOffline ?? [], last: false);
        ClipSection(sb, "rotation", model.Rotation ?? [], last: true);
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>Writes the export to <paramref name="path"/>: UTF-8, no BOM, temp-then-move.</summary>
    public static void WriteFile(PortableSettings model, string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        File.WriteAllText(temp, Write(model), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, full, overwrite: true);
    }

    private static void ClipSection(StringBuilder sb, string name, IReadOnlyList<PortableGroup> groups, bool last)
    {
        if (groups.Count == 0)
        {
            sb.Append("  \"").Append(name).Append("\": {}").Append(last ? "\n" : ",\n");
            return;
        }

        sb.Append("  \"").Append(name).Append("\": {\n");
        for (var g = 0; g < groups.Count; g++)
        {
            sb.Append("    ").Append(Str(groups[g].Source)).Append(": [\n");
            var clips = groups[g].Clips;
            for (var c = 0; c < clips.Count; c++)
            {
                sb.Append("      { \"key\": ").Append(Str(clips[c].Key))
                  .Append(", \"title\": ").Append(Str(clips[c].Title)).Append(" }")
                  .Append(c < clips.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("    ]").Append(g < groups.Count - 1 ? ",\n" : "\n");
        }
        sb.Append("  }").Append(last ? "\n" : ",\n");
    }

    private static void Line(StringBuilder sb, int indent, string name, string value, bool last = false) =>
        Raw(sb, indent, name, Str(value), last);

    private static void Raw(StringBuilder sb, int indent, string name, string json, bool last = false) =>
        sb.Append(' ', indent).Append('"').Append(name).Append("\": ").Append(json).Append(last ? "\n" : ",\n");

    private static string Str(string value) => JsonSerializer.Serialize(value, StringOptions);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string StringArray(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Str)) + "]";

    // ---- parse -----------------------------------------------------------------------------------

    /// <summary>
    /// Reads and validates an export. False with a message fit for the user when the text is not
    /// JSON or not this format. Unknown members, comments, trailing commas, and wrongly typed
    /// values are tolerated (the value is just treated as absent).
    /// </summary>
    public static bool TryParse(string text, out PortableSettings? settings, out string? error)
    {
        settings = null;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(text.TrimStart('\uFEFF'), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("about", out var about) || about.ValueKind != JsonValueKind.Object ||
                !string.Equals(Str(about, "format"), FormatName, StringComparison.OrdinalIgnoreCase))
            {
                error = "This is not a Win4KSS settings export. Choose a file made with \"Export settings\" (it ends in .win4kss.json).";
                return false;
            }

            if (!about.TryGetProperty("formatVersion", out var fv) || fv.ValueKind != JsonValueKind.Number ||
                !fv.TryGetInt32(out var version) || version < 1)
            {
                error = "This settings export has no valid format version, so it cannot be read safely.";
                return false;
            }

            DateTime? exported = DateTime.TryParse(Str(about, "exported"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when) ? when : null;

            settings = new PortableSettings
            {
                FormatVersion = version,
                AppVersion = Str(about, "appVersion"),
                ExportedUtc = exported,
                Quality = Enum_<VideoQuality>(Section(root, "playback"), "quality"),
                MultiMonitorMode = Enum_<MultiMonitorMode>(Section(root, "playback"), "multiMonitorMode"),
                Shuffle = Bool_(Section(root, "playback"), "shuffle"),
                UseTimeOfDay = Bool_(Section(root, "playback"), "timeOfDay"),
                CacheEnabled = Bool_(Section(root, "cache"), "enabled"),
                CacheBudgetGb = Section(root, "cache") is { } cache && cache.TryGetProperty("budgetGb", out var b) &&
                                b.ValueKind == JsonValueKind.Number && b.TryGetDouble(out var gb) && double.IsFinite(gb) ? gb : null,
                AutoAddSources = Section(root, "sources") is { } src ? Strings(src, "autoAdd") : null,
                Shortcuts = Section(root, "shortcuts") is { } sh
                    ? new PortableShortcuts(Bool_(sh, "desktop"), Bool_(sh, "startMenu"), Str(sh, "hotkey"))
                    : null,
                Machine = Section(root, "machine") is { } m
                    ? new PortableMachine(NullIfBlank(Str(m, "cacheDirectory")), Strings(m, "localFolders") ?? [], NullIfBlank(Str(m, "ffmpegPath")))
                    : null,
                KeptOffline = Clips(root, "keptOffline"),
                Rotation = Clips(root, "rotation"),
            };
            return true;
        }
        catch (JsonException ex)
        {
            error = "This file is not valid JSON, so it is not a Win4KSS settings export: " + ex.Message;
            return false;
        }
    }

    /// <summary>Reads <paramref name="path"/> and parses it; the error also covers an unreadable or oversized file.</summary>
    public static bool TryParseFile(string path, out PortableSettings? settings, out string? error)
    {
        settings = null;
        try
        {
            if (new FileInfo(path) is not { Exists: true } info)
            {
                error = $"The file does not exist: {path}";
                return false;
            }
            if (info.Length > 64 * 1024 * 1024)
            {
                error = "This file is far too large to be a Win4KSS settings export.";
                return false;
            }
            return TryParse(File.ReadAllText(path), out settings, out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Could not read {path}: {ex.Message}";
            return false;
        }
    }

    private static JsonElement? Section(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;


    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Str(JsonElement? obj, string name) => obj is { } o ? Str(o, name) : null;

    private static bool? Bool_(JsonElement? obj, string name) =>
        obj is { } o && o.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static T? Enum_<T>(JsonElement? obj, string name) where T : struct, Enum =>
        Str(obj, name) is { } text && Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value) && !int.TryParse(text, out _)
            ? value
            : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string>? Strings(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(s => s.Trim().Length > 0).ToList()
            : null;

    /// <summary>A clip section: { "<source>": [ { key, title }… ] }. A bare array of clips is accepted too, and a clip may be a plain string.</summary>
    private static List<PortableGroup>? Clips(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var section))
            return null;

        PortableClip? One(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String when !string.IsNullOrWhiteSpace(e.GetString()) => new PortableClip(e.GetString()!, e.GetString()!),
            JsonValueKind.Object when Str(e, "key") is { Length: > 0 } key => new PortableClip(key, Str(e, "title") ?? key),
            _ => null,
        };

        List<PortableClip> Many(JsonElement array) =>
            array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Select(One).OfType<PortableClip>().ToList() : [];

        switch (section.ValueKind)
        {
            case JsonValueKind.Object:
                return section.EnumerateObject().Select(p => new PortableGroup(p.Name, Many(p.Value))).ToList();
            case JsonValueKind.Array:
                return [new PortableGroup(OtherSource, Many(section))];
            default:
                return null;
        }
    }

    // ---- plan and apply ----------------------------------------------------------------------------

    /// <summary>
    /// What importing <paramref name="file"/> into <paramref name="current"/> would change. Changes nothing.
    /// </summary>
    public static ImportPlan Plan(AppSettings current, PortableSettings file, IEnumerable<ClipInfo> catalogue,
                                  PortableShortcuts? currentShortcuts)
    {
        var known = catalogue.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var rotation = file.Rotation is null ? current.RotationKeys.ToList() : PortableSettings.KeysOf(file.Rotation).ToList();
        var pins = file.KeptOffline is null ? current.KeptOffline.ToList() : PortableSettings.KeysOf(file.KeptOffline).ToList();

        var changes = new List<string>();
        void Diff<T>(string label, T before, T? after, Func<T, string>? show = null) where T : struct
        {
            if (after is { } a && !EqualityComparer<T>.Default.Equals(before, a))
                changes.Add($"{label}: {(show ?? (x => x.ToString()!))(before)} → {(show ?? (x => x.ToString()!))(a)}");
        }

        string OnOff(bool b) => b ? "on" : "off";
        Diff("Quality", current.Quality, file.Quality);
        Diff("Multi-monitor", current.MultiMonitorMode, file.MultiMonitorMode);
        Diff("Shuffle", current.Shuffle, file.Shuffle, OnOff);
        Diff("Time of day", current.UseTimeOfDay, file.UseTimeOfDay, OnOff);
        Diff("Cache", current.CacheEnabled, file.CacheEnabled, OnOff);
        if (file.CacheBudgetGb is { } budget && Math.Abs(budget - current.CacheBudgetGb) > 0.0001)
            changes.Add($"Cache limit: {Number(current.CacheBudgetGb)} → {Number(Math.Clamp(budget, 1, 100_000))} GB");

        if (file.AutoAddSources is not null)
        {
            var after = ValidSources(file.AutoAddSources);
            if (!after.ToHashSet(StringComparer.Ordinal).SetEquals(current.AutoAddSources))
                changes.Add($"Auto-add sources: {Describe(SortSources(current.AutoAddSources))} → {Describe(after)}");
        }

        if (file.Shortcuts is { } sc && currentShortcuts is { } cur)
        {
            if (sc.Desktop is { } d && d != (cur.Desktop ?? false))
                changes.Add($"Desktop shortcut: {OnOff(cur.Desktop ?? false)} → {OnOff(d)}");
            if (sc.StartMenu is { } s && s != (cur.StartMenu ?? false))
                changes.Add($"Start menu shortcut: {OnOff(cur.StartMenu ?? false)} → {OnOff(s)}");
            if (sc.Hotkey is { } h && !string.Equals(h, cur.Hotkey ?? "", StringComparison.OrdinalIgnoreCase))
                changes.Add($"Hotkey: {(string.IsNullOrEmpty(cur.Hotkey) ? "none" : cur.Hotkey)} → {(h.Length == 0 ? "none" : h)}");
        }

        var machineChanges = new List<string>();
        if (file.Machine is { } m)
        {
            if (!string.Equals(m.CacheDirectory, NullIfBlank(current.CacheDirectory), StringComparison.OrdinalIgnoreCase))
                machineChanges.Add($"Cache folder: {NullIfBlank(current.CacheDirectory) ?? "default"} → {m.CacheDirectory ?? "default"}");
            if (!m.LocalFolders.SequenceEqual(current.LocalFolders, StringComparer.OrdinalIgnoreCase))
                machineChanges.Add($"Local folders: {current.LocalFolders.Count} → {m.LocalFolders.Count}");
            if (!string.Equals(m.FfmpegPath, NullIfBlank(current.FfmpegPath), StringComparison.OrdinalIgnoreCase))
                machineChanges.Add($"ffmpeg path: {NullIfBlank(current.FfmpegPath) ?? "automatic"} → {m.FfmpegPath ?? "automatic"}");
        }

        var currentRotation = current.RotationKeys;
        var rotationSet = rotation.ToHashSet(StringComparer.Ordinal);
        return new ImportPlan(
            current.RotationKeys.Count, rotation.Count,
            rotation.Count(k => !currentRotation.Contains(k)),
            file.Rotation is null ? 0 : currentRotation.Count(k => !rotationSet.Contains(k)),
            rotation.Count(k => !known.Contains(k)),
            current.KeptOffline.Count, pins.Count, pins.Count(k => !known.Contains(k)),
            changes, file.Machine is not null, machineChanges);
    }

    private static string Describe(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list);
    }

    private static List<string> ValidSources(IEnumerable<string> ids) =>
        SortSources(ids.Where(Rotation.SourceIds.Contains));

    /// <summary>
    /// Applies the file to <paramref name="settings"/>: playback, cache, auto-add, and the rotation
    /// (replaced; keys the catalogue does not list stay). Kept-offline is NOT touched: the caller
    /// routes it through <see cref="Caching.PinManager"/> (<see cref="ImportApplied.PinKeys"/>).
    /// Machine values are applied only with <paramref name="includeMachine"/>. KnownKeys and
    /// FailedClips are never touched. The caller saves.
    /// </summary>
    public static ImportApplied Apply(AppSettings settings, PortableSettings file, bool includeMachine)
    {
        var qualityChanged = file.Quality is { } q && q != settings.Quality;
        if (file.Quality is { } quality) settings.Quality = quality;
        if (file.MultiMonitorMode is { } mode) settings.MultiMonitorMode = mode;
        if (file.Shuffle is { } shuffle) settings.Shuffle = shuffle;
        if (file.UseTimeOfDay is { } timeOfDay) settings.UseTimeOfDay = timeOfDay;
        if (file.CacheEnabled is { } enabled) settings.CacheEnabled = enabled;

        var lowered = false;
        if (file.CacheBudgetGb is { } gb && double.IsFinite(gb))
        {
            gb = Math.Clamp(gb, 1, 100_000);
            lowered = gb < settings.CacheBudgetGb;
            settings.CacheBudgetGb = gb;
        }

        if (file.AutoAddSources is not null)
        {
            var wanted = ValidSources(file.AutoAddSources).ToHashSet(StringComparer.Ordinal);
            foreach (var source in Rotation.SourceIds)
                Rotation.SetAutoAdd(settings, source, wanted.Contains(source));
        }

        IReadOnlyList<string> added = [], removed = [];
        if (file.Rotation is not null)
        {
            var wanted = PortableSettings.KeysOf(file.Rotation);
            var wantedSet = wanted.ToHashSet(StringComparer.Ordinal);
            removed = settings.RotationKeys.Where(k => !wantedSet.Contains(k)).ToList();
            added = wanted.Where(k => !settings.RotationKeys.Contains(k)).ToList();
            Rotation.SetInRotation(settings, removed, false);
            Rotation.SetInRotation(settings, added, true);
        }

        bool cacheDir = false, ffmpeg = false, folders = false;
        if (includeMachine && file.Machine is { } machine)
        {
            cacheDir = !string.Equals(machine.CacheDirectory, NullIfBlank(settings.CacheDirectory), StringComparison.OrdinalIgnoreCase);
            ffmpeg = !string.Equals(machine.FfmpegPath, NullIfBlank(settings.FfmpegPath), StringComparison.OrdinalIgnoreCase);
            folders = !machine.LocalFolders.SequenceEqual(settings.LocalFolders, StringComparer.OrdinalIgnoreCase);
            settings.CacheDirectory = machine.CacheDirectory;
            settings.FfmpegPath = machine.FfmpegPath;
            settings.LocalFolders = machine.LocalFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return new ImportApplied
        {
            QualityChanged = qualityChanged,
            BudgetLowered = lowered,
            CacheDirectoryChanged = cacheDir,
            FfmpegPathChanged = ffmpeg,
            LocalFoldersChanged = folders,
            RotationAdded = added,
            RotationRemoved = removed,
            PinKeys = file.KeptOffline is null ? null : PortableSettings.KeysOf(file.KeptOffline),
        };
    }

    /// <summary>
    /// Copies the current settings.json to settings.before-import.json beside it, replacing any
    /// earlier backup. Returns false (and logs) when there is nothing to copy or the copy failed.
    /// </summary>
    public static bool BackUp(string? settingsPath = null)
    {
        try
        {
            var path = settingsPath ?? AppPaths.SettingsFile;
            if (!File.Exists(path))
                return false;
            File.Copy(path, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, BackupFileName), overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings: could not back up before import: {ex.Message}");
            return false;
        }
    }
}
