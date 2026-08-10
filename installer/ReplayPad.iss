; Inno Setup script for ReplayPad
; Build: ISCC.exe ReplayPad.iss   (expects the app published to ..\publish)

#define MyAppName "ReplayPad"
#define MyAppVersion "1.3.2"
#define MyAppPublisher "TareqAli-CS"
#define MyAppURL "https://github.com/TareqAli-CS/ReplayPad"
#define MyAppExeName "ReplayPad.exe"

[Setup]
AppId={{7A3F9C41-5E82-4B1D-9F60-2C8A41D7B3E9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
; Per-user install: no admin prompt, lands in %LocalAppData%\Programs
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\ReplayPad
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=ReplayPad-Setup-{#MyAppVersion}
SetupIconFile=..\ReplayPad\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[InstallDelete]
; Leftovers from before the rename (AudioReplayBuffer → ReplayPad).
Type: files; Name: "{app}\AudioReplayBuffer.exe"
Type: files; Name: "{userprograms}\Audio Replay Buffer.lnk"
Type: files; Name: "{userdesktop}\Audio Replay Buffer.lnk"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Make sure the app isn't running while files are removed
Filename: "{cmd}"; Parameters: "/C taskkill /im {#MyAppExeName} /f"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
// A pre-rename instance may be running during the upgrade; stop it so the
// old exe can be deleted.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
    Exec(ExpandConstant('{cmd}'), '/C taskkill /im AudioReplayBuffer.exe /f', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
