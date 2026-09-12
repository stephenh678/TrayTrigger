; Inno Setup Script for TrayTrigger
; Generates a lightweight, self-contained Windows Installer (.exe)

#ifndef MyAppVersion
#define MyAppVersion "1.0.0"
#endif

; VersionInfoVersion must be numeric: strip any SemVer pre-release suffix ("1.3.9-beta.1").
#if Pos("-", MyAppVersion) > 0
  #define MyAppNumericVersion Copy(MyAppVersion, 1, Pos("-", MyAppVersion) - 1)
#else
  #define MyAppNumericVersion MyAppVersion
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
AppCopyright=Copyright (c) 2026 Steph
VersionInfoCompany={#MyAppPublisher}
VersionInfoCopyright=Copyright (c) 2026 Steph
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup
; Setup.exe carries the same product/version metadata as TrayTrigger.exe so the code-signing
; service's file-metadata restrictions (product name) accept both binaries.
VersionInfoVersion={#MyAppNumericVersion}
VersionInfoProductTextVersion={#MyAppVersion}
AppMutex=TrayTrigger_SingleInstance_Mutex
SetupMutex=TrayTrigger_Setup_Mutex
; The published exe is win-x64 self-contained .NET 10: refuse 32-bit Windows outright and
; require Windows 10 1809 (the floor the README promises).
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=LICENSE
; Per-user only, and deliberately no PrivilegesRequiredOverridesAllowed. Offering "install for
; all users" showed admins a mode this script cannot actually deliver: DefaultDirName above is
; {localappdata}, [Icons] uses {userprograms} and [Registry] writes HKCU, so an all-users install
; still lands inside the installing user's profile - while the uninstall entry went to HKLM and
; the desktop shortcut to the Public desktop, where other users click through to an exe they have
; no permission to read.
PrivilegesRequired=lowest
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
; Skipped on an in-app update (/UPDATE): the app owns this value once installed, and re-writing
; it from the remembered task would undo a user who turned "Start with Windows" off in Settings.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; Flags: uninsdeletevalue; Tasks: startwithwindows; Check: not IsUpdate

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; In-app updates run Setup silently, so the postinstall checkbox above never shows; bring the
; app back up ourselves.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser; Check: IsUpdate

[Code]
const
  RunKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run';
  StartupApprovedKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
  AppMutexName = 'TrayTrigger_SingleInstance_Mutex';
  // Must match SystemTweaksService.UltimatePlanName / BalancedPlanGuid.
  PowerSchemesKeyPath = 'SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes';
  UltimatePlanName = 'Ultimate Plan - TrayTrigger';
  BalancedPlanGuid = '381b4222-f694-41f0-9685-ff5bb260df2e';

function CmdLineParamExists(const Value: string): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Value) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

// The in-app updater launches Setup as "/SILENT /NORESTART /SP- /SUPPRESSMSGBOXES /UPDATE".
function IsUpdate(): Boolean;
begin
  Result := CmdLineParamExists('/UPDATE');
end;

function StartupEntryExists(): Boolean;
begin
  Result := RegValueExists(HKCU, RunKeyPath, '{#MyAppName}');
end;

function UninstallKeyPath(): string;
begin
  Result := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\'
    + ExpandConstant('{#SetupSetting("AppId")}') + '_is1';
end;

function PreviousInstallExists(): Boolean;
begin
  Result := RegKeyExists(HKCU, UninstallKeyPath()) or RegKeyExists(HKLM, UninstallKeyPath());
end;

// Setup used to offer an "install for all users" mode, which put the uninstall entry in HKLM
// while installing into the user's own profile anyway. Now that Setup is always per-user, that
// entry is left behind pointing at an uninstaller this install is about to replace - so Apps &
// Features would list TrayTrigger twice, one of them dead. Clearing it needs admin rights, which
// a per-user Setup does not have.
function LegacyAllUsersInstallExists(): Boolean;
begin
  Result := RegKeyExists(HKLM, UninstallKeyPath());
end;

procedure RemoveLegacyAllUsersEntry();
begin
  if not LegacyAllUsersInstallExists() then Exit;

  if IsAdmin() and RegDeleteKeyIncludingSubkeys(HKLM, UninstallKeyPath()) then
  begin
    Log('Removed the legacy all-users uninstall entry from HKLM.');
    // Its desktop shortcut went to the Public desktop, where this install's own per-user
    // shortcut cannot replace it.
    DeleteFile(ExpandConstant('{commondesktop}\{#MyAppName}.lnk'));
  end
  else
    Log('An all-users install is registered in HKLM but Setup is not elevated; leaving it for the user to remove.');
end;

// Removes the Run entry the app (or a previous Setup) registered, plus the Task Manager
// "Startup" enable/disable flag that Windows keeps alongside it.
procedure RemoveStartupEntry();
begin
  if RegValueExists(HKCU, RunKeyPath, '{#MyAppName}') then
    RegDeleteValue(HKCU, RunKeyPath, '{#MyAppName}');
  if RegValueExists(HKCU, StartupApprovedKeyPath, '{#MyAppName}') then
    RegDeleteValue(HKCU, StartupApprovedKeyPath, '{#MyAppName}');
end;

// %TEMP%\TrayTriggerUpdates is where the in-app updater downloads installers.
procedure RemoveDownloadedInstallers();
var
  Dir: string;
begin
  Dir := AddBackslash(GetTempDir()) + 'TrayTriggerUpdates';
  if DirExists(Dir) then
    DelTree(Dir, True, True, True);
end;

// TrayTrigger.exe is a single-file self-contained build, so the .NET host unpacks its native
// libraries to %TEMP%\.net\TrayTrigger on every launch and never cleans up. That's ~100 MB of
// cache for an exe that no longer exists, so it goes whether or not user data was opted into.
procedure RemoveBundleExtractionCache();
var
  Dir: string;
begin
  Dir := AddBackslash(GetTempDir()) + '.net\{#MyAppName}';
  if DirExists(Dir) then
    DelTree(Dir, True, True, True);
end;

// Inno only removes the files it logged at install time. Anything else in {app} - an exe that
// was locked during an in-app update and got renamed for reboot-deletion, a stray log - keeps
// the folder alive, and the next Setup then warns that the directory already exists. Name the
// files that can legitimately be ours rather than emptying {app} wholesale: the user can Browse
// to any folder on the Select Destination page, including one holding their own files.
procedure RemoveAppFolderLeftovers();
var
  Dir: string;
  I: Integer;
  Leftovers: TArrayOfString;
  FindRec: TFindRec;
begin
  Dir := AddBackslash(ExpandConstant('{app}'));
  if not DirExists(Dir) then Exit;

  SetArrayLength(Leftovers, 4);
  Leftovers[0] := '{#MyAppExeName}';
  Leftovers[1] := 'TrayTrigger.pdb';
  Leftovers[2] := 'README.md';
  Leftovers[3] := 'LICENSE';
  for I := 0 to GetArrayLength(Leftovers) - 1 do
    if FileExists(Dir + Leftovers[I]) then
      DeleteFile(Dir + Leftovers[I]);

  // Any log written next to the exe (a crash handler running before %LocalAppData% is usable).
  if FindFirst(Dir + '*.log', FindRec) then
  begin
    try
      repeat
        DeleteFile(Dir + FindRec.Name);
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;

  // Succeeds only once unins000.* are gone, which Inno does after this step; the call is a
  // no-op until then and Inno removes the empty folder itself.
  RemoveDir(Dir);
end;

// The app duplicates Windows' hidden "Ultimate Performance" scheme into one of its own the
// first time a performance profile asks for it (SystemTweaksService.CreateUltimateTrayTriggerPlan).
// Nothing else ever deletes it, so uninstalling without this leaves a TrayTrigger-made power
// plan in Settings forever - and if it is the active one, the machine stays on it.
procedure RemoveUltimatePowerPlan();
var
  SchemeGuids: TArrayOfString;
  I, ResultCode: Integer;
  FriendlyName, ActiveGuid: string;
begin
  if not RegGetSubkeyNames(HKLM, PowerSchemesKeyPath, SchemeGuids) then Exit;

  ActiveGuid := '';
  if RegQueryStringValue(HKLM, PowerSchemesKeyPath, 'ActivePowerScheme', ActiveGuid) then
    ActiveGuid := RemoveQuotes(Trim(ActiveGuid));
  StringChangeEx(ActiveGuid, '{', '', True);
  StringChangeEx(ActiveGuid, '}', '', True);

  for I := 0 to GetArrayLength(SchemeGuids) - 1 do
  begin
    FriendlyName := '';
    RegQueryStringValue(HKLM, PowerSchemesKeyPath + '\' + SchemeGuids[I], 'FriendlyName', FriendlyName);
    if CompareText(FriendlyName, UltimatePlanName) = 0 then
    begin
      // powercfg refuses to delete the active scheme. Balanced is the same fallback the app
      // itself uses when it cannot tell which scheme was active before the profile ran.
      if CompareText(SchemeGuids[I], ActiveGuid) = 0 then
        Exec(ExpandConstant('{sys}\powercfg.exe'), '/setactive ' + BalancedPlanGuid, '',
             SW_HIDE, ewWaitUntilTerminated, ResultCode);

      Exec(ExpandConstant('{sys}\powercfg.exe'), '/delete ' + SchemeGuids[I], '',
           SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  Waited: Integer;
begin
  Result := True;
  // The in-app updater starts Setup and then shuts TrayTrigger down, so the single-instance
  // mutex can still be held for a moment when Setup starts. Give it up to 15 s to go away;
  // otherwise the AppMutex check below would abort a silent install on the spot.
  Waited := 0;
  while CheckForMutexes(AppMutexName) and (Waited < 15000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;

  // Say this once, up front, rather than leaving a dead second entry in Apps & features with no
  // explanation. Only when Setup cannot clear it itself, and never during an in-app update.
  if LegacyAllUsersInstallExists() and (not IsAdmin()) and (not WizardSilent()) then
    MsgBox('An older "all users" installation of TrayTrigger is still registered on this computer.'#13#10#13#10
      + 'TrayTrigger now installs for the current user only. Setup will continue, but that older entry stays '
      + 'listed in Apps & features until it is removed: uninstall it from there, or run this installer as '
      + 'administrator and Setup will clear it for you.', mbInformation, MB_OK);
end;

var
  TasksPageSynced: Boolean;

procedure CurPageChanged(CurPageID: Integer);
begin
  // Inno remembers the task selection from the *previous Setup run*, not what the user has
  // since chosen in Settings. Mirror the real Run-key state the first time the page shows.
  if (CurPageID = wpSelectTasks) and not TasksPageSynced then
  begin
    TasksPageSynced := True;
    if PreviousInstallExists() then
    begin
      if StartupEntryExists() then
        WizardSelectTasks('startwithwindows')
      else
        WizardSelectTasks('!startwithwindows');
    end;
  end;
end;
// Tell Explorer to drop its cached icons so existing Desktop / taskbar / Start Menu
// shortcuts pick up the icon embedded in the freshly installed exe instead of the
// previous version's.
procedure SHChangeNotify(wEventId: Integer; uFlags: Cardinal; dwItem1, dwItem2: Integer);
  external 'SHChangeNotify@shell32.dll stdcall';

const
  SHCNE_ASSOCCHANGED = $08000000;
  SHCNF_IDLIST = $0000;

procedure RefreshShellIcons();
begin
  SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);
end;

procedure UpdateAppSettings();
var
  SettingsDir: string;
  SettingsFile: string;
  Lines: TArrayOfString;
  Original: string;
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
          // Keep the line's trailing comma exactly as it was: adding one to a final
          // property (or dropping one from a middle property) would corrupt the JSON.
          Original := TrimRight(Lines[I]);
          if StartWithWin then
            Lines[I] := '  "StartWithWindows": true'
          else
            Lines[I] := '  "StartWithWindows": false';
          if (Length(Original) > 0) and (Original[Length(Original)] = ',') then
            Lines[I] := Lines[I] + ',';
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
    // An in-app update leaves startup registration and settings.json alone: the app has
    // owned both since the original install.
    if not IsUpdate() then
    begin
      UpdateAppSettings();
      // The [Registry] entry only adds the value; unticking the task should remove it too.
      if not WizardIsTaskSelected('startwithwindows') then
        RemoveStartupEntry();
    end;
    RemoveLegacyAllUsersEntry();
    RefreshShellIcons();
  end;
end;

var
  DeleteUserData: Boolean;

// DelTree gives up on the first file it cannot delete and reports nothing but a Boolean, so a
// single handle still open on one cached cover used to leave the rest of the library on disk
// after the user asked for all of it to go. Retry - the holder is transient (the app's own
// process finishing its exit, a shell thumbnail read) - and record what happened in the log
// rather than failing silently.
procedure DeleteDataFolder(const Dir: string);
var
  Attempt: Integer;
begin
  if not DirExists(Dir) then Exit;

  for Attempt := 1 to 5 do
  begin
    if DelTree(Dir, True, True, True) and not DirExists(Dir) then
    begin
      Log('Removed user data folder: ' + Dir);
      Exit;
    end;
    Log('Could not fully remove ' + Dir + ' (attempt ' + IntToStr(Attempt) + '); retrying.');
    Sleep(500);
  end;

  Log('Gave up removing user data folder: ' + Dir + ' - files are still in use.');
end;

function InitializeUninstall(): Boolean;
var
  UninstallForm: TSetupForm;
  InfoLabel: TNewStaticText;
  DeleteDataCheckBox: TNewCheckBox;
  OKButton, CancelButton: TNewButton;
begin
  Result := True;
  DeleteUserData := False;

  // A silent uninstall has no one to answer the prompt, and ShowModal would hang the process
  // forever waiting. Keep user data unless the caller asked for it with /DELETEDATA.
  if UninstallSilent() then
  begin
    DeleteUserData := CmdLineParamExists('/DELETEDATA');
    Exit;
  end;

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
  LegacyDataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Always: a Run entry pointing at a deleted exe is just a startup error waiting to
    // happen, the caches belong to an exe that no longer exists, and the power plan and
    // program folder are artefacts of an install that is over.
    RemoveStartupEntry();
    RemoveDownloadedInstallers();
    RemoveBundleExtractionCache();
    RemoveUltimatePowerPlan();
    RemoveAppFolderLeftovers();
    RefreshShellIcons();
  end;

  if (CurUninstallStep = usPostUninstall) and DeleteUserData then
  begin
    AppDataDir := ExpandConstant('{userappdata}\TrayTrigger');
    LocalAppDataDir := ExpandConstant('{localappdata}\TrayTrigger');
    // Pre-1.3 kept the library, settings and artwork here. StorageService and LoggingService
    // *copy* rather than move on migration, so an upgraded install still has a full second
    // copy of the game library sitting in Documents - leaving it behind would ignore exactly
    // what the checkbox promised.
    LegacyDataDir := ExpandConstant('{userdocs}\TrayTrigger');

    DeleteDataFolder(AppDataDir);
    DeleteDataFolder(LocalAppDataDir);
    DeleteDataFolder(LegacyDataDir);
  end;
end;
