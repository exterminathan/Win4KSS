using System.Text;
using Win4K.ScreenSaver.Browser;

namespace Win4K.Tests;

/// <summary>The page's static files on https://app.win4k/, and the fallback page until the UI lands.</summary>
public class UiAssetsTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/index.html?v=3")]
    [InlineData("/INDEX.HTML")]
    public void The_root_serves_html_whether_or_not_the_ui_is_embedded(string path)
    {
        var asset = UiAssets.Resolve(path);

        Assert.NotNull(asset);
        Assert.Equal("text/html; charset=utf-8", asset.ContentType);
        if (!UiAssets.HasIndex)
            Assert.Contains("UI not built", Encoding.UTF8.GetString(asset.Body));
    }

    [Fact]
    public void The_fallback_page_does_the_ready_handshake_and_reports_the_clip_count()
    {
        Assert.Contains("type: 'ready'", UiAssets.FallbackPage);
        Assert.Contains("m.type === 'catalog'", UiAssets.FallbackPage);
        Assert.Contains("clips.length", UiAssets.FallbackPage);
    }

    [Theory]
    [InlineData("/../secret.txt")]
    [InlineData("/img/../../x.js")]
    [InlineData("/no-such-file.js")]
    public void Missing_files_and_traversal_are_404s(string path)
    {
        Assert.Null(UiAssets.Resolve(path));
    }

    [Fact]
    public void Every_embedded_file_resolves_with_its_content_type()
    {
        foreach (var file in UiAssets.Files)
        {
            var asset = UiAssets.Resolve("/" + file);
            Assert.NotNull(asset);
            Assert.Equal(UiAssets.ContentTypeFor(file), asset.ContentType);
        }
    }

    [Theory]
    [InlineData("index.html", "text/html; charset=utf-8")]
    [InlineData("app.js", "text/javascript; charset=utf-8")]
    [InlineData("app.css", "text/css; charset=utf-8")]
    [InlineData("data/fixture.json", "application/json; charset=utf-8")]
    [InlineData("icons/pin.svg", "image/svg+xml")]
    [InlineData("fonts/x.woff2", "font/woff2")]
    [InlineData("thing.bin", "application/octet-stream")]
    public void Content_types_follow_the_extension(string path, string expected)
    {
        Assert.Equal(expected, UiAssets.ContentTypeFor(path));
    }
}
