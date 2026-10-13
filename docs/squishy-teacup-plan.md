# Clip Browser + streaming reliability — plan

## Context
Two asks: (1) a Wallpaper-Engine-style browser to look through all ~1,500 available clips, filter/sort by tags, choose what plays and what is kept offline, and manage the cache; (2) make non-Apple sources stream as reliably as Apple.

The streaming investigation found the problem is ours, not archive.org's:
- **Bug 1 — URL unescaping.** `Uri.ToString()` turns `LA%2001` into `LA 01`; libcurl rejects the space (`URL using bad/illegal format`, mpv.log:3257) → mpv `-13`. **163/163** attempts to stream an uncached archive clip failed. Apple URLs have no spaces, so Apple never failed. Sites: `VideoCache.cs:114`, `PlaylistEngine.cs:25`, `SettingsForm.cs:237` (+ cosmetic `VideoCache.cs:247`). .NET downloads work because they pass the `Uri` object.
- **Bug 2 — one failure blacks out the saver.** mpv holds only one queued entry; a pre-failed entry fails again in ~1 ms before C# appends the next, mpv goes idle (`idle=yes`), and `loadfile … append` doesn't restart from idle. 165/169 failures were followed by a black screen until dismissal (median 1.6 h). The "23%" rate was masked by this.
- **Plus real flakiness:** archive.org 500/502/503 and timeouts, with no retries anywhere; 2 `-17` failures were probably error pages handed to the demuxer.

## Decisions (user, 2026-10-12)
| Topic | Decision |
|---|---|
| UI | WebView2 HTML UI hosted in the WinForms exe |
| Per-clip state | Two independent toggles: **In rotation** and **Keep offline** |
| Entry point | Browser **replaces** the `/c` settings dialog; general settings become a panel in it |
| Tags | Auto-derived from metadata (no custom tags / ratings / frame analysis) |
| Budget | Kept-offline clips count against the one cache limit; rolling cache gets the rest; UI blocks over-pinning |
| New clips | Per-source "auto-add new clips" toggle; new clips get a `New` tag |
| Preview | Live mpv preview in the detail pane **and** animated hover previews in the grid |
| Catalogues | In-app refresh; port `scripts/harvest.py` to C# and retire it |

---

## Phase A — Streaming reliability (ships first, standalone)
1. **Escaping:** add `Clip.PlayableUrl => Url.IsFile ? Url.LocalPath : Url.AbsoluteUri` and use it at all four sites. Test with a `%20` URL.
2. **No more stalls** (`PlaylistEngine`, `MpvPlayer`):
   - Enqueue with `append-play` and keep **two** entries queued ahead.
   - Add `MPV_EVENT_IDLE` (11) to `MpvEventId`. If idle while the saver is running, advance immediately.
   - Use `MpvEventEndFile.PlaylistEntryId` (already marshalled, `MpvInterop.cs:152`) to map end-file events to the clip that actually ended, so the index can't drift.
   - **Watchdog:** no `file-loaded`/playback-time progress for 20 s → `Next()`.
3. **Failure policy:**
   - Retry a failed clip once after 2 s. A second failure marks it bad for this session.
   - After 3 consecutive failures, switch to cached-only rotation for the session. Log loudly.
   - Clips that failed in 3 separate sessions get persisted as `unhealthy`, which surfaces as a tag in the browser.
4. **Download-ahead** (this is what makes non-Apple as good as Apple): while clip N plays, ensure clip N+1 (and N+2 if it's cheap) is cached through the download queue (Phase C). If it's ready by switch time it plays from disk; otherwise it streams. Archive clips are 70–80 MB, so they arrive well ahead.
5. **Download hardening** (`VideoCache.DownloadAsync`):
   - Retry 5xx and timeouts with backoff (1 s / 4 s / 15 s), honouring `Retry-After`.
   - Reject `text/html` responses and Content-Length mismatches, so error pages never get cached.
   - Log the status of every attempt.
6. **Logging:**
   - Log every `loadfile` URL.
   - `mpv_request_log_messages(ctx,"error")` → forward to `win4k.log`.
   - Permanent `msg-level=all=warn,curl=v` in mpv.log.
   - Warn "playlist stalled" on idle.
7. **Test seam:** extract `IMpvPlayer` from `MpvPlayer` so `PlaylistEngine` is unit-testable with a fake. The fake simulates instant failures, idle and entry ids.

## Phase B — Unified clip model + in-app catalogue refresh
1. **Rich model.** Replace the thin `Clip` record with `ClipInfo` (keep `Clip` as the playable projection, or extend it; `Clip.cs:32`):
   - Identity and source: `Key` (namespaced: `apple:<guid>`, `ia:<item>/<file>`, `nasa:<id>`, `amazon:<id>`, `local:<path>`), `SourceId`, `SourceIds` (after merge)
   - Text: `Title`, `Category`, `Subcategory`, `Captions[]`, `Creator`, `Licence`
   - Media facts: `Width`, `Height`, `DurationSeconds`, `SizeBytes`, `Codec`, `HasHdr`, `TimeOfDay` (enum adds Sunrise, Sunset)
   - Links and tags: per-quality `Urls`, `ThumbnailUrl`, `Tags`, `AddedUtc`
2. **Dedupe/merge:**
   - Apple resources-16 ⋈ tvOS 26 by GUID gives one clip with Apple's URL as primary, plus tvOS 26 subcategory, captions and preview image. The 25 tvOS-only clips stay separate.
   - archive-org: drop `*_201507` mirrors by `(file name, size)`.
3. **Catalogue harvesting in C#** (`Win4K.Core/Catalogs/`), one harvester per feed:
   - `ArchiveOrgHarvester`: search → `metadata/<id>`. Keep `subject` (list or `;`-string), title, creator, licence, per-file `length`, and the `.thumbs/` frame linked via `files[].original`.
   - `NasaHarvester`: keep `keywords`, `description`; ffprobe for dims and duration when ffmpeg is present.
   - `AppleFeedHarvester` for the `aerial-manifests` feeds:
     - Keep the `categories` tree.
     - Fetch the strings file, normalising `Â\u00A0` → space.
     - Resolve category/subcategory names and POI captions.
     - Read Amazon's `timeOfDay` and `scene`.
   - Driven by the existing `assets/registry/sources.json` (groups, queries, feeds).
   - Output goes to `%LOCALAPPDATA%\Win4KScreensavers\catalogs\catalog-<id>.json` (schema v2, still Apple-compatible plus extension fields). Load order: local catalogue → embedded fallback. `SourceRegistry.LoadCatalog` gains the override.
   - Writes `AddedUtc` per asset by diffing against the previous catalogue. That date drives the `New` tag and per-source auto-add.
   - Refresh runs from the browser button. It may also run automatically every 7 days in the background, from the browser only and never the saver.
   - Delete `scripts/harvest.py` once it's at parity. One-time check: the C# output matches Python's asset counts (1060 / 289 / 9).
4. **`TagBuilder`** (pure, unit-tested) produces faceted tags:
   - **Source**: Apple, Internet Archive, NASA, Amazon, Jetson, Robin, Local
   - **Category / Subcategory**: Space, Landscapes, Cities, Underwater, plus 59 tvOS subcategories and vendor scenes
   - **Subject**: archive subjects + normalised label prefix ("Thin Smoke", "Clouds Timelapse"), stop-worded ("stock footage", "4k", creator names)
   - **Time of day**: Day, Night, Sunrise, Sunset
   - **Resolution**: 1080p, 4K, 5K+, 8K
   - **Aspect**: 16:9, Ultrawide
   - **Dynamic range**: HDR available
   - **Duration**: <30 s, 30–60 s, 1–3 min, 3–10 min, 10 min+
   - **Codec**: HEVC, H.264
   - **Licence**
   - **State** (live): In rotation, Kept offline, Cached, Downloading, New, Unhealthy

## Phase C — Cache: pins, queue, multi-process safety
1. **Pins:**
   - `Settings.KeptOffline` holds the set of clip keys.
   - `VideoCache.EvictToBudget` never evicts pinned files.
   - Budget is `pinnedBytes + rollingBytes ≤ CacheBudgetGb`.
   - The UI refuses a pin that would exceed the budget, with "Raise limit to X GB?".
   - On a quality change, pinned clips re-download at the new quality, and the old file is released once the new one is done.
2. **Index:** `CacheEntry` gains `ClipKey`. Cache keys stay URL-based (quality-specific). Store `AbsoluteUri`.
3. **`DownloadQueue` service:**
   - Priority: user pin > download-ahead > played-clip background.
   - Concurrency 2, per-clip progress and cancel, retries per A5.
   - Persists `queue.json` so pins resume after restart.
   - **Partials are no longer deleted on startup.** Only orphans older than 7 days are cleaned up, so Range-resume works across sessions.
4. **Cross-process:** the browser and the saver can both run (the idle timer fires while the browser is open).
   - Named mutex `Local\Win4KAerial.CacheIndex` around index read-modify-write.
   - Partial files are opened `FileShare.None`; a locked partial means another process owns it, so skip it.
   - `Changed` is raised via a `FileSystemWatcher` on `cache-index.json` so each process sees the other's writes.
5. Small fixes: atomic `ActiveDownloads` (`Interlocked`); `Changed` marshalled by consumers; "Download all" replaced by "Keep offline: all filtered".

## Phase D — Thumbnails + hover previews (`MediaPreviewService`)
- Store under `%LOCALAPPDATA%\Win4KScreensavers\previews\<clipKeyHash>.jpg` / `.mp4`. LRU with a 1 GB cap, counted separately from the video cache since it's tiny by comparison.
- **Stills (480 px JPEG)**, first that works:
  1. catalogue `ThumbnailUrl` (tvOS `previewImage`, archive `.thumbs`, NASA `~thumb`), fetched through `Http.Client` (AppleTrust)
  2. ffmpeg frame from the cached file
  3. ffmpeg frame from the remote head segment
  4. a placeholder tinted by category
- **Hover clips** (4 s, 480 px, H.264 CRF 30, no audio, ~200 KB): `ffmpeg -ss 8 -t 4 -vf scale=480:-2 -an -c:v libx264 -preset veryfast -crf 30 -movflags +faststart`.
  - Input is the cached file if present. Otherwise fetch the first ~20 MB with an HTTP Range request via `HttpClient` into a temp file, so TLS trust stays ours. If the container needs its index from the tail (`moov` at end), fall back to ffmpeg-over-HTTPS with `-ca_file <ca-bundle.pem>` (ffmpeg is a GnuTLS build).
- **Scheduling:**
  - On demand for cards scrolled into view (priority).
  - Background fill while the browser is open, 2 workers. ~1,500 clips is about 1–2 h one-time and ~300 MB.
  - Never runs during the saver.
- ffmpeg path is a setting, default `C:\ffmpeg\ffmpeg.exe` then `PATH`. If missing: remote stills + placeholders, no hover clips, with a notice in the UI.

## Phase E — Browser UI (WebView2)
- **Packaging:**
  - Add `Microsoft.Web.WebView2` to Win4K.ScreenSaver.
  - Single-file publish with `IncludeNativeLibrariesForSelfExtract` (for `WebView2Loader.dll`).
  - User data folder `%LOCALAPPDATA%\Win4KScreensavers\webview2`.
  - UI files (`index.html`, `app.js`, `app.css`, vanilla JS, no framework or CDN) embedded as resources and served through `WebResourceRequested` on `https://app.win4k/`.
  - Previews served via `SetVirtualHostNameToFolderMapping("previews.win4k", previewsDir)`.
- **`BrowserForm`** (WinForms) hosts the WebView2 and a native mpv `Panel` positioned over the detail pane's video rect. JS posts the rect on resize/scroll. The panel is a sibling HWND above the WebView2, which sidesteps WebView2's lack of Apple TLS trust and HEVC uncertainty.
- **Bridge:** typed JSON messages over `PostWebMessageAsJson` / `WebMessageReceived`.
  - Host → page: `catalog` (full list once), `stateDelta` (rotation, pin, cached, progress, health), `previewReady`, `settings`, `cacheStats`.
  - Page → host: `setRotation(keys,bool)`, `setPinned(keys,bool)`, `selectClip(key)`, `requestPreviews(keys)`, `saveSettings(patch)`, `refreshCatalogs`, `startNow`.
  - A `BrowserBridge` class keeps C# logic testable without WebView2.
- **Layout:**
  - **Left:** facet sidebar (tag groups with counts; OR within a facet, AND across facets) plus search.
  - **Top:** sort (name, duration, size, resolution, date added, last played, source), view size, "select all filtered".
  - **Grid:** cards with still → hover clip, title, duration badge, 4K/8K/HDR badges, rotation/pin/cached indicators, download progress ring. `content-visibility:auto` for 1,500 cards.
  - **Right:** detail pane with live mpv preview, metadata, captions, tag chips (clickable to filter), and **In rotation** / **Keep offline** toggles.
  - **Multi-select:** ctrl- and shift-click, with bulk actions in a bottom bar.
- **Settings panel** (tabs in the page): everything in today's `SettingsForm` (sources, folders, quality, multi-monitor, shuffle, time-of-day, trigger/shortcuts/hotkey, about), plus:
  - Cache: limit slider, a usage bar (kept / rolling / free disk), folder, purge non-pinned, ffmpeg path.
  - Per-source auto-add toggles.
- **Changes apply immediately** (no OK/Cancel); the settings save is debounced.
- `/c` → `BrowserForm`. If the WebView2 runtime is missing (shouldn't happen on Windows 11), show a message box with the install link. Delete `SettingsForm.cs` after parity, carrying over its shortcut/hotkey wiring (`ApplyShortcuts`) and `StartNow`.

## Phase F — Settings v2 + saver integration
- **`AppSettings` v2:**
  - `RotationKeys` (set), `KeptOffline` (set), `AutoAddSources` (set), `KnownKeys` (for detecting new clips), `FfmpegPath`, `FailedClips` (key → session count).
  - Retire `SelectedClipIds`, `EnabledCatalogIds` and `SourceMode` via migration.
- **First real use of `Version`.** v1 → v2 migration:
  - Rotation = clips from the currently enabled sources (Apple + archive-org today), filtered by `SelectedClipIds` if non-empty.
  - AutoAdd = those sources.
  - Back up `settings.v1.json`.
- **Saver:**
  - Resolve all catalogues (cheap, local JSON) and filter by `RotationKeys`.
  - Merge in new keys from auto-add sources at load time.
  - Empty rotation → all cached clips → Apple.
- `ClipResolver` stops caring about `SourceMode`. Every source is always resolved for the browser; rotation is per-clip.

## Phase G — Docs
Update CLAUDE.md (status, layout, the streaming root cause in "Key facts", retire harvest.py references) and PLAN.md (add phases 9+ to the build-order table, findings section).

---

## Critical files
- Modify:
  - `src/Win4K.Core/Playback/{PlaylistEngine,MpvPlayer,MpvInterop}.cs`
  - `src/Win4K.Core/Caching/{VideoCache,CacheIndex}.cs`
  - `src/Win4K.Core/Sources/{Clip,ClipResolver,RegistryCatalogSource,AppleAerialSource}.cs`
  - `src/Win4K.Core/Apple/AppleEntries.cs`, `src/Win4K.Core/Settings/AppSettings.cs`
  - `src/Win4K.ScreenSaver/{Program,SaverForm}.cs`, `Win4K.ScreenSaver.csproj`
- New:
  - `Win4K.Core/Catalogs/*` (harvesters, `TagBuilder`, merge)
  - `Win4K.Core/Caching/DownloadQueue.cs`, `Win4K.Core/Media/MediaPreviewService.cs`
  - `Win4K.ScreenSaver/Browser/{BrowserForm,BrowserBridge}.cs` + `Browser/ui/*`
- Delete (after parity): `SettingsForm.cs`, `scripts/harvest.py`

## Reuse
- `Http.Client` + `AppleTrust` for every fetch
- `CaBundle` PEM for ffmpeg `-ca_file` and mpv
- `VideoCache.KeyFor`/`FormatSize`
- `AppleManifestClient` disk-cache/fallback pattern for the catalogue store
- `AppleAerialSource.ToClips` category-resolution logic
- The `SettingsForm` preview lifecycle (`EnsurePreviewPlayer`/`StopPreview`) moves to `BrowserForm`
- `ShortcutManager` for the trigger tab
- `WIN4K_HOME` test isolation

## Verification
- **Unit tests** (fakes via `HttpMessageHandler` and the new `IMpvPlayer`):
  - Escaping
  - Playlist under instant failure, idle and entry-id mismatch
  - Retry/backoff, and an HTML response rejected
  - Pins never evicted; budget refusal; partial resume across instances
  - Index mutex (two `VideoCache` instances)
  - `TagBuilder` facets for each source sample
  - Merge/dedupe
  - Settings v1→v2 migration from today's real `settings.json` shape
  - Bridge message round-trips
- **Live tests** (`WIN4K_LIVE_TESTS=1`):
  - Stream smoke test: libmpv with `vo=null`, load 5 uncached clips per source and require `file-loaded` within 15 s. This is the "as well as Apple" gate.
  - Catalogue refresh produces counts ≥ today's.
  - Preview generation for one clip per source.
- **Soak:** run `/w` for 1 h on a rotation that's mostly archive clips. `win4k.log` must show zero stalls, and any failures must recover within one clip.
- **Manual:**
  - `/c` opens the browser; filter by two facets, sort, hover-play, detail live preview.
  - Pin 3 clips and watch progress; restart mid-download and confirm it resumes.
  - Exceed the budget and confirm the refusal.
  - Leave the browser open past the idle timeout and confirm the saver plays and both processes stay consistent.
- `dotnet test` green; publish; reinstall with `install-scr.ps1`.

## Order of work
A (ship + reinstall immediately; it fixes today's black screens) → B → C → D → E → F → G. Run tests and publish after each phase. A stands alone; B–F build on each other.
