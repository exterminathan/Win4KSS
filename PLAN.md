# Win4KScreensavers — Implementation Plan

> **Status: all eight phases complete** (2026-08-12), plus the clip browser and streaming
> reliability work as phases 9–15 (2026-10-13; plan in
> [docs/squishy-teacup-plan.md](docs/squishy-teacup-plan.md)). 483 tests green. The one
> remaining manual step is `scripts/install-scr.ps1 -SetAsDefault`, which needs elevation to
> write System32.
>
> This document is the original design; the clip-browser plan supersedes its Settings section
> (`/c` is now a WebView2 browser, not a tabbed dialog) and its Layout. What the build actually
> taught us is in **[Findings](#findings-from-implementation)** at the bottom.

## Context

[`cdima/aerial`](https://github.com/cdima/aerial) brought Apple TV's aerial footage to Windows and was abandoned in Oct 2022. Studying it produced a clear picture of what to keep and what to replace. Two things killed it: it was built on the **Windows Media Player ActiveX control** (deprecated, removable on Win10/11, no HEVC decode), and it pointed at Apple's **1080p H.264** manifest, which is all its player could handle anyway.

Both of Apple's CDNs are still live (verified 2026-08-12):

| | Legacy | Current |
|---|---|---|
| Host | `a1.phobos.apple.com` (Akamai, 23.11.232.13) | `sylvan.apple.com` (Apple AS, 17.253.17.203) |
| Manifest | `/us/r1000/000/Features/atv/AutumnResources/videos/entries.json` (16 KB, live) | inside `https://sylvan.apple.com/Aerials/resources-16.tar` |
| Content | H.264 1080p ~6 Mbps | HEVC Main10, 4K + 1080, SDR + HDR |

The current manifest holds **114 assets**, each with `url-4K-HDR`, `url-4K-SDR`, `url-1080-HDR`, `url-1080-SDR`, `url-1080-H264`, plus `accessibilityLabel`, `shotID`, `categories` (GUIDs), and `pointsOfInterest` (timestamped caption keys). Verified sample:

```
SE_A016_C009_SDR_20190717_SDR_4K_HEVC.mov
hevc / Main 10 / 3840x2160 / 29.97fps / ~12 Mbps / 231.9s / 346,649,491 bytes
```

~346 MB per clip, ~40 GB for the full 4K set. HTTP Range (`206`) is supported, so streaming and resumable downloads both work.

**Trap:** the standalone `https://sylvan.apple.com/Aerials/2x/entries.json` still responds but its video URLs 404. Only the tarball manifest is correct. `resources-17.tar`/`-18` do not exist; `-16` is the newest.

**Outcome:** a Windows screensaver that plays true 4K HEVC — Apple's aerials plus any local folder of 4K video — with gapless transitions, a bounded disk cache, an on-demand walk-away hotkey, and no dependency on a deprecated media stack.

## Decisions

- **Content:** Apple aerials as the built-in source, but behind a pluggable provider interface, with a local-folder source alongside it.
- **Engine:** **libmpv** — `libmpv-2.dll` rendering into a borderless Win32 HWND. Native HEVC Main10 `.mov` support, d3d11va/NVDEC hardware decode, gapless playlists.
- **Delivery:** cache-first, stream on miss, with a size budget and LRU eviction.
- **Packaging:** a real `.scr` honoring `/s /c /p /w /now`, plus cdima's dual-identity trick so the same binary runs as a resizable desktop window.

## Environment (verified)

.NET 10.0.302 SDK + WindowsDesktop 10.0.10 runtime · VS2022 · ffmpeg/ffprobe at `C:\ffmpeg` · RTX 2080 Super (hardware HEVC Main10 decode) · HEVC Video Extensions installed.

## Stack

**C# on .NET 10, `net10.0-windows`, WinForms.** WinForms because libmpv needs a raw `HWND` for its `wid` option — WinForms hands that over trivially, where WPF would need `HwndHost`. It also makes borderless per-monitor windows and the settings dialog straightforward.

Two dependencies worth noting, both already available:
- **`System.Formats.Tar`** — built into .NET 7+, so reading Apple's `resources-16.tar` needs no third-party package.
- **`System.Text.Json`** with source generation for the manifest models.

## Layout

```
Win4KScreensavers/
├── src/
│   ├── Win4K.Core/                  class library — all logic, no UI
│   │   ├── Sources/                 IVideoSource, AppleAerialSource, LocalFolderSource, Clip
│   │   ├── Apple/                   AppleManifestClient, AppleEntries (JSON models), Categories
│   │   ├── Caching/                 VideoCache, CacheIndex, LruEviction, ResumableDownloader
│   │   ├── Playback/                MpvInterop, MpvPlayer, PlaylistEngine
│   │   └── Settings/                AppSettings (JSON-backed)
│   ├── Win4K.ScreenSaver/           WinForms exe → .scr (Program, SaverForm, SettingsForm)
│   └── Win4K.Tests/                 xunit
├── native/mpv/                      libmpv-2.dll — gitignored, fetched by script
├── assets/fallback-entries.json     bundled manifest copy for offline/first-run
├── scripts/                         fetch-libmpv.ps1, build.ps1, install-scr.ps1
└── Win4KScreensavers.sln
```

## Design

### Apple manifest — `Win4K.Core/Apple/AppleManifestClient.cs`

Probe `resources-{N}.tar` downward from N=20 to 13, then bare `resources.tar`, taking the first that returns 200 — so a future tvOS manifest is picked up automatically. Extract `entries.json` via `System.Formats.Tar.TarReader` straight from the response stream. Cache the parsed manifest at `%LOCALAPPDATA%\Win4KScreensavers\manifest.json` with a 7-day TTL; fall back to that, then to the bundled `assets/fallback-entries.json`, so a fresh install works even if Apple 404s.

The same tarball carries `TVIdleScreenStrings.bundle/en.lproj/*.strings`, which maps the `categories` GUIDs to names (Cities, Landscape, Underwater, Space) and the `pointsOfInterest` keys to caption text. Parse the category names in this phase for the picker tree; on-screen captions are a later stretch goal.

### Source abstraction — `Win4K.Core/Sources/`

```csharp
interface IVideoSource {
    string Id { get; }
    Task<IReadOnlyList<Clip>> GetClipsAsync(CancellationToken ct);
}
record Clip(string Id, string Title, string Category, string? TimeOfDay,
            Uri Url, bool IsLocal, long? SizeBytes);
```

`AppleAerialSource` resolves the manifest and picks a URL field per the configured quality; `LocalFolderSource` enumerates a user folder for `.mp4/.mkv/.mov/.webm` and yields `IsLocal` clips that bypass the cache entirely.

**Quality selection** defaults to `url-4K-SDR`, auto-downgrading to 1080 if the primary display is under 2560px wide. HDR is offered as an explicit option — the 4K-HDR clips are PQ, and mpv tone-maps them on SDR displays via `target-colorspace-hint`, but SDR is the safer default.

### Playback — `Win4K.Core/Playback/`

`MpvInterop` is a thin P/Invoke layer over `mpv_create`, `mpv_set_option_string`, `mpv_initialize`, `mpv_command`, `mpv_observe_property`, `mpv_wait_event`, `mpv_terminate_destroy` (client API 2.5). Options set before `mpv_initialize`:

| Option | Value | Why |
|---|---|---|
| `wid` | form `Handle` | render into our borderless window |
| `hwdec` | `auto-safe` | d3d11va/NVDEC on the 2080 Super |
| `vo` | `gpu-next` | modern renderer, best HDR handling |
| `panscan` | `1.0` | cover-fit crop — replaces cdima's manual fill math |
| `audio` | `no` | aerials are silent; avoids grabbing the audio device |
| `keep-open` | `no` | let files end so the playlist advances |
| `input-default-bindings`, `input-vo-keyboard`, `osc`, `osd-level` | off / `0` | mpv must not eat our input or draw UI |
| `cursor-autohide` | `no` | we manage the cursor |

`PlaylistEngine` queues the next clip with `loadfile <path> append-play` on `MPV_EVENT_END_FILE`, giving **genuinely gapless** transitions — the direct fix for cdima's ~1 second black gap, which came from advancing on a 1 Hz `playState` poll.

Ordering mirrors the one genuinely clever bit of cdima's design: shuffle, then `OrderByDescending(time-of-day matches now)`, relying on LINQ's stable sort to keep clips shuffled *within* the matching and non-matching groups. Day is 06:00–19:00. Apple's current manifest has no `timeOfDay` field, so derive it from the category/label or make it opt-in — do not assume the legacy field exists.

### Cache — `Win4K.Core/Caching/`

Root `%LOCALAPPDATA%\Win4KScreensavers\cache\`.

- **Content-addressed keys:** `<original-stem>.<sha256(url)[0..8]>.mov`. cdima keyed on bare `Path.GetFileName(url)`, so different sources with the same filename collided.
- **Staging:** download into `cache\partial\`, `File.Move` into place on completion — a half-file can never be seen as a hit. Purge `partial\` at startup.
- **Resumable:** 346 MB files need `Range` resume on interruption; the CDN supports `206`.
- **Budget + LRU:** default 20 GB ceiling, configurable, evicting least-recently-played. cdima had only a 1 GB free-space floor and would happily consume 40 GB.
- **Concurrency:** one download at a time by default via `SemaphoreSlim`, started ~10s after playback begins so it doesn't starve the currently streaming clip (cdima's good idea, kept).
- **Primary-monitor-only caching**, so N monitors don't fetch N copies.
- `HttpClient` (single static instance) with `CancellationToken` throughout — replacing cdima's `WebClient`-disposed-mid-download and `async void` patterns.

### Screensaver shell — `Win4K.ScreenSaver/`

`Program.Main` handles the standard contract, including the colon-packed `/c:12345` form:

- `/s` — full-screen, one form per monitor
- `/c` — settings dialog
- `/p <HWND>` — `SetParent` into the preview thumbnail; show a **static thumbnail**, not video (Windows creates and destroys previews rapidly; cdima reached the same conclusion)
- `/w` — resizable desktop window
- `/now` — start the configured screensaver immediately and exit (see below)
- no args — `.exe` → window mode, `.scr` → settings (module-filename sniffing, from cdima)

**Multi-monitor** keeps cdima's four modes: `MainOnly` (default), `SameOnEach`, `DifferentVideos`, `SpanAll` (union bounds). Add PerMonitorV2 DPI awareness via `app.manifest` — cdima predates it and mis-sizes on mixed-DPI setups.

**Exit semantics** — a deliberate divergence. cdima exits on any mouse movement >5px with no grace period, a known cause of screensavers dying instantly on a jittery mouse or a stray startup event. Instead: ignore all input for a grace period (below), then exit on cumulative movement >10px, any key, or any click. Keep `n` to skip to the next clip.

Keep `SetThreadExecutionState(ES_CONTINUOUS)` on playback state changes so the monitor can still sleep — mpv otherwise asserts a display-required lock.

### On-demand trigger (walk-away lock)

Waiting out the idle timeout is useless when you're standing up to leave. Ship a way to start the screensaver *now*.

Two mechanisms, because they behave differently:

| | `/now` — post `SC_SCREENSAVE` | `/s` — launch directly |
|---|---|---|
| What runs | whatever screensaver Windows is configured to use | our `.scr`, unconditionally |
| "On resume, display logon screen" | **honored** — the system started it | **not honored** — no lock on exit |
| Use | the walk-away case | testing, or when ours isn't the configured saver |

`/now` is the default the shortcut uses. It posts `WM_SYSCOMMAND` / `SC_SCREENSAVE` (`0xF140`) to `HWND_BROADCAST` and exits immediately — the process does no rendering itself, it just asks the system to engage. This is the only route that gets password-on-resume, which is the entire point of a walk-away key. If the broadcast is swallowed (a fullscreen exclusive app can eat it), fall back to launching our own `.scr /s`.

`scripts/install-scr.ps1` creates the shortcut via `WScript.Shell`:
- **Target** `%SystemRoot%\System32\Win4KAerial.scr`, **Arguments** `/now`
- **Hotkey** `Ctrl+Alt+S` — *(superseded in v1.2.6 by a resident `RegisterHotKey` listener; see finding 13)* `.lnk` files carry a native `HotKey` field that Explorer binds globally, so this needs **no background process, no tray icon, and no `RegisterHotKey` agent**. It only works for shortcuts on the Desktop or in the Start Menu, so the installer places one in each.
- Icon pulled from the `.scr` itself

The stock `C:\Windows\System32\scrnsave.scr` (the blank built-in saver) is the same idea — any `.scr` run with `/s` starts immediately. We point at ours instead, and prefer `/now` for the reasons above.

**The input-grace problem this creates.** A screensaver started by keyboard is racing the key release and any mouse settling — with a naive handler it dies the instant it appears. So the grace period is not a fixed constant: **1000 ms when system-triggered, 1500 ms when launched via `/now` or `/s`**, and the first input event of each kind after launch is swallowed regardless of timing. Test this explicitly — it is the single most likely way the feature feels broken.

### Settings — `Win4K.Core/Settings/AppSettings.cs`

JSON at `%LOCALAPPDATA%\Win4KScreensavers\settings.json`, not the registry. Versioned, diffable, and immune to the `bool.Parse` crashes cdima had on hand-edited registry values. Written atomically (temp + move). WinForms tabbed dialog: **Sources** (Apple / local folder picker) · **Clips** (category tree with checkboxes + live preview) · **Quality** (4K-SDR / 4K-HDR / 1080-*) · **Cache** (size, budget, purge, download-all with an explicit ~40 GB warning) · **Display** (multi-monitor mode, time-of-day) · **About**. The Display tab also carries a "Start now" button and the shortcut/hotkey controls — create or remove the Desktop and Start Menu shortcuts, and rebind the hotkey from `Ctrl+Alt+S` — so the trigger is discoverable without re-running the installer.

### Packaging

`dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true`, post-build copying `.exe` → `.scr`. A screensaver must be a single file in `System32` to appear in the Windows dropdown, so `libmpv-2.dll` cannot simply sit beside it. Modernize cdima's embedded-DLL trick: let single-file publish bundle the native library, and register a `NativeLibrary.SetDllImportResolver` that extracts to `%LOCALAPPDATA%\Win4KScreensavers\runtime\` and loads from there on first run. Fallback if that proves awkward: a folder install under `%LOCALAPPDATA%\Programs\` with a thin `.scr` shim.

`scripts/fetch-libmpv.ps1` pulls the current `mpv-dev-x86_64` build from `shinchiro/mpv-winbuild-cmake` (actively maintained — latest release 2026-08-12, 31 MB) into `native/mpv/`, which stays gitignored.

## Build order

Each phase ends in something runnable.

| # | Phase | Ends with | Done |
|---|---|---|---|
| 0 | Solution scaffold, `fetch-libmpv.ps1`, gitignore, CLAUDE.md update | `dotnet build` succeeds | ☑ |
| 1 | `AppleManifestClient` + models + tests | console lists all 114 clips with sizes | ☑ |
| 2 | **`MpvInterop` + `/w` window mode** | a streamed 4K clip plays in a resizable window | ☑ |
| 3 | `PlaylistEngine` — gapless queue, shuffle, time-of-day | clips advance with no black gap | ☑ |
| 4 | `VideoCache` — staged, resumable, LRU | second run plays from disk; budget holds | ☑ |
| 5 | Screensaver shell — `/s /c /p /now`, multi-monitor, exit + launch grace, DPI | installs and runs as a real screensaver | ☑ |
| 6 | Settings UI, incl. shortcut/hotkey controls | all options editable and persisted | ☑ |
| 7 | `LocalFolderSource` | a local 4K folder plays | ☑ |
| 8 | Packaging + `install-scr.ps1` incl. hotkey shortcuts | single `.scr`, installable, `Ctrl+Alt+S` works | ☑ |

Added beyond the original plan: the harvested **clip registry** (`assets/registry`) is wired in
as extra sources via `RegistryCatalogSource` — 1,358 further 4K clips from Archive.org,
NASA and mirrored aerial manifests (1,577 since the 2026-10-13 re-harvest), all in Apple's
schema so they reuse the same parser.

Phases 9–15 are the **clip browser + streaming reliability** plan,
[docs/squishy-teacup-plan.md](docs/squishy-teacup-plan.md) (its phases A–G):

| # | Phase | Ends with | Done |
|---|---|---|---|
| 9 (A) | Streaming reliability — URL escaping, no idle stalls, watchdog, retry/backoff, failure policy, download-ahead, `IMpvPlayer` seam | uncached archive clips stream; one failure no longer blacks out the saver | ☑ |
| 10 (B) | Unified clip model (`ClipInfo`, `ClipKey`, `TagBuilder`, `ClipMerger`) + catalogue harvesting in C#; `harvest.py` retired | `ClipLibrary` merges every source; in-app refresh at parity | ☑ |
| 11 (C) | Cache pins, `DownloadQueue` (`queue.json`), cross-process mutexes, partials kept for resume | browser and saver share one cache safely; pins survive restarts | ☑ |
| 12 (D) | `MediaPreviewService` — 480 px stills, 4 s hover clips, 1 GB LRU | previews for every source | ☑ |
| 13 (E) | WebView2 clip browser on `/c` (`BrowserForm`, `BrowserBridge`, `Browser/ui/`); `SettingsForm` deleted | filter, sort, hover-play, live preview, pin, refresh | ☑ |
| 14 (F) | Settings v2 (`RotationKeys`/`KeptOffline`/`AutoAddSources`/`KnownKeys`), v1→v2 migration, per-clip rotation in the saver | the saver plays exactly what the browser selects | ☑ |
| 15 (G) | Docs | CLAUDE.md and this file current | ☑ |

**Phase 2 is the risk gate** — libmpv embedding is the one assumption that would force a redesign, so it lands before any cache or UI work.

## Verification

- **Unit (xunit):** manifest parse against a committed sample `entries.json` · cache key derivation and collision resistance · LRU eviction ordering · shuffle + time-of-day stable-sort ordering · CLI arg parsing including `/c:12345` and `/p 12345`.
- **Integration:** live manifest fetch (skippable when offline) asserting ≥100 assets and reachable URLs · a resumable download interrupted mid-transfer and resumed, verifying the final byte count matches `Content-Range` total.
- **Manual, in order:**
  1. `dotnet run --project src/Win4K.ScreenSaver -- /w` — 4K plays, hardware decode confirmed via mpv's `hwdec-current` property
  2. Watch two clips transition — no black gap
  3. `-- /c` — settings open, save, and round-trip
  4. `scripts/install-scr.ps1`, then Windows Screen Saver Settings — appears in the dropdown, preview thumbnail renders, Preview button goes full-screen
  5. Multi-monitor: each of the four modes
  6. Idle-trigger the real screensaver; confirm it does *not* exit instantly, and that a deliberate mouse move does exit it
  7. Press `Ctrl+Alt+S` — the screensaver appears immediately and **survives the key release**. Repeat from a mouse-driven Start Menu click. Then enable "On resume, display logon screen" and confirm `/now` locks on exit while `/s` does not
  8. Check cache dir size stays under budget after a long run

## Risks

1. **libmpv `wid` embedding** — well-trodden, but gated at Phase 2 so failure is cheap.
2. **Single-file `.scr` + native extraction** — medium; folder-install fallback documented above.
3. **Antivirus flagging `.scr`** — a documented, long-standing false-positive problem for screensavers (cdima's FAQ covers it). Local-only use sidesteps it.
4. **Apple changes or removes sylvan** — mitigated by the bundled fallback manifest, the cached manifest TTL, the disk cache, and the local-folder source.
5. **HDR tone-mapping** — defaulting to 4K-SDR avoids it entirely; HDR is opt-in.
6. **`.lnk` hotkeys are Explorer-dependent** — they bind only for shortcuts under the Desktop or Start Menu, can be slow to fire on a cold Explorer, and lose to any app already owning the combo. If `Ctrl+Alt+S` proves unreliable, the fallback is a real `RegisterHotKey` tray agent, which is why the settings tab exposes rebinding rather than hardcoding the combo.
7. **Licensing** — shinchiro's libmpv builds are GPL; mpv is LGPLv2+ only when built with `-Dgpl=false`. Irrelevant for local personal use, but if this is ever distributed, either ship an LGPL build or have the installer fetch the DLL separately.

## Findings from implementation

Findings 1–7 come from the original build (phases 0–8); 8 onwards from the clip-browser
phases 9–15.

### 1. `sylvan.apple.com` fails TLS on Windows — twice, in two different stacks

Apple's CDN presents a genuine certificate chaining `sylvan.apple.com → Apple Server
Authentication CA → Apple Root CA`, and **Apple Root CA is not in the Windows root store**.
It is trusted on Apple's own platforms, which is why the macOS Aerial project never hit this.
Every handshake fails with *unable to get local issuer certificate*.

It then bites a second time: mpv does its own networking through **libcurl**, with its own
trust store, so fixing .NET's HttpClient left playback still failing with
*TLS certificate verification failed*. Two separate fixes were needed:

- `AppleTrust` pins Apple's real root as a **process-local** trust anchor for HttpClient.
- `CaBundle` writes a PEM of *every Windows-trusted root plus Apple's* and hands it to mpv via
  `tls-ca-file`.

Neither downgrades to `http://` nor sets `tls-verify=no`, and neither touches the machine's
certificate store. Name mismatches stay fatal.

### 2. A shortcut must never target the `.scr`

The plan specified the shortcut's target as `%SystemRoot%\System32\Win4KAerial.scr` with
arguments `/now`. That silently does the wrong thing. Windows registers scrfile's `open` verb
as `"%1" /S`, so the shell **discards a shortcut's arguments** and substitutes `/S`. Measured
with a real `.lnk` against this build:

| Shortcut target | Verb actually dispatched |
|---|---|
| `.scr` + `/now` | `FullScreen` — runs the saver directly, **no resume lock** |
| `.exe` + `/now` | `StartNow` — correct |

The failure is invisible: the screen saver appears either way, it just stops locking the
machine, which is the entire point of the walk-away key. Fixed by installing a companion
`.exe` (a hard link to the `.scr` when both are on one volume, so it costs no disk) and
targeting that. `ShortcutTargetTests` guards it.

### 3. libmpv is 114 MB, not ~45 MB

The published `.scr` is **115 MB**. Single-file publish handles it and the folder-install
fallback was never needed, but the size estimate in this plan was wrong by 2.5×.

### 4. Time of day is nearly meaningless on the 4K catalogue

Only **4 of Apple's 114** clips carry a day/night marker; the legacy 1080p manifest marked
every one. The ordering is implemented and honest about itself — matching clips first,
unmarked in the middle, wrong-time last — but it moves almost nothing, and the settings UI
says so rather than implying a filter that does not exist.

### 5. Writing the screen-saver registry keys does not enable it

`HKCU\Control Panel\Desktop\ScreenSaveActive = 1` is not enough. The running session caches
the setting, so until `SystemParametersInfo(SPI_SETSCREENSAVEACTIVE, …, SPIF_SENDCHANGE)` is
called, `SPI_GETSCREENSAVEACTIVE` keeps reporting **false** and Windows ignores
`SC_SCREENSAVE` outright — the hotkey silently does nothing. `install-scr.ps1` now calls SPI
and reports loudly when the session refuses the change.

### 6. Force-killing a screen saver wedges Windows

Killing the saver process with `Stop-Process` instead of letting it exit leaves the system
believing a screen saver is still running: `SPI_GETSCREENSAVERRUNNING` stays **true**, Windows
keeps a handle open on the `.scr` (so reinstalling fails with a sharing violation), refuses to
start another saver, and rejects `SPI_SETSCREENSAVEACTIVE` with error 329, *"an operation is
currently in progress with the device"*. It clears on sign-out. Worth knowing before
debugging a saver that "will not start".

### 7. mpv shells out to yt-dlp on any load failure

Every failed clip spawned a `yt-dlp` subprocess and waited about a second for it to fail.
For direct video URLs the hook can never succeed, and a screen saver spawning subprocesses is
its own problem. Disabled with `ytdl=no`.

### 8. The archive-org "23% failure rate" was three bugs of ours, and the plan found two

The clip-browser plan diagnosed two causes before Phase A: `Uri.ToString()` unescaping `%20`
into a space, which libcurl rejects (163/163 uncached archive streams failed with mpv `-13`),
and mpv going idle after an instant failure, which `loadfile … append` cannot restart from
(165 of 169 failures left a black screen until dismissal, median 1.6 h). The "23%" understated
both, because a stalled saver stopped loading clips.

A third cause only showed once those were fixed: **an expired ISRG Root X2 cross-sign** from
the Windows intermediate store sat in `ca-bundle.pem`. libcurl's OpenSSL checks the bundle
first, picked the expired certificate over the valid chain the server sent, and failed TLS on
every archive.org download host, since they redirect to Let's Encrypt-signed hosts. `CaBundle`
now excludes any certificate outside its validity period, and `CaBundleTests` guards it.

### 9. `harvest.py` silently lost every NASA clip with a space in its id

The plan's parity check expected the C# harvesters to match the Python counts (1060 / 289 / 9).
NASA came back with **19**: `urllib` refuses a URL containing a space, and harvest.py swallowed
the error, so it skipped 10 clips without a word. The C# set is a strict superset.

### 10. archive.org grew underneath the parity check

archive-org went from **1060** files (2026-08-12) to **1316** upstream, so the counts could not
match. Parity was checked instead as same-day identical URL sets for archive-org (1316) and
aerial-manifests (289). The mirror dedupe the plan described as `*_201507` turned out to be
`_YYYYMM` re-uploads in general: 47 dropped, leaving 1269.

### 11. 20 MB head-segment previews are not the cheap route

The plan had previews Range-fetch the first ~20 MB through `HttpClient` and fall back to
ffmpeg-over-HTTPS only for tail-indexed files. Every Apple and Amazon `.mov` keeps `moov` at
the tail, so the head is useless for them; `Mp4Boxes.IndexIsBeyond` spots this after 64 KB.
On archive.org (~1.3 MB/s) the head route took **~22 s and 27 MB** per hover clip, against
**~7.6 s** for ffmpeg's own input seek. Head segments remain the default behind
`MediaPreviewOptions.UseHeadSegments`.

Three more preview assumptions were wrong. ffmpeg here is a GnuTLS build whose `tls_verify`
**defaults to 0**, so `-ca_file` alone checked nothing; previews pass `-tls_verify 1` too.
archive.org `.thumbs` are 160 px, not usable 480 px stills, so stills are upgraded from a
hover clip's frame. Hover clips need `-pix_fmt yuv420p`, because 10-bit sources otherwise
produce High 10 H.264, which Chromium cannot play.

### 12. WinForms undoes `SetParent` after `OnHandleCreated` — the `/p` leak

The preview thumbnail attached to Windows' HWND in `OnHandleCreated`. WinForms resets a
top-level form's parent after that call, silently undoing `SetParent`. The preview became an
orphaned child of the desktop that nothing destroyed, and `/p` processes leaked for hours.
Attaching in `OnLoad` fixes it, and `PreviewLifetime` polls the parent HWND every second as a
backstop.

### 13. Cross-process caching needed a download owner, not just an index lock

The plan specified one named mutex around the cache index. Two processes downloading pins
would still race for the same files, so a second mutex, `DownloadQueueOwner`, picks one
process to own the queue. The first to start owns it, and the saver takes over when the
browser exits. Both mutex names carry a hash of the cache root, so two cache folders never
contend.

### 14. `KnownKeys` cannot drive the New tag

Settings v2 planned `KnownKeys` for detecting new clips. The saver merges new keys into it on
every run, so a clip would lose its New tag the first time the saver ran. `KnownKeys` only
drives auto-add; New comes from the catalogue's first-seen `addedUtc`.

### 15. Smaller surprises

- **Test isolation:** tests that set `WIN4K_HOME` were flipping a process-wide variable under
  every other test running in parallel, which made `CaBundleTests` fail intermittently.
  Replaced by an `AsyncLocal` scope, `AppPaths.UseRoot`.
- **WebView2's NuGet package drags in its WPF assembly**, which references WindowsBase 5.0 and
  trips MSB3277 in a WinForms-only project. The csproj removes that reference.
- **The page and host were built in parallel lanes against one contract**, and the first
  integration run still missed the handshake: the host waits for `ready`, and the page never
  posted it. The page now posts `ready` once every handler is registered. Chromium also
  re-sorts the keys of posted JSON, so key order means nothing on either side.

### 13. The `.lnk` hotkey was slow and tied to the shortcuts — risk 6 came true

Risk 6 predicted it. The hotkey only existed while a Desktop or Start Menu shortcut carried it,
so turning the Desktop shortcut off could turn the hotkey off. It was also slow: in
`win4k.log`, Explorer took 190–370 ms to spawn a cold `/now`, which then asked Windows to start
the saver, so the screen switched 460–600 ms after the key press (more on a cold machine)
against ~90 ms from the browser's already-running process. v1.2.6 takes the fallback the risk
named, minus the tray icon: `Win4KSS.exe /hotkey` (`HotkeyAgent`) holds the hotkey with
`RegisterHotKey`, starts from `HKCU\…\Run`, and calls `SaverLauncher.StartNow` warm (key →
screen switch ~170 ms, measured). The hotkey moved into `settings.json`; the listener strips
the old `.lnk` field, which Explorer otherwise keeps holding (`RegisterHotKey` → 1409).
