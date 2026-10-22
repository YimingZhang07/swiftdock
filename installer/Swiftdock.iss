#ifndef AppVersion
  #error AppVersion must be supplied by scripts/Release.ps1
#endif

; Test builds can isolate their uninstall entry. TestLegacyDir is only honoured
; with TestAppId; Release.ps1 never sets either define.
#ifndef TestAppId
  #define ProductAppId "{22501D8C-9899-4325-8628-268B36134004}"
#else
  #if Copy(TestAppId, 1, 1) == "{"
    #define ProductAppId TestAppId
  #else
    #define ProductAppId "{" + TestAppId + "}"
  #endif
#endif
#ifndef PublishDir
  #error PublishDir must be supplied by scripts/Release.ps1
#endif
#ifndef ReleaseDir
  #error ReleaseDir must be supplied by scripts/Release.ps1
#endif

[Setup]
; Permanent product identity: NEVER change this GUID, including for future versions.
AppId={{#ProductAppId}
AppName=Swiftdock
VersionInfoProductName=Swiftdock
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={autopf}\Swiftdock
DisableDirPage=no
UsePreviousAppDir=no
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\Swiftdock\Assets\Swiftdock.ico
UninstallDisplayIcon={app}\Swiftdock.exe
Compression=lzma2
SolidCompression=yes
OutputDir={#ReleaseDir}
OutputBaseFilename=Swiftdock-{#AppVersion}-Setup
ShowLanguageDialog=no
LanguageDetectionMethod=uilanguage
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\Swiftdock.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Swiftdock"; Filename: "{app}\Swiftdock.exe"
Name: "{autodesktop}\Swiftdock"; Filename: "{app}\Swiftdock.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Swiftdock.exe"; Description: "{cm:LaunchProgram,Swiftdock}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductAppId}_is1';
  ExitMessage = '迅启或旧版 QuickLaunch 仍在运行或无法确认其已退出。请从托盘退出后重试。' + #13#10 +
    'Swiftdock or QuickLaunch is still running or its exit could not be verified. Exit it from the tray, then retry.';

var
  PreviousDirectory, PreviousUninstaller: String;
  LegacyInstallation: Boolean;

function EnumProcesses(var ProcessIds: Cardinal; Size: Cardinal; var BytesReturned: Cardinal): BOOL;
  external 'EnumProcesses@psapi.dll stdcall';
function OpenProcess(Access: Cardinal; InheritHandle: BOOL; ProcessId: Cardinal): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function QueryFullProcessImageName(Process: THandle; Flags: Cardinal; Name: String; var Size: Cardinal): BOOL;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';
function GetTickCount64: Int64;
  external 'GetTickCount64@kernel32.dll stdcall';

function InstalledProcessRunning(InstalledExe: String): Boolean;
var
  ProcessIds: array[0..4095] of Cardinal;
  BytesReturned, Size: Cardinal;
  I: Integer;
  Process: THandle;
  Name: String;
begin
  Result := True; { Fail closed if enumeration cannot confirm shutdown. }
  if not EnumProcesses(ProcessIds[0], 4096 * 4, BytesReturned) then Exit;
  if BytesReturned >= 4096 * 4 then Exit;
  InstalledExe := ExpandFileName(InstalledExe);
  for I := 0 to (BytesReturned div 4) - 1 do begin
    Process := OpenProcess($1000, False, ProcessIds[I]); { PROCESS_QUERY_LIMITED_INFORMATION }
    if Process <> 0 then begin
      try
        Size := 32768;
        SetLength(Name, Size);
        if QueryFullProcessImageName(Process, 0, Name, Size) then begin
          SetLength(Name, Size);
          if CompareText(ExpandFileName(Name), InstalledExe) = 0 then Exit;
        end;
      finally
        CloseHandle(Process);
      end;
    end;
  end;
  Result := False;
end;

function StopApp(Executable: String): Boolean;
var
  ErrorCode: Integer;
  Deadline: Int64;
begin
  Result := True;
  if not FileExists(Executable) then Exit;
  { Never wait indefinitely for the exit client and never terminate a process.
    Poll by full executable path, including the exit client, not by process name. }
  Deadline := GetTickCount64 + 10000;
  if not Exec(Executable, '--exit', ExtractFileDir(Executable),
      SW_HIDE, ewNoWait, ErrorCode) then
    Log(Executable + ' --exit failed: ' + SysErrorMessage(ErrorCode));
  repeat
    if not InstalledProcessRunning(Executable) then Exit;
    Sleep(100);
  until GetTickCount64 >= Deadline;
  Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if LegacyInstallation and (CompareText(ExpandFileName(PreviousDirectory), ExpandFileName(ExpandConstant('{app}'))) = 0) then begin
    Result := '请选择新的迅启安装目录。' + #13#10 + 'Choose a new Swiftdock installation directory.';
    Exit;
  end;
  if not StopApp(ExpandConstant('{app}\Swiftdock.exe')) then begin Result := ExitMessage; Exit; end;
  if PreviousDirectory <> '' then begin
    if not StopApp(AddBackslash(PreviousDirectory) + 'QuickLaunch.exe') then begin Result := ExitMessage; Exit; end;
    if not StopApp(AddBackslash(PreviousDirectory) + 'Swiftdock.exe') then Result := ExitMessage;
  end;
end;

function StopInstalledAppForUninstall: Boolean;
begin
  Result := StopApp(ExpandConstant('{app}\Swiftdock.exe'));
  while not Result do begin
    if UninstallSilent then begin
      Log(ExitMessage);
      Exit;
    end;
    if MsgBox(ExitMessage, mbError, MB_RETRYCANCEL) <> IDRETRY then Exit;
    Result := StopApp(ExpandConstant('{app}\Swiftdock.exe'));
  end;
end;

function CommandExecutable(Command: String): String;
var
  I: Integer;
begin
  Result := '';
  Command := Trim(Command);
  if Command = '' then Exit;
  if Command[1] = '"' then begin
    Delete(Command, 1, 1);
    I := Pos('"', Command);
    if I = 0 then Exit;
    Result := Copy(Command, 1, I - 1);
  end else begin
    I := 1;
    while I <= Length(Command) do begin
      if (Command[I] = ' ') or (Command[I] = #9) then Break;
      I := I + 1;
    end;
    Result := Copy(Command, 1, I - 1);
  end;
  { Product commands use absolute paths. Leave ambiguous relative commands alone. }
  if (Copy(Result, 2, 2) <> ':\') and (Copy(Result, 1, 2) <> '\\') then Result := '';
end;

function InitializeSetup: Boolean;
var
  Command, FallbackDirectory: String;
begin
  { Read before Setup updates the shared product identity to the new location. }
  RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', PreviousDirectory);
  FallbackDirectory := '';
#ifndef TestAppId
  FallbackDirectory := ExpandConstant('{localappdata}\Programs\QuickLaunch');
#else
  #ifdef TestLegacyDir
  FallbackDirectory := '{#StringChange(TestLegacyDir, "'", "''")}';
  #endif
#endif
  { Recover the known legacy install whose uninstall entry may be missing.
    Test builds never inspect the owner's default legacy installation. }
  if (PreviousDirectory = '') and (FallbackDirectory <> '') then begin
    FallbackDirectory := RemoveBackslash(ExpandFileName(FallbackDirectory));
    if FileExists(AddBackslash(FallbackDirectory) + 'QuickLaunch.exe') then begin
      PreviousDirectory := FallbackDirectory;
      { No recorded uninstaller: only these exact names in the fallback folder
        may be removed, still subject to the same-directory guard below. }
      PreviousUninstaller := AddBackslash(FallbackDirectory) + 'unins000.exe';
      Log('Found legacy QuickLaunch installation without InstallLocation: ' + PreviousDirectory);
    end;
  end;
  if PreviousDirectory <> '' then begin
    PreviousDirectory := RemoveBackslash(ExpandFileName(PreviousDirectory));
    LegacyInstallation := FileExists(AddBackslash(PreviousDirectory) + 'QuickLaunch.exe') or
      (CompareText(ExtractFileName(PreviousDirectory), 'QuickLaunch') = 0);
    if PreviousUninstaller = '' then
      if RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', Command) then
        PreviousUninstaller := CommandExecutable(Command);
  end;
  Result := True;
end;

procedure InitializeWizard;
begin
  { Keep an existing Swiftdock location; never reuse the legacy directory. }
  if (PreviousDirectory <> '') and not LegacyInstallation then WizardForm.DirEdit.Text := PreviousDirectory;
end;

procedure RemoveLegacyShortcut(ShortcutPath: String);
var
  Shell, Shortcut: Variant;
  Target, LegacyDirectory: String;
begin
  if not FileExists(ShortcutPath) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(ShortcutPath);
    Target := Shortcut.TargetPath;
    { Leave empty, relative or unreadable targets alone. Include a trailing
      separator so a sibling such as QuickLaunchOther cannot match. }
    if (Copy(Target, 2, 2) <> ':\') and (Copy(Target, 1, 2) <> '\\') then Exit;
    Target := ExpandFileName(Target);
    LegacyDirectory := AddBackslash(ExpandFileName(PreviousDirectory));
    if CompareText(Copy(Target, 1, Length(LegacyDirectory)), LegacyDirectory) = 0 then
      DeleteFile(ShortcutPath);
  except
    Log('Could not inspect legacy shortcut; kept it: ' + ShortcutPath + ': ' + GetExceptionMessage);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep <> ssPostInstall) or not LegacyInstallation then Exit;
  { Only the previous app and its recorded uninstaller are removed, after the
    new files are installed. Preserve the new uninstaller if Setup reused the
    previous directory. User files prevent RemoveDir from succeeding. }
  DeleteFile(AddBackslash(PreviousDirectory) + 'QuickLaunch.exe');
  if CompareText(RemoveBackslash(ExpandConstant('{app}')), PreviousDirectory) <> 0 then begin
    if (PreviousUninstaller <> '') and
        (CompareText(ExtractFileDir(PreviousUninstaller), PreviousDirectory) = 0) and
        (CompareText(Copy(ExtractFileName(PreviousUninstaller), 1, 5), 'unins') = 0) and
        (CompareText(ExtractFileExt(PreviousUninstaller), '.exe') = 0) then begin
      DeleteFile(PreviousUninstaller);
      DeleteFile(ChangeFileExt(PreviousUninstaller, '.dat'));
      DeleteFile(ChangeFileExt(PreviousUninstaller, '.msg'));
    end;
    RemoveDir(PreviousDirectory);
  end;
  RemoveLegacyShortcut(ExpandConstant('{autoprograms}\QuickLaunch.lnk'));
  RemoveLegacyShortcut(ExpandConstant('{autodesktop}\QuickLaunch.lnk'));
end;

procedure RemoveInstalledStartupValues;
var
  Names: TArrayOfString;
  I: Integer;
  Command, Exe, AppDirectory: String;
begin
  AppDirectory := AddBackslash(ExpandFileName(ExpandConstant('{app}')));
  if not RegGetValueNames(HKCU, RunKey, Names) then Exit;
  for I := 0 to GetArrayLength(Names) - 1 do begin
    if (CompareText(Copy(Names[I], 1, 10), 'Swiftdock_') = 0) or
        (CompareText(Copy(Names[I], 1, 12), 'QuickLaunch_') = 0) then begin
      if RegQueryStringValue(HKCU, RunKey, Names[I], Command) then begin
        Exe := CommandExecutable(Command);
        if Exe <> '' then begin
          Exe := ExpandFileName(Exe);
          if CompareText(Copy(Exe, 1, Length(AppDirectory)), AppDirectory) = 0 then
            if not RegDeleteValue(HKCU, RunKey, Names[I]) then
              Log('Could not remove startup value: ' + Names[I]);
        end;
      end;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then begin
    { Run only after confirmation, before files, shortcuts, Run values or data
      are removed. Abort is supported here and exits without an extra error dialog:
      https://jrsoftware.org/ishelp/topic_isxfunc_abort.htm }
    if not StopInstalledAppForUninstall then Abort;
    RemoveInstalledStartupValues;
    if not UninstallSilent then
      if MsgBox('是否同时删除迅启和旧版 QuickLaunch 的配置、图标缓存和日志？默认保留。' + #13#10 +
          'Also delete Swiftdock and legacy QuickLaunch configuration, icon cache and logs? Data is kept by default.' + #13#10#13#10 +
          ExpandConstant('{userappdata}\Swiftdock') + #13#10 + ExpandConstant('{localappdata}\Swiftdock') + #13#10 +
          ExpandConstant('{userappdata}\QuickLaunch') + #13#10 + ExpandConstant('{localappdata}\QuickLaunch'),
          mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then begin
        if not DelTree(ExpandConstant('{userappdata}\Swiftdock'), True, True, True) then
          Log('Could not completely delete the configuration directory.');
        if not DelTree(ExpandConstant('{localappdata}\Swiftdock'), True, True, True) then
          Log('Could not completely delete the cache/log directory.');
        if DirExists(ExpandConstant('{userappdata}\QuickLaunch')) then
          if not DelTree(ExpandConstant('{userappdata}\QuickLaunch'), True, True, True) then
            Log('Could not completely delete the legacy configuration directory.');
        if DirExists(ExpandConstant('{localappdata}\QuickLaunch')) then
          if not DelTree(ExpandConstant('{localappdata}\QuickLaunch'), True, True, True) then
            Log('Could not completely delete the legacy cache/log directory.');
      end;
  end;
end;
