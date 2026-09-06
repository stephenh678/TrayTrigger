; Inno Setup Script for TrayTrigger
; Generates a lightweight, self-contained Windows Installer (.exe)

#ifndef MyAppVersion
#define MyAppVersion "1.0.0"
#endif

#define MyAppName "TrayTrigger"
#define MyAppPublisher "Steph"
#define MyAppURL "https://github.com/Steph/TrayTrigger"
#define MyAppExeName "TrayTrigger.exe"

[Setup]
AppId={{C8E32D15-3B10-4A99-8D1F-4395A9BF41C2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=LICENSE
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=publish\installer
OutputBaseFilename=TrayTrigger-v{#MyAppVersion}-Setup
SetupIconFile=Assets\app_icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startwithwindows"; Description: "&Start TrayTrigger when Windows starts (recommended)"; GroupDescription: "System Integration:"

[Files]
Source: "publish\bin\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\bin\*.pdb"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; Flags: uninsdeletevalue; Tasks: startwithwindows

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure UpdateAppSettings();
var
  SettingsDir: string;
  SettingsFile: string;
  Lines: TArrayOfString;
  I: Integer;
  StartWithWin: Boolean;
begin
  SettingsDir := ExpandConstant('{localappdata}\TrayTrigger');
  SettingsFile := SettingsDir + '\settings.json';
  StartWithWin := WizardIsTaskSelected('startwithwindows');

  if not DirExists(SettingsDir) then
    ForceDirectories(SettingsDir);

  if FileExists(SettingsFile) then
  begin
    if LoadStringsFromFile(SettingsFile, Lines) then
    begin
      for I := 0 to GetArrayLength(Lines) - 1 do
      begin
        if Pos('"StartWithWindows"', Lines[I]) > 0 then
        begin
          if StartWithWin then
            Lines[I] := '  "StartWithWindows": true,'
          else
            Lines[I] := '  "StartWithWindows": false,';
        end;
      end;
      SaveStringsToFile(SettingsFile, Lines, False);
    end;
  end
  else
  begin
    SetArrayLength(Lines, 6);
    Lines[0] := '{';
    if StartWithWin then
      Lines[1] := '  "StartWithWindows": true,'
    else
      Lines[1] := '  "StartWithWindows": false,';
    Lines[2] := '  "StartMinimizedToTray": true,';
    Lines[3] := '  "AutoCheckForUpdates": true,';
    Lines[4] := '  "GitHubRepository": "Steph/TrayTrigger"';
    Lines[5] := '}';
    SaveStringsToFile(SettingsFile, Lines, False);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    UpdateAppSettings();
  end;
end;
