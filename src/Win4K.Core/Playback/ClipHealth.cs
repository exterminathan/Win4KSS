namespace Win4K.Core.Playback;

/// <summary>
/// Remembers clips that keep failing across sessions. A clip benched by the playlist (it
/// failed twice in one session) is counted once per session in
/// <see cref="AppSettings.FailedClips"/>; at <see cref="UnhealthyAfterSessions"/> it is
/// "unhealthy", which the clip browser surfaces as a tag.
///
/// Keyed by <see cref="Clip.Id"/>, the clip key — "apple:&lt;GUID&gt;", "ia:&lt;item&gt;/&lt;file&gt;",
/// "nasa:&lt;id&gt;", "local:&lt;path&gt;"… (<see cref="Catalogs.ClipKey"/>) — which is stable across
/// manifest refreshes and quality changes, unlike the URL. Counts recorded under the
/// pre-key ids are rewritten by <see cref="AppSettings.MigrateClipKeys"/> on load.
///
/// Counts are cumulative and never reset by a later success: "failed in three separate
/// sessions" is the signal, and the browser is where a user can judge it.
/// </summary>
public sealed class ClipHealth
{
    public const int UnhealthyAfterSessions = 3;

    /// <summary>
    /// One per process: a multi-monitor saver runs one playlist per display, and a clip that
    /// fails on two of them in the same session is still one bad session.
    /// </summary>
    public static ClipHealth Shared { get; } = new();

    private readonly Func<AppSettings> _load;
    private readonly Action<AppSettings> _save;
    private readonly HashSet<string> _recordedThisSession = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <param name="load">Reads current settings; defaults to settings.json.</param>
    /// <param name="save">Writes them back; defaults to settings.json.</param>
    public ClipHealth(Func<AppSettings>? load = null, Action<AppSettings>? save = null)
    {
        _load = load ?? (() => AppSettings.Load());
        _save = save ?? (s => s.Save());
    }

    /// <summary>
    /// Counts this session against the clip, at most once per session.
    /// </summary>
    /// <returns>The clip's new session-failure count, or null if already counted this session.</returns>
    public int? RecordSessionFailure(Clip clip)
    {
        lock (_gate)
        {
            if (!_recordedThisSession.Add(clip.Id))
                return null;

            try
            {
                // Read-modify-write against the file rather than the copy the saver loaded at
                // startup, so a settings change saved meanwhile is not clobbered.
                var settings = _load();
                var count = settings.FailedClips.GetValueOrDefault(clip.Id) + 1;
                settings.FailedClips[clip.Id] = count;
                _save(settings);

                if (count >= UnhealthyAfterSessions)
                    Log.Warn($"Health: {clip} has now failed in {count} sessions; marked unhealthy");
                else
                    Log.Info($"Health: {clip} failed this session ({count} of {UnhealthyAfterSessions})");

                return count;
            }
            catch (Exception ex)
            {
                Log.Error($"Health: could not record failure of {clip}", ex);
                return null;
            }
        }
    }

    public static bool IsUnhealthy(AppSettings settings, string clipId) =>
        settings.FailedClips.TryGetValue(clipId, out var count) && count >= UnhealthyAfterSessions;
}
