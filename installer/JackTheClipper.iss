; Windows installer (Inno Setup 6). Built by .github/workflows/release.yml after publishing the app to ..\out:
;   ISCC /DAppVersion=1.2.3 installer\JackTheClipper.iss
#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

[Setup]
AppId={{8F3C2A51-6B0E-4C7D-9A4E-2B1D5E7F9C30}
AppName=Jack the Clipper
AppVersion={#AppVersion}
AppPublisher=parapsychic
AppPublisherURL=https://github.com/parapsychic/JackTheClipper
; Per-user install: no admin prompt
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Jack the Clipper
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\JackTheClipper.exe
OutputDir=Output
OutputBaseFilename=JackTheClipper-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The app is stopped by StopApp below; Windows' restart manager can't close a tray app that hides on close
CloseApplications=no

[Tasks]
Name: autostart; Description: "Start Jack the Clipper when I sign in (keeps the hotkey and sharing ready)"

[Files]
Source: "..\out\JackTheClipper.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Jack the Clipper"; Filename: "{app}\JackTheClipper.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Jack the Clipper"; \
  ValueData: """{app}\JackTheClipper.exe"" --background"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\JackTheClipper.exe"; Description: "Start Jack the Clipper now"; Flags: nowait postinstall skipifsilent

[Code]
// Close a running copy (it saves first) so its exe can be replaced or removed. Notes in %APPDATA% are kept.
procedure StopApp();
var
  Code: Integer;
begin
  if FileExists(ExpandConstant('{app}\JackTheClipper.exe')) then
  begin
    Exec(ExpandConstant('{app}\JackTheClipper.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(1500);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopApp();
  Result := True;
end;
