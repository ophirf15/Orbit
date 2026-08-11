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
CloseApplications=force
RestartApplications=no
; Host + MCP are separate processes; Restart Manager alone often leaves them locking DLLs
; under {app} and %LocalAppData%\Orbit\orbit-mcp (Hermes).

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
begin
  { App / Host / MCP can each lock setup destinations (Program Files + LocalAppData). }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Orbit.App.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Orbit.Core.Host.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Orbit.Mcp.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  NeedsRestart := False;
  KillOrbitProcesses;
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  KillOrbitProcesses;
  Result := True;
end;
