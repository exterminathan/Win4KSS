using System.Security.Cryptography;
using System.Text;
using Win4K.Core.Updates;
using Win4K.Tests.Fakes;

namespace Win4K.Tests;

/// <summary>
/// <see cref="UpdateInstaller"/>: verification against the API-reported size and a published
/// .sha256 release asset (never GitHub's own <c>digest</c>), refusing and cleaning up on any
/// mismatch, and never actually launching anything unless verification passed.
/// </summary>
public class UpdateInstallerTests
{
    private const string InstallerUrl = "https://example.test/releases/Win4KSS-Setup-1.10.0.exe";
    private const string HashUrl = "https://example.test/releases/Win4KSS-Setup-1.10.0.exe.sha256";
    private const string AssetName = "Win4KSS-Setup-1.10.0.exe";

    private static byte[] InstallerBytes => Encoding.UTF8.GetBytes("this is a fake installer payload, repeated a bit to look like a real file. ");

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ReleaseInfo ReleaseWith(IReadOnlyList<ReleaseAsset> assets) =>
        new("v1.10.0", "Win4KSS 1.10.0", "notes", false, false, assets, null);

    private sealed class LaunchRecorder
    {
        public List<string> Launched { get; } = [];
        public void Launch(string path) => Launched.Add(path);
    }

    [Fact]
    public async Task A_correct_download_verifies_and_leaves_a_runnable_temp_file()
    {
        var bytes = InstallerBytes;
        var http = new FixtureHttp()
            .Respond(InstallerUrl, () => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })
            .Text(HashUrl, $"{Sha256Hex(bytes)}  {AssetName}\n");

        var release = ReleaseWith([new ReleaseAsset(AssetName, bytes.Length, InstallerUrl), new ReleaseAsset(AssetName + ".sha256", 90, HashUrl)]);
        var installer = new UpdateInstaller(new UpdateInstallerOptions { Http = http.Client() });

        var stages = new List<string>();
        var result = await installer.DownloadAndVerifyAsync(release, stages.Add, onProgress: null, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.NotNull(result.FilePath);
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal(["Downloading", "Verifying"], stages);

        File.Delete(result.FilePath!);
    }

    [Fact]
    public async Task A_size_mismatch_refuses_to_run_and_deletes_the_download()
    {
        var bytes = InstallerBytes;
        var http = new FixtureHttp()
            .Respond(InstallerUrl, () => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })
            .Text(HashUrl, $"{Sha256Hex(bytes)}  {AssetName}\n");

        // The API claims a different size than what actually downloads.
        var release = ReleaseWith([new ReleaseAsset(AssetName, bytes.Length + 999, InstallerUrl), new ReleaseAsset(AssetName + ".sha256", 90, HashUrl)]);
        var installer = new UpdateInstaller(new UpdateInstallerOptions { Http = http.Client() });

        var before = ExistingTempFiles();
        var result = await installer.DownloadAndVerifyAsync(release, onStage: null, onProgress: null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(InstallVerifyFailure.SizeMismatch, result.Failure);
        Assert.Null(result.FilePath);
        AssertNoNewTempFiles(before);
    }

    [Fact]
    public async Task A_hash_mismatch_refuses_to_run_and_deletes_the_download()
    {
        var bytes = InstallerBytes;
        var http = new FixtureHttp()
            .Respond(InstallerUrl, () => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })
            // Wrong hash on purpose -- a SHA-256 of different bytes.
            .Text(HashUrl, $"{Sha256Hex(Encoding.UTF8.GetBytes("not the right content"))}  {AssetName}\n");

        var release = ReleaseWith([new ReleaseAsset(AssetName, bytes.Length, InstallerUrl), new ReleaseAsset(AssetName + ".sha256", 90, HashUrl)]);
        var installer = new UpdateInstaller(new UpdateInstallerOptions { Http = http.Client() });

        var before = ExistingTempFiles();
        var result = await installer.DownloadAndVerifyAsync(release, onStage: null, onProgress: null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(InstallVerifyFailure.HashMismatch, result.Failure);
        Assert.Null(result.FilePath);
        AssertNoNewTempFiles(before);
    }

    [Fact]
    public async Task A_missing_hash_asset_refuses_without_ever_downloading_the_installer()
    {
        var http = new FixtureHttp(); // no routes at all: any request would 404
        var release = ReleaseWith([new ReleaseAsset(AssetName, 12345, InstallerUrl)]); // no .sha256 asset published
        var installer = new UpdateInstaller(new UpdateInstallerOptions { Http = http.Client() });

        var result = await installer.DownloadAndVerifyAsync(release, onStage: null, onProgress: null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(InstallVerifyFailure.MissingHashAsset, result.Failure);
        Assert.Equal(0, http.CountOf(InstallerUrl)); // never even attempted
    }

    [Fact]
    public async Task No_installer_asset_at_all_is_refused()
    {
        var release = ReleaseWith([]);
        var installer = new UpdateInstaller();

        var result = await installer.DownloadAndVerifyAsync(release, onStage: null, onProgress: null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(InstallVerifyFailure.NoInstallerAsset, result.Failure);
    }

    [Fact]
    public void FindHashAsset_matches_the_installer_name_plus_sha256_case_insensitively()
    {
        var installerAsset = new ReleaseAsset(AssetName, 1, InstallerUrl);
        var release = ReleaseWith([installerAsset, new ReleaseAsset(AssetName.ToUpperInvariant() + ".SHA256", 2, HashUrl)]);

        var found = UpdateInstaller.FindHashAsset(installerAsset, release);

        Assert.NotNull(found);
        Assert.Equal(HashUrl, found!.DownloadUrl);
    }

    [Fact]
    public void A_verified_result_is_only_ever_launched_after_Ok_never_before()
    {
        var recorder = new LaunchRecorder();
        var installer = new UpdateInstaller(new UpdateInstallerOptions { Launch = recorder.Launch });

        installer.LaunchInstaller(@"C:\temp\verified.exe");

        Assert.Equal([@"C:\temp\verified.exe"], recorder.Launched);
    }

    // A real-shaped (64 hex char) digest: "deadbeef" + 52 zeros + "beef".
    private const string Hash64 = "deadbeef0000000000000000000000000000000000000000000000000000beef";

    public static IEnumerable<object[]> Sha256Lines()
    {
        yield return [$"{Hash64}  file.exe\n", true];
        yield return [$"{Hash64.ToUpperInvariant()}  file.exe\n", true]; // case tolerated
        yield return [$"{Hash64} *file.exe\n", true]; // binary-mode marker
        yield return [$"# a comment\n{Hash64}  file.exe\n", true];
        yield return ["not a hash at all\n", false];
        yield return ["", false];
        yield return ["deadbeef  file.exe\n", false]; // too short to be a real SHA-256
    }

    [Theory]
    [MemberData(nameof(Sha256Lines))]
    public void Sha256_line_parsing(string text, bool expectOk)
    {
        var ok = UpdateInstaller.TryParseSha256Line(text, out var hex);
        Assert.Equal(expectOk, ok);
        if (expectOk)
            Assert.Equal(64, hex.Length);
    }

    // Snapshotted before/after, rather than asserting the whole temp dir is empty of this
    // pattern: other tests in this class (or a parallel test collection) may have their own
    // win4kss-update-*.exe in flight at the same moment.
    private static HashSet<string> ExistingTempFiles() =>
        Directory.GetFiles(Path.GetTempPath(), "win4kss-update-*.exe").ToHashSet();

    private static void AssertNoNewTempFiles(HashSet<string> before) =>
        Assert.Empty(ExistingTempFiles().Except(before));
}
