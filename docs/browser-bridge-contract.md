# Browser ↔ host bridge contract (Phase E)

This is the interface the Phase E browser UI (WebView2 page) is built against. It
reflects the **real C# types already in this repo** — Phases A–D and F are all landed
on `main` (`7aae289`). Corrected 2026-10-13 against win4kscreensavers-67's actual
implementation; earlier enum/field names in this doc (`Verifying`, `Canceled`,
`IMediaPreviewService`, bare preview `path`, `enabledCatalogIds` in `settings`) were
wrong — this revision is the source of truth.

**Lane split (2026-10-13):** 67 owns `BrowserBridge.cs`, `BrowserForm.cs`, the WebView2
`.csproj` reference, `Program.cs`'s `/c` wiring, and deleting `SettingsForm.cs` — all on
`main`, because it has to compile and run on Windows. The UI lane (this contract's
other reader) owns **only** `src/Win4K.ScreenSaver/Browser/ui/*` and
`docs/browser-mock/*`. **Do not write `BrowserBridge.cs`** — 67 is coding it to this
doc independently; the two sides meet at this contract, not by one side writing both.

See `squishy-teacup-plan.md` for the full plan this implements.

## Why this doc exists

The browser UI (HTML/CSS/JS under `src/Win4K.ScreenSaver/Browser/ui/`) is being built
on a machine that can't compile or run this Windows-only project. It is built against a
**mock bridge** — a hand-written JSON fixture + a tiny `window.chrome.webview` shim —
that must match these real shapes exactly, so wiring it to the real `BrowserBridge.cs`
(built separately, on `main`) is mechanical, not a negotiation.

## Source types (read these, don't guess)

- `src/Win4K.Core/Catalogs/ClipInfo.cs` — `ClipInfo`, `ClipUrls`, `ClipCaption`
- `src/Win4K.Core/Catalogs/TagBuilder.cs` — `TagFacet`, `ClipTag`, `ClipState`, `Values.*`
- `src/Win4K.Core/Catalogs/ClipLibrary.cs` — loads all clips from all sources
- `src/Win4K.Core/Caching/PinManager.cs` — `PinCheck`, `Pin`/`Unpin`/`CanPin`/`IsPinned`/`Pinned`
- `src/Win4K.Core/Caching/DownloadQueue.cs` — `DownloadStatus`, `DownloadState`, `DownloadPriority`, `Enqueue`/`EnqueuePlayed`/`Cancel`/`RetryFailed`/`Snapshot`/`Get`
- `src/Win4K.Core/Media/MediaPreviewService.cs` + `PreviewIndex.cs` — `PreviewState`, `PreviewKind`, `PreviewCapabilities`, `PreviewStats`, `Request`/`Get`/`Stats`, `VirtualHost = "previews.win4k"`. **No `IMediaPreviewService` interface exists — the concrete class is the contract.**
- `src/Win4K.Core/Playback/ClipHealth.cs` — `ClipHealth.IsUnhealthy(settings, key)`, static, `FailedClips` count ≥ 3. There is no health enum.
- `src/Win4K.Core/Catalogs/CatalogRefresher.cs` — `RefreshAsync(sourceIds?, IProgress<CatalogRefreshProgress>?, ct)`
- `src/Win4K.Core/Settings/AppSettings.cs` — **v2, landed.** `RotationKeys`, `AutoAddSources`, `KnownKeys` are real `HashSet<string>` fields keyed by `ClipInfo.Key` (same value as `Clip.Id`), alongside the existing `KeptOffline`. `FfmpegPath` is `string?`. `FailedClips` is `Dictionary<string,int>`. `SelectedClipIds`, `EnabledCatalogIds` and `SourceMode` are **null and retired** — don't build UI against them; see the `settings` message below, which no longer sends `enabledCatalogIds`.
- `src/Win4K.Core/Settings/Rotation.cs` — static helper the host uses, not something the page calls directly, but it shapes what `setRotation`/`setAutoAdd` actually do: `SetInRotation(settings, keys, bool)`, `SetAutoAdd(settings, sourceId, bool)` (future clips only — pairing it with `SetInRotation(Rotation.KeysOf(catalogue, sourceId))` is how existing clips get included), `IsInRotation`, `SourceIds`, `SourceOf(ClipInfo)`.
- `src/Win4K.Core/Settings/SettingsMigrator.cs` — `Prepare(settings, catalogue)`, called once by the host right after loading the catalogue (v1→v2 migration). Nothing the page calls.

### Exact enum members (don't guess — these were wrong in an earlier draft of this doc)
- `DownloadState`: `Queued, Waiting, Downloading, Done, Failed, Cancelled` — no `Verifying`; `Waiting` means another process holds the partial; note the double-L spelling of `Cancelled`.
- `DownloadPriority`: `Background` (lowest) `< DownloadAhead < Pin`.
- `PreviewStatus`: `Missing, Pending, Ready, Placeholder, Failed, Unavailable`. `Placeholder` carries only `PlaceholderColor`, no file. `Unavailable` = a hover clip request with no ffmpeg available.
- `PreviewKind`: `None, Still, Hover, Both` (flags).

## Transport

`CoreWebView2.PostWebMessageAsJson` (host→page) and `WebMessageReceived` (page→host).
Every message is `{ "type": "<name>", ...payload }`. The page never talks to anything
but this channel — no fetch to a real backend except `https://previews.win4k/...` for
preview images and `https://app.win4k/...` for the page's own static assets (previews via
`SetVirtualHostNameToFolderMapping`, the page's files via `WebResourceRequested` from
embedded resources; either way paths are plain `/path/to/file.jpg`).

## Handshake (load order)

1. The page installs its `window.chrome.webview.addEventListener('message', …)` listener,
   then posts **`{ "type": "ready" }`**. The host posts nothing before that.
2. The host answers with `settings`, then — as soon as the catalogue has loaded, which may
   already be the case — `catalog`, a **full** `stateDelta` (every clip), and `cacheStats`.
   If `ready` arrives before the catalogue has loaded, the page gets `settings` +
   `cacheStats` at once, and `catalog` + full `stateDelta` + `cacheStats` + `settings` (now
   with per-source clip counts) when it lands. Show a spinner until `catalog`.
3. After that, deltas are incremental. Send `ready` again only after a reload; the host then
   resends everything from scratch (reset page state on every `catalog`).
4. Then post `panelRect` (and `selectClip` if something is selected) so the native preview
   lands in the right place.

## Host → page messages

### `catalog` (sent once after load, and again after a catalogue refresh)
```json
{
  "type": "catalog",
  "clips": [
    {
      "key": "apple:7825C73A-...",
      "sourceId": "apple",
      "sourceIds": ["apple", "aerial-manifests:apple-tvos26"],
      "title": "Hawaii Coastline",
      "category": "Landscapes",
      "subcategory": "Hawaii",
      "captions": [{ "atSeconds": 0, "text": "Volcanic coast at sunrise" }],
      "creator": null,
      "licence": null,
      "subjects": ["coastline", "volcanic"],
      "width": 3840, "height": 2160,
      "durationSeconds": 274.8,
      "sizeBytes": 346000000,
      "codec": "hevc",
      "hasHdr": true,
      "timeOfDay": "Day",
      "thumbnailUrl": "https://sylvan.apple.com/itunes-assets/.../GMT110_900x580.png",
      "isLocal": false,
      "addedUtc": "2026-08-12T00:00:00Z",
      "description": null,
      "sources": ["aerial-manifests", "apple"],
      "tags": [{ "facet": "Source", "value": "Apple" }, { "facet": "Category", "value": "Landscapes" }, { "facet": "Resolution", "value": "4K" }, { "facet": "DynamicRange", "value": "HDR available" }]
    }
  ]
}
```
`TagFacet` values (see `TagBuilder.cs` enum order): `Source, Category, Subcategory,
Subject, TimeOfDay, Resolution, Aspect, DynamicRange, Duration, Codec, Licence, State`.
`State` tags (`TagBuilder.Values`) are **not** included here — they come live via
`stateDelta`, because they change without a catalogue refresh.
Host additions: `description` (NASA prose, else null) and `sources` — the clip's rotation
source ids (`Rotation.SourcesOf`: `apple`, `aerial-manifests`, `archive-org`, `nasa`,
`local`), the same ids `setAutoAdd` and `settings.sources` use. `timeOfDay` is one of
`Unknown|Day|Night|Sunrise|Sunset`. `addedUtc` is ISO-8601 UTC or null.

### `stateDelta` (sent on any change: rotation, pin, cache, download progress, health)
```json
{
  "type": "stateDelta",
  "clips": {
    "apple:7825C73A-...": {
      "inRotation": true,
      "pinned": false,
      "cached": true,
      "download": null,
      "health": "Ok",
      "isNew": false
    },
    "ia:Fire7345s4kRes/Fire 66 - 45s - 4k res.mp4": {
      "inRotation": true,
      "pinned": true,
      "cached": false,
      "download": { "state": "Downloading", "fraction": 0.42, "bytesReceived": 35000000, "totalBytes": 83072197, "error": null },
      "health": "Ok",
      "isNew": true
    }
  }
}
```
- `download.state` mirrors `DownloadState` exactly: `Queued|Waiting|Downloading|Done|Failed|Cancelled`.
- `health` is derived by the host from `ClipHealth.IsUnhealthy(settings, key)` and sent as the string `"Ok"` or `"Unhealthy"` (there's no C# health enum — this mapping happens in `BrowserBridge.cs`). Use `Unhealthy` to render the tag.
- `isNew` comes from `TagBuilder.IsNew(clip, nowUtc)` (`AddedUtc` within 30 days) — **never** derive "new" on the page from anything else; there's no `KnownKeys`-based signal in this message.
- This message can be partial (only changed clips) or full; the page must merge by key, never replace the whole map.
  The host only sends a clip when its state differs from what it last sent. User actions
  (setRotation/setPinned/…) are answered at once; download progress and cache changes are
  batched to ~4 Hz.
- `cached` is true when the clip's playable rendition (at the current quality) is in the
  video cache. Local clips are always `cached: false`; use `isLocal` from `catalog`.
- `download` is null when no job exists (including once a job retires after `Done` or
  `Cancelled`); `error` is the failure text for `Failed`.
- `inRotation` = `RotationKeys`; `pinned` = `KeptOffline`.

### `previewReady`
```json
{ "type": "previewReady", "key": "apple:7825C73A-...", "kind": "Still", "status": "Ready", "url": "https://previews.win4k/a1b2c3d4.jpg?v=3", "placeholderColor": "#2b4a3d" }
```
- Use the full `url` as given (`PreviewState.Url`, already `https://previews.win4k/<file>?v=<Version>` — the `?v=` query is cache-busting on regeneration; don't strip it or construct the URL yourself from a bare path).
- `status` mirrors `PreviewStatus` exactly: `Missing|Pending|Ready|Placeholder|Failed|Unavailable`.
- `kind` is `"Still"` or `"Hover"`.
- On `Placeholder`/`Failed`/`Unavailable`/`Missing` (any status other than `Ready`), there is no usable `url` — render a placeholder tinted `placeholderColor` (`MediaPreviewService.PlaceholderColor`, keyed by category) instead.

### `settings`
```json
{
  "type": "settings",
  "values": {
    "quality": "Auto", "multiMonitorMode": "MainOnly", "shuffle": true, "useTimeOfDay": true,
    "cacheEnabled": true, "cacheDirectory": "C:\\Users\\...\\cache", "cacheBudgetGb": 20.0,
    "localFolders": [], "ffmpegPath": null,
    "autoAddSources": ["apple", "local"], "availableSources": ["apple", "aerial-manifests", "archive-org", "nasa", "local"],
    "previewCapabilities": { "ffmpegAvailable": true, "hoverClips": true, "ffmpegPath": "C:\\ffmpeg\\ffmpeg.exe" },
    "shortcutDesktop": true, "shortcutStartMenu": false, "hotkey": "CTRL+ALT+S"
  },
  "sources": [
    { "id": "apple", "name": "Apple TV aerials", "clipCount": 114, "autoAdd": true },
    { "id": "aerial-manifests", "name": "Aerial manifests (tvOS 26, Fire TV, community)", "clipCount": 289, "autoAdd": false },
    { "id": "archive-org", "name": "Internet Archive", "clipCount": 1060, "autoAdd": false },
    { "id": "nasa", "name": "NASA", "clipCount": 9, "autoAdd": false },
    { "id": "local", "name": "Local folders", "clipCount": 0, "autoAdd": true }
  ],
  "about": { "version": "1.0.0.0", "mpvVersion": "2.5", "dataFolder": "…", "settingsFile": "…",
             "logFile": "…", "mpvLog": "…", "cacheFolder": "…", "previewsFolder": "…" }
}
```
- **No `enabledCatalogIds`** — that field is retired in v2 (`SourceMode`/`EnabledCatalogIds`/`SelectedClipIds` are all null now). Every source is always resolved; what plays is purely per-clip rotation (`stateDelta.inRotation`) plus per-source `autoAddSources`.
- `availableSources` is the full list of source ids to render the per-source auto-add toggle row against (derived from `Rotation.SourceIds`, not a settings field itself — the host computes it from the loaded catalogue).
- `autoAddSources` is `AppSettings.AutoAddSources`, landed in Phase F — the host sends it as-is.
- **Host correction:** `availableSources` is exactly `Rotation.SourceIds` — the five plain
  ids `apple`, `aerial-manifests`, `archive-org`, `nasa`, `local` (vendor feeds such as
  `apple-tvos26` all belong to `aerial-manifests`; they are not separately toggleable).
  `sources` (outside `values`, read-only) gives the same ids with a display name, clip count
  and auto-add state — the easiest thing to render the toggle row from. `about` feeds the
  About tab.
- Enum values: `quality` = `Auto|Uhd4KSdr|Uhd4KHdr|Hd1080Sdr|Hd1080Hdr|Hd1080H264`,
  `multiMonitorMode` = `MainOnly|SameOnEach|DifferentVideos|SpanAll`.
- Shortcut fields (host addition) reflect the real `.lnk` files (`ShortcutManager`).
  `hotkey` is in the `.lnk` form `CTRL+ALT+S`; on save the host also accepts DOM
  `KeyboardEvent.code` names (`Ctrl+Alt+KeyS`, `Digit1`), requires at least one of
  Ctrl/Alt/Shift (else a `notice`), and `""`/null clears it.
- The host re-sends `settings` after every `saveSettings`: render what comes back.

### `cacheStats`
```json
{ "type": "cacheStats", "pinnedBytes": 12000000000, "rollingBytes": 7800000000, "budgetBytes": 21474836480, "freeDiskBytes": 90000000000,
  "pinnedFiles": 31, "rollingFiles": 22, "pendingPins": 3, "ownsPins": true }
```
`ownsPins` false means another process (the saver) is downloading pins; show them as queued.

## Page → host messages

| Message | Payload | Maps to |
|---|---|---|
| `ready` | `{}` | handshake, see above. Send once the message listener is installed |
| `setRotation` | `{ keys: string[], value: bool }` | `Rotation.SetInRotation(settings, keys, value)` + debounced save; answered with a `stateDelta` |
| `setAutoAdd` | `{ sourceId: string, value: bool, includeExisting?: bool }` | `Rotation.SetAutoAdd(settings, sourceId, value)`; `value: true` with `includeExisting: true` also calls `Rotation.SetInRotation(settings, Rotation.KeysOf(catalogue, sourceId), true)`. The UI's per-source toggle should prompt "also add the existing N clips?" (N = count of that source's clips not yet in rotation) and send `includeExisting` from the answer — don't silently default it either way. Host: `includeExisting` applies `value` to the existing clips both ways (`value: false` + `includeExisting: true` takes the source's clips out of the rotation). `sourceId` must be one of `availableSources`; answered with `settings` + `stateDelta`. |
| `setPinned` | `{ keys: string[], value: bool }` | `PinManager.Pin`/`Unpin`; on `Pin`, host first runs `CanPin` and may reply with a one-off `pinRefused` message (see below) instead of a `stateDelta` |
| `selectClip` | `{ key: string \| null }` | drives the native mpv preview panel the host positions over the detail pane's video rect (see `panelRect` below) |
| `panelRect` | `{ x, y, width, height, visible: bool }` | page tells host where to position the native preview `HWND`, in **viewport** CSS pixels: `getBoundingClientRect()` of the video box, so scroll is already applied; sent on resize/scroll/select/layout change. The host converts with the WebView zoom and monitor DPI. The native panel draws **above** all page content, so send `visible: false` whenever anything (a menu, a modal, the settings panel) covers the video box or it scrolls out of view; the host hides the panel and pauses mpv |
| `requestPreviews` | `{ keys: string[], kinds: "Still"\|"Hover"\|"Both" }` | `MediaPreviewService.Request(keys, PreviewKind)` — `kinds` string maps 1:1 to the flag enum member name. Every preview already decided (Ready, Placeholder, Failed, Unavailable, Missing) is answered at once with `previewReady`; Pending ones arrive when done. Background fill also emits `previewReady` for clips never requested: keep them |
| `cancelPreviews` | `{ keys: string[] }` | `MediaPreviewService.Cancel(keys)`, for cards scrolled away |
| `saveSettings` | `{ patch: { ...partial of settings.values } }` | applied to `AppSettings` at once, written 500 ms after the last edit; answered with `settings`. Unknown/read-only fields are ignored. `cacheBudgetGb` is clamped to ≥ 1 (lowering it evicts now). `cacheDirectory` and `ffmpegPath` take effect next launch (a `notice` says so). `localFolders` (the full list) reloads the catalogue. `autoAddSources` (the full list) sets every source's auto-add. Shortcut fields write the `.lnk` files at once |
| `refreshCatalogs` | `{}` | `CatalogRefresher.RefreshAsync` over every auto source; streams `refreshProgress` (below), then a new `catalog` + full `stateDelta` (settings re-prepared, so new clips from auto-add sources join the rotation). Ignored while one is running |
| `startNow` | `{}` | asks Windows to start the configured saver (the `/now` path; honours the resume lock). A refusal comes back as a `notice` |
| `purgeCache` | `{}` | `VideoCache.PurgeUnpinned()` (confirm in the page first); a `notice` reports the count |
| `retryDownloads` | `{}` | `DownloadQueue.RetryFailed()` |
| `pickFolder` | `{ purpose: "localFolder"\|"cacheDirectory" }` | native folder dialog; the choice is added to `localFolders` (catalogue reloads) or becomes `cacheDirectory`; answered with `settings` |
| `openFolder` | `{ which: "cache"\|"logs"\|"previews" }` | opens it in Explorer |
| `log` | `{ level: "info"\|"warn"\|"error", text: string }` | written to win4k.log as `Page: …`; use it for page errors (`window.onerror`) |

### `pinRefused` (host → page, reply to a refused `setPinned`)
```json
{ "type": "pinRefused", "keys": [...], "requiredGb": 24, "budgetGb": 20, "shortfallGb": 4 }
```
Shape mirrors `PinCheck` (`RequiredGb`, budget, `ShortfallBytes`). Page should offer
"Raise limit to {requiredGb} GB?" → on yes, `saveSettings({ patch: { cacheBudgetGb: requiredGb } })` then retry `setPinned`.
Also carries `requiredBytes`, `budgetBytes`, `shortfallBytes`; `shortfallGb` is rounded up
to 0.1. Nothing is pinned on refusal and no `stateDelta` follows. A successful pin is
answered with a `stateDelta` (`pinned: true`, `download.state: "Queued"`) and `cacheStats`.

### `refreshProgress` (host → page, during `refreshCatalogs`)
```json
{ "type": "refreshProgress", "running": true, "sourceId": "archive-org", "stage": "Pixabay: items", "done": 120, "total": 1060, "ok": null, "sources": null, "error": null }
{ "type": "refreshProgress", "running": false, "sourceId": null, "stage": null, "done": null, "total": null, "ok": true,
  "sources": [{ "sourceId": "nasa", "succeeded": true, "assetCount": 9, "added": 1, "removed": 0, "error": null }], "error": null }
```
Running messages come at most ~4 Hz, the latest per source. The final one (`running: false`)
is followed by a fresh `catalog`, unless `error` is set (the whole refresh failed and the old
catalogue stays).

### `notice` (host → page)
```json
{ "type": "notice", "level": "info", "text": "The new cache folder is used the next time …" }
```
`level` is `info|warning|error`. Show it as a toast.

## Build/test approach on a machine without Windows

1. Write `docs/browser-mock/fixture.json` — a `catalog` + initial `stateDelta` + `settings`
   + `cacheStats` message, covering **at least one clip per source** (apple, ia, nasa,
   amazon/aerial-manifests, local) and at least one of each state (pinned+downloading,
   cached, unhealthy, new).
2. Write a tiny `docs/browser-mock/shim.js` implementing just enough of
   `window.chrome.webview` (`postMessage`, `addEventListener('message', ...)`) to replay
   the fixture and log page→host calls to the console, so `index.html` can be opened in
   any browser or served with a static file server for manual/automated (Playwright)
   testing.
3. Build the real UI files under `src/Win4K.ScreenSaver/Browser/ui/` (`index.html`,
   `app.css`, `app.js`, vanilla JS, no build step, no CDN — see plan Phase E) against
   that shim. They must not import the shim in production — it's dev-only, loaded by a
   separate `dev.html` harness, not `index.html`.
4. **Do not write `BrowserBridge.cs`.** 67 owns the whole C# side of Phase E
   (`BrowserBridge.cs`, `BrowserForm.cs`, the WebView2 `.csproj` reference, `Program.cs`'s
   `/c` wiring, deleting `SettingsForm.cs`) on `main`, building it to this contract
   independently. The UI lane's job ends at the JSON messages above and the mock harness.
5. Leave `TODO(local)` comments anywhere you had to guess at a detail you couldn't
   verify from the source — grep for them before merging.

## Host implementation (Phase E C# half, landed on `main`)

- `src/Win4K.ScreenSaver/Browser/BrowserBridge.cs` implements everything above. Inbound is
  `OnPageMessage(string json)` (a JSON string literal holding JSON is unwrapped); outbound is
  an `Action<string>` post callback; no WinForms/WebView2 types. Tested in
  `Win4K.Tests/BrowserBridgeTests.cs`.
- `BrowserForm.cs` hosts it. The page is served from `https://app.win4k/` through
  `WebResourceRequested`, from resources embedded by the `Browser/ui/**` wildcard, so no
  csproj edit is needed when the UI lands. The start URL is `https://app.win4k/index.html`.
  Until `Browser/ui/index.html` exists, a built-in fallback page ("UI not built") is served.
  Navigating to any other origin, and `target=_blank` links, open in the user's browser.
- `https://previews.win4k/` is `SetVirtualHostNameToFolderMapping` onto the previews folder.
  The mapping ignores the `?v=` query (verified: URLs with `?v=` load).
- Page assets must be relative or `https://app.win4k/…`: no CDN, no network.
- `/c` opens this window. It is single-instance: a second `/c` activates the open one.
- Key order inside a received message is not meaningful (Chromium re-sorts posted JSON).
