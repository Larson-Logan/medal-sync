#ifndef AppVersion
  #error AppVersion must be supplied by the packaging script
#endif
#ifndef PayloadDir
  #error PayloadDir must be supplied by the packaging script
#endif
#ifndef OutputPath
  #error OutputPath must be supplied by the packaging script
#endif
#ifndef StorageDependencies
  #error StorageDependencies must name the verified pinned storage dependency directory
#endif

#ifdef IsolatedTestId
  #ifndef IsolatedTestRoot
    #error IsolatedTestRoot is required for a test installer
  #endif
  #ifndef ReadyChecker
    #error ReadyChecker is required for a test installer
  #endif
#endif

[Setup]
#ifdef IsolatedTestId
AppId={#IsolatedTestId}
AppName=Medal Sync Test {#IsolatedTestId}
#else
AppId={{168562DF-1041-441D-96D0-2244EA744E18}
AppName=Medal Sync
#endif
AppVersion={#AppVersion}
AppPublisher=Medal Sync contributors
AppPublisherURL=https://github.com/Larson-Logan/medal-sync
AppSupportURL=https://github.com/Larson-Logan/medal-sync/issues
AppUpdatesURL=https://github.com/Larson-Logan/medal-sync/releases
DefaultDirName={autopf}\Medal Sync
#ifdef IsolatedTestId
DefaultGroupName=Medal Sync Test {#IsolatedTestId}
#else
DefaultGroupName=Medal Sync
#endif
DisableProgramGroupPage=yes
DisableDirPage=no
DisableWelcomePage=no
AllowUNCPath=no
LicenseFile={#PayloadDir}\LICENSE
OutputDir={#OutputPath}
#ifdef IsolatedTestId
OutputBaseFilename=medal-sync-isolated-{#IsolatedTestId}
#else
OutputBaseFilename=medal-sync-{#AppVersion}-setup-win-x64
#endif
UninstallDisplayIcon={app}\bin\medal-sync-launch.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern dynamic windows11
#ifdef IsolatedTestId
; Disposable test setups compress fast; the decoder and every installed byte are unchanged.
Compression=lzma2/fast
#else
Compression=lzma2
#endif
SolidCompression=yes
CloseApplications=no
RestartApplications=no
AllowCancelDuringInstall=no
#ifdef IsolatedTestId
SetupMutex=Local\MedalSync.Test.Setup.{#IsolatedTestId}
AppMutex=Local\MedalSync.Test.App.{#IsolatedTestId}
#else
SetupMutex=Global\LarsonServer.MedalSync.Setup
AppMutex=Local\LarsonServer.MedalSync
#endif
Uninstallable=yes
; Tells Explorer and new terminals to reload PATH after setup or uninstall changes it.
ChangesEnvironment=yes
UninstallLogMode=append
VersionInfoVersion={#AppVersion}

[Messages]
ConfirmUninstall=Remove %1 and its program shortcuts?%n%nYour settings, device identity, recordings, cache and backups will be kept. WinFsp and existing portable installations will also be kept.

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: addtopath; Description: "Add medal-sync to PATH so it runs from any new terminal"; GroupDescription: "Command line:"; Flags: unchecked
Name: winfsp; Description: "Install the pinned WinFsp clip-drive driver if it is missing (all-users setup)"; GroupDescription: "Storage dependencies:"; Check: ShouldOfferWinFsp

[Files]
Source: "{#StorageDependencies}\rclone.exe"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "{#StorageDependencies}\RCLONE-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StorageDependencies}\winfsp.msi"; Flags: dontcopy
Source: "..\..\docs\setup.html"; DestDir: "{app}"; Flags: ignoreversion
Source: "setup-layout.json"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "{#PayloadDir}\medal-sync.exe"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "{#PayloadDir}\medal-sync-launch.exe"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "{#PayloadDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\THIRD_PARTY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\DOTNET-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\DOTNET-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
#ifdef IsolatedTestId
Source: "{#ReadyChecker}"; DestName: "medal-sync-check.exe"; Flags: dontcopy
Source: "{#ReadyChecker}"; DestDir: "{app}\bin"; DestName: "medal-sync-test-check.exe"; Flags: ignoreversion
#else
Source: "{#PayloadDir}\medal-sync.exe"; DestName: "medal-sync-check.exe"; Flags: dontcopy
#endif

[Icons]
Name: "{group}\Medal Sync Status"; Filename: "{app}\bin\medal-sync-launch.exe"; Parameters: "--status"; WorkingDir: "{app}\bin"
Name: "{group}\Medal Sync Setup Guide"; Filename: "{app}\setup.html"
Name: "{group}\Medal Sync"; Filename: "{app}\bin\medal-sync-launch.exe"; WorkingDir: "{app}\bin"
Name: "{group}\Check for Medal Sync updates"; Filename: "{app}\bin\medal-sync-launch.exe"; Parameters: "--check-updates"; WorkingDir: "{app}\bin"
Name: "{group}\Uninstall Medal Sync"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Medal Sync"; Filename: "{app}\bin\medal-sync-launch.exe"; WorkingDir: "{app}\bin"; Tasks: desktopicon

[Run]
Filename: "{app}\setup.html"; Description: "Read the setup and storage guide"; Flags: shellexec postinstall skipifsilent runasoriginaluser
Filename: "{app}\bin\medal-sync-launch.exe"; Description: "Open Medal Sync"; Flags: nowait postinstall skipifsilent runasoriginaluser unchecked

[Code]
var
  CheckNumber: Integer;
  StoragePage: TOutputMsgWizardPage;
  { Whether setup, rather than the user, added the PATH entry: before and after this run. }
  PathWasOwned, PathOwnedAfterInstall: Boolean;

{ PATH lives in a different key for all-users and per-user installations. }
function PathRoot(): Integer;
begin
  if IsAdminInstallMode then Result := HKLM else Result := HKCU;
end;

function PathKey(): String;
begin
  if IsAdminInstallMode then
    Result := 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
  else
    Result := 'Environment';
end;

function SamePathEntry(Entry, Directory: String): Boolean;
begin
  Entry := Trim(Entry);
  if (Length(Entry) >= 2) and (Entry[1] = '"') and (Entry[Length(Entry)] = '"') then
    Entry := Copy(Entry, 2, Length(Entry) - 2);
  Result := (Entry <> '') and
    (CompareText(RemoveBackslashUnlessRoot(Entry), RemoveBackslashUnlessRoot(Directory)) = 0);
end;

{ Returns Path without any entry for Directory, and whether one was found. }
function PathWithout(Path, Directory: String; var Found: Boolean): String;
var
  Rest, Entry: String;
  Separator: Integer;
begin
  Result := '';
  Found := False;
  Rest := Path;
  while Rest <> '' do begin
    Separator := Pos(';', Rest);
    if Separator = 0 then begin
      Entry := Rest;
      Rest := '';
    end else begin
      Entry := Copy(Rest, 1, Separator - 1);
      Rest := Copy(Rest, Separator + 1, Length(Rest));
    end;
    if SamePathEntry(Entry, Directory) then
      Found := True
    else if Trim(Entry) <> '' then begin
      if Result <> '' then Result := Result + ';';
      Result := Result + Entry;
    end;
  end;
end;

{ Appends the bin folder, so programs already on PATH (such as another rclone) keep priority. }
function ProgramsOnPath(): Boolean;
var
  Path, Unused: String;
  Found: Boolean;
begin
  if not RegQueryStringValue(PathRoot(), PathKey(), 'Path', Path) then Path := '';
  Unused := PathWithout(Path, ExpandConstant('{app}\bin'), Found);
  Result := Found;
end;

procedure AddProgramsToPath();
var
  Path, Directory: String;
begin
  Directory := ExpandConstant('{app}\bin');
  if ProgramsOnPath() then exit;
  if not RegQueryStringValue(PathRoot(), PathKey(), 'Path', Path) then Path := '';
  if (Path <> '') and (Path[Length(Path)] <> ';') then Path := Path + ';';
  if not RegWriteExpandStringValue(PathRoot(), PathKey(), 'Path', Path + Directory) then
    Log('Could not add ' + Directory + ' to PATH.');
end;

procedure RemoveProgramsFromPath();
var
  Path, Directory, Remaining: String;
  Found: Boolean;
begin
  Directory := ExpandConstant('{app}\bin');
  if not RegQueryStringValue(PathRoot(), PathKey(), 'Path', Path) then exit;
  Remaining := PathWithout(Path, Directory, Found);
  if not Found then exit;
  if Remaining = '' then begin
    if not RegDeleteValue(PathRoot(), PathKey(), 'Path') then Log('Could not remove ' + Directory + ' from PATH.');
  end else if not RegWriteExpandStringValue(PathRoot(), PathKey(), 'Path', Remaining) then
    Log('Could not remove ' + Directory + ' from PATH.');
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  if PathOwnedAfterInstall then
    SetPreviousData(PreviousDataKey, 'PathEntryOwned', 'yes')
  else
    SetPreviousData(PreviousDataKey, 'PathEntryOwned', 'no');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { Decide ownership before setup records it; an entry the user already had stays theirs. }
  if CurStep = ssInstall then
    PathOwnedAfterInstall := WizardIsTaskSelected('addtopath') and (PathWasOwned or (not ProgramsOnPath()));
  if CurStep = ssPostInstall then begin
    if WizardIsTaskSelected('addtopath') then
      AddProgramsToPath()
    { Only undo an entry an earlier setup added, never one the user added by hand. }
    else if PathWasOwned then
      RemoveProgramsFromPath();
  end;
end;

function RegisteredInstallation(var Directory, Version: String): Boolean;
var
  Key: String;
begin
#ifdef IsolatedTestId
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#IsolatedTestId}_is1';
#else
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{168562DF-1041-441D-96D0-2244EA744E18}_is1';
#endif
  Result := RegQueryStringValue(HKA, Key, 'InstallLocation', Directory) and
    RegQueryStringValue(HKA, Key, 'DisplayVersion', Version);
end;

// Releases are MAJOR.MINOR.PATCH.NN; installations before 1.3.0.00 registered
// MAJOR.MINOR.PATCH, which packs as revision 0.
function PackVersion(const Value: String; var Packed: Int64): Boolean;
var
  I, Dots: Integer;
begin
  Dots := 0;
  for I := 1 to Length(Value) do
    if Value[I] = '.' then Dots := Dots + 1;
  if Dots = 2 then
    Result := StrToVersion(Value + '.0', Packed)
  else
    Result := (Dots = 3) and StrToVersion(Value, Packed);
end;

function WinFspInstalled(): Boolean;
var
  Directory: String;
begin
  Result := RegQueryStringValue(HKLM32, 'SOFTWARE\WinFsp', 'InstallDir', Directory) and
    FileExists(AddBackslash(Directory) + 'bin\winfsp-x64.dll');
end;

function ShouldOfferWinFsp(): Boolean;
begin
#ifdef IsolatedTestId
  Result := False;
#else
  Result := IsAdminInstallMode and (not WinFspInstalled());
#endif
end;

function CheckReady(ForUninstall: Boolean): String;
var
  Executable, ReportPath, Params: String;
  Report: AnsiString;
  ResultCode: Integer;
  Started: Boolean;
begin
  Result := '';
  Inc(CheckNumber);
  ReportPath := ExpandConstant('{tmp}\medal-sync-ready-') + IntToStr(CheckNumber) + '.txt';
  Params := 'setup check --out "' + ReportPath + '"';
#ifdef IsolatedTestId
  Params := Params + ' --state-root "{#IsolatedTestRoot}"';
#endif
  if ForUninstall then begin
    Executable := ExpandConstant('{app}\bin\medal-sync.exe');
#ifdef IsolatedTestId
    Executable := ExpandConstant('{app}\bin\medal-sync-test-check.exe');
#endif
    if not FileExists(Executable) then begin
      Result := 'The installed readiness checker is missing. Reinstall Medal Sync before uninstalling.';
      exit;
    end;
    Started := Exec(Executable, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end else begin
    ExtractTemporaryFile('medal-sync-check.exe');
    Executable := ExpandConstant('{tmp}\medal-sync-check.exe');
    Started := ExecAsOriginalUser(Executable, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  if (not Started) or (ResultCode <> 0) then begin
    if LoadStringFromFile(ReportPath, Report) then Result := Utf8Decode(Report);
    if Result = '' then
      Result := 'Cannot verify that Medal Sync is Ready. Cancel setup, quit Medal normally and finish any pending session, then run setup again.';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Installed, FileVersion, Target: Int64;
  ResultCode: Integer;
  ProgramPath, RegisteredDir, RegisteredVersion: String;
  HasRegisteredVersion: Boolean;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\config.json')) or FileExists(ExpandConstant('{app}\device.json')) or
     FileExists(ExpandConstant('{app}\bin\config.json')) or FileExists(ExpandConstant('{app}\bin\device.json')) then begin
    Result := 'Choose a separate program folder. This location contains user configuration that must stay separate from the installed programs.';
    exit;
  end;
  HasRegisteredVersion := RegisteredInstallation(RegisteredDir, RegisteredVersion);
  if HasRegisteredVersion then begin
    if CompareText(RemoveBackslashUnlessRoot(RegisteredDir), RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) <> 0 then begin
      Result := 'Medal Sync is already installed at ' + RegisteredDir + '. Select that folder to upgrade or repair it. Uninstall first if you want to move the programs.';
      exit;
    end;
    if not PackVersion(RegisteredVersion, Installed) then begin
      Result := 'Cannot identify the registered Medal Sync version. Recover the existing installation before continuing.';
      exit;
    end;
  end;
  ProgramPath := ExpandConstant('{app}\bin\medal-sync.exe');
  if FileExists(ProgramPath) or FileExists(ExpandConstant('{app}\bin\medal-sync-launch.exe')) then begin
    if (not HasRegisteredVersion) and (not FileExists(ExpandConstant('{app}\bin\setup-layout.json'))) then begin
      Result := 'This folder contains a portable installation. Choose a new program folder; existing user settings will be reused.';
      exit;
    end;
    if GetPackedVersion(ProgramPath, FileVersion) then begin
      if (not HasRegisteredVersion) or (ComparePackedVersion(FileVersion, Installed) > 0) then Installed := FileVersion;
    end else if not HasRegisteredVersion then begin
      Result := 'Cannot identify the installed version. Recover the existing installation before continuing.';
      exit;
    end;
  end;
  if HasRegisteredVersion or FileExists(ProgramPath) then begin
    if not PackVersion('{#AppVersion}', Target) then begin
      Result := 'Cannot identify the installed version. Repair the existing installation before continuing.';
      exit;
    end;
    if ComparePackedVersion(Installed, Target) > 0 then begin
      Result := 'A newer Medal Sync version is already installed. Use that version or a newer setup package.';
      exit;
    end;
  end;
  Result := CheckReady(False);
  if Result <> '' then exit;
  if ShouldOfferWinFsp() and WizardIsTaskSelected('winfsp') then begin
    ExtractTemporaryFile('winfsp.msi');
    if not Exec(ExpandConstant('{sys}\msiexec.exe'), '/i "' + ExpandConstant('{tmp}\winfsp.msi') + '" /passive /norestart',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Result := 'WinFsp installation could not start. Retry setup or install the pinned driver separately.'
    else if ResultCode = 3010 then begin
      NeedsRestart := True;
      Result := 'WinFsp requires a Windows restart. Restart when convenient, then rerun Medal Sync setup.';
    end
    else if (ResultCode <> 0) or (not WinFspInstalled()) then
      Result := 'WinFsp installation did not complete. Retry setup or install the pinned driver separately.';
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  InstalledVersion, RegisteredDir, Operation: String;
  Installed, Target: Int64;
begin
  Operation := 'Install Medal Sync {#AppVersion}';
  if RegisteredInstallation(RegisteredDir, InstalledVersion) and
     (CompareText(RemoveBackslashUnlessRoot(RegisteredDir), RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) = 0) and
     PackVersion(InstalledVersion, Installed) and
     PackVersion('{#AppVersion}', Target) then begin
    if ComparePackedVersion(Installed, Target) = 0 then
      Operation := 'Repair Medal Sync {#AppVersion}'
    else
      Operation := 'Upgrade Medal Sync ' + InstalledVersion + ' to {#AppVersion}';
  end;
  Result := Operation + NewLine + NewLine + MemoDirInfo + NewLine + NewLine;
  if MemoGroupInfo <> '' then Result := Result + MemoGroupInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then Result := Result + MemoTasksInfo + NewLine + NewLine;
  Result := Result + 'Your settings, identity, recordings, cache and backups will be kept.' + NewLine +
    'Setup includes the Windows uninstaller and a Check for Medal Sync updates shortcut.' + NewLine +
    'Quit Medal normally and wait for Ready. Setup checks this before changing programs.';
end;

function InitializeUninstall(): Boolean;
var
  Error: String;
begin
#ifdef IsolatedTestId
  if CheckForMutexes('Local\MedalSync.Test.Setup.{#IsolatedTestId}') then begin
#else
  if CheckForMutexes('Global\LarsonServer.MedalSync.Setup') then begin
#endif
    SuppressibleMsgBox('Another Medal Sync setup or uninstall is open. Close it before uninstalling.', mbError, MB_OK, IDOK);
    Result := False;
    exit;
  end;
#ifdef IsolatedTestId
  CreateMutex('Local\MedalSync.Test.Setup.{#IsolatedTestId}');
#else
  CreateMutex('Global\LarsonServer.MedalSync.Setup');
#endif
  Error := CheckReady(True);
  Result := Error = '';
  if not Result then SuppressibleMsgBox(Error, mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Error: String;
begin
  { The bin folder is gone after uninstall, so its PATH entry would point nowhere. }
  if CurUninstallStep = usPostUninstall then RemoveProgramsFromPath();
  { The initial check precedes the confirmation dialog. Recheck after the user confirms. }
  if CurUninstallStep = usUninstall then begin
    Error := CheckReady(True);
    if Error <> '' then begin
      Log('Uninstall refused before program removal: ' + Error);
      SuppressibleMsgBox(Error, mbError, MB_OK, IDOK);
      Abort;
    end;
  end;
end;

procedure InitializeWizard();
var
  WinFspDirectory, DependencyStatus: String;
begin
  { Read before setup rewrites its registration with this run's choices. }
  PathWasOwned := GetPreviousData('PathEntryOwned', '') = 'yes';
  if RegQueryStringValue(HKLM32, 'SOFTWARE\WinFsp', 'InstallDir', WinFspDirectory) and
     FileExists(AddBackslash(WinFspDirectory) + 'bin\winfsp-x64.dll') then
    DependencyStatus := 'WinFsp is installed.'
  else
    DependencyStatus := 'WinFsp is not detected. All-users setup can install the bundled pinned driver. Per-user setup needs an administrator to install WinFsp separately.';
  StoragePage := CreateOutputMsgPage(wpWelcome, 'Plan your storage',
    'Choose how to store your recordings after setup',
    'Server with SSD cache: clips upload to your SMB server; the session cache is released after verified handoff.' + #13#10 + #13#10 +
    'Keep a local copy: record to a persistent local folder, then copy and verify media on the server before switching PCs.' + #13#10 + #13#10 +
    'Optional cloud copies: add OneDrive, Dropbox or Google Drive with a named rclone remote. Sign in through rclone after setup.' + #13#10 + #13#10 +
    DependencyStatus + #13#10 + #13#10 +
    'Setup includes rclone and an offline guide. Local storage with cloud copies works without an SMB server. Multi-PC handoff uses SMB.');
  WizardForm.WelcomeLabel2.Caption :=
    'This will install Medal Sync on your computer.' + #13#10 + #13#10 +
    'Programs are installed in the folder you choose. Your settings, library, recordings and cache stay in your user profile and existing storage locations.' + #13#10 + #13#10 +
    'Quit Medal normally and wait for Ready before continuing.';
end;
