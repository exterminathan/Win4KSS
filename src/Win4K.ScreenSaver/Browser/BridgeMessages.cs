using System.Text.Json;
using System.Text.Json.Serialization;
using Win4K.Core;
using Win4K.Core.Caching;
using Win4K.Core.Catalogs;
using Win4K.Core.Media;

namespace Win4K.ScreenSaver.Browser;

// The wire shapes of docs/browser-bridge-contract.md. Host → page messages are records with a
// leading "type"; page → host messages are parsed by hand in BrowserBridge (they are small and
// a partial or malformed one must be ignored, not thrown).

internal static class BridgeJson
{
    /// <summary>camelCase, enums as their member names, nulls written (the page tests for null, not undefined).</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null, // clip keys are data, never re-cased
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize(object message) => JsonSerializer.Serialize(message, message.GetType(), Options);
}

internal abstract record HostMessage([property: JsonPropertyOrder(-1)] string Type);

// ---- catalog --------------------------------------------------------------------------------

internal sealed record ClipDto(
    string Key,
    string SourceId,
    IReadOnlyList<string> SourceIds,
    IReadOnlyList<string> Sources,
    string Title,
    string Category,
    string? Subcategory,
    IReadOnlyList<ClipCaption> Captions,
    string? Creator,
    string? Licence,
    string? Description,
    IReadOnlyList<string> Subjects,
    int? Width,
    int? Height,
    double? DurationSeconds,
    long? SizeBytes,
    string? Codec,
    bool HasHdr,
    TimeOfDay TimeOfDay,
    string? ThumbnailUrl,
    bool IsLocal,
    DateTime? AddedUtc,
    IReadOnlyList<ClipTag> Tags)
{
    public static ClipDto From(ClipInfo clip) => new(
        clip.Key,
        clip.SourceId,
        clip.SourceIds.Count > 0 ? clip.SourceIds : [clip.SourceId],
        Rotation.SourcesOf(clip).Order(StringComparer.Ordinal).ToList(),
        clip.Title,
        clip.Category,
        clip.Subcategory,
        clip.Captions,
        clip.Creator,
        clip.Licence,
        clip.Description,
        clip.Subjects,
        clip.Width,
        clip.Height,
        clip.DurationSeconds,
        clip.SizeBytes,
        clip.Codec,
        clip.HasHdr,
        clip.TimeOfDay,
        clip.ThumbnailUrl,
        clip.IsLocal,
        clip.AddedUtc is { } added ? DateTime.SpecifyKind(added, DateTimeKind.Utc) : null,
        // Static tags only: State tags change without a catalogue refresh, so they travel in stateDelta.
        TagBuilder.Build(clip).Where(t => t.Facet != TagFacet.State).ToList());
}

internal sealed record CatalogMessage(IReadOnlyList<ClipDto> Clips) : HostMessage("catalog");

// ---- stateDelta -----------------------------------------------------------------------------

internal sealed record DownloadDto(DownloadState State, double? Fraction, long BytesReceived, long? TotalBytes, string? Error)
{
    public static DownloadDto? From(DownloadStatus? status) =>
        status is null ? null : new(status.State, status.Fraction, status.BytesReceived, status.TotalBytes, status.Error);
}

/// <param name="LastPlayedUtc">
/// When the clip last played from the cache (or was stored in it), from the cache index; null
/// when it is not cached, or is local. The browser's preview panel does not count as a play.
/// </param>
internal sealed record ClipStateDto(bool InRotation, bool Pinned, bool Cached, DownloadDto? Download, string Health, bool IsNew, DateTime? LastPlayedUtc)
{
    public const string Ok = "Ok";
    public const string Unhealthy = "Unhealthy";
}

internal sealed record StateDeltaMessage(IReadOnlyDictionary<string, ClipStateDto> Clips) : HostMessage("stateDelta");

// ---- previews -------------------------------------------------------------------------------

internal sealed record PreviewReadyMessage(string Key, PreviewKind Kind, PreviewStatus Status, string? Url, string PlaceholderColor) : HostMessage("previewReady")
{

    public static PreviewReadyMessage From(PreviewState state) =>
        new(state.Key, state.Kind, state.Status, state.Url, state.PlaceholderColor);
}

// ---- settings -------------------------------------------------------------------------------

internal sealed record PreviewCapabilitiesDto(bool FfmpegAvailable, bool HoverClips, string? FfmpegPath);

internal sealed record SettingsValues(
    VideoQuality Quality,
    MultiMonitorMode MultiMonitorMode,
    bool Shuffle,
    bool UseTimeOfDay,
    bool CacheEnabled,
    string CacheDirectory,
    double CacheBudgetGb,
    IReadOnlyList<string> LocalFolders,
    string? FfmpegPath,
    IReadOnlyList<string> AutoAddSources,
    IReadOnlyList<string> AvailableSources,
    PreviewCapabilitiesDto PreviewCapabilities,
    bool ShortcutDesktop,
    bool ShortcutStartMenu,
    string? Hotkey);

/// <param name="Id">A rotation source id (<see cref="Rotation.SourceIds"/>): what setAutoAdd takes.</param>
internal sealed record SourceDto(string Id, string Name, int ClipCount, bool AutoAdd);

internal sealed record AboutDto(string Version, string MpvVersion, string DataFolder, string SettingsFile, string LogFile, string MpvLog, string CacheFolder, string PreviewsFolder);

internal sealed record SettingsMessage(SettingsValues Values, IReadOnlyList<SourceDto> Sources, AboutDto About) : HostMessage("settings");

// ---- cache ----------------------------------------------------------------------------------

internal sealed record CacheStatsMessage(
    long PinnedBytes,
    long RollingBytes,
    long BudgetBytes,
    long FreeDiskBytes,
    int PinnedFiles,
    int RollingFiles,
    int PendingPins,
    bool OwnsPins) : HostMessage("cacheStats");

internal sealed record PinRefusedMessage(
    IReadOnlyList<string> Keys,
    int RequiredGb,
    double BudgetGb,
    double ShortfallGb,
    long RequiredBytes,
    long BudgetBytes,
    long ShortfallBytes) : HostMessage("pinRefused")
{
    private const double GiB = 1024.0 * 1024 * 1024;


    public static PinRefusedMessage From(IReadOnlyList<string> keys, PinCheck check, double budgetGb) => new(
        keys,
        check.RequiredGb,
        budgetGb,
        Math.Ceiling(check.ShortfallBytes / GiB * 10) / 10,
        check.RequiredBytes,
        check.BudgetBytes,
        check.ShortfallBytes);
}

// ---- catalogue refresh ----------------------------------------------------------------------

internal sealed record RefreshSourceDto(string SourceId, bool Succeeded, int AssetCount, int Added, int Removed, string? Error);

/// <summary>
/// Running: one per source as it progresses (<see cref="SourceId"/>, <see cref="Stage"/>,
/// <see cref="Done"/>/<see cref="Total"/>). Finished: <see cref="Running"/> false with
/// <see cref="Ok"/> and per-source <see cref="Sources"/>; a fresh catalog follows.
/// </summary>
internal sealed record RefreshProgressMessage(
    bool Running,
    string? SourceId,
    string? Stage,
    int? Done,
    int? Total,
    bool? Ok,
    IReadOnlyList<RefreshSourceDto>? Sources,
    string? Error) : HostMessage("refreshProgress")
{

    public static RefreshProgressMessage Progress(CatalogRefreshProgress p) =>
        new(true, p.SourceId, p.Stage, p.Done, p.Total, null, null, null);

    public static RefreshProgressMessage Finished(CatalogRefreshReport report) =>
        new(false, null, null, null, null, report.AllSucceeded,
            report.Sources.Select(s => new RefreshSourceDto(s.SourceId, s.Succeeded, s.AssetCount, s.Added, s.Removed, s.Error)).ToList(),
            null);

    public static RefreshProgressMessage Failed(string error) =>
        new(false, null, null, null, null, false, [], error);
}

// ---- settings import ------------------------------------------------------------------------

/// <param name="NotInCatalogue">Keys the current catalogue does not list (rotation: kept anyway; kept offline: skipped).</param>
internal sealed record ImportCountDto(int Before, int After, int Added, int Removed, int NotInCatalogue);

/// <summary>
/// What importing the chosen file would change; the page shows it and answers with confirmImport.
/// <see cref="PinsFit"/> is whether the file's kept-offline clips fit the cache limit it carries.
/// </summary>
internal sealed record ImportPreviewMessage(
    string FileName,
    string? AppVersion,
    DateTime? ExportedUtc,
    int FormatVersion,
    bool NewerFormat,
    ImportCountDto Rotation,
    ImportCountDto Pins,
    IReadOnlyList<string> Changes,
    bool HasMachine,
    IReadOnlyList<string> MachineChanges,
    bool PinsFit,
    double PinsRequiredGb,
    double BudgetGb) : HostMessage("importPreview");

// ---- notices --------------------------------------------------------------------------------

/// <summary>Something the page should tell the user: "info", "warning" or "error".</summary>
internal sealed record NoticeMessage(string Level, string Text) : HostMessage("notice");
