using System.Diagnostics;
using System.Security.Cryptography;

namespace Win4K.Core.Updates;

public sealed class UpdateInstallerOptions
{
    public HttpClient? Http { get; init; }

    /// <summary>How often <see cref="UpdateInstaller.DownloadAndVerifyAsync"/> reports download progress.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Actually launches the verified installer. Default: <see cref="Process.Start(ProcessStartInfo)"/> with <c>UseShellExecute = true</c>, so the installer's own UAC prompt follows. A test seam — never replaced in a real run.</summary>
    public Action<string>? Launch { get; init; }
}

/// <summary>
/// Downloads a release's installer asset, verifies it two ways before anything is allowed to run
/// it, and launches it. Never auto-installs: the caller only reaches <see cref="LaunchInstaller"/>
/// after the user explicitly clicked Install in the page, and only after
/// <see cref="DownloadAndVerifyAsync"/> came back <c>Ok</c>.
///
/// <para><b>Verification.</b> (1) The downloaded byte count must match the release asset's own
/// reported <c>size</c>. (2) The download's SHA-256 must match a hash published <i>in the
/// release itself</i> — a <c>&lt;installer&gt;.sha256</c> asset, plain text, in the conventional
/// <c>sha256sum</c> format (see <c>scripts/build-installer.ps1</c>, which emits it at build time)
/// — not GitHub's own <c>digest</c> field, which only proves the upload matches what was
/// uploaded, not that the right thing was uploaded. Any failure (missing asset, size, hash)
/// deletes the temp download and refuses to hand back a runnable path.</para>
///
/// <para>TODO(review): once SignPath signing is actually live (docs/SIGNING-POLICY.md), also
/// verify the downloaded exe's Authenticode signature (<c>WinVerifyTrust</c>) against the
/// expected publisher here, inside the <c>Ok</c> branch below, before returning success — there
/// is nothing real to check against yet, so it is not implemented.</para>
/// </summary>
public sealed class UpdateInstaller
{
    private readonly HttpClient _http;
    private readonly TimeSpan _progressInterval;
    private readonly Action<string> _launch;

    public UpdateInstaller(UpdateInstallerOptions? options = null)
    {
        _http = options?.Http ?? Http.Client;
        _progressInterval = options?.ProgressInterval ?? TimeSpan.FromMilliseconds(200);
        _launch = options?.Launch ?? (path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
    }

    /// <summary>The installer asset: the one whose name ends in .exe and contains "Setup" (see installer/Win4KSS.iss's OutputBaseFilename).</summary>
    public static ReleaseAsset? FindInstallerAsset(ReleaseInfo release) =>
        release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase));

    /// <summary>The published checksum asset for <paramref name="installer"/>: "&lt;installer name&gt;.sha256".</summary>
    public static ReleaseAsset? FindHashAsset(ReleaseAsset installer, ReleaseInfo release) =>
        release.Assets.FirstOrDefault(a => string.Equals(a.Name, installer.Name + ".sha256", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Downloads the release's installer to a fresh temp file and verifies it. Reports stage
    /// changes via <paramref name="onStage"/> ("Downloading" then "Verifying") and byte progress
    /// via <paramref name="onProgress"/> during the download. Never throws for a verification or
    /// network failure — that comes back as a failed <see cref="InstallVerifyResult"/>.
    /// </summary>
    public async Task<InstallVerifyResult> DownloadAndVerifyAsync(
        ReleaseInfo release,
        Action<string>? onStage,
        IProgress<DownloadProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var installerAsset = FindInstallerAsset(release);
        if (installerAsset is null)
            return InstallVerifyResult.Fail(InstallVerifyFailure.NoInstallerAsset, "This release has no installer to download.");

        // Checked, and the hash fetched, before spending any bandwidth on the (100+ MB)
        // installer itself: a release with no published checksum can never be verified, so
        // there is nothing to gain from downloading it first.
        var hashAsset = FindHashAsset(installerAsset, release);
        if (hashAsset is null)
        {
            return InstallVerifyResult.Fail(InstallVerifyFailure.MissingHashAsset,
                "This release has no published checksum file, so the download can't be verified. Refusing to run it.");
        }

        string expectedHex;
        try
        {
            var hashText = await _http.GetStringAsync(hashAsset.DownloadUrl, cancellationToken).ConfigureAwait(false);
            if (!TryParseSha256Line(hashText, out expectedHex))
                return InstallVerifyResult.Fail(InstallVerifyFailure.MissingHashAsset, "The published checksum file could not be read.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("Updates: fetching the published checksum failed", ex);
            return InstallVerifyResult.Fail(InstallVerifyFailure.MissingHashAsset, "Could not fetch the published checksum: " + ex.Message);
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"win4kss-update-{Guid.NewGuid():N}.exe");
        InstallVerifyResult result;
        try
        {
            onStage?.Invoke("Downloading");
            await DownloadFileAsync(installerAsset.DownloadUrl, tempPath, onProgress, cancellationToken).ConfigureAwait(false);

            onStage?.Invoke("Verifying");

            var actualSize = new FileInfo(tempPath).Length;
            if (installerAsset.Size > 0 && actualSize != installerAsset.Size)
            {
                result = InstallVerifyResult.Fail(InstallVerifyFailure.SizeMismatch,
                    $"The download is {actualSize:N0} bytes but the release says it should be {installerAsset.Size:N0}. It has not been run.");
            }
            else
            {
                var actualHex = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
                result = string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase)
                    ? InstallVerifyResult.Success(tempPath)
                    : InstallVerifyResult.Fail(InstallVerifyFailure.HashMismatch,
                        "The download's checksum does not match the one published with this release. It has not been run.");
            }
        }
        catch (OperationCanceledException)
        {
            SafeDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("Updates: download/verify failed", ex);
            result = InstallVerifyResult.Fail(InstallVerifyFailure.DownloadFailed, "Could not download the update: " + ex.Message);
        }

        // Every failure path above leaves the temp file behind for the others to delete here, in
        // one place, rather than at each return — only a verified Ok result is left on disk.
        if (!result.Ok)
            SafeDelete(tempPath);
        return result;
    }

    /// <summary>Starts the verified installer, detached (does not wait for it to finish). Caller must have gotten <see cref="InstallVerifyResult.Ok"/> true first — never called otherwise.</summary>
    public void LaunchInstaller(string verifiedPath) => _launch(verifiedPath);

    private async Task DownloadFileAsync(string url, string destinationPath, IProgress<DownloadProgress>? onProgress, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        var buffer = new byte[1 << 16];
        long received = 0;
        var lastReport = DateTime.UtcNow;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;

            var now = DateTime.UtcNow;
            if (onProgress is not null && now - lastReport >= _progressInterval)
            {
                lastReport = now;
                onProgress.Report(new DownloadProgress(received, total));
            }
        }
        onProgress?.Report(new DownloadProgress(received, total));
    }

    /// <summary>
    /// The conventional <c>sha256sum</c> line format: a 64-character hex digest, whitespace,
    /// then the file name (optionally preceded by "*" for binary mode). Only the digest is read;
    /// the file name is not checked against the asset name (Setup's own version number is
    /// already encoded in the release, so a mismatched file name inside the text would be a
    /// build-script bug, not a thing worth refusing an otherwise-matching download over).
    /// </summary>
    internal static bool TryParseSha256Line(string text, out string hex)
    {
        hex = "";
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var space = line.IndexOfAny([' ', '\t']);
            var token = space >= 0 ? line[..space] : line;
            if (token.Length == 64 && token.All(Uri.IsHexDigit))
            {
                hex = token;
                return true;
            }
        }
        return false;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Updates: could not delete {path}: {ex.Message}");
        }
    }
}
