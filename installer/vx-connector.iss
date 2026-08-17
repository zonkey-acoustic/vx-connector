; Inno Setup script for VX Connector.
;
; Per-user by default: installs to %LOCALAPPDATA%\Programs\VxConnector with no UAC
; prompt. That location is deliberate, not just convenient —
;
;   * the install directory stays writable by the app, so the in-app updater can
;     replace its own exe without elevation (see UpdateApplier), and
;   * GspConnectStub can hard-link GSPconnect.exe beside the exe instead of falling
;     back to %LOCALAPPDATA%\VxConnector\stub, which on a cross-volume layout means a
;     full copy of a ~78 MB binary.
;
; Build:
;   ISCC.exe /DMyAppVersion=1.5.2 installer\vx-connector.iss
; expects the published exe at installer\payload\vx-connector.exe.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName "VX Connector"
#define MyAppExeName "vx-connector.exe"
#define MyAppPublisherUrl "https://github.com/zonkey-acoustic/vx-connector"

[Setup]
; Never change AppId — it is the upgrade identity. A different value would install
; alongside the existing copy instead of over it, leaving two tray icons fighting over
; :12321. UpdateApplier also derives the uninstall key from this GUID.
AppId={{8F3A7C21-5D64-4E9B-9C0A-2B7E14D6A3F5}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppPublisherURL={#MyAppPublisherUrl}
AppSupportURL={#MyAppPublisherUrl}/issues
AppUpdatesURL={#MyAppPublisherUrl}/releases

DefaultDirName={localappdata}\Programs\VxConnector
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Points at the exe rather than a copy of the .ico, so Add/Remove Programs picks up
; whatever icon the installed binary carries — including after a self-update swaps it.
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

; The setup binary's own icon. Sourced from the app project so there is one .ico in
; the repo; ISCC resolves it relative to this script.
SetupIconFile=..\dotnet\vx-connector.ico

; "lowest" keeps the common path prompt-free; the override lets an admin pick a
; per-machine location if they really want one. Note the in-app updater detects a
; non-writable install directory and hands off to the release page instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\dist
OutputBaseFilename=VxConnectorSetup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "startupentry"; Description: "Start {#MyAppName} when I sign in"; Flags: unchecked

[Files]
Source: "payload\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Same key, value name and quoted-path format that StartupRegistration writes, so the
; tray menu's "Start with Windows" reads back as ticked. No mode flag: the saved
; preference decides the mode at login.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "VX Connector"; \
    ValueData: """{app}\{#MyAppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: startupentry

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The GSPconnect stub the app hard-links beside itself at runtime, and the fallback
; staging directory it uses when the install directory isn't writable. Neither is
; tracked by the installer, so both need removing by hand.
Type: files; Name: "{app}\GSPconnect.exe"
Type: filesandordirs; Name: "{localappdata}\VxConnector\stub"
Type: filesandordirs; Name: "{localappdata}\VxConnector\update"

[Code]
{ VX Connector has no mutex; its own single-instance handling is kill-based (see
  Program.KillOlderInstances), so mirror that rather than asking Inno to detect a
  running app it can't see.

  /T is what takes the GSPconnect.exe stub *child* with it. Killing by the image name
  GSPconnect.exe would be wrong: that could be a real licensed GSPconnect, and PTL's
  :12321 lane depends on it. }
procedure StopRunningInstance;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM vx-connector.exe',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  { A non-zero exit just means nothing was running. }
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningInstance;
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningInstance;
  Result := True;
end;
