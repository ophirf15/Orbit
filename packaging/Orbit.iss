; Orbit Windows installer (classic wizard)
; Built by scripts/pack-installer.ps1 — do not compile by hand without publishing first.
;
; ADR 0019 keeps MSIX + App Installer as the update lane.
; This Inno Setup package is the takeaway first-install wizard for a clean PC.

#define MyAppName "Orbit"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "Orbit"
#define MyAppURL "https://github.com/ophirf15/Orbit"
#define MyAppExeName "Orbit.App.exe"

#ifndef PublishDir
  #define PublishDir "..\artifacts\installer\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{A7C3E2F1-4B8D-4E9A-9C2F-1D6E8A0B5C47}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=
InfoBeforeFile=
OutputDir={#OutputDir}
OutputBaseFilename=Orbit-Setup-{#MyAppVersion}
SetupIconFile=..\src\Orbit.App\Assets\AppIcon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
; Host + MCP are separate processes; Restart Manager alone often leaves them locking DLLs
; under {app} and %LocalAppData%\Orbit\orbit-mcp (Hermes). PrepareToInstall taskkills them.
; Avoid CloseApplications=force — it can abort silent upgrades when elevation races the App exit.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Published self-contained WinUI app + Core Host + orbit-mcp (Hermes MCP bridge) + Outlook launcher
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Also stage MCP under the installing user's LocalAppData so Hermes can launch immediately
; even before the first Connect (Connect still re-syncs from {app}\orbit-mcp on upgrade).
Source: "{#PublishDir}\orbit-mcp\*"; DestDir: "{localappdata}\Orbit\orbit-mcp"; Flags: ignoreversion recursesubdirs createallsubdirs
; Stage Outlook launcher under LocalAppData so Settings → Install works before first launch.
; ({app}\outlook-launcher is already included via PublishDir\* above.)
Source: "{#PublishDir}\outlook-launcher\*"; DestDir: "{localappdata}\Orbit\OutlookLauncher"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\AppIcon.ico"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\AppIcon.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure KillOrbitProcesses;
var
  ResultCode: Integer;
  Cmd: String;
begin
  { Multi-pass kill — Hermes often respawns Orbit.Mcp and holds clrjit.dll under LocalAppData. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Orbit.App.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Orbit.Core.Host.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Orbit.Mcp.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Orbit.Mcp.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Orbit.Core.Host.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { Command-line match catches MCP launched via `dotnet` / Hermes when image name differs. }
  Cmd := '-NoProfile -NonInteractive -Command "Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and (($_.CommandLine -like ''*Orbit.Mcp*'') -or ($_.CommandLine -like ''*\Orbit\orbit-mcp\*'') -or ($_.ExecutablePath -like ''*\Orbit\orbit-mcp\*'') -or ($_.ExecutablePath -like ''*\Program Files\Orbit\*'')) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"';
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Cmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(2000);
end;

procedure QuarantineLocalMcp;
var
  ResultCode: Integer;
  McpDir, BakDir, Cmd: String;
begin
  { If orbit-mcp is still locked after kill, move it aside so Files: can create a fresh tree. }
  McpDir := ExpandConstant('{localappdata}\Orbit\orbit-mcp');
  if DirExists(McpDir) then
  begin
    BakDir := McpDir + '.old.' + GetDateTimeString('yyyymmddhhnnss', #0, #0);
    Cmd := '/c move /Y "' + McpDir + '" "' + BakDir + '"';
    Exec(ExpandConstant('{cmd}'), Cmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure UnblockOrbitFiles;
var
  ResultCode: Integer;
  Cmd: String;
begin
  { Strip Mark-of-the-Web (separate from file-lock unlock). }
  Cmd := '-NoProfile -NonInteractive -Command "Get-ChildItem -LiteralPath ''' + ExpandConstant('{app}') + ''' -Recurse -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue; Get-ChildItem -LiteralPath ''' + ExpandConstant('{localappdata}\Orbit') + ''' -Recurse -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue"';
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Cmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  NeedsRestart := False;
  KillOrbitProcesses;
  QuarantineLocalMcp;
  KillOrbitProcesses;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    UnblockOrbitFiles;
end;

function InitializeUninstall(): Boolean;
begin
  KillOrbitProcesses;
  Result := True;
end;
