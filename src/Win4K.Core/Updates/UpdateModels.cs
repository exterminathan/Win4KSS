namespace Win4K.Core.Updates;

/// <summary>One release asset (the installer, or its published .sha256), as GitHub's API reports it.</summary>
public sealed record ReleaseAsset(string Name, long Size, string DownloadUrl);

/// <summary>
/// A GitHub release, as much of it as the update checker/installer need. <see cref="Draft"/> and
/// <see cref="Prerelease"/> are read defensively even though the "latest release" endpoint
/// already excludes both by GitHub's own semantics — see CLAUDE.md-equivalent spec note: never
/// trust that alone.
/// </summary>
public sealed record ReleaseInfo(
    string TagName,
    string? Name,
    string? Body,
    bool Draft,
    bool Prerelease,
    IReadOnlyList<ReleaseAsset> Assets,
    DateTime? PublishedUtc);

/// <summary>
/// What the browser's About tab shows: the wire shape of the <c>updateStatus</c> bridge message
/// (docs/browser-bridge-contract.md), verbatim. <see cref="Error"/> is set on a fetch/parse
/// failure (offline, GitHub error, unparsable version) and is never shown as a dialog for a
/// background check — only a manual "Check now" or an install attempt surfaces one.
/// </summary>
public sealed record UpdateCheckResult(
    string Current,
    string? Latest,
    bool Available,
    string? Notes,
    DateTime CheckedUtc,
    string? Error);

/// <summary>
/// A completed check: the page-facing <see cref="Result"/>, plus the live <see cref="Release"/>
/// (asset URLs and all) when one was actually fetched this time — null on a throttled check that
/// just replayed the last persisted outcome, or when there is nothing to install. Install
/// (<see cref="UpdateInstaller"/>) needs <see cref="Release"/>, not just the status text.
/// </summary>
public sealed record UpdateCheckOutcome(UpdateCheckResult Result, ReleaseInfo? Release);

/// <summary>Download progress for the installer download; <see cref="Fraction"/> is null when the server did not report a length.</summary>
public sealed record DownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Fraction => TotalBytes is > 0 ? (double)BytesReceived / TotalBytes.Value : null;
}

/// <summary>Why <see cref="UpdateInstaller.DownloadAndVerifyAsync"/> refused to hand back a runnable file.</summary>
public enum InstallVerifyFailure
{
    None,
    NoInstallerAsset,
    MissingHashAsset,
    SizeMismatch,
    HashMismatch,
    DownloadFailed,
}

/// <summary>
/// The result of downloading and verifying an update. <see cref="Ok"/> true means
/// <see cref="FilePath"/> is a verified, safe-to-run temp file; otherwise it is null and any
/// partial download has already been deleted.
/// </summary>
public sealed record InstallVerifyResult(bool Ok, InstallVerifyFailure Failure, string? Message, string? FilePath)
{
    public static InstallVerifyResult Success(string filePath) => new(true, InstallVerifyFailure.None, null, filePath);
    public static InstallVerifyResult Fail(InstallVerifyFailure failure, string message) => new(false, failure, message, null);
}
