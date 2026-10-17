; Modlight installer.
;
; Written to sit BESIDE an existing ShareX install without touching it. Three
; things in the upstream script would have broken that promise, and each is
; called out where it is fixed below:
;
;   1. AppId was ShareX's GUID, so installing Modlight would have taken over
;      ShareX's entry in Add/Remove Programs.
;   2. AppMutex was that same GUID, which is not the mutex this app takes - so
;      the uninstaller would have failed to notice Modlight running, and would
;      have complained about ShareX instead.
;   3. Several registry lines carried `uninsdeletekey` for keys this installer
;      never creates (ShareX's own file associations). Uninstalling Modlight
;      would have deleted them out from under ShareX.

#define MyAppName "Modlight"
; The project FOLDER is still named ShareX - only the product was renamed - so
; the build path cannot be derived from the app name any more.
#define MyProjectDirName "ShareX"

#ifndef Platform
  #define Platform "x64"
#endif
#if Platform == "arm64"
  #define RuntimeId "win-arm64"
  #define MyArchitecturesAllowed "arm64"
#else
  #define RuntimeId "win-x64"
  #define MyArchitecturesAllowed "x64compatible"
#endif

#define MyAppRootDirectory "..\.."
#define MyAppOutputDirectory MyAppRootDirectory + "\Output"
; The PUBLISH tree, not bin\Release\<rid> itself. `dotnet build` leaves a
; framework-dependent app there whose runtimeconfig.json names Microsoft.NETCore.App and
; Microsoft.WindowsDesktop.App as SHARED frameworks - install that on a PC without the
; .NET 10 Desktop Runtime and nothing starts, with no message the user can act on.
; `dotnet publish ShareX/ShareX.csproj -c Release -p:Platform=x64` writes a self-contained
; tree here instead (PublishSelfContained is set in ShareX.csproj), where runtimeconfig.json
; lists "includedFrameworks" and the runtime itself - hostfxr, hostpolicy, coreclr, clrjit,
; System.Private.CoreLib - sits beside the exe. That is the whole of "make it work everywhere".
;
; A RID-specific publish also FLATTENS native assets into the root instead of leaving them
; under runtimes\win-x64\native, which is why three wildcards are still enough below:
; libSkiaSharp.dll, av_libglesv2.dll, DirectML.dll and the rest are top-level files.
#define MyAppReleaseDirectory MyAppRootDirectory + "\" + MyProjectDirName + "\bin\Release\" + RuntimeId + "\publish"
#define MyAppFileName MyAppName + ".exe"
#define MyAppFilePath MyAppReleaseDirectory + "\" + MyAppFileName
#define MyAppVersion GetStringFileInfo(MyAppFilePath, "ProductVersion")
#define MyAppPublisher "Modlight"
; Publisher link shown in Apps & features. Override with /DMyAppURL=https://...
#ifndef MyAppURL
  #define MyAppURL "https://github.com/Nimfek/Modlight-App"
#endif

; OUR id, and deliberately the same GUID the running app uses for its single
; instance mutex (ShareX.Program.MutexName). Two consequences, both wanted:
; Add/Remove Programs gets a Modlight entry of its own, and AppMutex below
; actually detects a running Modlight.
#define MyAppId "3030612E-160A-4C79-8811-E6E709671DD8"

; Upstream ShareX's GUID: its single instance mutex, its installer's AppMutex and
; its key in Add/Remove Programs are all this one value. Ours above is
; deliberately different, which is what makes this a reliable test for THEIR app
; and only theirs. Used by the "ShareX is running" page in [Code].
#define ShareXMutex "82E6AC09-0FEF-4390-AD9F-0DD3F5561EFC"

[Setup]
AppCopyright=Copyright (c) 2007-2026 ShareX Team, Modlight contributors
AppId={#MyAppId}
AppMutex={#MyAppId}
AppName={#MyAppName}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppVerName={#MyAppName} {#MyAppVersion}
AppVersion={#MyAppVersion}
ArchitecturesAllowed={#MyArchitecturesAllowed}
ArchitecturesInstallIn64BitMode={#MyArchitecturesAllowed}
DefaultDirName={commonpf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; The installer's OWN icon, in Explorer and in the UAC prompt. Without it the
; downloaded file wears Inno Setup's generic icon, which is the first thing
; anybody sees of this product and says nothing about it.
SetupIconFile={#MyAppRootDirectory}\{#MyProjectDirName}\Modlight_Icon.ico
; The wizard wears the product, not Inno Setup's stock blue. Two sizes each so
; the images stay sharp on a 150% or 200% display instead of being upscaled.
WizardStyle=modern
WizardImageFile=brand\wizard-large.bmp,brand\wizard-large@2x.bmp
WizardSmallImageFile=brand\wizard-small.bmp,brand\wizard-small@2x.bmp
WizardImageStretch=no
; GPL-3.0: the fork carries upstream's licence and must keep showing it.
LicenseFile={#MyAppRootDirectory}\LICENSE.txt
MinVersion=10.0.14393
OutputBaseFilename={#MyAppName}-{#MyAppVersion}-setup-{#Platform}
OutputDir={#MyAppOutputDirectory}
PrivilegesRequired=none
SolidCompression=yes
UninstallDisplayIcon={app}\{#MyAppFileName}
UninstallDisplayName={#MyAppName}
VersionInfoCompany={#MyAppPublisher}
VersionInfoTextVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}

[Tasks]
Name: "CreateDesktopIcon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Check: not IsUpdating and not DesktopIconExists
Name: "CreateContextMenuButton"; Description: "Show ""Upload with Modlight"" button in Windows Explorer context menu"; GroupDescription: "Additional shortcuts:"; Check: not IsUpdating
Name: "CreateSendToIcon"; Description: "Create a send to shortcut"; GroupDescription: "Additional shortcuts:"; Check: not IsUpdating
Name: "CreateStartupIcon"; Description: "Run Modlight when Windows starts"; GroupDescription: "Other tasks:"; Check: not IsUpdating
Name: "DisablePrintScreenKeyForSnippingTool"; Description: "Disable Print Screen key for Snipping Tool"; GroupDescription: "Other tasks:"; Check: not IsUpdating
; The ShareX import is NOT a task any more - it is a page of its own. See [Code].

[Files]
; No *.pdb: the publish tree carries 114 MB of debug symbols no user needs. No *.lib
; either - onnxruntime.lib is a linker artifact with nothing to do at runtime.
Source: "{#MyAppReleaseDirectory}\*.exe"; DestDir: {app}; Flags: ignoreversion
Source: "{#MyAppReleaseDirectory}\*.dll"; DestDir: {app}; Flags: ignoreversion
Source: "{#MyAppReleaseDirectory}\*.json"; DestDir: {app}; Flags: ignoreversion
Source: "{#MyAppRootDirectory}\Licenses\*.txt"; DestDir: {app}\Licenses; Flags: ignoreversion
; QuickJS, which the Download tab hands to yt-dlp as --js-runtimes quickjs:<path>.
; BOTH files. The DLL is the one that gets forgotten: qjs.exe names libwinpthread-1.dll in
; its PE import table, so without it the process dies at load with 0xC0000135 before it runs
; a line - and it survives testing on developer machines because Git for Windows and MSYS
; each leave a copy on PATH.
; No skipifsourcedoesntexist on these two: they are committed to the repo, so a build that
; cannot find them is a broken checkout and should say so, rather than quietly producing an
; installer whose Download tab is worse than the previous one.
Source: "{#MyAppRootDirectory}\Tools\quickjs\qjs.exe"; DestDir: {app}\Tools; Flags: ignoreversion
Source: "{#MyAppRootDirectory}\Tools\quickjs\libwinpthread-1.dll"; DestDir: {app}\Tools; Flags: ignoreversion
; ffmpeg.exe / exiftool.exe when they have been fetched into Output first.
; Upstream downloads them as a separate build step; this fork does not. The application
; fetches them itself - see the components page in [Code] - and looks beside the executable
; before it looks in its own Tools folder, so a build that DID put them here still wins and
; nothing is downloaded twice. Either way the installer has to build with or without them
; rather than refusing outright.
;
; NAMED, not "Output\*.exe" as before. OutputDir is that same folder, so the wildcard also
; matched the PREVIOUS setup executable sitting in it and installed that into {app} - a
; 30 MB passenger that would grow with every release it was carried through.
Source: "{#MyAppOutputDirectory}\ffmpeg.exe"; DestDir: {app}; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#MyAppOutputDirectory}\exiftool.exe"; DestDir: {app}; Flags: ignoreversion skipifsourcedoesntexist
; The real file name. This line asked for "Modlight_File_Icon.ico", which no build has ever
; produced - the icon ships as ShareX_File_Icon.ico and IntegrationHelpers.FileIconPath looks
; it up under exactly that name - so skipifsourcedoesntexist swallowed it every time and the
; Explorer context-menu icon was never actually installed.
Source: "{#MyAppReleaseDirectory}\ShareX_File_Icon.ico"; DestDir: {app}; Flags: ignoreversion
Source: "{#MyAppReleaseDirectory}\pl\*.resources.dll"; DestDir: {app}\Languages\pl; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppFileName}"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"; WorkingDir: "{app}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppFileName}"; WorkingDir: "{app}"; Tasks: CreateDesktopIcon
Name: "{usersendto}\{#MyAppName}"; Filename: "{app}\{#MyAppFileName}"; WorkingDir: "{app}"; Tasks: CreateSendToIcon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppFileName}"; WorkingDir: "{app}"; Parameters: "-silent"; Tasks: CreateStartupIcon

[Run]
Filename: "{app}\{#MyAppFileName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall; Check: not IsNoRun

[Registry]
; Every key here is under our own name, so a machine with both products gets two
; context-menu entries rather than one fighting over the other's key.
Root: "HKCU"; Subkey: "Software\Classes\*\shell\{#MyAppName}"; ValueType: string; ValueData: "Upload with {#MyAppName}"; Tasks: CreateContextMenuButton
Root: "HKCU"; Subkey: "Software\Classes\*\shell\{#MyAppName}"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppFileName}"",0"; Tasks: CreateContextMenuButton
Root: "HKCU"; Subkey: "Software\Classes\*\shell\{#MyAppName}\command"; ValueType: string; ValueData: """{app}\{#MyAppFileName}"" ""%1"""; Tasks: CreateContextMenuButton
Root: "HKCU"; Subkey: "Software\Classes\Directory\shell\{#MyAppName}"; ValueType: string; ValueData: "Upload with {#MyAppName}"; Tasks: CreateContextMenuButton
Root: "HKCU"; Subkey: "Software\Classes\Directory\shell\{#MyAppName}"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppFileName}"",0"; Tasks: CreateContextMenuButton
Root: "HKCU"; Subkey: "Software\Classes\Directory\shell\{#MyAppName}\command"; ValueType: string; ValueData: """{app}\{#MyAppFileName}"" ""%1"""; Tasks: CreateContextMenuButton
Root: "HKCU"; Subkey: "Software\Classes\*\shell\{#MyAppName}"; Flags: dontcreatekey uninsdeletekey
Root: "HKCU"; Subkey: "Software\Classes\Directory\shell\{#MyAppName}"; Flags: dontcreatekey uninsdeletekey
; Upstream also cleaned up .sxcu / .sxie / ShareXImageEditor here. Those lines
; are gone on purpose: this installer never creates them (the custom-uploader
; subsystem was cut), so keeping `uninsdeletekey` on them would mean uninstalling
; Modlight silently deletes a ShareX user's own file associations.
Root: "HKCU"; Subkey: "Control Panel\Keyboard"; ValueType: dword; ValueName: "PrintScreenKeyForSnippingEnabled"; ValueData: "0"; Flags: uninsdeletevalue; Tasks: DisablePrintScreenKeyForSnippingTool
; The components page below leaves its answer here for the application to read once on its
; first run. Only the Setup subkey is removed on uninstall - SystemOptions reads PersonalPath
; and DisableUpdateCheck straight out of Software\Modlight itself, and those can have been put
; there deliberately.
Root: "HKCU"; Subkey: "Software\Modlight\Setup"; Flags: dontcreatekey uninsdeletekey

[Code]
function ShareXProfilePath(): String;
begin
  Result := ExpandConstant('{userdocs}\ShareX');
end;

function ModlightProfilePath(): String;
begin
  Result := ExpandConstant('{userdocs}\Modlight');
end;

{ Only offer the import when there is something to import. A settings file is a
  better test than the folder: ShareX creates the folder on first run and leaves
  it empty if the user never got as far as saving anything. }
function ShareXProfileExists(): Boolean;
begin
  Result := FileExists(ShareXProfilePath() + '\ApplicationConfig.json');
end;

{ Copy the two settings files, and nothing else.

  Deliberately not a folder copy: "Documents\ShareX" also holds the user's whole
  screenshot history and every image they ever captured, which can be gigabytes,
  and duplicating that to make a second app start up is indefensible.

  PersonalPath.cfg must NEVER be one of them, and this is the important line in
  this file. That file does not hold a setting; it holds the ANSWER to "where is
  my data folder", and ShareX writes it when its user moved that folder. Copying
  it into Documents\Modlight makes Modlight read it on first run
  (Program.LoadPersonalPathConfig) and adopt ShareX's data directory as its own -
  so the two products would then share one folder, and Modlight would rewrite
  ShareX's live ApplicationConfig.json, its HotkeysConfig.json and its history in
  place. That is the exact opposite of the promise in Program.PersonalFolderName,
  which is that Modlight starts from a clean Documents\Modlight and that an
  import is a copy the user asked for, not a takeover of the original.

  History.db is deliberately absent, which is why the task caption no longer
  claims to bring history over. Every row in it names a local file under
  Documents\ShareX\Screenshots, and those are exactly the gigabytes this
  procedure refuses to duplicate - so an imported history would be a list whose
  entries point into the OTHER product's folder. That is the same cross-wiring
  PersonalPath.cfg causes, one row at a time.

  UploadersConfig is deliberately absent too. It holds credentials for the
  third-party services this fork removed, so copying it would move a file full
  of live API keys into a new location for no working feature. }
function CmdLineParamExists(const value: string): Boolean;
var
  i: Integer;
begin
  Result := False;
  for i := 1 to ParamCount do
    if CompareText(ParamStr(i), value) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function IsUpdating(): Boolean;
begin
  Result := CmdLineParamExists('/UPDATE');
end;

function IsNoRun(): Boolean;
begin
  Result := CmdLineParamExists('/NORUN');
end;

function DesktopIconExists(): Boolean;
begin
  Result := FileExists(ExpandConstant('{userdesktop}\{#MyAppName}.lnk'));
end;

procedure ImportShareXSettings();
var
  Source, Target: String;
  Names: array[0..1] of String;
  I: Integer;
begin
  Source := ShareXProfilePath();
  Target := ModlightProfilePath();

  Names[0] := 'ApplicationConfig.json';
  Names[1] := 'HotkeysConfig.json';

  if not DirExists(Target) then
  begin
    if not ForceDirectories(Target) then Exit;
  end;

  for I := 0 to 1 do
  begin
    if FileExists(Source + '\' + Names[I]) then
    begin
      { True = FailIfExists, so an existing file is left alone. Re-running the
        installer must never throw away settings the user has since changed in
        Modlight itself. }
      FileCopy(Source + '\' + Names[I], Target + '\' + Names[I], True);
    end;
  end;
end;

{ The ShareX import, asked as a question rather than buried in a checklist.

  It used to be one unchecked box among five under "Other tasks:", which is the
  wrong shape for it twice over: it is the only one of those that depends on
  something already ON the machine, and it is the only one a ShareX user
  actually cares about. A page states the finding ("we found your ShareX
  settings") and asks, so the answer is a decision instead of a box nobody read.

  The page is created only when there is something to import, so a machine
  without ShareX never sees it and the wizard stays short. }
var
  ImportPage: TInputOptionWizardPage;

procedure CreateImportPage(const AfterID: Integer);
begin
  ImportPage := CreateInputOptionPage(AfterID,
    'ShareX settings found',
    'Modlight can start from your existing ShareX setup.',
    'Your ShareX hotkeys and capture options are on this PC. Copy them into Modlight,' + #13#10 +
    'or start clean? Nothing in your ShareX installation is changed either way, and' + #13#10 +
    'you can change any of it later inside Modlight.',
    True,   { exclusive - these are two answers to one question }
    False);
  ImportPage.Add('Copy my ShareX hotkeys and capture options');
  ImportPage.Add('Start with Modlight defaults');
  { Copying is a real change to a fresh install, so the neutral answer leads. }
  ImportPage.SelectedValueIndex := 1;
end;

function ShouldImportShareX(): Boolean;
begin
  Result := (ImportPage <> nil) and (ImportPage.SelectedValueIndex = 0);
end;

{ A running ShareX is why the import above can succeed and still look broken.

  Windows gives a global hotkey to whichever program registers it FIRST and hands
  the next one a failure code. So on a machine with ShareX up, Modlight imports
  the user's own shortcuts, fails to register every one of them, and the keys
  keep working - they just keep running ShareX. Nothing looks wrong except the
  new product, which is the worst possible way for this to present.

  CheckForMutexes is the right test rather than looking for ShareX.exe: ShareX
  takes that mutex for single-instance mode, so it is held exactly while an
  instance is up, whatever a portable copy has been renamed to. }
function IsShareXRunning(): Boolean;
begin
  Result := CheckForMutexes('{#ShareXMutex}');
end;

var
  ShareXRunningPage: TInputOptionWizardPage;

procedure CreateShareXRunningPage(const AfterID: Integer);
begin
  ShareXRunningPage := CreateInputOptionPage(AfterID,
    'ShareX is running',
    'Two programs cannot hold the same keyboard shortcut.',
    'Windows gives a global shortcut to whichever program asks for it first, and refuses' + #13#10 +
    'the next one. While ShareX is running it holds yours - so Modlight''s capture' + #13#10 +
    'shortcuts will silently do nothing, and your keys will keep opening ShareX.' + #13#10#13#10 +
    'Closing ShareX fixes this session. It comes back at the next restart, because ShareX' + #13#10 +
    'starts with Windows: to settle it for good, uninstall ShareX, or turn off "Run ShareX' + #13#10 +
    'when Windows starts" in ShareX''s own settings.',
    True,   { exclusive - these are two answers to one question }
    False);
  ShareXRunningPage.Add('Close ShareX now (recommended)');
  ShareXRunningPage.Add('Leave ShareX running - I will close it myself');
  { The neutral answer leads on the import page, because copying files is a real
    change to a fresh install. Here it is the other way round: leaving ShareX up
    is the answer that produces the broken state, so the fix leads. }
  ShareXRunningPage.SelectedValueIndex := 0;
end;

function ShouldCloseShareX(): Boolean;
begin
  Result := (ShareXRunningPage <> nil) and (ShareXRunningPage.SelectedValueIndex = 0) and
            IsShareXRunning();
end;

{ "Close ShareX" means ask it to close, not kill it.

  taskkill WITHOUT /f posts WM_CLOSE to the process's windows - the same thing
  clicking its X does - so ShareX gets to write its settings out. /f ends it
  where it stands and throws away whatever it had not saved, and that is another
  product's data, not ours to lose on its behalf.

  ShareX closes to the notification area by default, so the polite ask often
  just hides the window and leaves the process - and the hotkeys - exactly where
  they were. Hence the mutex re-check, and hence asking again before doing
  anything harsher. }
procedure CloseShareX();
var
  ErrorCode, I: Integer;
begin
  Exec('taskkill.exe', '/im ShareX.exe', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);

  for I := 1 to 20 do
  begin
    if not IsShareXRunning() then Exit;
    Sleep(250);
  end;

  if MsgBox('ShareX is still running - it closes to the notification area instead of exiting.' + #13#10#13#10 +
            'End it now? Anything it has not saved will be lost.' + #13#10 +
            'Choose No to leave it alone and close it yourself from its tray icon.',
            mbConfirmation, MB_YESNO) = IDYES then
  begin
    Exec('taskkill.exe', '/f /im ShareX.exe', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
  end;
end;

{ Which external components to fetch. All ticked, each undoable.

  Four features of this application need a file that is not part of it: screen recording and
  GIFs need ffmpeg, the Download tab needs yt-dlp, the background remover needs an ONNX
  model, and the metadata tools need ExifTool. Together they are well over a gigabyte, which
  is why they are not in this installer: the update channel ships the WHOLE setup executable
  for every patch, so anything bundled is not paid once, it is paid again on every update.

  So the question is asked here and the fetching is done by the application, once, on its
  first start - which also means there is exactly ONE piece of code that downloads them, with
  one checksum check in it, instead of a second implementation in Pascal that would have to
  be kept honest separately.

  Sizes are stated because one of these is a gigabyte, and "all ticked by default" is only
  defensible if the person can see what they are agreeing to. Nothing here is irreversible:
  the same list, with the same ticks, lives in Modlight under Settings > Components. }
var
  ComponentsPage: TInputOptionWizardPage;
  ComponentIds: array of String;

{ One row, and its id, added together.

  The id list and the checkbox list have to stay in step - SkippedComponents pairs them by
  index - so nothing gets to add one without the other. Two hand-maintained lists in the
  same order is how an installer ends up writing 'exiftool' when the user unticked the
  model. The ids are the ones in ModlightComponentCatalog and must not drift from them. }
procedure AddComponent(const Id, Caption, Description: String);
var
  Index, Count: Integer;
begin
  Count := GetArrayLength(ComponentIds);
  SetArrayLength(ComponentIds, Count + 1);
  ComponentIds[Count] := Id;

  { AddCheckBox rather than the page's own Add, for the second line: a list of four proper
    nouns tells somebody nothing about what they are turning off. }
  Index := ComponentsPage.CheckListBox.AddCheckBox(Caption, Description, 0, True, True, False, False, nil);

  if Index <> Count then
    MsgBox('Internal error: the components list is out of step.', mbError, MB_OK);
end;

procedure CreateComponentsPage(const AfterID: Integer);
begin
  ComponentsPage := CreateInputOptionPage(AfterID,
    'Extra components',
    'Some features need a file that is not part of Modlight itself.',
    'Untick anything you do not want. Modlight fetches the rest the first time it starts, over' + #13#10 +
    'HTTPS, and checks every file against a published checksum before anything uses it.' + #13#10 +
    'You can add or remove any of them later in Modlight, under Settings and then Components.',
    False,   { four independent choices, not four answers to one question }
    False);

  SetArrayLength(ComponentIds, 0);

  AddComponent('ffmpeg', 'FFmpeg - 68.6 MB',
    'Screen recording, GIFs and video conversion');
  AddComponent('yt-dlp', 'yt-dlp - 18.2 MB',
    'Downloading videos in the Download tab');
  AddComponent('background-model', 'Background removal model - 1.02 GB',
    'Cutting the background out of a screenshot');
  AddComponent('exiftool', 'ExifTool - 10.1 MB',
    'Reading and removing file metadata');
end;

{ The unticked ones, comma separated, for the application to read once and forget.

  The list of NOs rather than the list of YESes, on purpose: a component added in a later
  version of Modlight is in nobody's list, so it starts out wanted for everybody. A list of
  yeses would have frozen every existing installation out of everything added after it. }
function SkippedComponents(): String;
var
  I: Integer;
begin
  Result := '';

  if ComponentsPage = nil then Exit;

  for I := 0 to GetArrayLength(ComponentIds) - 1 do
  begin
    if not ComponentsPage.CheckListBox.Checked[I] then
    begin
      if Result <> '' then Result := Result + ',';
      Result := Result + ComponentIds[I];
    end;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { An update must not ask again, and must not answer for the user either - see
    CurStepChanged, which only writes the value when this page was actually shown. }
  if (ComponentsPage <> nil) and (PageID = ComponentsPage.ID) then
  begin
    Result := IsUpdating;
    Exit;
  end;

  { Nothing to import, or this is an unattended update: do not show the page. }
  if (ImportPage <> nil) and (PageID = ImportPage.ID) then
  begin
    Result := IsUpdating or not ShareXProfileExists;
    Exit;
  end;

  { The user closed ShareX while the wizard was open, or this is an unattended
    update: there is nothing left to ask about. }
  if (ShareXRunningPage <> nil) and (PageID = ShareXRunningPage.ID) then
  begin
    Result := IsUpdating or not IsShareXRunning();
    Exit;
  end;

  Result := False;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    { Written even when nothing was unticked: the presence of the value is what tells the
      application that an installer has just run and it should go and fetch what is missing.
      Not on /UPDATE - the answers from the first install are already in the application's
      own settings by then, and writing an empty list here would re-ask a question the user
      has already answered, possibly differently. }
    if not IsUpdating then
    begin
      RegWriteStringValue(HKEY_CURRENT_USER, 'Software\Modlight\Setup', 'SkipComponents',
        SkippedComponents());
    end;

    if ShouldImportShareX() then
    begin
      ImportShareXSettings();
    end;

    { Before the [Run] entry starts Modlight, which is the moment it tries to
      take the hotkeys. Doing this any later would be doing it too late. }
    if ShouldCloseShareX() then
    begin
      CloseShareX();
    end;
  end;
end;

procedure InitializeWizard;
var
  AfterID: Integer;
begin
  AfterID := wpSelectTasks;

  if ShareXProfileExists then
  begin
    CreateImportPage(AfterID);
    AfterID := ImportPage.ID;
  end;

  { Deliberately after the import question: it is that answer's caveat. The
    offer to copy the shortcuts reads first, then the reason they will not fire
    until ShareX lets go of them. }
  if IsShareXRunning() then
  begin
    CreateShareXRunningPage(AfterID);
    AfterID := ShareXRunningPage.ID;
  end;

  { Last of the questions, and the only one every installation sees. }
  CreateComponentsPage(AfterID);

  if not IsAdmin then
  begin
    WizardForm.DirEdit.Text := ExpandConstant('{userpf}\{#MyAppName}');
  end;
end;

{ Give a Modlight that is on its way out the moment it needs to finish going.

  The app starts this installer itself, with /UPDATE, from inside its updater
  window - so at that instant the process is still alive and still owns the
  single instance mutex, which is the same GUID AppMutex above tests. It exits
  immediately afterwards (ShareX.HelpersLib.ApplicationExit, wired to
  Program.Exit), and by the time SetupLdr has unpacked itself and got this far
  it is normally already gone.

  "Normally" is the word this loop is here to remove. A slow settings write on a
  busy disk is all it takes for Setup to arrive first and put up "Setup has
  detected that Modlight is currently running", in the middle of an update the
  user asked for and thought was finished. That prompt is recoverable - it waits
  for an OK - but it is a confusing thing to show for a second of overlap.

  Only on /UPDATE. Somebody who ran this installer by hand while Modlight is
  open has NOT asked anything to close, so the prompt is exactly right for them
  and this must not swallow it. The cap is fifteen seconds: long enough for any
  honest shutdown, short enough that a Modlight which is not exiting at all
  still reaches the prompt rather than looking hung. }
function InitializeSetup(): Boolean;
var
  I: Integer;
begin
  if IsUpdating() then
  begin
    for I := 1 to 60 do
    begin
      if not CheckForMutexes('{#MyAppId}') then Break;
      Sleep(250);
    end;
  end;

  Result := True;
end;

function InitializeUninstall(): Boolean;
var
  ErrorCode: Integer;
begin
  if CheckForMutexes('{#MyAppId}') then
  begin
    if MsgBox('Uninstall has detected that {#MyAppName} is currently running.' + #13#10#13#10 + 'Would you like to close it?', mbError, MB_YESNO) = IDYES then
    begin
      Exec('taskkill.exe', '/f /im {#MyAppFileName}', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
    end
    else
    begin
      Result := False;
      Exit;
    end;
  end;

  Result := True;
end;
