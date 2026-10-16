#ifndef AppVersion
  #error AppVersion must be supplied by scripts/Release.ps1
#endif
#ifndef PublishDir
  #error PublishDir must be supplied by scripts/Release.ps1
#endif
#ifndef ReleaseDir
  #error ReleaseDir must be supplied by scripts/Release.ps1
#endif

[Setup]
; Permanent product identity: NEVER change this GUID, including for future versions.
AppId={{22501D8C-9899-4325-8628-268B36134004}
AppName=QuickLaunch
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={autopf}\QuickLaunch
DisableDirPage=no
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\QuickLaunch\Assets\QuickLaunch.ico
UninstallDisplayIcon={app}\QuickLaunch.exe
Compression=lzma2
SolidCompression=yes
OutputDir={#ReleaseDir}
OutputBaseFilename=QuickLaunch-{#AppVersion}-Setup
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
Source: "{#PublishDir}\QuickLaunch.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\QuickLaunch"; Filename: "{app}\QuickLaunch.exe"
Name: "{autodesktop}\QuickLaunch"; Filename: "{app}\QuickLaunch.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\QuickLaunch.exe"; Description: "{cm:LaunchProgram,QuickLaunch}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ExitMessage = 'QuickLaunch 仍在运行或无法确认其已退出。请从托盘菜单退出 QuickLaunch，然后重试。' + #13#10 +
    'QuickLaunch is still running or its exit could not be verified. Exit QuickLaunch from the tray menu, then retry.';

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

function InstalledProcessRunning: Boolean;
var
  ProcessIds: array[0..4095] of Cardinal;
  BytesReturned, Size: Cardinal;
  I: Integer;
  Process: THandle;
  Name, InstalledExe: String;
begin
  Result := True; { Fail closed if enumeration cannot confirm shutdown. }
  if not EnumProcesses(ProcessIds[0], 4096 * 4, BytesReturned) then Exit;
  if BytesReturned >= 4096 * 4 then Exit;
  InstalledExe := ExpandFileName(ExpandConstant('{app}\QuickLaunch.exe'));
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

function StopInstalledApp: Boolean;
var
  ErrorCode: Integer;
  Deadline: Int64;
begin
  Result := True;
  if not FileExists(ExpandConstant('{app}\QuickLaunch.exe')) then Exit;
  { Never wait indefinitely for the exit client and never terminate a process.
    Poll by full executable path, including the exit client, not by process name. }
  Deadline := GetTickCount64 + 10000;
  if not Exec(ExpandConstant('{app}\QuickLaunch.exe'), '--exit', ExpandConstant('{app}'),
      SW_HIDE, ewNoWait, ErrorCode) then
    Log('QuickLaunch --exit failed: ' + SysErrorMessage(ErrorCode));
  repeat
    if not InstalledProcessRunning then Exit;
    Sleep(100);
  until GetTickCount64 >= Deadline;
  Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopInstalledApp then Result := ExitMessage;
end;

function StopInstalledAppForUninstall: Boolean;
begin
  Result := StopInstalledApp;
  while not Result do begin
    if UninstallSilent then begin
      Log(ExitMessage);
      Exit;
    end;
    if MsgBox(ExitMessage, mbError, MB_RETRYCANCEL) <> IDRETRY then Exit;
    Result := StopInstalledApp;
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

procedure RemoveInstalledStartupValues;
var
  Names: TArrayOfString;
  I: Integer;
  Command, Exe, AppDirectory: String;
begin
  AppDirectory := AddBackslash(ExpandFileName(ExpandConstant('{app}')));
  if not RegGetValueNames(HKCU, RunKey, Names) then Exit;
  for I := 0 to GetArrayLength(Names) - 1 do begin
    if CompareText(Copy(Names[I], 1, 12), 'QuickLaunch_') = 0 then begin
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
      if MsgBox('是否同时删除 QuickLaunch 的配置、图标缓存和日志？默认保留。' + #13#10 +
          'Also delete QuickLaunch configuration, icon cache and logs? Data is kept by default.' + #13#10#13#10 +
          ExpandConstant('{userappdata}\QuickLaunch') + #13#10 + ExpandConstant('{localappdata}\QuickLaunch'),
          mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then begin
        if not DelTree(ExpandConstant('{userappdata}\QuickLaunch'), True, True, True) then
          Log('Could not completely delete the configuration directory.');
        if not DelTree(ExpandConstant('{localappdata}\QuickLaunch'), True, True, True) then
          Log('Could not completely delete the cache/log directory.');
      end;
  end;
end;
