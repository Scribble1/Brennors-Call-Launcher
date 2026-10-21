#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
[Setup]
AppId={{75D86475-2095-4B82-A584-D63A43A9E438}
AppName=Brennor's Call Launcher
AppVersion={#AppVersion}
AppPublisher=Brennor's Call
DefaultDirName={localappdata}\Programs\BrennorsCallLauncher
DefaultGroupName=Brennor's Call
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=BrennorsCall-Launcher-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dark
UninstallDisplayName=Brennor's Call Launcher
UninstallDisplayIcon={app}\app\{#AppVersion}\BrennorsCall.Launcher.exe
AppMutex=Local\BrennorsCallLauncher
CloseApplications=yes
CloseApplicationsFilter=BrennorsCall.Launcher.exe
RestartApplications=no
SetupLogging=yes
DisableProgramGroupPage=yes
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}\app\{#AppVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Brennor's Call Launcher"; Filename: "{app}\app\{#AppVersion}\BrennorsCall.Launcher.exe"
Name: "{autodesktop}\Brennor's Call Launcher"; Filename: "{app}\app\{#AppVersion}\BrennorsCall.Launcher.exe"; Tasks: desktopicon
[Registry]
Root: HKCU; Subkey: "Software\BrennorsCall"; ValueType: string; ValueName: "GameDirectory"; ValueData: "{code:GetGameDirectory}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "BrennorsCallLauncher"; ValueData: """{app}\app\{#AppVersion}\BrennorsCall.Launcher.exe"""; Flags: uninsdeletevalue; Check: HasStartupEntry
[Run]
Filename: "{app}\app\{#AppVersion}\BrennorsCall.Launcher.exe"; Description: "Open Brennor's Call Launcher and install the game"; Flags: nowait postinstall skipifsilent
[Code]
var GamePage: TInputDirWizardPage;
function HasStartupEntry: Boolean;
begin
  Result := RegValueExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'BrennorsCallLauncher');
end;
procedure InitializeWizard;
var Previous: String;
begin
  GamePage := CreateInputDirPage(wpSelectDir, 'Choose your game directory', 'Launcher and game files are installed separately.', 'The launcher downloads and verifies the game on first use. Allow room for a staged update as well as the installed game.', False, '');
  GamePage.Add('Game installation directory:');
  if not RegQueryStringValue(HKCU, 'Software\BrennorsCall', 'GameDirectory', Previous) then Previous := ExpandConstant('{localappdata}\BrennorsCall\Game');
  GamePage.Values[0] := Previous;
end;
function GetGameDirectory(Param: String): String;
begin
  Result := GamePage.Values[0];
end;
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = GamePage.ID then
    if Pos(Lowercase(ExpandConstant('{app}')), Lowercase(GamePage.Values[0])) = 1 then begin
      MsgBox('Choose a game directory separate from the launcher directory.', mbError, MB_OK);
      Result := False;
    end;
end;
