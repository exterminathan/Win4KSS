; Win4KSS installer (Inno Setup 6).
;
; Packages the single-file publish\ output (built by `dotnet publish`, which already bundles
; libmpv-2.dll and WebView2Loader.dll inside Win4KSS.exe/.scr via IncludeNativeLibrariesForSelfExtract
; -- see scripts/build-installer.ps1, which publishes before compiling this script and verifies
; those two files exist; nothing extra needs to be added here for them).
;
; Deliberately does NOT reimplement the registry/shortcut/SPI_SETSCREENSAVEACTIVE work that
; scripts/install-scr.ps1 already does and already got right (see CLAUDE.md -- a shortcut
; pointed at the .scr silently loses the resume lock, and writing the registry alone does not
; enable the screen saver). That script is bundled into {app} and invoked as a [Run] step
; instead, with -SkipBuild -PublishDir "{app}" so it installs from what Setup just extracted
; rather than trying to run `dotnet publish` on a machine that has no .NET SDK.
;
; Build with scripts/build-installer.ps1, or manually:
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\Win4KSS.iss

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppName "Win4KSS"
#define MyAppExeName "Win4KSS.exe"
#define MyAppScrName "Win4KSS.scr"
; TODO(review): "Publisher" is shown in Windows' Apps & features list, which makes it a decent
; place to put the disclaimer somewhere a recipient will actually see it. Pick whatever reads
; better to you -- this is not a registered company name.
#define MyAppPublisher "Win4KSS (unofficial, not affiliated with Apple)"

[Setup]
; Generated once for this app; do not reuse it for a different app and do not regenerate it
; on a whim -- Windows uses it to recognise upgrades of the same product.
AppId={{FDEAF37E-5817-43BA-A346-B9BB300B9B46}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
; No AppURL/AppSupportURL/AppUpdatesURL on purpose -- the GitHub repo this ships from is
; private, so a link a friend can't open is worse than no link. TODO(review): revisit if this
; ever gets a public landing page.

DefaultDirName={localappdata}\Programs\{#MyAppName}
; Locked, not just defaulted: Win4K.ScreenSaver\ShortcutManager.cs hardcodes
; %LOCALAPPDATA%\Programs\Win4KSS\Win4KSS.exe as the one path shortcuts (and the clip
; browser's own "create shortcut" button) ever target. Letting Setup's directory page move
; the real install elsewhere would leave the running copy pointing at a second, empty
; "install" that nothing uses.
DisableDirPage=yes
DisableProgramGroupPage=yes

; Writing the System32 copy (for the Screen Saver Settings dropdown) and HKLM-adjacent registry
; lookups needs admin; HKCU and %LOCALAPPDATA% writes happen under the same elevated-but-same-
; user session, which Inno resolves back to the invoking user's profile as usual.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Inno otherwise warns that mixing an admin install with per-user areas (localappdata,
; userprograms) is suspicious. It is deliberate here, the same way install-scr.ps1's own
; self-elevation is: this is a single-user machine being elevated via UAC as the SAME user,
; not an admin installing for other users, so %LOCALAPPDATA%\Programs\Win4KSS and HKCU are
; exactly where the one person running Setup wants things -- Windows resolves those
; constants back to the invoking user's profile even while elevated, which is what makes
; this work at all.
UsedUserAreasWarning=no

OutputDir=output
OutputBaseFilename=Win4KSS-Setup-{#MyAppVersion}
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
InfoBeforeFile=InfoBefore.txt
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Checked by default: this is the one thing most people installing a screen saver actually
; want -- to see it in the normal Windows screen saver picker. Unchecking falls back to
; install-scr.ps1 -UserScope, which is still a fully working install in every other respect
; (see that script's own doc comment).
Name: "systemscr"; Description: "Make {#MyAppName} available in Windows' Screen Saver Settings (recommended)"; GroupDescription: "Screen saver registration:"
; TODO(review): left unchecked on purpose -- changing someone's active screen saver and idle
; timeout as a side effect of installing software felt presumptuous. Flip the default, or the
; wording, if that is wrong for how this actually gets shared.
Name: "setdefault"; Description: "Set {#MyAppName} as my active screen saver now (10 minute idle timeout)"; GroupDescription: "Screen saver registration:"; Flags: unchecked
; Only offered on a genuinely fresh machine (Check: NotExistingInstall) -- when upgrading an
; existing install, PrepareToInstall below already backs up the current settings for safety,
; and overwriting a working setup with an imported file mid-upgrade is not what "upgrade"
; should mean. Checking this box prompts for a *.win4kss.json file via GetOpenFileName when
; leaving this wizard page (NextButtonClick below); the actual import runs once files are on
; disk, via the install-scr.ps1 [Run] entry's own BeforeInstall callback below, which Inno
; guarantees fires after [Files]/[Icons] but before that entry (and so before install-scr.ps1
; touches shortcuts).
; TODO(review): wording, and whether this belongs on the Tasks page at all vs. a dedicated
; wizard page -- this is the "simpler" of the two options the spec offered.
Name: "importsettings"; Description: "Import settings from a file..."; GroupDescription: "Settings:"; Flags: unchecked; Check: NotExistingInstall

[Files]
; ignoreversion: these are single-file self-contained builds whose internal version doesn't
; move independently of this installer's, and re-running Setup over an existing install should
; always win.
Source: "..\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\{#MyAppScrName}"; DestDir: "{app}"; Flags: ignoreversion
; Bundled so the [Run]/[UninstallRun] steps below can call it, and so it is still sitting in
; {app} afterwards if the registration ever needs to be re-run by hand (e.g. -SetAsDefault
; later, or after a Windows feature update resets screen saver state).
Source: "..\scripts\install-scr.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
; Per-user Start Menu entries, to match everything else this app touches (settings, cache,
; the companion .exe) being per-user even though Setup itself runs elevated. Opens the clip
; browser (/c) -- the bare .exe with no arguments opens a windowed preview instead, which is
; not what someone clicking a Start Menu icon would expect.
Name: "{userprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "/c"; WorkingDir: "{app}"; Comment: "Open the Win4KSS clip browser"
Name: "{userprograms}\{#MyAppName}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

; The Desktop/Start Menu "Start Screen Saver.lnk" with its Ctrl+Alt+S hotkey is created by the
; install-scr.ps1 [Run] step below, not here -- Inno's [Icons] section has no way to set a
; shortcut's native Hotkey field, and that field is the entire point of that shortcut.

[Run]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-scr.ps1"" -SkipBuild -PublishDir ""{app}"" {code:GetInstallScrArgs}"; \
    StatusMsg: "Registering {#MyAppName} as a screen saver..."; \
    Flags: runhidden waituntilterminated; BeforeInstall: ImportSettingsIfChosen
Filename: "{app}\{#MyAppExeName}"; Parameters: "/c"; Description: "Open the {#MyAppName} clip browser now"; Flags: postinstall skipifsilent nowait unchecked

[UninstallRun]
; Runs as the FIRST step of uninstall, before Setup removes any files registered in [Files] --
; so install-scr.ps1 is still at {app}\install-scr.ps1 when this executes. It removes the
; System32 copy (if any), the Desktop/Start Menu "Start Screen Saver.lnk" shortcuts, the whole
; {app} companion dir, and repoints HKCU's SCRNSAVE.EXE away from the file we are about to
; delete. It deliberately leaves %LOCALAPPDATA%\Win4KScreensavers (settings + the video cache)
; alone -- CurUninstallStepChanged below asks about that separately, since it can be tens of GB
; and silently deleting someone's cache on every reinstall would be hostile.
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-scr.ps1"" -Uninstall"; \
    Flags: runhidden waituntilterminated; RunOnceId: "RemoveWin4KSS"

[Code]
const
  // The WebView2 Runtime's own client GUID, as Microsoft's bootstrapper registers it. Same
  // detection approach Microsoft documents for apps that need to check before relying on it.
  WebView2ClientGuid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  // Same evergreen-bootstrapper link Program.cs itself offers when the clip browser can't find
  // the runtime at launch (Win4K.ScreenSaver\Program.cs, WebView2RuntimeUrl) -- kept in sync by
  // hand since Inno's preprocessor can't read a C# source file.
  WebView2DownloadUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

var
  // Set once in InitializeSetup and read everywhere else -- {app} is fixed (DisableDirPage=yes),
  // so this is decided before the wizard shows any pages and never changes during the run.
  ExistingInstallDetected: Boolean;
  // '' until the user picks a file for the "importsettings" task via GetOpenFileName
  // (NextButtonClick). ImportSettingsIfChosen -- a BeforeInstall callback on the install-scr.ps1
  // [Run] entry below -- imports it if non-empty.
  ImportSettingsFile: String;

function IsWebView2RuntimeInstalled(): Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2ClientGuid, 'pv', Version) or
    RegQueryStringValue(HKLM32, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2ClientGuid, 'pv', Version) or
    RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2ClientGuid, 'pv', Version);
end;

// True when there is something here for the settings CLI to export, or for an import to
// matter to: either a previous install's exe at the (locked) {app} path -- whether put there
// by this installer or by running scripts/install-scr.ps1 by hand, which targets the very same
// %LOCALAPPDATA%\Programs\Win4KSS path as its companion dir, see CLAUDE.md -- or a settings
// profile under %LOCALAPPDATA%\Win4KScreensavers (AppPaths.SettingsFile), which several past
// installs/uninstalls can leave behind even with no exe currently sitting at {app}.
function NotExistingInstall(): Boolean;
begin
  Result := not ExistingInstallDetected;
end;

function DetectExistingInstall(): Boolean;
begin
  Result :=
    FileExists(ExpandConstant('{app}\{#MyAppExeName}')) or
    FileExists(ExpandConstant('{localappdata}\Win4KScreensavers\settings.json'));
end;

// Runs ExePath with Args via cmd.exe, capturing stderr to a temp file and returning it as
// StdErrText. Exec() itself has no way to capture a child process's output, so this is the
// standard Inno idiom: the outer "" .. "" around the whole /C argument lets cmd.exe strip one
// layer of quoting so the quoted, space-containing exe path inside still parses correctly.
// ExePath is a GUI-subsystem exe (SettingsCli.cs) -- that only matters for how *PowerShell*
// would wait for it (Start-Process -Wait); cmd.exe's own synchronous child-process wait, which
// Exec's ewWaitUntilTerminated relies on here, is unaffected by subsystem.
function RunSettingsCli(const ExePath, Args: String; out ExitCode: Integer; out StdErrText: AnsiString): Boolean;
var
  StdErrFile, CmdLine: String;
  ResultCode: Integer;
begin
  StdErrFile := ExpandConstant('{tmp}') + '\win4kss-cli-' + IntToStr(Random(1000000)) + '.err';
  CmdLine := '/C ""' + ExePath + '" ' + Args + ' 2>"' + StdErrFile + '""';

  Result := Exec(ExpandConstant('{cmd}'), CmdLine, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  ExitCode := ResultCode;

  StdErrText := '';
  if FileExists(StdErrFile) then
  begin
    LoadStringFromFile(StdErrFile, StdErrText);
    DeleteFile(StdErrFile);
  end;
  StdErrText := Trim(StdErrText);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;

  ExistingInstallDetected := DetectExistingInstall();
  ImportSettingsFile := '';

  // Not a blocker: the screen saver itself needs no WebView2 at all, only the clip browser
  // (/c), and Win4KSS already detects this itself and offers the same download link at that
  // point. This just saves a confused "why won't /c open" moment later.
  if not IsWebView2RuntimeInstalled() then
    MsgBox(
      'One part of {#MyAppName} -- the clip browser used to choose what plays -- needs the ' +
      'free Microsoft Edge WebView2 Runtime, which this PC does not appear to have yet.' +
      Chr(13) + Chr(10) + Chr(13) + Chr(10) +
      'The screen saver itself will work fine either way. Windows 11 already includes the ' +
      'runtime, and Windows 10 almost certainly picked it up via Windows Update some time ' +
      'in the last few years -- but if the clip browser later refuses to open, get it from:' +
      Chr(13) + Chr(10) + WebView2DownloadUrl,
      mbInformation, MB_OK);
end;

// Builds the extra arguments for the install-scr.ps1 [Run] step above from the Tasks page.
function GetInstallScrArgs(Param: String): String;
var
  Args: String;
begin
  Args := '';
  if not WizardIsTaskSelected('systemscr') then
    Args := Args + ' -UserScope';
  if WizardIsTaskSelected('setdefault') then
    Args := Args + ' -SetAsDefault -TimeoutMinutes 10';
  Result := Trim(Args);
end;

// Leaving the Tasks page with "importsettings" checked prompts for the file right away, so a
// bad pick (or Cancel) is caught before Setup commits to anything -- Result := False keeps the
// wizard on this page instead of advancing with the task checked but nothing chosen.
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID <> wpSelectTasks then
    Exit;

  if WizardIsTaskSelected('importsettings') then
  begin
    if ImportSettingsFile = '' then
    begin
      if not GetOpenFileName('Select a Win4KSS settings file to import', ImportSettingsFile, '',
           'Win4KSS settings files (*.win4kss.json)|*.win4kss.json|All files (*.*)|*.*', '') then
        Result := False; // user cancelled the dialog -- stay on the Tasks page
    end;
  end
  else
    ImportSettingsFile := ''; // unchecked again after picking a file -- forget the choice
end;

// Shared by the pre-upgrade export and the fresh-install import: runs the CLI call, and turns
// its exit code into exactly the handling the spec asks for -- 0 silent, 1 a message box with
// the captured stderr but the install carries on, 2 (or anything else unexpected) a louder but
// still non-fatal warning. Never raises/aborts: settings export/import is a nice-to-have next
// to a working install of the screen saver itself, never a reason to fail the whole thing.
//
// ShowFailureBox: False turns the two MsgBox calls below into Log()-only, for a caller whose
// failure genuinely does not deserve a pop-up -- the post-upgrade bonus export below is the
// one such case: it is a nicer-to-read extra on top of PrepareToInstall's real settings.json
// backup, and an upgrading user should not see a dialog about something optional failing.
// The fresh-install import (ImportSettingsIfChosen) keeps ShowFailureBox True: the user just
// explicitly asked to import a specific file, so if that fails, they should be told.
procedure RunSettingsCliAndWarn(const ExePath, Args, FailureContext: String; const ShowFailureBox: Boolean);
var
  ExitCode: Integer;
  StdErr: AnsiString;
begin
  if not RunSettingsCli(ExePath, Args, ExitCode, StdErr) then
  begin
    Log(FailureContext + ': could not launch ' + ExePath + '.');
    Exit;
  end;

  case ExitCode of
    0:
      Log(FailureContext + ': succeeded.');
    1:
      begin
        Log(FailureContext + ': ' + StdErr);
        if ShowFailureBox then
          MsgBox(FailureContext + ':' + #13#10#13#10 + String(StdErr) + #13#10#13#10 +
                 'This does not affect the rest of the install.', mbInformation, MB_OK);
      end;
  else
    begin
      Log(FailureContext + ': unexpected exit code ' + IntToStr(ExitCode) + '. ' + StdErr);
      if ShowFailureBox then
        MsgBox(FailureContext + ' failed unexpectedly (exit code ' + IntToStr(ExitCode) + '):' + #13#10#13#10 +
               String(StdErr) + #13#10#13#10 + 'This does not affect the rest of the install.', mbInformation, MB_OK);
    end;
  end;
end;

// Runs before any files are copied (per spec: "before copying new files"). Backs up the
// about-to-be-upgraded install's settings.json as a safety net.
//
// TODO(review)-turned-fix: this used to call the OLD exe's --export-settings here. That breaks
// upgrading from v1.0.0, the public release, which predates this feature entirely: its
// Program.ParseArguments has no idea what --export-settings is, maps it to Verb.Unknown, and
// Unknown pops a MODAL MessageBox ("Unrecognised argument: ...") that Exec sits there waiting
// on -- every v1.0.0 user upgrading would hit a confusing blocking dialog mid-setup. Both
// builds report AssemblyVersion 1.0.0.0, so there is no version number to gate on either.
// A plain file copy needs no exe at all, so it can never hit this: it works unconditionally,
// for every past version, forever. It is lossless for the fields that matter -- SettingsPortability
// deliberately already omits KnownKeys/FailedClips from a real export, same as this raw copy
// omits nothing beyond what export would have -- and a user can restore it by renaming it back
// to settings.json by hand if an upgrade ever goes wrong. A human-readable *.win4kss.json
// export of the same moment, made with the NEW exe (so it carries whatever SettingsCli fixes
// shipped after the version being upgraded from), happens separately below in
// CurStepChanged(ssPostInstall), once that new exe actually exists at {app}.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  SettingsPath, BackupPath: String;
begin
  Result := '';
  if not ExistingInstallDetected then
    Exit;

  SettingsPath := ExpandConstant('{localappdata}\Win4KScreensavers\settings.json');
  if not FileExists(SettingsPath) then
  begin
    Log('PrepareToInstall: no settings.json to back up at ' + SettingsPath + '.');
    Exit;
  end;

  BackupPath := ExpandConstant('{localappdata}\Win4KScreensavers\pre-upgrade-settings.json');
  if CopyFile(SettingsPath, BackupPath, False) then
    Log('PrepareToInstall: backed up ' + SettingsPath + ' to ' + BackupPath + '.')
  else
    Log('PrepareToInstall: could not back up ' + SettingsPath + ' to ' + BackupPath + '.');
end;

// Runs immediately before the install-scr.ps1 [Run] entry below (its BeforeInstall), which
// Inno guarantees happens after [Files]/[Icons] are on disk but before that entry itself runs
// -- i.e. before install-scr.ps1 touches shortcuts/registration. Deliberately NOT done in
// CurStepChanged(ssPostInstall): Inno's own docs don't specify whether ssPostInstall fires
// before or after a non-postinstall [Run] entry, and this needs to be certain, not probably-
// fine. --include-machine is deliberately never passed: machine-specific paths (cache folder,
// local folders, ffmpeg path) from someone else's export are not something a fresh install
// should silently adopt (see SettingsCli.cs's own doc comment).
procedure ImportSettingsIfChosen();
begin
  if ImportSettingsFile <> '' then
    RunSettingsCliAndWarn(ExpandConstant('{app}\{#MyAppExeName}'),
      '--import-settings "' + ImportSettingsFile + '"',
      'Importing settings from ' + ImportSettingsFile, True);
end;

// Once every file is on disk (ssPostInstall), makes a human-readable *.win4kss.json export of
// the settings an upgrade just carried over -- using the exe that was just installed, which is
// why this waits for ssPostInstall rather than running in PrepareToInstall alongside the raw
// settings.json backup above. That plain-copy backup is the real safety net and does not
// depend on this succeeding; this is a nicer-to-read bonus on top of it. Not attempted on a
// fresh install (ExistingInstallDetected false): there is nothing yet to export.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and ExistingInstallDetected then
    RunSettingsCliAndWarn(ExpandConstant('{app}\{#MyAppExeName}'),
      '--export-settings "' + ExpandConstant('{localappdata}\Win4KScreensavers\pre-upgrade-settings.win4kss.json') + '"',
      'Making a readable export of your settings before the upgrade', False);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  CacheDir: String;
  Answer: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Separate from {app}: settings.json, the manifest cache and the video cache all live
    // under %LOCALAPPDATA%\Win4KScreensavers regardless of which name or scope the app was
    // installed under (see CLAUDE.md). install-scr.ps1 -Uninstall deliberately never touches
    // it; asking here, once, with "keep" as the default answer, is the one place that does.
    CacheDir := ExpandConstant('{localappdata}\Win4KScreensavers');
    if DirExists(CacheDir) then
    begin
      Answer := MsgBox(
        '{#MyAppName} has been removed.' + #13#10#13#10 +
        'Its settings and video cache are still here:' + #13#10 + CacheDir + #13#10#13#10 +
        'That cache can be tens of gigabytes. Delete it too?',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2);
      if Answer = IDYES then
        DelTree(CacheDir, True, True, True);
    end;
  end;
end;
