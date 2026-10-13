; VU Studio installer - Inno Setup 6.3 or newer (also compiles with Inno Setup 7).
;
; Build, from the repository root:
;   ISCC.exe /DAppVersion=0.1.0 /DStageDir=C:\path\to\stage installer\VuStudio.iss
;
;   AppVersion  Product version, e.g. 0.2.0 or 0.2.0-rc.1 (default 0.0.0-dev).
;   StageDir    Folder holding the published Server + Tray (see .github\workflows\release.yml
;               and build\publish.ps1). Default: ..\artifacts\stage
;
; This file must stay UTF-8 *with* BOM: without it Inno Setup reads the script as ANSI and the
; Italian accented letters below turn into garbage. installer\.editorconfig keeps editors from
; stripping the BOM.
;
; What Setup does, in order:
;   1. Close the tray app, stop and delete an existing "VUStudio" service  (PrepareToInstall)
;   2. Copy the files                                                      ([Files])
;   3. Create the service, set description + failure actions, start it    (CurStepChanged)
;   4. Register the tray app to start at logon for all users               ([Registry])
;   5. Offer to launch the tray app                                        ([Run], postinstall)
; Uninstall reverses this and asks before deleting %ProgramData%\VU Studio.

#if VER < EncodeVer(6, 3, 0)
  #error Inno Setup 6.3 or newer is required (ArchitecturesAllowed=x64compatible).
#endif

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef StageDir
  #define StageDir "..\artifacts\stage"
#endif

; VersionInfoVersion must be numeric (a.b.c.d): drop any "+build" and then any "-prerelease"
; suffix. The build metadata goes first because it may itself contain dashes.
#define PlusPos Pos("+", AppVersion)
#if PlusPos > 0
  #define NoBuild Copy(AppVersion, 1, PlusPos - 1)
#else
  #define NoBuild AppVersion
#endif
#define DashPos Pos("-", NoBuild)
#if DashPos > 0
  #define VersionCore Copy(NoBuild, 1, DashPos - 1)
#else
  #define VersionCore NoBuild
#endif

#define AppName      "VU Studio"
#define AppPublisher "VU Studio contributors"
#define AppUrl       "https://github.com/blinkone/vudials"
#define ServiceName  "VUStudio"
#define ServerExe    "VuStudio.Server.exe"
#define TrayExe      "VuStudio.exe"
#define DataFolder   "VU Studio"

[Setup]
; Fixed forever: Setup recognises an existing installation (and upgrades it) by this GUID.
; The leading "{{" is Inno's escape for a literal "{".
AppId={{247202CF-D213-4579-8832-0B4415585B0B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=Copyright (C) 2026 {#AppPublisher}
VersionInfoVersion={#VersionCore}.0
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
VersionInfoCopyright=Copyright (C) 2026 {#AppPublisher}

; 64-bit only. x64compatible also admits Windows 11 on Arm, which runs x64 code.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; The Tray targets Windows 10 2004 (10.0.19041).
MinVersion=10.0.19041
; Creating a service and writing HKLM needs administrator rights.
PrivilegesRequired=admin

DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#TrayExe}

OutputDir=..\artifacts\installer
OutputBaseFilename=VuStudio-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto
SetupMutex=VuStudioSetupMutex
; A log in %TEMP% makes service problems diagnosable after the fact.
SetupLogging=yes
; Setup stops the service and closes the tray itself (see [Code]); no Restart Manager prompts.
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[InstallDelete]
; Vite emits content-hashed file names, so drop the previous web UI before copying the new one.
Type: filesandordirs; Name: "{app}\wwwroot"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#TrayExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#TrayExe}"; Tasks: desktopicon

[Registry]
; Tray app starts at logon for every user. The value is removed again on uninstall.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#TrayExe}"""; Flags: uninsdeletevalue

[Run]
; runasoriginaluser: start the tray as the signed-in user, not elevated. It runs the user's
; scripts, so it must never inherit Setup's administrator token.
Filename: "{app}\{#TrayExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

; The service is registered from [Code] rather than from [Run]/[UninstallRun]: sc.exe needs
; nested quotes in binPath, and [Code] can also wait for the service to stop and check results.

[CustomMessages]
english.VuServerPageCaption=Before you start
english.VuServerPageDescription=Only one program can use the VU1 hub at a time
english.VuServerPageText=VU Studio talks directly to the VU1 hub over its USB serial port, and only one program can use that port at a time.%n%nThe official VU-Server must therefore not run together with VU Studio. Close it, and turn off its automatic start, before using VU Studio.%n%nIf VU-Server is running when you click Next, Setup offers to close it for you.
english.VuServerRunning=The official VU-Server (%1) is running. VU Studio cannot open the hub's serial port while it is running.%n%nDo you want Setup to close it now?
english.WebView2Missing=The Microsoft Edge WebView2 Runtime was not found on this PC. The VU Studio desktop window needs it, although the web panel still works in your browser.%n%nWindows 10 and 11 normally include the runtime. You can download it from:%n%1%n%nOpen the download page now?
english.ServiceInstallFailed=The VU Studio service could not be created or started. VU Studio will not work until it is running.%n%nSee the Setup log for details, or run Setup again as administrator.
english.ServiceDescription=Drives Streacom VU1 dials: reads sensors, renders the faces and controls needle and backlight.
english.DeleteDataPrompt=Do you also want to delete the VU Studio data (settings, profiles, uploaded images and history)?%n%n%1%n%nChoose No to keep it, for example if you plan to reinstall.

italian.VuServerPageCaption=Prima di iniziare
italian.VuServerPageDescription=Un solo programma alla volta può usare l'hub VU1
italian.VuServerPageText=VU Studio comunica direttamente con l'hub VU1 tramite la porta seriale USB, e questa porta può essere usata da un solo programma alla volta.%n%nIl VU-Server ufficiale non deve quindi essere in esecuzione insieme a VU Studio: va chiuso, e il suo avvio automatico va disattivato, prima di usare VU Studio.%n%nSe VU-Server è in esecuzione quando si fa clic su Avanti, il programma di installazione propone di chiuderlo.
italian.VuServerRunning=Il VU-Server ufficiale (%1) è in esecuzione. Finché resta aperto, VU Studio non può usare la porta seriale dell'hub.%n%nChiuderlo ora?
italian.WebView2Missing=Microsoft Edge WebView2 Runtime non è stato trovato su questo PC. La finestra desktop di VU Studio ne ha bisogno, ma il pannello web continua a funzionare nel browser.%n%nWindows 10 e 11 di norma lo includono. Si può scaricare da:%n%1%n%nAprire ora la pagina di download?
italian.ServiceInstallFailed=Non è stato possibile creare o avviare il servizio VU Studio. VU Studio non funzionerà finché il servizio non è in esecuzione.%n%nPer i dettagli consultare il log di installazione, oppure eseguire di nuovo il programma come amministratore.
italian.ServiceDescription=Gestisce i quadranti Streacom VU1: legge i sensori, disegna le facce e controlla ago e retroilluminazione.
italian.DeleteDataPrompt=Eliminare anche i dati di VU Studio (impostazioni, profili, immagini caricate e cronologia)?%n%n%1%n%nSelezionare No per conservarli, ad esempio se si intende reinstallare il programma.

[Code]
const
  ServiceName    = '{#ServiceName}';
  ServerExeName  = '{#ServerExe}';
  TrayExeName    = '{#TrayExe}';
  DataFolderName = '{#DataFolder}';
  // Evergreen WebView2 Runtime: its version ("pv") is registered under this EdgeUpdate client.
  WebView2Client = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Url    = 'https://developer.microsoft.com/microsoft-edge/webview2/';

var
  VuServerPage: TOutputMsgWizardPage;

// ---------- helpers shared by Setup and the uninstaller ----------

// Runs a program hidden, waits for it and returns its exit code (-1 if it could not be started).
function RunHidden(const FileName, Params: String): Integer;
var
  ResultCode: Integer;
begin
  if Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode
  else
    Result := -1;
  Log(Format('Exec "%s" %s -> %d', [FileName, Params, Result]));
end;

function SysExe(const Name: String): String;
begin
  Result := ExpandConstant('{sys}\') + Name;
end;

// "sc query" exits with 0 when the service is installed and with 1060 when it is not.
function ServiceExists: Boolean;
begin
  Result := (RunHidden(SysExe('sc.exe'), 'query ' + ServiceName) = 0);
end;

// "sc query" prints "STATE : 1  STOPPED" for a stopped service, and find exits with 0 on a match.
function ServiceIsStopped: Boolean;
begin
  Result := (RunHidden(ExpandConstant('{cmd}'), '/C sc query ' + ServiceName + ' | find "STOPPED" >nul') = 0);
end;

procedure StopService;
var
  I: Integer;
begin
  // Ask the service control manager to stop it, then wait (up to 30 s) for the shutdown to finish.
  RunHidden(SysExe('sc.exe'), 'stop ' + ServiceName);
  for I := 1 to 60 do
  begin
    if ServiceIsStopped then
      Exit;
    Sleep(500);
  end;
  // Hung or stuck while starting: end the process so that its files are released.
  Log('The service did not stop in time; terminating ' + ServerExeName);
  RunHidden(SysExe('taskkill.exe'), '/F /IM ' + ServerExeName);
end;

procedure DeleteService;
var
  I: Integer;
begin
  RunHidden(SysExe('sc.exe'), 'delete ' + ServiceName);
  // The entry disappears once the last handle to it is closed; wait for that (up to 10 s).
  for I := 1 to 40 do
  begin
    if not ServiceExists then
      Exit;
    Sleep(250);
  end;
  Log('The service entry is still marked for deletion');
end;

procedure RemoveExistingService;
begin
  if not ServiceExists then
  begin
    Log('Service ' + ServiceName + ' is not installed');
    Exit;
  end;
  StopService;
  DeleteService;
end;

procedure CloseTray;
begin
  // Politely first (WM_CLOSE), then forcefully. taskkill exits with 128 when nothing matches.
  if RunHidden(SysExe('taskkill.exe'), '/IM ' + TrayExeName) = 0 then
    Sleep(1500);
  RunHidden(SysExe('taskkill.exe'), '/F /IM ' + TrayExeName);
end;

// ---------- installation ----------

// Creates the service, sets its description and failure actions, and starts it.
function CreateAndStartService: Boolean;
var
  Code, Attempt: Integer;
begin
  Result := False;

  // sc.exe syntax quirks: every "name=" is followed by a space, and binPath must contain the
  // quoted executable path (the installation folder may contain spaces) followed by its
  // arguments, all wrapped in one more pair of quotes. The result of the expression below is:
  //   create VUStudio binPath= "\"C:\Program Files\VU Studio\VuStudio.Server.exe\" --service"
  //     start= delayed-auto DisplayName= "VU Studio"
  for Attempt := 1 to 5 do
  begin
    Code := RunHidden(SysExe('sc.exe'),
      'create ' + ServiceName +
      ' binPath= "\"' + ExpandConstant('{app}') + '\' + ServerExeName + '\" --service"' +
      ' start= delayed-auto' +
      ' DisplayName= "{#AppName}"');
    // 1072: a previous service of that name is still marked for deletion. Wait and retry.
    if Code <> 1072 then
      Break;
    Sleep(1000);
  end;
  if Code <> 0 then
  begin
    Log(Format('sc create failed with exit code %d', [Code]));
    Exit;
  end;

  RunHidden(SysExe('sc.exe'), 'description ' + ServiceName + ' "' + CustomMessage('ServiceDescription') + '"');

  // First, second and every further failure: restart after 5 seconds. The failure counter is
  // reset after one day (86400 s) without failures.
  RunHidden(SysExe('sc.exe'), 'failure ' + ServiceName + ' reset= 86400 actions= restart/5000/restart/5000/restart/5000');

  // 1056: the service is already running.
  Code := RunHidden(SysExe('sc.exe'), 'start ' + ServiceName);
  Result := (Code = 0) or (Code = 1056);
  if not Result then
    Log(Format('sc start failed with exit code %d', [Code]));
end;

function IsProcessRunning(const ImageName: String): Boolean;
begin
  // tasklist always exits with 0; find exits with 0 only when a line matches.
  Result := (RunHidden(ExpandConstant('{cmd}'),
    '/C tasklist /NH /FI "IMAGENAME eq ' + ImageName + '" | find /I "' + ImageName + '" >nul') = 0);
end;

// Returns the image name of a running official VU-Server, or '' when there is none.
// Windows image names are case-insensitive, so the second spelling is only belt and braces.
function RunningVuServer: String;
begin
  if IsProcessRunning('VU-Server.exe') then
    Result := 'VU-Server.exe'
  else if IsProcessRunning('vu-server.exe') then
    Result := 'vu-server.exe'
  else
    Result := '';
end;

function WebView2VersionRegistered(const RootKey: Integer; const SubKey: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

// The runtime registers itself machine-wide (32-bit view of HKLM) or, for per-user installs, in HKCU.
function IsWebView2Installed: Boolean;
begin
  Result := WebView2VersionRegistered(HKLM, 'SOFTWARE\WOW6432Node\' + WebView2Client)
         or WebView2VersionRegistered(HKCU, 'Software\' + WebView2Client);
end;

procedure CheckWebView2;
var
  ErrorCode: Integer;
begin
  if IsWebView2Installed then
    Exit;
  Log('The WebView2 Runtime was not found');
  // Not bundled on purpose: Windows 10/11 normally have it. Silent installs just log the fact.
  if SuppressibleMsgBox(FmtMessage(CustomMessage('WebView2Missing'), [WebView2Url]), mbInformation, MB_YESNO, IDNO) = IDYES then
    ShellExecAsOriginalUser('open', WebView2Url, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure InitializeWizard;
begin
  // The note about the official VU-Server, right after the license.
  VuServerPage := CreateOutputMsgPage(wpLicense,
    CustomMessage('VuServerPageCaption'),
    CustomMessage('VuServerPageDescription'),
    CustomMessage('VuServerPageText'));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Running: String;
begin
  Result := True;
  if CurPageID = VuServerPage.ID then
  begin
    Running := RunningVuServer;
    if Running <> '' then
    begin
      if MsgBox(FmtMessage(CustomMessage('VuServerRunning'), [Running]), mbConfirmation, MB_YESNO) = IDYES then
        RunHidden(SysExe('taskkill.exe'), '/F /IM ' + Running);
    end;
  end;
end;

// Runs after "Install" is clicked and before any file is copied.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseTray;
  RemoveExistingService;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not CreateAndStartService then
      SuppressibleMsgBox(CustomMessage('ServiceInstallFailed'), mbError, MB_OK, IDOK);
    CheckWebView2;
  end;
end;

// ---------- uninstallation ----------

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Before Inno removes any file: the tray and the service would keep them locked.
    CloseTray;
    RemoveExistingService;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\') + DataFolderName;
    if DirExists(DataDir) then
    begin
      // Default is No, also for silent uninstalls: user data is never deleted by surprise.
      if SuppressibleMsgBox(FmtMessage(CustomMessage('DeleteDataPrompt'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
      begin
        if not DelTree(DataDir, True, True, True) then
          Log('Could not delete ' + DataDir);
      end;
    end;
  end;
end;
