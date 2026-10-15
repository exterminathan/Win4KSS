# Troubleshooting

## "Windows protected your PC" when running the installer

This is expected, not a bug. `Win4KSS-Setup-*.exe` is not signed with a paid code-signing
certificate (this is a small project shared with a few people, not a commercial release), so
Windows SmartScreen flags it as from an "unrecognized publisher." To run it anyway:

1. Click **More info** on the SmartScreen dialog.
2. Click **Run anyway**.

You only need to do this once per downloaded copy of the installer.

## Win4KSS doesn't show up in Windows' own Screen Saver Settings dropdown

That dropdown only ever lists screen savers installed to `C:\Windows\System32`, which needs
administrator rights. Two ways to fix it:

- Re-run the installer and make sure **"Make Win4KSS available in Windows' Screen Saver
  Settings"** is ticked on the Tasks page (it's ticked by default).
- Or, from the folder Win4KSS was installed to (`%LOCALAPPDATA%\Programs\Win4KSS` by
  default), run, in an elevated PowerShell window:

  ```powershell
  .\install-scr.ps1 -SkipBuild -PublishDir . -SetAsDefault
  ```

Either way, Win4KSS works fully without this step — the walk-away hotkey (`Ctrl+Alt+S`) and
the clip browser behave identically either way. The dropdown is the only thing that needs it.

## Never force-close a running screen saver

If Win4KSS is running full-screen (as the actual screen saver, triggered by idle timeout or
`Ctrl+Alt+S`), **don't end it from Task Manager** or any other "kill the process" tool.
Windows keeps a flag that says a screen saver is currently running, and only clears it when
the screen saver process exits on its own. Force-killing it leaves that flag stuck:

- Windows refuses to start the screen saver again (Win4KSS's own re-installer will even
  refuse to run, with a "the screen saver is running" error, as a safety check).
- Screen Saver Settings may silently fail to save changes.

It clears automatically the next time you sign out and back in (or restart). In the
meantime, dismiss a running screen saver the normal way instead: move the mouse, press a
key, or click.

## The hotkey (Ctrl+Alt+S) doesn't do anything

- Since v1.2.6 the hotkey doesn't depend on any shortcut: a small background helper
  ("Win4KSS" in Task Manager → Startup apps) listens for it. If it was disabled there, or
  ended, opening the clip browser starts it again; re-enable it in Startup apps so it also
  starts when you sign in.
- Something else on your PC might already be using Ctrl+Alt+S (the clip browser warns when
  it notices). Open the clip browser
  (`/c`, or the Start Menu's "Win4KSS" shortcut) → the gear icon → **Start now** tab, and
  record a different combination there (it needs at least one of Ctrl/Alt/Shift plus a key).

## The clip browser says it needs WebView2 / won't open

The clip browser (`/c`) is built on Microsoft Edge WebView2. Windows 11 ships with it
already; Windows 10 almost certainly picked it up via Windows Update some time in the last
few years. If it's genuinely missing, Win4KSS will tell you and offer to open the download
page — or get it directly from
<https://developer.microsoft.com/microsoft-edge/webview2/>. The screen saver itself doesn't
need this at all; only the clip browser does.

## A clip keeps failing to play

Win4KSS retries a failed clip once, and after 3 failed sessions in a row marks it unhealthy
and skips it rather than risk a stuck screen. If a clip you want back keeps getting skipped,
open it in the clip browser's detail pane and use **Retry failed downloads** — it clears the
failure count and gives it another chance.

## The cache keeps filling up / won't download something you pinned

Open the clip browser → gear icon → **Cache** tab to see how much of your budget is used by
pinned ("Keep offline") clips versus the normal rolling cache, and to raise the size limit.
Pinned clips are never evicted automatically, so if the budget is too small for everything
you've pinned, new downloads are refused rather than silently evicting something else you
wanted kept.

## Uninstalling

Use **Settings → Apps → Win4KSS → Uninstall** (or the "Uninstall Win4KSS" shortcut in the
Start Menu). It removes the installed app, the System32 copy if present, and the Desktop/
Start Menu shortcuts. It will ask separately whether to also delete the video cache and
settings folder (`%LOCALAPPDATA%\Win4KScreensavers`) — it defaults to **keeping** it, since
that folder can be tens of gigabytes and reinstalling later would otherwise mean
re-downloading everything. If you kept it and want to remove it later by hand:

```powershell
Remove-Item -Recurse "$env:LOCALAPPDATA\Win4KScreensavers"
```
