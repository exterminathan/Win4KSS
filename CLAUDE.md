# Win4KScreensavers

## What this is

A Windows screensaver that plays true 4K HEVC video — Apple TV's aerial footage streamed from Apple's CDN, plus any local folder of 4K clips — with gapless transitions, a bounded disk cache, and a hotkey to trigger it on demand when walking away. It is a ground-up replacement for the abandoned [cdima/aerial](https://github.com/cdima/aerial), which died on Windows Media Player ActiveX and 1080p H.264.

Built for one user, on one machine, for personal use.

## Status

**Built, installed (user scope), and working.** All eight phases complete; clip-browser Phase B (unified clip model + in-app catalogue refresh) Phase C (cache pins, download queue, multi-process safety) and Phase D (`MediaPreviewService`: grid stills + hover clips) landed 2026-10-13; 389 tests green. `/s`, `/c`, `/p`, `/w` and `/now` all verified against the published build; 4K HEVC streams and plays with `d3d11va` hardware decode, including the settings-dialog live preview.

Installed at `%LOCALAPPDATA%\Programs\Win4KSS\` (renamed from `Win4KAerial` on 2026-10-12 so Screen Saver Settings shows "Win4KSS"; `install-scr.ps1` removes the old files and repoints `SCRNSAVE.EXE`) with Desktop + Start Menu shortcuts bound to `Ctrl+Alt+S`. The full walk-away chain was observed working end to end: shortcut → `StartNow` → Windows launched our saver → 1500 ms manual grace → playing from cache.

Outstanding:
- **System32 install** (only needed to appear in the Screen Saver Settings *dropdown*) — needs elevation: `scripts/install-scr.ps1 -SetAsDefault`.
- **archive-org clips fail to stream ~23% of the time** (169 of 724 loads, Aug 14 – Oct 12 2026; zero Apple failures). mpv reports `loading failed (-13)` and the playlist skips ahead; the background download then caches the same clip fine, so the URL is valid and the stream path is what fails. Not yet diagnosed — re-run with `WIN4K_MPV_LOG=1` to see why.

In daily use since 2026-08-14 as the idle saver (30-min timeout). `publish/` holds the Win4KSS build (rename + `/p` leak fix + Phases B–C, published 2026-10-13); the installed Win4KAerial build is older until reinstalled. The cache sits at its 20 GB budget, so LRU eviction is active. The earlier wedged `SPI_GETSCREENSAVERRUNNING` state cleared on reboot.

See [PLAN.md](PLAN.md) for the design and, at the bottom, the findings that contradicted it.

## Layout

```
Win4KScreensavers/
├── src/
│   ├── Win4K.Core/          all logic, no UI
│   │   ├── Apple/           manifest client + models (tar-backed, cached, fallback)
│   │   ├── Catalogs/        ClipInfo, ClipKey, TagBuilder, ClipMerger, ClipLibrary, harvesters, CatalogRefresher/Store
│   │   ├── Caching/         VideoCache (content-addressed, staged, resumable, LRU, pin-aware), DownloadQueue, PinManager, CrossProcess
│   │   ├── Media/           MediaPreviewService (browser stills + hover clips), Mp4Boxes, PreviewCommands, ProcessRunner
│   │   ├── Playback/        MpvInterop, MpvLoader, MpvPlayer, PlaylistEngine
│   │   ├── Sources/         IVideoSource, Apple, LocalFolder, RegistryCatalog, ClipResolver
│   │   ├── Settings/        AppSettings (JSON)
│   │   └── AppleTrust.cs, CaBundle.cs, Http.cs, Log.cs, Display.cs, AppPaths.cs
│   ├── Win4K.ScreenSaver/   WinForms exe → Win4KSS.scr / Win4KSS.exe
│   │   ├── Program.cs       CLI verbs; SaverForm.cs; SettingsForm.cs
│   │   └── InputDismissWatcher.cs, LaunchContext.cs, PreviewLifetime.cs, ShortcutManager.cs, NativeMethods.cs
│   └── Win4K.Tests/         xunit (389); Fixtures/ = trimmed upstream samples for the harvesters
├── assets/                  fallback-entries.json, apple-root-ca.cer, registry/
├── native/mpv/              libmpv-2.dll — gitignored, fetched by script
├── scripts/                 fetch-libmpv.ps1, install-scr.ps1
├── publish/                 single-file output (gitignored)
├── CLAUDE.md                this file
└── PLAN.md                  design + findings
```

Runtime state lives in `%LOCALAPPDATA%\Win4KScreensavers\`: `settings.json`, `manifest.json`, `catalogs/`, `ca-bundle.pem`, `win4k.log`, `mpv.log`, `cache/videos`, `cache/partial` (kept across sessions for Range resume; 7-day orphans removed), `cache/cache-index.json`, `cache/queue.json` (pending pins), `previews/` (`<sha1(key)[:16]>.jpg`/`.mp4` + `index.json`; 1 GB LRU, separate from the cache budget; browser only).

## Conventions

- **Language/runtime:** C# on .NET 10, `net10.0-windows`, WinForms. WinForms specifically because libmpv needs a raw `HWND` for its `wid` option.
- **Video engine:** libmpv (`libmpv-2.dll`), fetched by `scripts/fetch-libmpv.ps1` into `native/mpv/`. Never commit the DLL.
- **Build:** `dotnet build` · **Test:** `dotnet test` · **Run windowed:** `dotnet run --project src/Win4K.ScreenSaver -- /w`
- **Live network tests** are inert by default: `$env:WIN4K_LIVE_TESTS="1"; dotnet test`
- **Test isolation:** `TestEnvironment` points `WIN4K_HOME` at a temp dir for the whole run; a test that needs its own app-data root uses `using var _ = AppPaths.UseRoot(dir)` (AsyncLocal). Never set `WIN4K_HOME` inside a test — it is process-wide, and flipping it is what made `CaBundleTests` fail intermittently. Harvester tests use `Fakes/FixtureHttp` (an `HttpMessageHandler`), never the network.
- **Publish:** `dotnet publish src/Win4K.ScreenSaver/Win4K.ScreenSaver.csproj -c Release -o publish` (single-file, ~115 MB)
- **Install:** `scripts/install-scr.ps1` (self-elevates; `-SetAsDefault`, `-Uninstall`)
- **Debugging playback:** `$env:WIN4K_MPV_LOG="1"` raises `mpv.log` from warnings to full debug
- Both projects need `<AllowUnsafeBlocks>` — `[LibraryImport]` source-generated marshalling requires it
- **Settings live in JSON**, not the registry: `%LOCALAPPDATA%\Win4KScreensavers\settings.json`. Cache and manifest live alongside it.
- Prefer `HttpClient` + `CancellationToken`; no `async void`, no `WebClient`.

## Key facts worth not re-deriving

- **Apple's current CDN is `sylvan.apple.com`.** The manifest is *inside* `https://sylvan.apple.com/Aerials/resources-16.tar` (read it with the built-in `System.Formats.Tar`; no package needed). 114 assets, HEVC Main10, 4K + 1080, SDR + HDR.
- **Trap:** `https://sylvan.apple.com/Aerials/2x/entries.json` still responds but its video URLs 404. It is stale. Only the tarball manifest is correct. `resources-17.tar` and `-18` do not exist.
- **`sylvan.apple.com` fails TLS on Windows out of the box.** Its chain is `sylvan.apple.com → Apple Server Authentication CA → Apple Root CA`, and **Apple Root CA is not in the Windows trusted root store** — every handshake fails with "unable to get local issuer certificate". We do not downgrade to `http://` and do not touch the machine's cert store; `AppleTrust` pins Apple's real root (embedded from `assets/apple-root-ca.cer`, SHA-1 `611E5B66…6C60`, valid to 2035) as a process-local trust anchor. Name mismatches stay fatal.
- The legacy `a1.phobos.apple.com` manifest is also still live, but it is 1080p H.264 only.
- **mpv needs its own fix for the same TLS problem.** It streams via **libcurl**, with a trust store separate from .NET's, so `AppleTrust` alone leaves playback failing. `CaBundle` writes a PEM of every Windows-trusted root *plus* Apple's and passes it as `tls-ca-file`. Never reach for `tls-verify=no`.
- **A shortcut must never target the `.scr`.** Windows registers scrfile's `open` verb as `"%1" /S`, so the shell discards a shortcut's arguments and substitutes `/S`. A `.lnk` aimed at the `.scr` with `/now` dispatches `FullScreen` — the saver runs directly and **never honours the resume lock**, invisibly. Shortcuts target a companion `.exe` under `%LOCALAPPDATA%\Programs\Win4KSS\` (hard-linked to the `.scr` when on one volume). Guarded by `ShortcutTargetTests`.
- **The browser and the saver share one cache, concurrently.** Index read-modify-writes run inside the named mutex `Local\Win4KSS.CacheIndex.<hash of cache root>`; partials are held `FileShare.None` (locked = another process owns that download); a `FileSystemWatcher` on `cache-index.json` raises `VideoCache.Changed` in the other process. Only the holder of `Local\Win4KSS.DownloadQueueOwner.<hash>` downloads pins (first process to start; the saver takes over when the browser exits, using 1 of its 2 slots). Pins live in `AppSettings.KeptOffline`, never evicted, and count against the one budget.
- **`ytdl=no` matters.** Without it mpv spawns a `yt-dlp` subprocess on every failed load and waits ~1s for it to fail.
- **Setting `ScreenSaveActive=1` in the registry does not enable the screen saver.** The session caches it; without `SystemParametersInfo(SPI_SETSCREENSAVEACTIVE, …, SPIF_SENDCHANGE)`, `SPI_GETSCREENSAVEACTIVE` stays false and Windows ignores `SC_SCREENSAVE` — the hotkey does nothing. `install-scr.ps1` handles this.
- **Never `Stop-Process` a running screen saver.** It leaves `SPI_GETSCREENSAVERRUNNING` stuck true: Windows holds a handle on the `.scr` (reinstall fails with a sharing violation), refuses to start another saver, and rejects `SPI_SETSCREENSAVEACTIVE` with error 329. Clears on sign-out. Dismiss with input or Escape instead.
- **`/p` must attach to the preview HWND in `OnLoad`, not `OnHandleCreated`.** WinForms resets a top-level form's parent after `OnHandleCreated`, silently undoing `SetParent` — the preview became a WS_CHILD of the desktop that nothing destroyed, and `/p` processes leaked for hours. `SaverForm` also polls `PreviewLifetime` (1 s) as a backstop. To launch a `.scr` with arguments from a script, use `CreateProcess` (`ProcessStartInfo.UseShellExecute=false`), never `Start-Process` — the shell swaps in `/S`.
- **Input dismissal cannot be tested from an automated session** — a non-interactive desktop refuses `SetCursorPos` and `GetCursorPos` reports `0,0`, so the watcher correctly sees no movement. The decision logic is unit-tested via `InputDismissWatcher.HasMovedEnough`; the polling needs a human with a real mouse.
- Only **4 of Apple's 114** clips carry a day/night label, so time-of-day ordering barely moves anything. The 1080p manifest marked all of them; the 4K one dropped the field.
- 4K clips are ~346 MB each, ~40 GB for all 114. Range requests are supported, so streaming and resumable downloads both work.
- **Previews (`Media/MediaPreviewService`) shell out to ffmpeg, which here is a GnuTLS build whose `tls_verify` defaults to 0, so it checks nothing.** Always pass `-tls_verify 1 -ca_file <ca-bundle.pem>` (`PreviewCommands` does; verified to reject a self-signed cert). Apple and Amazon `.mov` files keep `moov` at the tail, so a Range-fetched head is useless for them; `Mp4Boxes.IndexIsBeyond` spots this after 64 KB and ffmpeg reads the URL instead (~4 s per hover clip). Archive.org serves ~1.3 MB/s: a hover clip via the head route takes ~22 s and 27 MB; ffmpeg direct takes ~7.6 s (`MediaPreviewOptions.UseHeadSegments`). archive.org `.thumbs` are 160 px, so stills are upgraded to a 480 px frame whenever a hover clip has the video at hand. Hover clips must be `-pix_fmt yuv420p`: Apple's sources are 10-bit, and High 10 H.264 won't play in Chromium.
- libmpv is **114 MB**, making the published `.scr` ~115 MB.
- **Local toolchain, verified:** .NET 10.0.302 SDK + WindowsDesktop 10.0.10 · VS2022 · ffmpeg/ffprobe at `C:\ffmpeg` · yt-dlp 2026.07.04 · RTX 2080 Super (hardware HEVC Main10 decode) · HEVC Video Extensions installed.

## Video source registry

`assets/registry/sources.json` is the catalogue of where clips come from. Every entry was probed live and records what was **measured**, not what the site claims — including the dead ends, so they don't get re-investigated.

The `harvest: "auto"` sources are refreshed **in-app, in C#** (`Win4K.Core/Catalogs/`; `scripts/harvest.py` was retired 2026-10-13 after a parity run). One harvester per source — `ArchiveOrgHarvester` (search → `metadata/<id>`), `NasaHarvester` (search → `collection.json` → `metadata.json`, ffprobe only as a fallback and only if installed), `AppleFeedHarvester` (the four aerial feeds + the tvOS 26 strings file) — all over `Http.Client`, driven by `CatalogRefresher.RefreshAsync(sourceIds?, IProgress<CatalogRefreshProgress>?, ct)`. Nothing calls it yet; the clip browser (Phase E) will, on demand and at most every 7 days (`IsDue()`), never from the saver.

- **Catalogue files** are `catalog-<id>.json` in the **Apple manifest schema**, so `AppleManifest` parses them with no new deserialiser, plus **schema v2** extension fields: `key`, `feed`, `title`, `timeOfDay`, `categoryName`/`subcategoryName`, resolved `captions`, `creator`, `licence`, `subjects`, `description`, `codec`, `thumbnailUrl`, `addedUtc`, `width`/`height`/`sizeBytes`/`durationSeconds`. v1 files (harvest.py) still load.
- **Load order:** `%LOCALAPPDATA%\Win4KScreensavers\catalogs\catalog-<id>.json` (written by a refresh) → the copy embedded from `assets/registry/` (`CatalogStore`, used by `SourceRegistry.LoadCatalog`). A refresh that fails, or finds nothing, keeps the previous catalogue; an item that fails keeps its previous rows.
- **`addedUtc`** is first-seen, by diffing against the previous catalogue. Everything in the original harvest is dated **2026-08-12** (`CatalogRefresher.BaselineAddedUtc`) so it is not tagged New.
- **Regenerating the embedded copies:** `$env:WIN4K_LIVE_TESTS="1"; $env:WIN4K_CATALOG_OUT="<scratch dir>"; dotnet test --filter FullyQualifiedName~CatalogRefreshLiveTests`, review, then copy into `assets/registry/`.

Harvested 2026-08-12 (harvest.py): `archive-org` 1060, `aerial-manifests` 289, `nasa` 9. **Re-harvested 2026-10-13** (C#, embedded now): `archive-org` 1316 files → **1269** after dropping 47 `_YYYYMM` mirror re-uploads; `aerial-manifests` **289**; `nasa` **19**. Same-day parity with harvest.py: identical URL sets for archive-org (1316) and aerial-manifests (289); NASA 19 vs 9 because **harvest.py silently lost every NASA id containing a space** (urllib refuses the URL) — the C# set is a strict superset.

- **Clip keys** (`ClipKey`, = `Clip.Id` = `ClipInfo.Key`): `apple:<GUID>` (resources-16 *and* tvOS 26 — that shared key is how they merge), `ia:<item>/<file>`, `nasa:<nasa_id>`, `amazon:<id>`, `jetson:<id>`, `robin:<id>`, `local:<path>`. Never a URL. Pre-key ids in `settings.json` are rewritten on load (`AppSettings.MigrateClipKeys`).
- **The cache is still keyed by URL**, so URL escaping is load-bearing: archive.org URLs are escaped exactly as harvest.py's `urllib.parse.quote` did (verified byte-for-byte). NASA URLs moved from `http://` to escaped `https://` in the 2026-10-13 catalogue.
- `ClipLibrary.LoadAsync` is the browser's view: every source, merged (`ClipMerger`) — 114 Apple + 25 tvOS-only + the rest. `ClipResolver` (the saver's playlist) still honours `SourceMode`/`EnabledCatalogIds` and dedupes by key, first source wins.

- **Apple's manifest has a newer generation than `resources-16.tar`:** the tvOS 26 manifest carries **139 assets** (vs 114), a 4-category/59-subcategory tree, and a companion 687-entry strings file that resolves `localizedNameKey` and `pointsOfInterest` to caption text — no binary plist parsing needed. Mirrored via the `aerial-manifests` source.
- **Three other vendors publish Apple's exact schema** — Amazon Fire TV ambient (112 assets, and it has a real `timeOfDay` field Apple dropped), Jetson Creative (20), Robin Fourcade (18). Same parser, no new code path.
- **YouTube clips must be watermark-checked by extracting a frame**, not by reading the description — 5 of 8 channels tested burn in persistent branding that is invisible in thumbnails. Verified-clean channels are listed in the registry.
- **Never pass `--force-keyframes-at-cuts` to yt-dlp.** It forces a full re-encode: 3 min vs 13 s for the same 20 s 4K cut. Without it you get a stream copy; cuts just snap to the nearest keyframe.
- **`4kmedia.org` is a trap:** the site is up and its specs are accurate, but all 159 downloads 404 — the Google Drive files are gone. Already recorded under `rejected`.

## Notes for Claude

- This project is **local only**. Do not initialize git, add remotes, or push unless asked.
- Keep this file current: when the layout, build command, or conventions change, update the sections above in the same change. Tick the build-order table in PLAN.md as phases land.
