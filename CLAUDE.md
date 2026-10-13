# Win4KScreensavers

## What this is

A Windows screensaver that plays true 4K HEVC video — Apple TV's aerial footage streamed from Apple's CDN, ~1,500 harvested 4K clips from Archive.org, NASA and other Apple-schema feeds, plus any local folder — with gapless transitions, a bounded disk cache, and a hotkey to trigger it on demand when walking away. `/c` opens a WebView2 **clip browser** for filtering the whole library, choosing what plays and what is kept offline. It is a ground-up replacement for the abandoned [cdima/aerial](https://github.com/cdima/aerial), which died on Windows Media Player ActiveX and 1080p H.264. The binary is **Win4KSS**.

Built for one user, on one machine, for personal use. Git repo with a private GitHub remote, `origin` = github.com/exterminathan/Win4KSS.

## Status

**Built, installed (user scope), and working.** The original eight phases (2026-08-12) and clip-browser phases A–G (2026-10-13) are complete: streaming reliability, unified clip model with in-app catalogue refresh, cache pins + download queue + cross-process safety, browser previews, the WebView2 browser on `/c`, settings v2 (per-clip rotation). **525 tests green** (live tests inert by default). `/s`, `/c`, `/p`, `/w` and `/now` verified against the published build; 4K HEVC streams and plays with `d3d11va` hardware decode. The browser was driven live against a copy of the real profile: stills, facet filtering, sorts, hover clips, the live mpv preview, pin progress, catalogue refresh, the auto-add prompt.

Installed at `%LOCALAPPDATA%\Programs\Win4KSS\` (renamed from `Win4KAerial` 2026-10-12; `install-scr.ps1` removes the old files and repoints `SCRNSAVE.EXE`) with Desktop + Start Menu shortcuts bound to `Ctrl+Alt+S`. The walk-away chain was observed end to end: shortcut → `StartNow` → Windows launched our saver → 1500 ms manual grace → playing from cache. In daily use since 2026-08-14 as the idle saver (30-min timeout); the cache sits at its 20 GB budget, so LRU eviction is active.

**The installed build matches `publish/`** (reinstalled 2026-10-13 with all clip-browser phases). Its first run migrates `settings.json` to v2, backing up v1 to `settings.v1.json`.

Outstanding:
- **System32 install** (only needed to appear in the Screen Saver Settings *dropdown*) — needs elevation: `scripts/install-scr.ps1 -SetAsDefault`.

Resolved: the "archive-org clips fail to stream ~23% of the time" problem (169 of 724 loads, Aug 14 – Oct 12). It was three bugs of ours, not archive.org flakiness — see the streaming entry under Key facts. The 23% understated it: 163/163 attempts to stream an *uncached* archive clip failed, and 165 of the 169 failures blacked out the saver until dismissal.

See [PLAN.md](PLAN.md) for the design and the findings that contradicted it, and [docs/squishy-teacup-plan.md](docs/squishy-teacup-plan.md) for the clip-browser plan (phases A–G).

## Layout

```
Win4KScreensavers/
├── src/
│   ├── Win4K.Core/          all logic, no UI
│   │   ├── Apple/           manifest client + models (tar-backed, cached, fallback; schema v2 extension fields)
│   │   ├── Catalogs/        ClipInfo, ClipKey, TagBuilder, ClipMerger, ClipLibrary, CatalogStore, CatalogRefresher,
│   │   │                    harvesters (ArchiveOrg, Nasa, AppleFeed) + Harvesting, MediaProbe, SourceDefinitions, CatalogClips
│   │   ├── Caching/         VideoCache (content-addressed, staged, resumable, LRU, pin-aware), CacheIndex,
│   │   │                    DownloadQueue, PinManager, CrossProcess (named mutexes)
│   │   ├── Media/           MediaPreviewService (stills + hover clips), PreviewIndex, PreviewCommands, Mp4Boxes, ProcessRunner
│   │   ├── Playback/        MpvInterop, MpvLoader, IMpvPlayer, MpvPlayer, PlaylistEngine, IDownloadAhead, ClipHealth
│   │   ├── Sources/         Clip, AppleAerialSource, LocalFolderSource, RegistryCatalogSource, ClipResolver
│   │   ├── Settings/        AppSettings (JSON, v2), SettingsMigrator (v1→v2), Rotation
│   │   └── AppleTrust.cs, CaBundle.cs, Http.cs, Log.cs, Display.cs, AppPaths.cs
│   ├── Win4K.ScreenSaver/   WinForms exe → Win4KSS.scr / Win4KSS.exe
│   │   ├── Program.cs       CLI verbs (/c → clip browser, single instance)
│   │   ├── SaverForm.cs, SaverLauncher.cs ("Start now"), PreviewLifetime.cs (/p backstop)
│   │   ├── InputDismissWatcher.cs, LaunchContext.cs, ShortcutManager.cs, NativeMethods.cs
│   │   └── Browser/         BrowserForm (WebView2 + native mpv preview panel), BrowserBridge (page⇄services, no
│   │                        WebView2 types), BridgeMessages, IBrowserHost, UiAssets;
│   │                        ui/ = index.html, app.js, app.css (vanilla JS, no build step), embedded by wildcard
│   └── Win4K.Tests/         xunit (483); Fakes/ (FakeMpvPlayer, FakeOrigin, FakeFfmpeg, FixtureHttp, …),
│                            Fixtures/ (trimmed upstream samples for the harvesters)
├── assets/                  fallback-entries.json, apple-root-ca.cer, registry/ (sources.json + catalog-<id>.json)
├── docs/                    browser-bridge-contract.md (page⇄host), squishy-teacup-plan.md (clip-browser plan),
│                            browser-mock/ (page dev harness: dev.html + shim.js + fixture.json + assets/; never embedded)
├── native/mpv/              libmpv-2.dll — gitignored, fetched by script
├── scripts/                 fetch-libmpv.ps1, install-scr.ps1
├── publish/                 single-file output (gitignored)
├── Win4KScreensavers.slnx
├── CLAUDE.md                this file
└── PLAN.md                  design, build order, findings
```

Runtime state lives in `%LOCALAPPDATA%\Win4KScreensavers\`:
- `settings.json` (v2), `settings.v1.json` (one-time backup written by the first v2 save over a v1 file)
- `manifest.json` (Apple, 7-day TTL), `catalogs/catalog-<id>.json` (written by a refresh; override the embedded copies)
- `ca-bundle.pem` (for mpv and ffmpeg), `win4k.log`, `mpv.log`
- `cache/videos`, `cache/partial` (kept across sessions for Range resume; 7-day orphans removed), `cache/cache-index.json`, `cache/queue.json` (pending pins)
- `previews/` — `<sha1(key)[:16]>.jpg`/`.mp4` + `index.json` + `tmp/`; 1 GB LRU, separate from the cache budget; browser only
- `webview2/` — the browser's WebView2 user data folder

## Conventions

- **Language/runtime:** C# on .NET 10, `net10.0-windows`, WinForms. WinForms specifically because libmpv needs a raw `HWND` for its `wid` option.
- **Video engine:** libmpv (`libmpv-2.dll`), fetched by `scripts/fetch-libmpv.ps1` into `native/mpv/`. Never commit the DLL.
- **Build:** `dotnet build` · **Test:** `dotnet test` · **Run windowed:** `dotnet run --project src/Win4K.ScreenSaver -- /w`
- **Live network tests** are inert by default: `$env:WIN4K_LIVE_TESTS="1"; dotnet test`. `StreamSmokeLiveTests` is the "streams as well as Apple" gate (5 uncached clips per source must reach `file-loaded` within 15 s).
- **Test isolation:** `TestEnvironment` points `WIN4K_HOME` at a temp dir for the whole run; a test that needs its own app-data root uses `using var _ = AppPaths.UseRoot(dir)` (AsyncLocal). Never set `WIN4K_HOME` inside a test — it is process-wide, and flipping it is what made `CaBundleTests` fail intermittently. Harvester tests use `Fakes/FixtureHttp`, playlist tests `FakeMpvPlayer` — never the network.
- **Publish:** `dotnet publish src/Win4K.ScreenSaver/Win4K.ScreenSaver.csproj -c Release -o publish` (single-file, ~124 MB; `WebView2Loader.dll` is bundled and self-extracted like libmpv)
- **Install:** `scripts/install-scr.ps1` (self-elevates; `-SetAsDefault`, `-Uninstall`)
- **Debugging playback:** `$env:WIN4K_MPV_LOG="1"` raises `mpv.log` from `all=warn,curl=v` to full debug. Every `loadfile` URL and mpv's errors are also logged to `win4k.log`.
- Both projects need `<AllowUnsafeBlocks>` — `[LibraryImport]` source-generated marshalling requires it
- Prefer `HttpClient` + `CancellationToken`; no `async void`, no `WebClient`.
- **Settings live in JSON**, not the registry.
- **Settings v2 (`Version` 2): the rotation is per clip.**
  - `RotationKeys` — what the saver plays. `KeptOffline` — pinned clips. `AutoAddSources` (`apple`/`aerial-manifests`/`archive-org`/`nasa`/`local`) — new clips from these join the rotation. `KnownKeys` — auto-add detection only; the saver refills it every run, so the browser's New tag comes from `AddedUtc` via `TagBuilder.IsNew`, never from it. `FailedClips` — key → sessions failed (`ClipHealth`: unhealthy at 3).
  - `SourceMode`/`SelectedClipIds`/`EnabledCatalogIds` are **retired**: read from v1, migrated, nulled, never written.
  - The v1→v2 migration needs the catalogue, so it is not in `AppSettings.Load`: call `SettingsMigrator.Prepare`/`PrepareAndSave(settings, ClipLibrary.LoadAsync(...))` after loading (idempotent; also merges new keys from auto-add sources).
  - Edit via `Rotation.SetInRotation`/`SetAutoAdd`, then `settings.Save()`. The saver plays `Rotation.Select`: rotation → else cached clips → else Apple → else everything.
- **Clip keys** (`ClipKey`, = `Clip.Id` = `ClipInfo.Key`, and the key of every per-clip setting): `apple:<GUID>` (upper-case; resources-16 *and* tvOS 26 share it, which is how they merge), `ia:<item>/<file>` (raw, unescaped file name), `nasa:<nasa_id>`, `amazon:<id>`, `jetson:<id>`, `robin:<id>`, `local:<path>`. Never a URL. Pre-key ids in `settings.json` are rewritten on load (`AppSettings.MigrateClipKeys`).
- **Catalogue refresh is in C#** (`Win4K.Core/Catalogs/`, see the registry section) and runs only from the browser, never the saver: the Refresh button, plus automatically in the background when the browser opens and `CatalogRefresher.IsDue()` (7 days) is true.
- **Clip browser (`/c`):** WebView2 page from `https://app.win4k/` (embedded `Browser/ui/**`, served via `WebResourceRequested`), previews from `https://previews.win4k/` (folder mapping). The page and host talk only through [docs/browser-bridge-contract.md](docs/browser-bridge-contract.md); the page sends `ready` first. The native mpv panel sits over the page's video rect (CSS px × zoom × dpi/96) and hides whenever anything covers it. Single instance via a named mutex.
- **Smoke-testing the browser:** use a temp `WIN4K_HOME` copy of the profile — opening it migrates the real `settings.json` to v2. Copy `settings.json` with `cacheDirectory` blanked (the real one points at the real cache), `manifest.json`, `ca-bundle.pem`; hard-link a few cache videos and copy `cache-index.json` for cached clips with real last-played times (entries without files are dropped on load).
- **Driving the live browser:** launch `Win4KSS.exe /c` with `UseShellExecute=false` and `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333 --disable-features=CalculateNativeWinOcclusion` — any CDP client (Node 22+ has `WebSocket`) can `Runtime.evaluate` and `Input.dispatchMouseEvent` (real hover). Capture with `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT=2)`, not `CopyFromScreen` (a running saver is topmost and would be captured instead). Diff two captures a few seconds apart to tell live mpv video from the still beneath it. Shortcut toggles write the **real** Desktop/Start Menu `.lnk` files even under a temp `WIN4K_HOME` — don't flip them in a test. Close with `WM_CLOSE`. Page errors land in `win4k.log` as `Page: …`.
- **Settings export/import:** Settings > About, or `Win4KSS.exe --export-settings FILE` / `--import-settings FILE [--include-machine]`. Format `*.win4kss.json` (readable, one clip per line; `Win4K.Core/Settings/SettingsPortability.cs`, protocol in the bridge contract). Machine paths are skipped on import unless asked; `KnownKeys`/`FailedClips` are never moved; the previous `settings.json` is kept as `settings.before-import.json`. With `WIN4K_HOME` set the browser's single-instance mutex is scoped to that root, so a smoke run does not collide with the real browser.
- **Page dev harness:** `python -m http.server` from the repo root, open `/docs/browser-mock/dev.html` — the real `index.html` markup and `app.js` against `shim.js` + `fixture.json`.

## Key facts worth not re-deriving

- **Apple's current CDN is `sylvan.apple.com`.** The manifest is *inside* `https://sylvan.apple.com/Aerials/resources-16.tar` (read it with the built-in `System.Formats.Tar`; no package needed). 114 assets, HEVC Main10, 4K + 1080, SDR + HDR. 4K clips are ~346 MB each, ~40 GB for all 114; Range requests work, so streaming and resumable downloads both do.
- **Trap:** `https://sylvan.apple.com/Aerials/2x/entries.json` still responds but its video URLs 404. It is stale. Only the tarball manifest is correct. `resources-17.tar` and `-18` do not exist. The legacy `a1.phobos.apple.com` manifest is also live, but 1080p H.264 only.
- **`sylvan.apple.com` fails TLS on Windows out of the box.** Its chain is `sylvan.apple.com → Apple Server Authentication CA → Apple Root CA`, and **Apple Root CA is not in the Windows trusted root store**. We do not downgrade to `http://` and do not touch the machine's cert store; `AppleTrust` pins Apple's real root (embedded from `assets/apple-root-ca.cer`, SHA-1 `611E5B66…6C60`, valid to 2035) as a process-local trust anchor. Name mismatches stay fatal.
- **mpv (libcurl) and ffmpeg have their own trust stores.** `CaBundle` writes `ca-bundle.pem` — every *currently valid* Windows root and intermediate plus Apple's — passed to mpv as `tls-ca-file` and to ffmpeg as `-ca_file`. Never reach for `tls-verify=no`. The bundle's first line is a format header; bump `CaBundle.FormatHeader` whenever its contents rule changes so old bundles regenerate.
- **Non-Apple streaming failed for three reasons, all ours** (fixed in Phase A):
  1. **`Uri.ToString()` unescapes `%20` to a space**, and libcurl rejects the URL (`URL using bad/illegal format` → mpv `-13`). Apple URLs have no spaces. Always hand mpv `Clip.PlayableUrl` (`AbsoluteUri`, or `LocalPath` for files); the cache is keyed by URL, so escaping is load-bearing there too.
  2. **One failure stalled mpv into idle.** With one entry queued, a failed entry failed again in ~1 ms, mpv went idle, and `loadfile … append` does not restart from idle — a black screen until dismissal. `PlaylistEngine` now uses `append-play`, keeps two entries queued, advances on `MPV_EVENT_IDLE`, maps end-file by playlist entry id, and has a 20 s no-progress watchdog; a clip is retried once, 3 consecutive failures switch to cached-only for the session.
  3. **An expired ISRG Root X2 cross-sign in `ca-bundle.pem`** (from the Windows intermediate store) was chosen by libcurl's OpenSSL over the valid chain the server sent, failing TLS on archive.org's Let's Encrypt redirect hosts. Expired and not-yet-valid certificates are now excluded (`CaBundleTests` guards it).
- **Downloads retry 5xx/timeouts** at 1 s / 4 s / 15 s honouring `Retry-After`, and reject `text/html` and Content-Length mismatches so error pages are never cached. While clip N plays, the queue fetches N+1 (download-ahead), which is what makes archive clips (70–80 MB) play from disk.
- **`ytdl=no` matters.** Without it mpv spawns a `yt-dlp` subprocess on every failed load and waits ~1s for it to fail.
- **The browser and the saver share one cache, concurrently.** Index read-modify-writes run inside the named mutex `Local\Win4KSS.CacheIndex.<hash of cache root>`; partials are held `FileShare.None` (locked = another process owns that download); a `FileSystemWatcher` on `cache-index.json` raises `VideoCache.Changed` in the other process. Only the holder of `Local\Win4KSS.DownloadQueueOwner.<hash>` downloads pins (first process to start; the saver takes over when the browser exits, using 1 of its 2 slots). Pins (`KeptOffline`) are never evicted and count against the one budget; a pin that would exceed it is refused.
- **A shortcut must never target the `.scr`.** Windows registers scrfile's `open` verb as `"%1" /S`, so the shell discards a shortcut's arguments and substitutes `/S`. A `.lnk` aimed at the `.scr` with `/now` dispatches `FullScreen` — the saver runs directly and **never honours the resume lock**, invisibly. Shortcuts target the companion `Win4KSS.exe` (hard-linked to the `.scr` when on one volume). Guarded by `ShortcutTargetTests`. Likewise, launch a `.scr` with arguments from a script via `CreateProcess` (`UseShellExecute=false`), never `Start-Process`.
- **`/p` must attach to the preview HWND in `OnLoad`, not `OnHandleCreated`.** WinForms resets a top-level form's parent after `OnHandleCreated`, silently undoing `SetParent` — the preview became an orphaned child of the desktop and `/p` processes leaked for hours. `SaverForm` also polls `PreviewLifetime` (1 s) as a backstop.
- **Setting `ScreenSaveActive=1` in the registry does not enable the screen saver.** The session caches it; without `SystemParametersInfo(SPI_SETSCREENSAVEACTIVE, …, SPIF_SENDCHANGE)`, Windows ignores `SC_SCREENSAVE` — the hotkey does nothing. `install-scr.ps1` handles this.
- **Never `Stop-Process` a running screen saver.** It leaves `SPI_GETSCREENSAVERRUNNING` stuck true: Windows holds a handle on the `.scr` (reinstall fails with a sharing violation), refuses to start another saver, and rejects `SPI_SETSCREENSAVEACTIVE` with error 329. Clears on sign-out. Dismiss with input or Escape instead.
- **Input dismissal cannot be tested from an automated session** — a non-interactive desktop refuses `SetCursorPos` and `GetCursorPos` reports `0,0`. The decision logic is unit-tested via `InputDismissWatcher.HasMovedEnough`; the polling needs a human with a real mouse.
- Only **4 of Apple's 114** clips carry a day/night label, so time-of-day ordering barely moves anything.
- **Previews shell out to ffmpeg, a GnuTLS build whose `tls_verify` defaults to 0** — it checks nothing. Always pass `-tls_verify 1 -ca_file <ca-bundle.pem>` (`PreviewCommands` does). Apple and Amazon `.mov` files keep `moov` at the tail, so a Range-fetched head is useless; `Mp4Boxes.IndexIsBeyond` spots this after 64 KB and ffmpeg reads the URL instead (~4 s per hover clip). archive.org serves ~1.3 MB/s, so the head-segment route is slower there too (~22 s and 27 MB vs ~7.6 s for ffmpeg direct); so ffmpeg reads the URL directly by default (`MediaPreviewOptions.UseHeadSegments` switches back to head segments). archive.org `.thumbs` are 160 px, so stills are upgraded to a 480 px frame whenever a hover clip has the video at hand. Hover clips must be `-pix_fmt yuv420p`: Apple's sources are 10-bit, and High 10 H.264 won't play in Chromium.
- libmpv is **114 MB**; the published `.scr` is ~124 MB with WebView2.
- **Local toolchain, verified:** .NET 10.0.302 SDK + WindowsDesktop 10.0.10 · VS2022 · ffmpeg/ffprobe at `C:\ffmpeg` · yt-dlp 2026.07.04 · RTX 2080 Super (hardware HEVC Main10 decode) · HEVC Video Extensions installed.

## Video source registry

`assets/registry/sources.json` is the catalogue of where clips come from. Every entry was probed live and records what was **measured**, not what the site claims — including the dead ends, so they don't get re-investigated.

The `harvest: "auto"` sources are harvested **in C#** (`Win4K.Core/Catalogs/`), one harvester per source — `ArchiveOrgHarvester` (search → `metadata/<id>`), `NasaHarvester` (search → `collection.json` → `metadata.json`, ffprobe only as a fallback and only if installed), `AppleFeedHarvester` (the four aerial feeds + the tvOS 26 strings file) — all over `Http.Client`, driven by `CatalogRefresher.RefreshAsync(sourceIds?, IProgress<CatalogRefreshProgress>?, ct)`.

- **Catalogue files** are `catalog-<id>.json` in the **Apple manifest schema**, so `AppleManifest` parses them with no new deserialiser, plus **schema v2** extension fields: `key`, `feed`, `title`, `timeOfDay`, `categoryName`/`subcategoryName`, resolved `captions`, `creator`, `licence`, `subjects`, `description`, `codec`, `thumbnailUrl`, `addedUtc`, `width`/`height`/`sizeBytes`/`durationSeconds`. v1 files still load.
- **Load order:** `catalogs/catalog-<id>.json` (written by a refresh) → the copy embedded from `assets/registry/` (`CatalogStore`). A refresh that fails, or finds nothing, keeps the previous catalogue; an item that fails keeps its previous rows.
- **`addedUtc`** is first-seen, by diffing against the previous catalogue. Everything in the original harvest is dated **2026-08-12** (`CatalogRefresher.BaselineAddedUtc`) so it is not tagged New.
- **Regenerating the embedded copies:** `$env:WIN4K_LIVE_TESTS="1"; $env:WIN4K_CATALOG_OUT="<scratch dir>"; dotnet test --filter FullyQualifiedName~CatalogRefreshLiveTests`, review, then copy into `assets/registry/`.
- **Embedded catalogue (harvested 2026-10-13):** `archive-org` 1316 files → **1269** after dropping 47 `_YYYYMM` mirror re-uploads; `aerial-manifests` **289**; `nasa` **19**. NASA URLs are escaped `https://`, and archive.org URLs are escaped exactly as the old Python harvester did, so existing cache entries still match.
- `ClipLibrary.LoadAsync` is the browser's view: every source, merged (`ClipMerger`) — 114 Apple + 25 tvOS-only + the rest. `ClipResolver` resolves every source; `ClipResolver.PlayableClip` is the one quality rule (Apple = user quality, harvested = 4K SDR), shared with `PinManager.ClipFor`.
- **Apple's manifest has a newer generation than `resources-16.tar`:** the tvOS 26 manifest carries **139 assets** (vs 114), a 4-category/59-subcategory tree, and a 687-entry strings file that resolves `localizedNameKey` and `pointsOfInterest` to caption text (normalise `Â\u00A0` → space). Mirrored via the `aerial-manifests` source.
- **Three other vendors publish Apple's exact schema** — Amazon Fire TV ambient (112 assets, with a real `timeOfDay` field Apple dropped), Jetson Creative (20), Robin Fourcade (18). Same parser, no new code path.
- **YouTube clips must be watermark-checked by extracting a frame**, not by reading the description — 5 of 8 channels tested burn in persistent branding that is invisible in thumbnails. Verified-clean channels are listed in the registry.
- **Never pass `--force-keyframes-at-cuts` to yt-dlp.** It forces a full re-encode: 3 min vs 13 s for the same 20 s 4K cut. Without it you get a stream copy; cuts just snap to the nearest keyframe.
- **`4kmedia.org` is a trap:** the site is up and its specs are accurate, but all 159 downloads 404 — the Google Drive files are gone. Already recorded under `rejected`.

## Notes for Claude

- Git repo with a private GitHub remote (`origin`). Commit on `main`; **do not push unless asked**.
- **No co-author or attribution lines** in commit messages or PR descriptions (user rule).
- Keep this file current: when the layout, build command, or conventions change, update the sections above in the same change. Tick the build-order table in PLAN.md as phases land, and add to its findings when something contradicts a plan.
