using System.Reflection;
using System.Text;

namespace Win4K.ScreenSaver.Browser;

/// <summary>A static file for https://app.win4k/.</summary>
internal sealed record UiAsset(byte[] Body, string ContentType);

/// <summary>
/// The page's static files (Browser/ui/**), embedded in the exe by a wildcard EmbeddedResource
/// with logical names "ui/&lt;relative path&gt;", and served by <see cref="BrowserForm"/> through
/// WebResourceRequested on <see cref="Origin"/>. Until the UI lands, there is no index.html and
/// <see cref="Resolve"/> serves <see cref="FallbackPage"/> instead: a bare bridge exerciser for
/// smoke-testing the host.
/// </summary>
internal static class UiAssets
{
    public const string Host = "app.win4k";
    public const string Origin = "https://" + Host + "/";
    public const string StartUrl = Origin + "index.html";

    private const string Prefix = "ui/";

    private static readonly Lazy<Dictionary<string, string>> Resources = new(() =>
    {
        var assembly = typeof(UiAssets).Assembly;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            // %(RecursiveDir) gives backslashes; normalise so lookups use URL form.
            var normalised = name.Replace('\\', '/');
            if (normalised.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                map[normalised[Prefix.Length..]] = name;
        }
        return map;
    });

    /// <summary>Relative paths ("index.html", "img/logo.svg") of every embedded UI file.</summary>
    public static IReadOnlyCollection<string> Files => Resources.Value.Keys;

    public static bool HasIndex => Resources.Value.ContainsKey("index.html");

    /// <summary>
    /// The file for a request URL's path ("/", "/index.html", "/app.js?v=2"), or null for a 404.
    /// "/" and "/index.html" fall back to <see cref="FallbackPage"/> when no UI is embedded.
    /// </summary>
    public static UiAsset? Resolve(string urlPath)
    {
        var path = urlPath;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
            path = path[..cut];

        path = Uri.UnescapeDataString(path).Replace('\\', '/').TrimStart('/');
        if (path.Length == 0)
            path = "index.html";

        // Lookups are by exact embedded name, so ".." can never escape, but refuse it outright.
        if (path.Split('/').Any(segment => segment is ".." or "."))
            return null;

        if (Resources.Value.TryGetValue(path, out var resource))
        {
            using var stream = typeof(UiAssets).Assembly.GetManifestResourceStream(resource);
            if (stream is null)
                return null;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return new UiAsset(buffer.ToArray(), ContentTypeFor(path));
        }

        if (path.Equals("index.html", StringComparison.OrdinalIgnoreCase))
            return new UiAsset(Encoding.UTF8.GetBytes(FallbackPage), ContentTypeFor(path));

        return null;
    }

    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" or ".map" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        ".txt" => "text/plain; charset=utf-8",
        ".mp4" => "video/mp4",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// Served as index.html while Browser/ui has none. Says so, and exercises the bridge: sends
    /// ready, counts every message by type, shows the clip count and the last message, asks
    /// for a few previews (and reports whether their ?v= URLs load from previews.win4k), and
    /// selects the first clip so the native mpv panel appears over the box on the right.
    /// </summary>
    public const string FallbackPage = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>Win4KSS — UI not built</title>
        <style>
          :root { color-scheme: dark; }
          body { margin: 0; padding: 24px; font: 14px/1.5 "Segoe UI", system-ui, sans-serif; background: #15181c; color: #e8eaed; }
          h1 { margin: 0 0 4px; font-size: 22px; }
          .muted { color: #9aa0a6; }
          .row { display: flex; gap: 24px; align-items: flex-start; margin-top: 16px; }
          .col { flex: 1; min-width: 0; }
          #video { width: 480px; height: 270px; flex: none; background: #000; border: 1px solid #3c4043; display: grid; place-items: center; color: #5f6368; }
          table { border-collapse: collapse; }
          td { padding: 2px 12px 2px 0; }
          #last { white-space: pre-wrap; word-break: break-all; max-height: 320px; overflow: auto; background: #202124; padding: 8px; border-radius: 4px; font: 12px Consolas, monospace; }
          #thumbs img { width: 160px; height: 90px; object-fit: cover; margin: 4px 4px 0 0; background: #303134; }
          button { background: #303134; color: inherit; border: 1px solid #5f6368; border-radius: 4px; padding: 4px 10px; cursor: pointer; }
        </style>
        </head>
        <body>
        <h1>UI not built</h1>
        <div class="muted">Browser/ui/index.html is not embedded in this build, so this is the host's built-in bridge exerciser.</div>
        <div class="row">
          <div class="col">
            <table>
              <tr><td>Clips in catalog</td><td id="clips">waiting…</td></tr>
              <tr><td>In rotation</td><td id="rotation">–</td></tr>
              <tr><td>Messages</td><td id="counts">none</td></tr>
              <tr><td>Previews</td><td id="previews">–</td></tr>
              <tr><td>Selected</td><td id="selected">–</td></tr>
            </table>
            <p><button id="none">Select none</button> <button id="next">Next clip</button></p>
            <div id="thumbs"></div>
            <h3>Last message</h3>
            <div id="last">–</div>
          </div>
          <div id="video">native mpv panel goes here</div>
        </div>
        <script>
        (() => {
          const wv = window.chrome && window.chrome.webview;
          const $ = id => document.getElementById(id);
          if (!wv) { $('clips').textContent = 'no chrome.webview: not running in the host'; return; }
          const post = m => wv.postMessage(m);
          const log = (text, level) => post({ type: 'log', level: level || 'info', text: 'fallback: ' + text });
          const counts = {};
          const state = {};
          let clips = [], index = 0, selected = null, loaded = 0, failed = 0;

          function sendRect() {
            const r = $('video').getBoundingClientRect();
            post({ type: 'panelRect', x: r.left, y: r.top, width: r.width, height: r.height, visible: selected !== null });
          }
          function select(i) {
            selected = clips.length ? clips[i % clips.length].key : null;
            $('selected').textContent = selected ? clips[i % clips.length].title + ' (' + selected + ')' : 'none';
            post({ type: 'selectClip', key: selected });
            sendRect();
          }

          wv.addEventListener('message', e => {
            const m = e.data;
            counts[m.type] = (counts[m.type] || 0) + 1;
            $('counts').textContent = Object.entries(counts).map(([k, v]) => k + ' ' + v).join(', ');
            const text = JSON.stringify(m);
            $('last').textContent = text.length > 3000 ? text.slice(0, 3000) + ' … (' + text.length + ' chars)' : text;

            if (m.type === 'catalog') {
              clips = m.clips;
              $('clips').textContent = clips.length.toLocaleString();
              log('catalog received with ' + clips.length + ' clips');
              const first = clips.filter(c => c.thumbnailUrl).slice(0, 6).map(c => c.key);
              if (first.length) post({ type: 'requestPreviews', keys: first, kinds: 'Still' });
              const apple = clips.findIndex(c => c.sourceId === 'apple');
              index = apple >= 0 ? apple : 0;
              select(index);
            } else if (m.type === 'stateDelta') {
              Object.assign(state, m.clips);
              $('rotation').textContent = Object.values(state).filter(s => s.inRotation).length.toLocaleString() + ' of ' + Object.keys(state).length.toLocaleString();
            } else if (m.type === 'previewReady' && m.status === 'Ready' && m.url) {
              const img = new Image();
              img.title = m.key;
              img.onload = () => { loaded++; $('previews').textContent = loaded + ' loaded, ' + failed + ' failed'; log('preview loaded ' + m.url); };
              img.onerror = () => { failed++; $('previews').textContent = loaded + ' loaded, ' + failed + ' failed'; log('preview FAILED ' + m.url, 'warn'); };
              img.src = m.url;
              $('thumbs').appendChild(img);
            }
          });

          $('none').onclick = () => { selected = null; post({ type: 'selectClip', key: null }); sendRect(); $('selected').textContent = 'none'; };
          $('next').onclick = () => select(++index);
          window.addEventListener('resize', sendRect);
          window.addEventListener('scroll', sendRect, { passive: true });
          post({ type: 'ready' });
        })();
        </script>
        </body>
        </html>
        """;
}
