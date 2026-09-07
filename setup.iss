; Inno Setup Script for TrayTrigger
; Generates a lightweight, self-contained Windows Installer (.exe)

#ifndef MyAppVersion
#define MyAppVersion "1.0.0"
#endif

#define MyAppName "TrayTrigger"
#define MyAppPublisher "stephenh678"
#define MyAppURL "https://github.com/stephenh678/TrayTrigger"
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
AppMutex=TrayTrigger_SingleInstance_Mutex
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
  SettingsDir := ExpandConstant('{userappdata}\TrayTrigger');
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
    Lines[4] := '  "GitHubRepository": "stephenh678/TrayTrigger"';
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

var
  DeleteUserData: Boolean;

function InitializeUninstall(): Boolean;
var
  UninstallForm: TSetupForm;
  InfoLabel: TNewStaticText;
  DeleteDataCheckBox: TNewCheckBox;
  OKButton, CancelButton: TNewButton;
begin
  Result := True;
  DeleteUserData := False;

  UninstallForm := CreateCustomForm(ScaleX(420), ScaleY(160), False, True);
  try
    UninstallForm.Caption := 'Uninstall TrayTrigger';

    InfoLabel := TNewStaticText.Create(UninstallForm);
    InfoLabel.Parent := UninstallForm;
    InfoLabel.Left := ScaleX(16);
    InfoLabel.Top := ScaleY(16);
    InfoLabel.Width := UninstallForm.ClientWidth - ScaleX(32);
    InfoLabel.AutoSize := False;
    InfoLabel.WordWrap := True;
    InfoLabel.Height := ScaleY(60);
    InfoLabel.Caption := 'Your game library, settings, and cached artwork are stored separately from the program files and are kept by default so a future reinstall picks up where you left off.';

    DeleteDataCheckBox := TNewCheckBox.Create(UninstallForm);
    DeleteDataCheckBox.Parent := UninstallForm;
    DeleteDataCheckBox.Left := ScaleX(16);
    DeleteDataCheckBox.Top := InfoLabel.Top + InfoLabel.Height + ScaleY(8);
    DeleteDataCheckBox.Width := UninstallForm.ClientWidth - ScaleX(32);
    DeleteDataCheckBox.Caption := 'Also delete my settings, game library, and cached artwork';
    DeleteDataCheckBox.Checked := False;

    OKButton := TNewButton.Create(UninstallForm);
    OKButton.Parent := UninstallForm;
    OKButton.Width := ScaleX(75);
    OKButton.Height := ScaleY(23);
    OKButton.Left := UninstallForm.ClientWidth - ScaleX(16) - ScaleX(75) - ScaleX(8) - ScaleX(75);
    OKButton.Top := UninstallForm.ClientHeight - ScaleY(16) - OKButton.Height;
    OKButton.Caption := 'Uninstall';
    OKButton.ModalResult := mrOk;
    OKButton.Default := True;

    CancelButton := TNewButton.Create(UninstallForm);
    CancelButton.Parent := UninstallForm;
    CancelButton.Width := ScaleX(75);
    CancelButton.Height := ScaleY(23);
    CancelButton.Left := UninstallForm.ClientWidth - ScaleX(16) - ScaleX(75);
    CancelButton.Top := OKButton.Top;
    CancelButton.Caption := 'Cancel';
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;

    if UninstallForm.ShowModal() = mrCancel then
      Result := False
    else
      DeleteUserData := DeleteDataCheckBox.Checked;
  finally
    UninstallForm.Free();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDataDir: string;
  LocalAppDataDir: string;
begin
  if (CurUninstallStep = usPostUninstall) and DeleteUserData then
  begin
    AppDataDir := ExpandConstant('{userappdata}\TrayTrigger');
    LocalAppDataDir := ExpandConstant('{localappdata}\TrayTrigger');

    if DirExists(AppDataDir) then
      DelTree(AppDataDir, True, True, True);

    if DirExists(LocalAppDataDir) then
      DelTree(LocalAppDataDir, True, True, True);
  end;
end;
