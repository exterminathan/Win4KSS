# Third-party notices

Win4KSS is an **unofficial** project. It is not made by, affiliated with, endorsed by, or
supported by Apple Inc., NASA, the Internet Archive, Amazon, or anyone else named below.
"Aerial" and Apple TV are trademarks of Apple Inc. This document exists so that anyone
running the app — not just its author — knows exactly what it bundles and what it streams,
and under what terms.

## The video engine: libmpv (GPL build)

Win4KSS plays video through `libmpv-2.dll`, fetched by `scripts/fetch-libmpv.ps1` from
[shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake)'s prebuilt
Windows releases, which in turn build [mpv](https://github.com/mpv-player/mpv) and a bundled
[FFmpeg](https://github.com/FFmpeg/FFmpeg).

**This is a GPL build, not an LGPL one.** `mpv-winbuild-cmake`'s FFmpeg is built with
`--enable-gpl` and mpv's own build is never passed `-Dgpl=false`, so the resulting
`libmpv-2.dll` is licensed under the **GNU General Public License, version 2 or later**
(confirmed by reading `packages/ffmpeg.cmake` and `packages/mpv.cmake` in that repo — there is
no "nogpl" variant published for this build). This was already flagged as a risk in
[`PLAN.md`](../PLAN.md) (finding 7) before any distribution beyond one machine was planned.

What that means in practice:

- **Source availability.** Anyone who receives a build of Win4KSS is entitled to the
  corresponding source of the GPL component, or a written offer to get it. The exact release
  this was built against is recorded in `native/mpv/VERSION.json` on the machine that built
  it (fetched fresh by `scripts/fetch-libmpv.ps1`); the upstream sources are
  <https://github.com/mpv-player/mpv>, <https://github.com/FFmpeg/FFmpeg>, and the build
  recipe itself at <https://github.com/shinchiro/mpv-winbuild-cmake>. Ask the person who gave
  you this installer for the exact `VERSION.json` it was built from if you want to reproduce
  the binary precisely.
- **Scope.** Win4KSS calls libmpv only through its public C client API (`Playback/MpvInterop.cs`),
  loaded as an ordinary DLL at runtime, not statically linked. This document does not take a
  position on what that does or doesn't require of the rest of the codebase's licensing —
  only that the component itself is GPL, and that matters once you're handing the binary to
  someone else, which informal friend-to-friend sharing still is.
- Win4KSS's own source (`src/Win4K.Core`, `src/Win4K.ScreenSaver`) is not published under any
  particular license at this time; this is a private project shared informally, not a public
  release.

TODO(review): if this is ever shared more widely than a handful of friends, consider having
`fetch-libmpv.ps1` pull a `-Dgpl=false` / LGPL-only build instead (shinchiro's releases don't
currently publish one; it would mean a different build source, or building mpv yourself)
rather than relying on the GPL source-offer being satisfied informally.

## Microsoft Edge WebView2 Runtime

The clip browser (`/c`) is a [WebView2](https://developer.microsoft.com/microsoft-edge/webview2/)
page, via the `Microsoft.Web.WebView2` NuGet package. WebView2 itself is a Microsoft
redistributable component under Microsoft's own license terms, evergreen on the end user's
machine (see `README.md` for whether that needs a separate install). It is not bundled inside
Win4KSS's own binaries; Win4KSS only links against the small loader (`WebView2Loader.dll`,
bundled the same way libmpv is) that talks to whatever runtime is already on the system.

## .NET 10 runtime

Win4KSS is a self-contained-ish single-file .NET 10 publish (see `Win4K.ScreenSaver.csproj`:
`SelfContained=false` but `PublishSingleFile=true` with native self-extraction — the .NET
runtime itself is still expected on the machine via Windows' usual .NET delivery, not
bundled). The .NET runtime and its libraries are MIT-licensed by Microsoft; no action needed.

## The video content itself

None of the video Win4KSS plays was produced by this project. It is streamed from each
source's own public CDN or mirror, exactly as published by that source — Win4KSS never
re-encodes, re-hosts, or redistributes it as a standalone package.

### Apple Aerials (the `apple:` source, and the tvOS 26 feed under `aerial-manifests`)

Apple's own aerial screen-saver footage, streamed directly from `sylvan.apple.com` (and
mirrored manifests of the same content — see `assets/registry/sources.json`, source
`aerial-manifests`, feed `apple-tvos26`). This is Apple's copyrighted content. Apple grants
no license here; Win4KSS simply plays back what Apple's Apple TV devices already stream
publicly, for personal viewing, the same way a browser would. **Not affiliated with or
endorsed by Apple.** Do not redistribute the video files themselves.

### Vendor and community aerial manifests (`aerial-manifests` source)

Mirrored, schema-identical manifests from three more vendors/creators, all pulled via the
[theothernt/AerialVideos](https://github.com/theothernt/AerialVideos) community mirror:

- **Amazon Fire TV ambient content** (112 assets) — Amazon's own content, same caveat as
  Apple's: unaffiliated, unendorsed, personal viewing only.
- **Jetson Creative** (20 assets) and **Robin Fourcade** (18 assets) — community-contributed
  aerial footage via the same mirror. Treat as creator-owned content; personal viewing only.

### Internet Archive (`archive-org` source)

As of the 2026-10-13 harvest, 1,269 clips. Checked directly against the harvested catalogue
(`assets/registry/catalog-archive-org.json`):

- **1,206 of 1,269 clips (the "scenic" RED-shot stock footage group) are licensed
  CC BY-NC-ND 3.0** ([Creative Commons Attribution-NonCommercial-NoDerivs
  3.0](https://creativecommons.org/licenses/by-nc-nd/3.0/)) by creator Mitch Martinez.
  **Non-commercial use only, and no modified/derivative redistribution.** Win4KSS plays
  them back unmodified, which this license permits; it does not permit using them
  commercially or re-cutting/re-encoding them for redistribution.
- **The remaining 63 clips** (the "pixabay-mirror" group, mostly credited to Pixabay, one to
  "LG") are under the **Pixabay Content License** — usable without attribution, but Pixabay's
  own terms still prohibit reselling the unmodified media as stock content or implying
  endorsement by whoever appears in it.

Source: <https://archive.org>. See `assets/registry/sources.json` (source id `archive-org`)
for how these were found.

### NASA Image and Video Library (`nasa` source)

19 clips as of the 2026-10-13 harvest, all **NASA media, which is public domain** (U.S.
government work) per [NASA's media usage
guidelines](https://www.nasa.gov/nasa-brand-center/images-and-media/). Generally free to use
with no restriction, but NASA asks that its use not imply NASA's endorsement of a product —
relevant mainly if you were to use a clip in marketing, which Win4KSS does not.

## Bottom line

This software is for personal, non-commercial use. Given the mix of licenses above —
especially the CC BY-NC-ND and Pixabay-licensed Internet Archive clips, and Apple's and other
vendors' own unlicensed content — none of the video it streams or caches should be resold,
used commercially, or redistributed as a standalone package, separately from the normal use
of this app as a screen saver.
