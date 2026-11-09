; Installer for one architecture, built by scripts/vm/release.ps1:
;   ISCC /DVersion=1.0.0 /DArch=x64 /DSource=<publish folder> /O<output folder> installer\MinerGuru.iss
; Per user, without admin rights. The settings in %LOCALAPPDATA%\MinerGuru stay on uninstall.
#define AppExe "MinerGuru.exe"

[Setup]
AppId={{B82B4540-E152-4F46-B2A8-2343E9FADAE2}
AppName=MinerGuru
AppVersion={#Version}
AppPublisher=MinerGuru
PrivilegesRequired=lowest
DefaultDirName={autopf}\MinerGuru
DefaultGroupName=MinerGuru
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputBaseFilename=MinerGuru-{#Version}-{#Arch}
SetupIconFile=..\src\MinerGuru\Assets\AppIcon.ico
; The app's copy adds the note on the Microsoft components to the MIT license.
LicenseFile=..\src\MinerGuru\Assets\LICENSE.txt
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.19041
#if Arch == "arm64"
ArchitecturesAllowed=arm64
#else
ArchitecturesAllowed=x64compatible
#endif
ArchitecturesInstallIn64BitMode={#Arch == "arm64" ? "arm64" : "x64compatible"}
; PrepareToInstall ends the app itself: the Restart Manager does not reach every running copy.
CloseApplications=no

[Languages]
; The first entry is the fallback when the Windows language is neither English nor German.
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\MinerGuru"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\MinerGuru"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,MinerGuru}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "StopApp"
; The widgets, if they were switched on in the settings: a package registered on this folder.
Filename: "powershell.exe"; Parameters: "-NoProfile -NonInteractive -Command ""Get-AppxPackage MinerGuru.Widgets | Remove-AppxPackage"""; Flags: runhidden; RunOnceId: "RemoveWidgets"

[UninstallDelete]
; Written by the app when the widgets are switched on.
Type: files; Name: "{app}\AppxManifest.xml"

[Code]
// The app may be sitting in the tray, and its files are locked while it runs.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
  Result := '';
end;
