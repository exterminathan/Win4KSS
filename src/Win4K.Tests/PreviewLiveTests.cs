using System.Diagnostics;
using Win4K.Core;
using Win4K.Core.Catalogs;
using Win4K.Core.Media;
using Xunit.Abstractions;

namespace Win4K.Tests;

/// <summary>
/// Generates a real still and hover clip for one clip per source with the installed ffmpeg,
/// into a temp root. Inert unless WIN4K_LIVE_TESTS=1 (and skipped without ffmpeg). Run with
/// <c>--logger "console;verbosity=detailed"</c> to see sizes and timings.
/// </summary>
public class PreviewLiveTests(ITestOutputHelper output) : IDisposable
{
    private static bool LiveEnabled => Environment.GetEnvironmentVariable("WIN4K_LIVE_TESTS") == "1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "win4k-preview-live-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Generates_a_still_and_hover_clip_per_source()
    {
        if (!LiveEnabled || FfmpegLocator.Locate(null) is null)
            return;

        Directory.CreateDirectory(_root);
        using var scope = AppPaths.UseRoot(_root);

        var clips = await ClipLibrary.LoadAsync(new AppSettings());
        var picks = new (string Label, ClipInfo? Clip, bool MustSucceed)[]
        {
            ("Apple", clips.FirstOrDefault(c => c.SourceId == AppleAerialSource.SourceId && c.ThumbnailUrl is not null), true),
            ("archive-org", clips.FirstOrDefault(c => c.SourceId == "archive-org"), true),
            ("NASA", clips.FirstOrDefault(c => c.SourceId == "nasa"), true),
            ("no-thumbnail", clips.FirstOrDefault(c => c.ThumbnailUrl is null && !c.IsLocal && !c.Urls.IsEmpty), false),
        };

        using var service = new MediaPreviewService(new AppSettings());
        service.SetCatalog(picks.Where(p => p.Clip is not null).Select(p => p.Clip!));
        output.WriteLine($"ffmpeg: {service.Capabilities.FfmpegPath}; previews in {service.Directory}");

        var probe = FfprobeMediaProbe.TryCreate();
        foreach (var (label, clip, mustSucceed) in picks)
        {
            Assert.True(clip is not null, $"no {label} clip in the catalogue");

            foreach (var kind in new[] { PreviewKind.Still, PreviewKind.Hover })
            {
                var sw = Stopwatch.StartNew();
                var state = await service.GenerateAsync(clip.Key, kind);
                sw.Stop();

                var detail = "";
                if (state.FileName is { } file)
                {
                    var path = Path.Combine(service.Directory, file);
                    detail = $"{new FileInfo(path).Length / 1024.0:0.0} KB";
                    if (probe is not null && await probe.ProbeAsync(path, CancellationToken.None) is { } media)
                        detail += $", {media.Width}x{media.Height} {media.Codec}";
                }

                output.WriteLine($"{label,-13} {kind,-5} {state.Status,-9} {state.Source,-9} {sw.Elapsed.TotalSeconds,6:0.0} s  {detail}  [{clip.Key}]" +
                                 (state.Error is null ? "" : $"  error: {state.Error}"));

                if (mustSucceed)
                    Assert.Equal(PreviewStatus.Ready, state.Status);
            }
        }

        var hovers = Directory.GetFiles(service.Directory, "*.mp4");
        Assert.NotEmpty(hovers);
        if (probe is not null)
        {
            foreach (var hover in hovers)
            {
                var media = await probe.ProbeAsync(hover, CancellationToken.None);
                Assert.NotNull(media);
                Assert.Equal(480, media.Width);
                Assert.Equal("h264", media.Codec);
            }
        }
    }
}
