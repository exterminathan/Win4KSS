<p align="center">
  <img src="assets/icon/banner.png" alt="Win4KSS — true 4K aerial screensaver for Windows" width="720">
</p>

<p align="center">
  <a href="https://github.com/exterminathan/Win4KSS/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/exterminathan/Win4KSS?label=release&color=145F6E"></a>
  <a href="https://github.com/exterminathan/Win4KSS/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/exterminathan/Win4KSS/total?color=145F6E"></a>
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-0A1222">
  <a href="LICENSE"><img alt="License: GPL-3.0" src="https://img.shields.io/github/license/exterminathan/Win4KSS?color=0A1222"></a>
</p>

A Windows screen saver that plays real 4K video — Apple TV's Aerial footage, plus roughly
1,500 more 4K clips from the Internet Archive, NASA, and a few vendor "ambient TV" feeds —
with smooth clip-to-clip transitions, a size-limited local cache so repeat clips play from
disk instead of re-streaming, and a keyboard shortcut to trigger it the moment you're about
to step away from your desk.

**This is an unofficial, hobby project.** It is not made by, affiliated with, or endorsed by
Apple, NASA, the Internet Archive, or anyone else whose public video it streams. See
[`docs/THIRD-PARTY-NOTICES.md`](docs/THIRD-PARTY-NOTICES.md) for exactly what it bundles and
streams, and under what licenses — some of the Internet Archive clips are
non-commercial-only (CC BY-NC-ND), which this document says plainly rather than glossing
over.

## Features

- **True 4K, not upscaled 1080p** — HEVC/H.265 Main10, hardware-decoded on any GPU from the
  last several years.
- **~1,500 browsable clips** from Apple's Aerials CDN, the Internet Archive, NASA, and a few
  vendor ambient-TV feeds, searchable and filterable by tag in a Wallpaper-Engine-style
  browser.
- **Gapless transitions** between clips, with a bounded, resumable local cache so repeat
  clips play instantly from disk instead of re-streaming.
- **One-press walk-away hotkey** (`Ctrl+Alt+S`) that triggers the real Windows screen saver
  — including your lock-on-resume setting, not just a video playing on top.
- **Built-in auto-updates**, checked from the clip browser and installed with a verified
  download — no reinstalling by hand for every release.

## What you need

- **Windows 10 or 11, 64-bit.** (32-bit Windows isn't supported.)
- **Free disk space for the video cache.** You choose the size (default suggestion: a few
  GB up to however much you're comfortable with; it's a slider from 1–500 GB in the clip
  browser's Cache settings). Nothing is downloaded until the screen saver actually plays it,
  so a fresh install uses almost no disk space.
- **An internet connection**, at least for the first run and whenever you want new clips.
  A small bundled fallback lets the original Apple clip set work even if Apple's servers are
  briefly unreachable, but everything else streams on demand.
- **A GPU capable of hardware HEVC/H.265 decode** — true of basically any graphics card or
  integrated GPU from the last several years. Video will still play without one (the app
  falls back to decoding in software), just using more CPU and battery than it should. If
  playback looks choppy on an older machine, installing the free **"HEVC Video Extensions
  from Device Manufacturer"** app from the Microsoft Store is worth trying, though Win4KSS
  does its own decoding and doesn't strictly depend on it.
- **The Microsoft Edge WebView2 Runtime**, but only for the clip browser (the window where
  you pick what plays) — the screen saver itself doesn't need it. Windows 11 already
  includes it. Windows 10 almost certainly does too by now, since Microsoft has been quietly
  delivering it via Windows Update for a few years. If it's genuinely missing, Win4KSS
  notices and offers to open the download page for you.

## Installing

1. Download `Win4KSS-Setup-<version>.exe` from the [latest release](https://github.com/exterminathan/Win4KSS/releases/latest).
2. Double-click it. Windows will very likely show a blue **"Windows protected your PC"**
   screen first — that's SmartScreen objecting to an installer with no paid code-signing
   certificate, not a sign anything is wrong. Click **More info**, then **Run anyway**. (More
   detail in [`docs/TROUBLESHOOTING.md`](docs/TROUBLESHOOTING.md) if you want it.)
3. Approve the "Do you want to allow this app to make changes" prompt. Installing needs
   administrator rights because it registers the screen saver with Windows.
4. On the Tasks page, leave **"Make Win4KSS available in Windows' Screen Saver Settings"**
   ticked unless you have a reason not to — it's what makes Win4KSS show up as a normal
   choice in Windows' own screen saver picker later. The second option, setting it as your
   active screen saver right now with a 10-minute idle timeout, is unticked by default; tick
   it if you want that set up immediately instead of doing it yourself afterward.
5. Finish. You'll end up with:
   - A **"Start Screen Saver"** shortcut on your Desktop and in the Start Menu, bound to
     **Ctrl+Alt+S**.
   - A **"Win4KSS"** entry in the Start Menu that opens the clip browser.

## Using it

### The walk-away hotkey: `Ctrl+Alt+S`

Press it any time you're stepping away and want the screen saver to kick in immediately,
rather than waiting out your idle timeout. If Windows is set up to require your password (or
show the lock screen) when the screen saver ends — **Settings → Personalization → Lock
screen → Screen saver settings → "On resume, display logon screen"** — this hotkey honours
that: the machine actually locks, not just shows a video. That's the entire point of it.

### The clip browser

Open it from the Start Menu ("Win4KSS") or by running the installed app with `/c`. This is
where you pick what the screen saver actually plays:

- The **toolbar** across the top: search, a sort dropdown, small/medium/large card sizes, a
  "Select all filtered" button, a "Refresh catalogues" button (re-checks the Internet
  Archive, NASA, and the aerial manifest mirrors for new clips), a "Start now" button, and a
  gear icon for Settings.
- The **sidebar** on the left filters the grid by tag (source, category, resolution, and so
  on).
- The **grid** in the middle shows every clip as a card; hover one to see a short preview
  play.
- Clicking a card opens the **detail pane** on the right: a live preview, an **"In
  rotation"** switch (whether the screen saver will actually play this clip), a **"Keep
  offline"** switch (pins it so it's always cached and never evicted, even under disk
  pressure), its download/caching progress, and its captions, description, credit, and tags.
- Selecting several cards brings up a **bulk action bar** at the bottom to add/remove them
  from rotation or pin/unpin them all at once.
- The **gear icon** opens Settings, with five tabs:
  - **Sources** — turn Apple's aerials, your own local folders, and the harvested catalogues
    (Internet Archive, NASA, vendor feeds) on or off, and toggle "auto-add" per source so
    new clips a refresh finds join the rotation automatically.
  - **Playback** — video quality (4K SDR/HDR, 1080p variants).
  - **Cache** — the size budget slider, and a breakdown of how much is pinned ("kept
    offline") versus the normal rolling cache.
  - **Start now** — the Desktop/Start Menu shortcut toggles and the hotkey recorder (click
    the box, then press the combination you want; it needs at least one of Ctrl/Alt/Shift
    plus a key).
  - **About** — version info, and an **Updates** section that checks for, downloads, and
    installs new releases.

### Making it your actual idle-timeout screen saver

If you didn't tick the box during install, you can do it any time:

- **Through Windows itself:** Settings → Personalization → Lock screen → Screen saver
  settings, pick **Win4KSS** from the dropdown, and choose your own idle time and whether to
  require sign-in on resume. (This needs the System32 registration from step 4 above to show
  up in the dropdown at all.)
- **Through the clip browser:** gear icon → **Start now** tab has the same shortcut/hotkey
  controls the installer sets up, if you ever want to change them.

### Uninstalling

**Settings → Apps → Win4KSS → Uninstall.** It asks separately whether to also delete the
video cache, which can be tens of gigabytes — it defaults to keeping it, in case you reinstall
later.

## Something not working?

See [`docs/TROUBLESHOOTING.md`](docs/TROUBLESHOOTING.md) — it covers the SmartScreen warning,
the dropdown-visibility step, why you should never force-close a running screen saver from
Task Manager, hotkey issues, and a few other common snags.

## Licensing and credits

Win4KSS's own source is licensed under the [GNU General Public License v3.0](LICENSE).

Win4KSS streams Apple's own Aerials CDN, the Internet Archive, NASA's video library, and a
few vendor/community feeds — see [`docs/THIRD-PARTY-NOTICES.md`](docs/THIRD-PARTY-NOTICES.md)
for exactly what comes from where, under what license, and what that means for how you can
use it (short version: personal viewing, not commercial use or redistribution of the video
itself). It also bundles a GPL build of libmpv; that document explains what that obligates.

### Code signing policy

See [`docs/SIGNING-POLICY.md`](docs/SIGNING-POLICY.md) for how releases are built and signed.
