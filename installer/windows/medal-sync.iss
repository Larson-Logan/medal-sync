#ifndef AppVersion
  #error AppVersion must be supplied by the packaging script
#endif
#ifndef PayloadDir
  #error PayloadDir must be supplied by the packaging script
#endif
#ifndef OutputPath
  #error OutputPath must be supplied by the packaging script
#endif

[Setup]
AppId={{168562DF-1041-441D-96D0-2244EA744E18}
AppName=Medal Sync
AppVersion={#AppVersion}
AppPublisher=Medal Sync contributors
AppPublisherURL=https://github.com/Larson-Logan/medal-sync
AppSupportURL=https://github.com/Larson-Logan/medal-sync/issues
AppUpdatesURL=https://github.com/Larson-Logan/medal-sync/releases
DefaultDirName={autopf}\Medal Sync
DefaultGroupName=Medal Sync
DisableProgramGroupPage=yes
DisableDirPage=no
DisableWelcomePage=no
AllowUNCPath=no
LicenseFile={#PayloadDir}\LICENSE
OutputDir={#OutputPath}
OutputBaseFilename=medal-sync-{#AppVersion}-setup-win-x64
UninstallDisplayIcon={app}\bin\medal-sync-launch.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
WizardStyle=modern dynamic windows11
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
AllowCancelDuringInstall=no
SetupMutex=Global\LarsonServer.MedalSync.Setup
AppMutex=Local\LarsonServer.MedalSync
Uninstallable=yes
UninstallLogMode=append
VersionInfoVersion={#AppVersion}

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "setup-layout.json"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "{#PayloadDir}\medal-sync.exe"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "{#PayloadDir}\medal-sync-launch.exe"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "{#PayloadDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\THIRD_PARTY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\DOTNET-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\DOTNET-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\medal-sync.exe"; DestName: "medal-sync-check.exe"; Flags: dontcopy

[Icons]
Name: "{group}\Medal Sync"; Filename: "{app}\bin\medal-sync-launch.exe"; WorkingDir: "{app}\bin"
Name: "{group}\Uninstall Medal Sync"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Medal Sync"; Filename: "{app}\bin\medal-sync-launch.exe"; WorkingDir: "{app}\bin"; Tasks: desktopicon

[Run]
Filename: "{app}\bin\medal-sync-launch.exe"; Description: "Open Medal Sync"; Flags: nowait postinstall skipifsilent runasoriginaluser unchecked

[Code]
var
  CheckNumber: Integer;

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
  if ForUninstall then begin
    Executable := ExpandConstant('{app}\bin\medal-sync.exe');
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
    if LoadStringFromFile(ReportPath, Report) then Result := Utf8Decode(Report)
    else Result := 'Cannot verify that Medal Sync is Ready. Quit Medal normally and finish any pending session, then retry.';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Installed, Target: Int64;
  ProgramPath: String;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\config.json')) or FileExists(ExpandConstant('{app}\device.json')) then begin
    Result := 'Choose a separate program folder. This location contains user configuration that must stay separate from the installed programs.';
    exit;
  end;
  ProgramPath := ExpandConstant('{app}\bin\medal-sync.exe');
  if FileExists(ProgramPath) then begin
    if not FileExists(ExpandConstant('{app}\bin\setup-layout.json')) then begin
      Result := 'This folder contains a portable installation. Choose a new program folder; existing user settings will be reused.';
      exit;
    end;
    if (not GetPackedVersion(ProgramPath, Installed)) or (not StrToVersion('{#AppVersion}.0', Target)) then begin
      Result := 'Cannot identify the installed version. Repair the existing installation before continuing.';
      exit;
    end;
    if ComparePackedVersion(Installed, Target) > 0 then begin
      Result := 'A newer Medal Sync version is already installed. Use that version or a newer setup package.';
      exit;
    end;
  end;
  Result := CheckReady(False);
end;

function InitializeUninstall(): Boolean;
var
  Error: String;
begin
  if CheckForMutexes('Global\LarsonServer.MedalSync.Setup') then begin
    SuppressibleMsgBox('Another Medal Sync setup or uninstall is open. Close it before uninstalling.', mbError, MB_OK, IDOK);
    Result := False;
    exit;
  end;
  CreateMutex('Global\LarsonServer.MedalSync.Setup');
  Error := CheckReady(True);
  Result := Error = '';
  if not Result then SuppressibleMsgBox(Error, mbError, MB_OK, IDOK);
end;

procedure InitializeWizard();
begin
  WizardForm.WelcomeLabel2.Caption :=
    'This will install Medal Sync on your computer.' + #13#10 + #13#10 +
    'Programs are installed in Program Files. Your settings, library, recordings and cache stay in your user profile and existing storage locations.' + #13#10 + #13#10 +
    'Quit Medal normally and wait for Ready before continuing.';
end;
