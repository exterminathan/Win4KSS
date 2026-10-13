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
preview images and `https://app.win4k/...` for the page's own static assets (both via
`SetVirtualHostNameToFolderMapping`, so paths are plain `/path/to/file.jpg`).

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
      "tags": [{ "facet": "Source", "value": "Apple" }, { "facet": "Category", "value": "Landscapes" }, { "facet": "Resolution", "value": "4K" }, { "facet": "DynamicRange", "value": "HDR available" }]
    }
  ]
}
```
`TagFacet` values (see `TagBuilder.cs` enum order): `Source, Category, Subcategory,
Subject, TimeOfDay, Resolution, Aspect, DynamicRange, Duration, Codec, Licence, State`.
`State` tags (`TagBuilder.Values`) are **not** included here — they come live via
`stateDelta`, because they change without a catalogue refresh.

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
      "download": { "state": "Downloading", "fraction": 0.42, "bytesReceived": 35000000, "totalBytes": 83072197 },
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
    "autoAddSources": ["apple"], "availableSources": ["apple", "archive-org", "nasa", "aerial-manifests:apple-tvos26", "aerial-manifests:amazon-fireos8"],
    "previewCapabilities": { "ffmpegAvailable": true, "hoverClips": true }
  }
}
```
- **No `enabledCatalogIds`** — that field is retired in v2 (`SourceMode`/`EnabledCatalogIds`/`SelectedClipIds` are all null now). Every source is always resolved; what plays is purely per-clip rotation (`stateDelta.inRotation`) plus per-source `autoAddSources`.
- `availableSources` is the full list of source ids to render the per-source auto-add toggle row against (derived from `Rotation.SourceIds`, not a settings field itself — the host computes it from the loaded catalogue).
- `autoAddSources` is `AppSettings.AutoAddSources`, landed in Phase F — the host sends it as-is.

### `cacheStats`
```json
{ "type": "cacheStats", "pinnedBytes": 12000000000, "rollingBytes": 7800000000, "budgetBytes": 21474836480, "freeDiskBytes": 90000000000 }
```

## Page → host messages

| Message | Payload | Maps to |
|---|---|---|
| `setRotation` | `{ keys: string[], value: bool }` | `Rotation.SetInRotation(settings, keys, value)` |
| `setAutoAdd` | `{ sourceId: string, value: bool, includeExisting?: bool }` | `Rotation.SetAutoAdd(settings, sourceId, value)`; `value: true` with `includeExisting: true` also calls `Rotation.SetInRotation(settings, Rotation.KeysOf(catalogue, sourceId), true)`. The UI's per-source toggle should prompt "also add the existing N clips?" (N = count of that source's clips not yet in rotation) and send `includeExisting` from the answer — don't silently default it either way. |
| `setPinned` | `{ keys: string[], value: bool }` | `PinManager.Pin`/`Unpin`; on `Pin`, host first runs `CanPin` and may reply with a one-off `pinRefused` message (see below) instead of a `stateDelta` |
| `selectClip` | `{ key: string \| null }` | drives the native mpv preview panel the host positions over the detail pane's video rect (see `panelRect` below) |
| `panelRect` | `{ x, y, width, height, visible: bool }` | page tells host where to position the native preview `HWND`, in page-relative CSS pixels; sent on resize/scroll/select |
| `requestPreviews` | `{ keys: string[], kinds: "Still"\|"Hover"\|"Both" }` | `MediaPreviewService.Request(keys, PreviewKind)` — `kinds` string maps 1:1 to the flag enum member name |
| `saveSettings` | `{ patch: { ...partial of settings.values } }` | debounced write to `AppSettings` |
| `refreshCatalogs` | `{}` | `CatalogRefresher` run; host streams progress via a `refreshProgress` message (not detailed yet — show an indeterminate spinner until the next `catalog` arrives) |
| `startNow` | `{}` | same as today's "Start now" |

### `pinRefused` (host → page, reply to a refused `setPinned`)
```json
{ "type": "pinRefused", "keys": [...], "requiredGb": 24, "budgetGb": 20, "shortfallGb": 4 }
```
Shape mirrors `PinCheck` (`RequiredGb`, budget, `ShortfallBytes`). Page should offer
"Raise limit to {requiredGb} GB?" → on yes, `saveSettings({ patch: { cacheBudgetGb: requiredGb } })` then retry `setPinned`.

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
