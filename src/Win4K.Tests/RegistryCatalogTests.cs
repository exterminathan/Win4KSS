using Win4K.Core;

namespace Win4K.Tests;

/// <summary>
/// The harvested catalogues in assets/registry are written in Apple's manifest schema, so
/// they must parse through exactly the same code path with no special casing.
/// </summary>
public class RegistryCatalogTests
{
    [Fact]
    public void All_shipped_catalogues_are_discoverable()
    {
        var sources = SourceRegistry.Available();

        Assert.NotEmpty(sources);
        Assert.All(sources, s => Assert.True(s.ClipCount > 0, $"{s.Id} shipped with no clips"));
    }

    [Theory]
    [InlineData("archive-org")]
    [InlineData("nasa")]
    [InlineData("aerial-manifests")]
    public void Catalogue_parses_with_the_Apple_manifest_model(string id)
    {
        var manifest = SourceRegistry.LoadCatalog(id);

        Assert.NotNull(manifest);
        Assert.NotEmpty(manifest!.Assets);
    }

    [Theory]
    [InlineData("archive-org")]
    [InlineData("nasa")]
    [InlineData("aerial-manifests")]
    public async Task Catalogue_yields_playable_clips(string id)
    {
        var clips = await new RegistryCatalogSource(id).GetClipsAsync();

        Assert.NotEmpty(clips);
        Assert.All(clips, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Title));
            Assert.True(c.Url.IsAbsoluteUri);
            Assert.False(c.IsLocal);
        });
    }

    [Fact]
    public async Task Scene_stands_in_for_the_missing_category_guids()
    {
        // The harvested catalogues have no category GUIDs, so grouping falls back to "scene".
        // Without that fallback every one of these clips would land in "Other".
        var clips = await new RegistryCatalogSource("nasa").GetClipsAsync();

        Assert.All(clips, c => Assert.NotEqual("Other", c.Category));
        Assert.Contains(clips, c => c.Category == "Space");
    }

    [Fact]
    public async Task Archive_org_sizes_survive_into_the_clip()
    {
        // This catalogue records sizeBytes; carrying it through lets the cache budget
        // decisions be made before a download starts rather than after.
        var clips = await new RegistryCatalogSource("archive-org").GetClipsAsync();

        Assert.Contains(clips, c => c.SizeBytes is > 0);
    }

    [Fact]
    public void Every_catalogue_is_a_source_whatever_the_settings()
    {
        // Settings v2: sources are never switched off; the rotation is per clip.
        var sources = ClipResolver.BuildSources(new AppSettings()).ToList();

        Assert.Contains(sources, s => s.Id == AppleAerialSource.SourceId);
        foreach (var id in SourceRegistry.KnownCatalogs)
            Assert.Contains(sources, s => s.Id == RegistryCatalogSource.SourceIdPrefix + id);
    }
}
