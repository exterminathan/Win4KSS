; Win4KSS installer (Inno Setup 6).
;
; Packages the loose multi-file publish\ output (built by `dotnet publish`). This used to be a
; single-file build with libmpv-2.dll/WebView2Loader.dll bundled and self-extracted inside
; Win4KSS.exe/.scr (IncludeNativeLibrariesForSelfExtract); that shape -- an unsigned exe that
; self-extracts a native DLL at launch -- is exactly what tripped a Bitdefender heuristic false
; positive on another machine (Windows Defender scans the same file clean). Publish now produces
; ordinary loose files, so every one of them is listed in [Files] below and has to land together
; in {app} -- see scripts/build-installer.ps1, which publishes before compiling this script and
; verifies the real output list rather than assuming it. A second, separate single-managed-file
; build with no native DLLs (publish\system32\{#MyAppScrName}) backs the "systemscr" Task below
; for the same reason loose files stay loose everywhere else: System32 is on every process's DLL
; search path, so copying libmpv-2.dll/WebView2Loader.dll there was rejected too.
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

; Per-user: everything Setup writes is under %LOCALAPPDATA% and HKCU, so installing and
; updating never ask for admin. The two things that do need it ask for themselves, and only
; when needed: install-scr.ps1 elevates just the System32 copy (for the Screen Saver Settings
; dropdown), and only when the relay there is missing or a different relay version; the .NET
; runtime installer is run elevated only when the runtime is missing. Before 1.2.5 Setup ran as
; admin and registered its uninstall entry in HKLM; see GetInstallScrArgs for that migration.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=output
OutputBaseFilename=Win4KSS-Setup-{#MyAppVersion}
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
InfoBeforeFile=InfoBefore.txt
; Setup.exe's own icon (Explorer, taskbar while it runs). UninstallDisplayIcon already points at
; {#MyAppExeName}, which carries the same icon baked into its own PE resources (Win4K.ScreenSaver.csproj's
; ApplicationIcon) -- no separate uninstall icon file needed.
SetupIconFile=..\assets\icon\win4kss.ico
; The .ico as a file, not the exe's resource: a fresh path that Windows' icon cache has no stale
; (icon-less) entry for. ChangesAssociations makes Setup broadcast SHCNE_ASSOCCHANGED at the end,
; which refreshes the icons Start and search show.
UninstallDisplayIcon={app}\win4kss.ico
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Checked by default: this is the one thing most people installing a screen saver actually
; want -- to see it in the normal Windows screen saver picker. install-scr.ps1 backs this with
; a separate single-managed-file, no-native-DLLs build (publish\system32\{#MyAppScrName},
; staged below) that resolves libmpv/WebView2 from the user-scope companion dir at its own
; startup, rather than copying those DLLs into System32 itself -- System32 is on every
; process's DLL search path, and a name like WebView2Loader.dll is shared across every
; WebView2 app on the machine, so our copies landing there could get picked up by unrelated
; software. Unchecking falls back to install-scr.ps1 -UserScope, still a fully working install
; in every other respect (see that script's own doc comment).
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
; Every file `dotnet publish` actually produces now has to be listed -- Win4KSS.exe/.scr alone,
; without their sibling DLLs, cannot run; see the header comment above. ignoreversion: these
; builds' internal version doesn't move independently of this installer's, and re-running Setup
; over an existing install should always win.
Source: "..\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\{#MyAppScrName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\Win4KSS.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\Win4KSS.deps.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\Win4KSS.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\Win4K.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\libmpv-2.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\WebView2Loader.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\Microsoft.Web.WebView2.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\Microsoft.Web.WebView2.WinForms.dll"; DestDir: "{app}"; Flags: ignoreversion
; NuGet also drops a RID-specific copy of WebView2Loader.dll under runtimes\win-x64\native\
; alongside the flattened top-level one above -- part of the real publish output, so it ships
; too rather than being assumed redundant.
Source: "..\publish\runtimes\*"; DestDir: "{app}\runtimes"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\publish\win4kss.ico"; DestDir: "{app}"; Flags: ignoreversion
; ffmpeg for clip-browser previews (scripts/fetch-ffmpeg.ps1), in its own folder so its av*.dll
; never sit beside Win4KSS.exe. It reads through Win4KSS's loopback relay, never the network.
Source: "..\publish\ffmpeg\*"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion
; TODO(review): .pdb symbol files -- small, and useful if a crash ever needs symbolicating, but
; drop these two lines if you'd rather keep {app} to only what can run.
Source: "..\publish\Win4KSS.pdb"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\Win4K.Core.pdb"; DestDir: "{app}"; Flags: ignoreversion
; Bundled so the [Run]/[UninstallRun] steps below can call it, and so it is still sitting in
; {app} afterwards if the registration ever needs to be re-run by hand (e.g. -SetAsDefault
; later, or after a Windows feature update resets screen saver state).
Source: "..\scripts\install-scr.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion isreadme
; The separate single-managed-file, no-native-DLLs build for the System32 copy (see the
; "systemscr" Task above) -- staged at {app}\system32\ so install-scr.ps1, invoked below with
; -PublishDir "{app}", finds it at the same relative path it uses from a repo clone's
; publish\system32\. Not placed directly under {app}: it would collide with the loose build's
; own {#MyAppScrName} right above.
Source: "..\publish\system32\{#MyAppScrName}"; DestDir: "{app}\system32"; Flags: ignoreversion

[Icons]
; Per-user Start Menu entries, to match everything else this app touches (settings, cache,
; the companion .exe) being per-user, like Setup itself. Opens the clip
; browser (/c) -- the bare .exe with no arguments opens a windowed preview instead, which is
; not what someone clicking a Start Menu icon would expect.
Name: "{userprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "/c"; WorkingDir: "{app}"; IconFilename: "{app}\win4kss.ico"; Comment: "Open the Win4KSS clip browser"
Name: "{userprograms}\{#MyAppName}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"; IconFilename: "{app}\win4kss.ico"

; The optional Desktop/Start Menu "Start Screen Saver.lnk" launchers are created by the
; install-scr.ps1 [Run] step below (fresh installs only; an update keeps the ones the user kept).
; The Ctrl+Alt+S hotkey is not on them: install-scr.ps1 starts the resident hotkey listener
; (Win4KSS.exe /hotkey), which holds the hotkey and adds itself to HKCU\...\Run. That step
; runs for silent (in-app update) and interactive installs alike; PrepareToInstall below stops
; the old listener first, since it keeps Win4KSS.exe and its DLLs open.

[Run]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-scr.ps1"" -SkipBuild -PublishDir ""{app}"" {code:GetInstallScrArgs}"; \
    StatusMsg: "Registering {#MyAppName} as a screen saver..."; \
    Flags: runhidden waituntilterminated; BeforeInstall: ImportSettingsIfChosen
Filename: "{app}\{#MyAppExeName}"; Parameters: "/c"; Description: "Open the {#MyAppName} clip browser now"; Flags: postinstall skipifsilent nowait unchecked
; A silent run is the in-app updater (UpdateInstaller.SilentArguments), which closed the clip
; browser to make way: bring it back, as the user, once everything above has run.
Filename: "{app}\{#MyAppExeName}"; Parameters: "/c"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallRun]
; Runs as the FIRST step of uninstall, before Setup removes any files registered in [Files] --
; so install-scr.ps1 is still at {app}\install-scr.ps1 when this executes. It removes the
; System32 copy (if any), the Desktop/Start Menu "Start Screen Saver.lnk" shortcuts, the hotkey
; listener and its HKCU Run entry, the whole {app} companion dir, and repoints HKCU's
; SCRNSAVE.EXE away from the file we are about to delete. It deliberately leaves %LOCALAPPDATA%\Win4KScreensavers (settings + the video cache)
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
  // The general release page, not a version-pinned installer link: it always lists the current
  // ".NET Desktop Runtime" download for whatever Windows/architecture the visitor is on, so this
  // does not go stale across .NET versions the way a direct installer URL would.
  DotNetDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';
  // Microsoft's own always-latest link for the .NET 10 Windows Desktop Runtime installer (x64).
  DotNetInstallerUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';
  // Where an admin-mode (pre-1.2.5) install registered itself for Apps & Features.
  MachineUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{FDEAF37E-5817-43BA-A346-B9BB300B9B46}_is1';

  // The hotkey listener's single-instance mutex and its stop event (HotkeyAgent.cs).
  HotkeyMutexName = 'Local\Win4KSS.Hotkey';
  HotkeyStopEventName = 'Local\Win4KSS.Hotkey.Stop';
  EVENT_MODIFY_STATE = $0002;
  SYNCHRONIZE = $00100000;

var
  // Set once in InitializeSetup and read everywhere else -- {app} is fixed (DisableDirPage=yes),
  // so this is decided before the wizard shows any pages and never changes during the run.
  ExistingInstallDetected: Boolean;
  // '' until the user picks a file for the "importsettings" task via GetOpenFileName
  // (NextButtonClick). ImportSettingsIfChosen -- a BeforeInstall callback on the install-scr.ps1
  // [Run] entry below -- imports it if non-empty.
  ImportSettingsFile: String;
  // Prerequisites this PC lacks, decided in InitializeSetup, installed before any files are copied.
  NeedDotNet, NeedWebView2, PrerequisitesHandled: Boolean;
  DownloadPage: TDownloadWizardPage;

function IsWebView2RuntimeInstalled(): Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2ClientGuid, 'pv', Version) or
    RegQueryStringValue(HKLM32, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2ClientGuid, 'pv', Version) or
    RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2ClientGuid, 'pv', Version);
end;

// {#MyAppName} is SelfContained=false (Win4K.ScreenSaver.csproj): unlike WebView2, which only
// gates the clip browser, a missing .NET Desktop Runtime means NOTHING runs after installing --
// not /c, not /w, not even the screen saver itself. {commonpf64} (not {app}: not yet valid here,
// see DetectExistingInstall's own comment) is where the x64 runtime always installs regardless
// of this installer's own bitness, since it is a per-machine, not per-app, install location.
// Checking for a 10.* subfolder under the Windows Desktop shared framework -- rather than
// shelling out to `dotnet --list-runtimes`, which assumes dotnet.exe is even on PATH, exactly
// the case this exists to catch on a bare machine -- is the same directory Microsoft's own
// runtime installer documents writing to.
function IsDotNetDesktopRuntimeInstalled(): Boolean;
var
  FindRec: TFindRec;
  BasePath: String;
begin
  Result := False;
  BasePath := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(BasePath) then Exit;
  if FindFirst(BasePath + '\10.*', FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
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

// Called from InitializeSetup, where {app} is NOT YET VALID -- Inno's docs are explicit that
// {app} cannot be expanded there (it throws "An attempt was made to expand the 'app' constant
// before it was initialized", 100% reproducibly, on every single run, regardless of DisableDirPage).
// DefaultDirName is {localappdata}\Programs\{#MyAppName} and the dir page is disabled, so {app}
// always equals that literal path anyway -- spelling it out here, instead of via {app}, is exactly
// equivalent and safe at this point in the sequence.
function DetectExistingInstall(): Boolean;
begin
  Result :=
    FileExists(ExpandConstant('{localappdata}\Programs\{#MyAppName}\{#MyAppExeName}')) or
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

  // Nothing to ask the user: whatever is missing is downloaded from Microsoft and installed
  // silently once they click Install (InstallPrerequisites). .NET is required -- the app is
  // SelfContained=false, so without it nothing runs; WebView2 only gates the clip browser.
  NeedDotNet := not IsDotNetDesktopRuntimeInstalled();
  NeedWebView2 := not IsWebView2RuntimeInstalled();
  PrerequisitesHandled := False;
end;

procedure InitializeWizard();
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

// Downloads one Microsoft installer into {tmp} and runs it silently. Returns '' on success,
// else a reason. Exit codes 3010 (reboot pending) and 1638 (a newer version is already there)
// count as success for the .NET installer.
function DownloadAndRun(const Url, FileName, Params, Title: String; Elevate: Boolean): String;
var
  ResultCode: Integer;
  Interactive: Boolean;
begin
  Result := '';
  Interactive := not WizardSilent();
  try
    if Interactive then
    begin
      DownloadPage.Clear;
      DownloadPage.Add(Url, FileName, '');
      DownloadPage.SetText('Downloading ' + Title + '...', '');
      DownloadPage.Show;
      try
        DownloadPage.Download;
        DownloadPage.SetText('Installing ' + Title + '...', 'This can take a minute.');
        DownloadPage.ProgressBar.Style := npbstMarquee;
      except
        DownloadPage.Hide;
        Result := 'Could not download ' + Title + ': ' + GetExceptionMessage;
        Exit;
      end;
    end
    else
      DownloadTemporaryFile(Url, FileName, '', nil);
  except
    Result := 'Could not download ' + Title + ': ' + GetExceptionMessage;
    Exit;
  end;

  try
    // Setup itself is not elevated, so a machine-wide runtime asks for admin just for itself.
    // Its outcome is judged afterwards by whether the runtime is actually there.
    if Elevate then
    begin
      if not ShellExec('runas', ExpandConstant('{tmp}\' + FileName), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        Result := 'Could not start the ' + Title + ' installer: ' + SysErrorMessage(ResultCode);
    end
    else if not Exec(ExpandConstant('{tmp}\' + FileName), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Result := 'Could not start the ' + Title + ' installer: ' + SysErrorMessage(ResultCode)
    else if (ResultCode <> 0) and (ResultCode <> 3010) and (ResultCode <> 1638) then
      Result := 'Installing ' + Title + ' failed (exit code ' + IntToStr(ResultCode) + ').';
  finally
    if Interactive then
    begin
      DownloadPage.ProgressBar.Style := npbstNormal;
      DownloadPage.Hide;
    end;
  end;
  Log('Prerequisite ' + Title + ': ' + Result);
end;

// Installs the missing runtimes. Returns '' to carry on, or why Setup must stop (.NET only:
// without WebView2 the screen saver still works and the clip browser explains itself).
function InstallPrerequisites(): String;
var
  Failure: String;
begin
  Result := '';
  if PrerequisitesHandled then
    Exit;

  if NeedDotNet then
  begin
    // Judged by whether the runtime is there afterwards, not by exit codes alone.
    Failure := DownloadAndRun(DotNetInstallerUrl, 'windowsdesktop-runtime-win-x64.exe',
      '/install /quiet /norestart', 'the .NET Desktop Runtime', True);
    if not IsDotNetDesktopRuntimeInstalled() then
    begin
      if Failure = '' then
        Failure := 'The .NET Desktop Runtime installer finished but the runtime is still missing.';
      Result := Failure + Chr(13) + Chr(10) + Chr(13) + Chr(10) +
        '{#MyAppName} cannot run without it. Check the internet connection and run Setup again, ' +
        'or install it from ' + DotNetDownloadUrl;
      Exit;
    end;
  end;

  if NeedWebView2 then
  begin
    Failure := DownloadAndRun(WebView2DownloadUrl, 'MicrosoftEdgeWebview2Setup.exe',
      '/silent /install', 'the Microsoft Edge WebView2 Runtime', False);
    if Failure = '' then
      Failure := 'The Microsoft Edge WebView2 Runtime did not install.';
    if not IsWebView2RuntimeInstalled() then
      SuppressibleMsgBox(Failure + Chr(13) + Chr(10) + Chr(13) + Chr(10) +
        'The screen saver will still work. The clip browser needs it; get it from ' + WebView2DownloadUrl,
        mbInformation, MB_OK, IDOK);
  end;

  PrerequisitesHandled := True;
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
  // An admin-mode install from before 1.2.5 left its Apps & Features entry in HKLM; this
  // per-user Setup writes its own to HKCU. Removing the old one needs admin, so it rides on
  // install-scr.ps1's one System32 prompt instead of asking separately.
  if RegKeyExists(HKLM64, MachineUninstallKey) or RegKeyExists(HKLM32, MachineUninstallKey) then
    Args := Args + ' -RemoveMachineUninstallEntry';
  Result := Trim(Args);
end;

// Leaving the Tasks page with "importsettings" checked prompts for the file right away, so a
// bad pick (or Cancel) is caught before Setup commits to anything -- Result := False keeps the
// wizard on this page instead of advancing with the task checked but nothing chosen.
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Failure: String;
begin
  Result := True;

  // Install clicked: fetch and install missing runtimes here, where the download page can show
  // progress. PrepareToInstall repeats the call for silent runs, which never click anything.
  if CurPageID = wpReady then
  begin
    Failure := InstallPrerequisites();
    if Failure <> '' then
    begin
      MsgBox(Failure, mbCriticalError, MB_OK);
      Result := False;
    end;
    Exit;
  end;

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
function OpenEvent(DesiredAccess: Cardinal; InheritHandle: LongBool; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Handle: THandle): LongBool;
  external 'SetEvent@kernel32.dll stdcall';
function OpenMutex(DesiredAccess: Cardinal; InheritHandle: LongBool; Name: String): THandle;
  external 'OpenMutexW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): LongBool;
  external 'CloseHandle@kernel32.dll stdcall';

// The hotkey listener holds Win4KSS.exe and its DLLs open, so it must be gone before [Files]
// replaces them. It exits cleanly on its stop event; install-scr.ps1 starts the new one.
// Nothing is running (OpenEvent fails) on a first install or an upgrade from before v1.2.6.
procedure StopHotkeyListener();
var
  Handle: THandle;
  I: Integer;
begin
  Handle := OpenEvent(EVENT_MODIFY_STATE, False, HotkeyStopEventName);
  if Handle = 0 then
    Exit;
  SetEvent(Handle);
  CloseHandle(Handle);
  for I := 1 to 50 do
  begin
    Handle := OpenMutex(SYNCHRONIZE, False, HotkeyMutexName);
    if Handle = 0 then
    begin
      Log('StopHotkeyListener: the hotkey listener has exited.');
      Exit;
    end;
    CloseHandle(Handle);
    Sleep(100);
  end;
  Log('StopHotkeyListener: the hotkey listener did not exit within 5 seconds.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  SettingsPath, BackupPath: String;
begin
  Result := InstallPrerequisites();
  if Result <> '' then
    Exit;

  StopHotkeyListener();

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
