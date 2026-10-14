namespace Win4K.Core.Updates;

public sealed class UpdateCheckerOptions
{
    public IUpdateSource? Source { get; init; }
    public TimeProvider? Time { get; init; }

    /// <summary>Unauthenticated api.github.com allows 60 requests/hour/IP; this is far under that. A manual check bypasses it.</summary>
    public TimeSpan Throttle { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Checks GitHub's releases API for a newer Win4KSS than <see cref="AppSettings"/> is running,
/// throttled to once a day and persisted across restarts (<see cref="AppSettings.LastUpdateCheckUtc"/>
/// and friends) — those fields are deliberately not part of a settings export/import
/// (<see cref="SettingsPortability"/>): they describe this machine's last check, not a choice to
/// carry elsewhere.
///
/// <para>Only ever constructed for the clip browser (see <c>Program.AllowsUpdateCheck</c> and
/// CLAUDE.md's "Never during the saver") — never for <c>/s</c>, <c>/p</c>, <c>/now</c>, or the
/// Settings preview child. Every member here is purely network + settings; it has no idea what
/// verb is running, so that gate lives entirely at the call site.</para>
/// </summary>
public sealed class UpdateChecker
{
    private readonly AppSettings _settings;
    private readonly IUpdateSource _source;
    private readonly TimeProvider _time;
    private readonly TimeSpan _throttle;

    public UpdateChecker(AppSettings settings, UpdateCheckerOptions? options = null)
    {
        _settings = settings;
        _source = options?.Source ?? new GitHubReleaseSource();
        _time = options?.Time ?? TimeProvider.System;
        _throttle = options?.Throttle ?? TimeSpan.FromHours(24);
    }

    public bool IsDue =>
        _settings.LastUpdateCheckUtc is not { } last || _time.GetUtcNow().UtcDateTime - last >= _throttle;

    /// <summary>
    /// Checks once, unless throttled (<paramref name="force"/> bypasses that — the manual
    /// "Check now" button always does). Never throws for a network/parse failure — that comes
    /// back as <see cref="UpdateCheckResult.Error"/>, not an exception, so a background check on
    /// open can never surface a dialog; only cancellation (shutdown) propagates. Persists the
    /// outcome into <see cref="AppSettings"/> immediately; the caller still owns actually saving it.
    /// </summary>
    public async Task<UpdateCheckOutcome> CheckAsync(string currentVersion, bool force, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        if (!force && !IsDue)
        {
            // Throttled: report what the last check (this run or an earlier one) found, without
            // touching the network. CheckedUtc is still the real last-check time, not now, so the
            // page shows how stale this is.
            var cached = new UpdateCheckResult(currentVersion, _settings.LastUpdateLatestVersion,
                _settings.LastUpdateAvailable, _settings.LastUpdateAvailable ? _settings.LastUpdateNotes : null,
                _settings.LastUpdateCheckUtc ?? now, null);
            return new UpdateCheckOutcome(cached, null);
        }

        ReleaseInfo? release;
        string? fetchError = null;
        try
        {
            release = await _source.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (release is null)
                fetchError = "Could not reach GitHub to check for updates.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        var result = Evaluate(currentVersion, release, now, fetchError);

        _settings.LastUpdateCheckUtc = now;
        _settings.LastUpdateLatestVersion = result.Latest;
        _settings.LastUpdateAvailable = result.Available;
        _settings.LastUpdateNotes = result.Notes;

        // Only hand back live assets when there is actually something to install — Install
        // (UpdateInstaller) is the only consumer of Release, and it has nothing to do otherwise.
        return new UpdateCheckOutcome(result, result.Available ? release : null);
    }

    /// <summary>Pure comparison logic, independent of I/O — directly unit-tested. Internal for UpdateCheckerTests.</summary>
    internal static UpdateCheckResult Evaluate(string currentVersion, ReleaseInfo? release, DateTime nowUtc, string? fetchError)
    {
        if (release is null)
            return new UpdateCheckResult(currentVersion, null, false, null, nowUtc, fetchError);

        // The "latest release" endpoint already excludes both by GitHub's own semantics; this
        // is the defensive re-check the spec calls for, not trusting that alone.
        if (release.Draft || release.Prerelease)
            return new UpdateCheckResult(currentVersion, null, false, null, nowUtc, null);

        if (!SemVer.TryParse(release.TagName, out var latest))
            return new UpdateCheckResult(currentVersion, release.TagName, false, null, nowUtc,
                $"The latest release's version ('{release.TagName}') could not be read.");

        if (!SemVer.TryParse(currentVersion, out var current))
            return new UpdateCheckResult(currentVersion, latest.ToString(), false, null, nowUtc,
                $"This build's own version ('{currentVersion}') could not be read.");

        var available = latest > current;
        return new UpdateCheckResult(currentVersion, latest.ToString(), available, available ? release.Body : null, nowUtc, null);
    }
}
