; DreamGrid installer for Windows 10/11 x64 (Inno Setup 6.3 or later). Built by build.ps1, which stages the package and
; passes the defines below:
;   ISCC /DAppVersion=<v> /DRuntimeVersion=<aspnetcore runtime> /DMariaDbVersion=<v> /DSource=<staging> DreamGrid.iss
;
; Installs the launcher as the DreamGrid Windows service (account NT SERVICE\DreamGrid). Program files go to
; {app}; the data in {commonappdata}\DreamGrid is never written, moved or deleted by setup or uninstall. The .NET
; runtime and MariaDB live in versioned folders beside each other (dotnet-<v>\, mariadb-<v>\): the world's processes
; keep running through a service stop and an upgrade, and still use the folders they started from.
;
; Command line: /WEBPORT=<n> serves the admin website on port <n> instead of 8001.
; Exit code 10: the files are installed but the service could not be set up or started.

#ifndef AppVersion
  #error AppVersion is not defined: build with build.ps1 (ISCC /DAppVersion=...)
#endif
#ifndef RuntimeVersion
  #error RuntimeVersion is not defined: build with build.ps1
#endif
#ifndef MariaDbVersion
  #error MariaDbVersion is not defined: build with build.ps1
#endif
#ifndef Source
  #error Source (the staging folder) is not defined: build with build.ps1
#endif

[Setup]
AppId={{DC03D674-08A7-42DF-AC65-F9045BF5707F}
AppName=DreamGrid
AppVersion={#AppVersion}
AppVerName=DreamGrid {#AppVersion}
AppPublisher=DreamGrid
DefaultDirName={autopf}\DreamGrid
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; No Restart Manager and no reboot (Review Focus 5): the service is stopped in PrepareToInstall, and the world's
; processes only hold the versioned runtime folders, which an upgrade never overwrites.
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
OutputBaseFilename=DreamGridSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
UninstallDisplayName=DreamGrid

[Messages]
FinishedLabel=DreamGrid is installed and running.%n%nWindows may ask whether DreamGrid can use the network; allow it for private networks.
FinishedLabelNoIcons=DreamGrid is installed and running.%n%nWindows may ask whether DreamGrid can use the network; allow it for private networks.

[InstallDelete]
; The program folders are replaced as a whole, so no file of an older version is left behind.
Type: filesandordirs; Name: "{app}\launcher"
Type: filesandordirs; Name: "{app}\bin"
Type: filesandordirs; Name: "{app}\content"

[Files]
Source: "{#Source}\launcher\*"; DestDir: "{app}\launcher"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Source}\bin\*"; DestDir: "{app}\bin"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Source}\content\*"; DestDir: "{app}\content"; Flags: ignoreversion recursesubdirs createallsubdirs
; A running database, grid service or region may hold these: an existing folder of the same version is left as is.
Source: "{#Source}\dotnet-{#RuntimeVersion}\*"; DestDir: "{app}\dotnet-{#RuntimeVersion}"; Flags: onlyifdoesntexist recursesubdirs createallsubdirs
Source: "{#Source}\mariadb-{#MariaDbVersion}\*"; DestDir: "{app}\mariadb-{#MariaDbVersion}"; Flags: onlyifdoesntexist recursesubdirs createallsubdirs
Source: "{#Source}\VERSION"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Source}\RUNTIME_VERSION"; DestDir: "{app}"; Flags: ignoreversion

[INI]
; Start menu: a plain Internet shortcut to the admin page, opened in the user's own browser without elevation.
Filename: "{autoprograms}\DreamGrid admin page.url"; Section: "InternetShortcut"; Key: "URL"; String: "http://localhost:{code:WebPortValue}/"

[Run]
; Opened as the user who started setup (postinstall runs as the original user), with the link read in [Code].
Filename: "{code:SetupLink}"; Description: "Open the setup page"; Flags: postinstall shellexec nowait skipifsilent; Check: ServiceRunning

[UninstallDelete]
Type: files; Name: "{autoprograms}\DreamGrid admin page.url"
; Runtime folders kept by an upgrade because a running process still used them.
Type: filesandordirs; Name: "{app}\dotnet-*"
Type: filesandordirs; Name: "{app}\mariadb-*"

[Code]
const
  ServiceName = 'DreamGrid';
  DefaultWebPort = 8001;
  ServiceFailedExitCode = 10;

var
  WebPortParam: String; { the /WEBPORT value, '' when not given }
  Link: String;         { the admin page link for the finish page, '' until read }
  ServiceError: String; { what went wrong with the service, '' when it was set up and started }

function DataDir: String;
begin
  Result := ExpandConstant('{commonappdata}\DreamGrid');
end;

function DotnetExe: String;
begin
  Result := ExpandConstant('{app}\dotnet-{#RuntimeVersion}\dotnet.exe');
end;

function LauncherDll: String;
begin
  Result := ExpandConstant('{app}\launcher\DreamGrid.Launcher.dll');
end;

{ The host options the service and stop-grid share. }
function LauncherArgs: String;
begin
  Result := '--data "' + DataDir + '" --opensim "' + ExpandConstant('{app}\bin') + '" --mariadb-bin "' +
    ExpandConstant('{app}\mariadb-{#MariaDbVersion}\bin') + '"';
end;

function ServiceCommand: String;
begin
  Result := '"' + DotnetExe + '" "' + LauncherDll + '" ' + LauncherArgs;
  if WebPortParam <> '' then
    Result := Result + ' --web-port ' + WebPortParam;
end;

function FirewallRuleName: String;
begin
  Result := 'DreamGrid (dotnet {#RuntimeVersion})';
end;

function ServiceExists: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\' + ServiceName);
end;

function HasSuffix(const S, Suffix: String): Boolean;
begin
  Result := (Length(S) >= Length(Suffix)) and (Copy(S, Length(S) - Length(Suffix) + 1, Length(Suffix)) = Suffix);
end;

function RunHidden(const Filename, Params: String): Integer;
var
  Code: Integer;
begin
  if not Exec(Filename, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Log(Filename + ' ' + Params + ' -> ' + IntToStr(Code));
  Result := Code;
end;

{ Runs Command through cmd.exe with its output in a temp file, which is read into Lines and deleted at once (it may
  hold the setup token). Returns the exit code, -1 when it could not run. Nothing of the output is logged. }
function Capture(const Command: String; var Lines: TArrayOfString): Integer;
var
  OutFile: String;
  Code: Integer;
begin
  SetArrayLength(Lines, 0);
  OutFile := GenerateUniqueName(GetTempDir, '.txt');
  if not Exec(ExpandConstant('{cmd}'), '/S /C "' + Command + ' > "' + OutFile + '""', '', SW_HIDE,
    ewWaitUntilTerminated, Code) then
    Code := -1
  else if not LoadStringsFromFile(OutFile, Lines) then
    SetArrayLength(Lines, 0);
  DeleteFile(OutFile);
  Result := Code;
end;

function Sc(const Params: String): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\sc.exe'), Params);
end;

function Netsh(const Params: String): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall ' + Params);
end;

{ Command must not contain double quotes. }
function PowerShell(const Command: String): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Command + '"');
end;

{ One sc.exe argument: the value in quotes, quotes inside it escaped. }
function ScQuoted(const S: String): String;
var
  T: String;
begin
  T := S;
  StringChangeEx(T, '"', '\"', True);
  Result := '"' + T + '"';
end;

{ Stops the service (only the launcher: the world keeps running). Waits up to 60 s for the Stopped state and then up
  to 60 s more for the launcher's process to end, since the service reports Stopped before dotnet.exe has exited and
  released the launcher's files. True once both happened or the service is absent. }
function StopService: Boolean;
begin
  Result := True;
  if not ServiceExists then
    exit;
  Result := PowerShell('$svc = Get-CimInstance Win32_Service | Where-Object Name -eq ' + ServiceName + '; ' +
    'if (-not $svc) { exit 0 }; ' +
    'sc.exe stop ' + ServiceName + ' | Out-Null; ' +
    'try { (Get-Service ' + ServiceName + ').WaitForStatus(''Stopped'', ''00:01:00'') } catch { exit 1 }; ' +
    'if ($svc.ProcessId) { $p = Get-Process -Id $svc.ProcessId -ErrorAction SilentlyContinue; ' +
    'if ($p -and -not $p.WaitForExit(60000)) { exit 1 } }; exit 0') = 0;
end;

{ --- Web port and links ------------------------------------------------------------------------------------------ }

{ The webPort saved in settings.json (setup runs elevated and may read it), or 0 when there is none. }
function SettingsWebPort: Integer;
var
  Raw: AnsiString;
  Text, Digits: String;
  I: Integer;
begin
  Result := 0;
  if not LoadStringFromFile(DataDir + '\settings.json', Raw) then
    exit;
  Text := Raw;
  I := Pos('"webPort"', Text);
  if I = 0 then
    exit;
  I := I + Length('"webPort"');
  while (I <= Length(Text)) and ((Text[I] = ' ') or (Text[I] = ':') or (Text[I] = #9) or (Text[I] = #13) or (Text[I] = #10)) do
    I := I + 1;
  Digits := '';
  while (I <= Length(Text)) and (Text[I] >= '0') and (Text[I] <= '9') do
  begin
    Digits := Digits + Text[I];
    I := I + 1;
  end;
  Result := StrToIntDef(Digits, 0);
end;

{ The admin website's port: /WEBPORT, else the saved one, else 8001. }
function WebPortValue(Param: String): String;
begin
  if WebPortParam <> '' then
    Result := WebPortParam
  else if SettingsWebPort > 0 then
    Result := IntToStr(SettingsWebPort)
  else
    Result := IntToStr(DefaultWebPort);
end;

{ The setup link (or the admin page once set up) from admin-link, run elevated since only elevated processes can read
  the settings; the address of the admin page when it gives none. }
function SetupLink(Param: String): String;
var
  Lines: TArrayOfString;
  Code: Integer;
begin
  if Link = '' then
  begin
    Code := Capture('"' + DotnetExe + '" "' + LauncherDll + '" admin-link --data "' + DataDir + '" --wait 120', Lines);
    Log('admin-link -> ' + IntToStr(Code));
    if (Code = 0) and (GetArrayLength(Lines) > 0) and (Pos('http://', Trim(Lines[0])) = 1) then
      Link := Trim(Lines[0])
    else
      Link := 'http://localhost:' + WebPortValue('') + '/';
  end;
  Result := Link;
end;

function ServiceRunning: Boolean;
begin
  Result := ServiceError = '';
end;

{ --- Busy web port ------------------------------------------------------------------------------------------------ }

function NextToken(var S: String): String;
var
  P: Integer;
begin
  S := TrimLeft(S);
  P := Pos(' ', S);
  if P = 0 then
  begin
    Result := S;
    S := '';
  end
  else
  begin
    Result := Copy(S, 1, P - 1);
    S := Copy(S, P + 1, Length(S));
  end;
end;

{ True when a TCP socket listens on Port. Reads netstat -ano by columns (protocol, local, foreign address): a listening
  socket's foreign address is 0.0.0.0:0 or [::]:0, which, unlike the state column, is not translated. }
function PortInUse(Port: Integer): Boolean;
var
  Line, Proto, Local, Foreign: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if (Capture('"' + ExpandConstant('{sys}\netstat.exe') + '" -ano', Lines) <> 0) or (GetArrayLength(Lines) = 0) then
  begin
    Log('netstat -ano could not be read; the web port is not checked.');
    exit;
  end;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[I];
    Proto := NextToken(Line);
    Local := NextToken(Line);
    Foreign := NextToken(Line);
    if (CompareText(Proto, 'TCP') = 0) and HasSuffix(Local, ':' + IntToStr(Port)) and
      ((Foreign = '0.0.0.0:0') or (Foreign = '[::]:0')) then
    begin
      Log('Listening on port ' + IntToStr(Port) + ': ' + Lines[I]);
      Result := True;
      exit;
    end;
  end;
end;

function InitializeSetup: Boolean;
var
  Port: Integer;
  Msg: String;
begin
  Result := True;
  WebPortParam := Trim(ExpandConstant('{param:WEBPORT|}'));
  Port := DefaultWebPort;
  if WebPortParam <> '' then
  begin
    Port := StrToIntDef(WebPortParam, 0);
    if (Port < 1) or (Port > 65535) then
    begin
      Msg := '/WEBPORT needs a port number between 1 and 65535.';
      Log(Msg);
      SuppressibleMsgBox(Msg, mbCriticalError, MB_OK, IDOK);
      Result := False;
      exit;
    end;
    WebPortParam := IntToStr(Port);
  end;
  { An upgrade finds the port taken by DreamGrid itself, so only a fresh install checks it. }
  if not ServiceExists and PortInUse(Port) then
  begin
    Msg := 'Port ' + IntToStr(Port) + ' is already used by another program.' + #13#10#13#10 +
      'Close that program, or run this installer with /WEBPORT=8101 to use another port.';
    Log(Msg);
    SuppressibleMsgBox(Msg, mbCriticalError, MB_OK, IDOK);
    Result := False;
  end;
end;

{ --- Install folder ----------------------------------------------------------------------------------------------- }

function DirIsEmpty(const Dir: String): Boolean;
var
  Rec: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Dir) + '*', Rec) then
  try
    repeat
      if (Rec.Name <> '.') and (Rec.Name <> '..') then
        Result := False;
    until not Result or not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

{ '' when Dir can take DreamGrid's program files, else the reason it cannot. The program folder is emptied on every
  install, so it must not be (or be inside) the data folder, nor a folder of other files. }
function CheckInstallDir(const Dir: String): String;
begin
  Result := '';
  if Pos(Lowercase(AddBackslash(DataDir)), Lowercase(AddBackslash(Dir))) = 1 then
    Result := 'DreamGrid cannot be installed in its data folder (' + DataDir + '). Please choose another folder.'
  else if DirExists(Dir) and not FileExists(AddBackslash(Dir) + 'VERSION') and
    not FileExists(AddBackslash(Dir) + 'launcher\DreamGrid.Launcher.dll') and not DirIsEmpty(Dir) then
    Result := 'The folder ' + Dir + ' already has other files in it. Please choose an empty or new folder for DreamGrid.';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Msg: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Msg := CheckInstallDir(WizardDirValue);
    if Msg <> '' then
    begin
      SuppressibleMsgBox(Msg, mbError, MB_OK, IDOK);
      Result := False;
    end;
  end;
end;

{ --- Install ------------------------------------------------------------------------------------------------------ }

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  NeedsRestart := False;
  { Again here for silent installs (/DIR=), which skip the folder page; before [InstallDelete] empties anything. }
  Result := CheckInstallDir(ExpandConstant('{app}'));
  if Result <> '' then
    exit;
  if not StopService then
    Result := 'The DreamGrid service did not stop within 60 seconds. Please try again in a minute.';
end;

{ Deletes the program folder's <Prefix>* folders other than Current. A folder that a running process still uses
  cannot be renamed, so each is renamed first and only then deleted: one in use is kept whole (not half-deleted
  under a running region), and a later upgrade tries again. }
procedure RemoveOldFolders(const Prefix, Current: String);
var
  Rec: TFindRec;
  Names: TArrayOfString;
  I: Integer;
  Path, Doomed: String;
begin
  if FindFirst(ExpandConstant('{app}\' + Prefix + '*'), Rec) then
  try
    repeat
      if ((Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (CompareText(Rec.Name, Current) <> 0) then
      begin
        SetArrayLength(Names, GetArrayLength(Names) + 1);
        Names[GetArrayLength(Names) - 1] := Rec.Name;
      end;
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    Path := ExpandConstant('{app}\') + Names[I];
    if HasSuffix(Names[I], '.removing') then
      Doomed := Path
    else
      Doomed := Path + '.removing';
    if (Doomed <> Path) and not RenameFile(Path, Doomed) then
      Log('Kept ' + Path + ': still in use.')
    else if not DelTree(Doomed, True, True, True) then
      Log('Could not delete ' + Doomed + ' completely.');
  end;
end;

procedure SetUpService;
var
  Common, Msg: String;
  Code: Integer;
begin
  Common := ServiceName + ' start= auto obj= "NT SERVICE\' + ServiceName + '" DisplayName= "DreamGrid" binPath= ' +
    ScQuoted(ServiceCommand);
  { Created before its first start, so the launcher gives NT SERVICE\DreamGrid access to the data folder. }
  if ServiceExists then
    Code := Sc('config ' + Common)
  else
    Code := Sc('create ' + Common);
  if Code <> 0 then
    ServiceError := 'its Windows service could not be set up (error ' + IntToStr(Code) + ')';
  Sc('description ' + ServiceName + ' "Runs your DreamGrid world and its admin website."');
  Sc('failure ' + ServiceName + ' reset= 86400 actions= restart/5000/restart/5000/restart/60000');

  if Netsh('show rule name="' + FirewallRuleName + '"') <> 0 then
    Netsh('add rule name="' + FirewallRuleName + '" dir=in action=allow program="' + DotnetExe + '" profile=private,domain');

  RemoveOldFolders('dotnet-', 'dotnet-{#RuntimeVersion}');
  RemoveOldFolders('mariadb-', 'mariadb-{#MariaDbVersion}');
  { Rules for runtime folders that are gone. }
  PowerShell('Get-NetFirewallRule -DisplayName ''DreamGrid (dotnet *)'' -ErrorAction SilentlyContinue | ForEach-Object { ' +
    '$p = ($_ | Get-NetFirewallApplicationFilter).Program; ' +
    'if (-not $p -or -not (Test-Path -LiteralPath (Split-Path -Parent $p))) { $_ | Remove-NetFirewallRule } }');

  if ServiceError = '' then
  begin
    Code := Sc('start ' + ServiceName);
    if Code <> 0 then
      ServiceError := 'its Windows service could not be started (error ' + IntToStr(Code) + ')';
  end;

  if ServiceError <> '' then
  begin
    Msg := 'DreamGrid is installed, but ' + ServiceError + '. Restart the computer and run this installer again. ' +
      'If that does not help, the setup log in ' + GetTempDir + ' says what went wrong.';
    Log(Msg);
    SuppressibleMsgBox(Msg, mbCriticalError, MB_OK, IDOK);
  end
  else if not WizardSilent then
    { Read now, while the progress page shows, rather than after Finish is clicked. }
    SetupLink('');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SetUpService;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and (ServiceError <> '') then
    WizardForm.FinishedLabel.Caption := 'DreamGrid is installed, but ' + ServiceError + '.' + #13#10#13#10 +
      'Restart the computer and run this installer again.';
end;

{ A silent install whose service failed must not report success. }
function GetCustomSetupExitCode: Integer;
begin
  if ServiceError <> '' then
    Result := ServiceFailedExitCode
  else
    Result := 0;
end;

{ --- Uninstall ---------------------------------------------------------------------------------------------------- }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
  Msg: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if not StopService then
      Log('The DreamGrid service did not stop within 60 seconds.');
    { The world goes down with the program: database, grid service and region. }
    if not Exec(DotnetExe, '"' + LauncherDll + '" stop-grid ' + LauncherArgs, ExpandConstant('{app}\launcher'),
      SW_HIDE, ewWaitUntilTerminated, Code) then
      Code := -1;
    Log('stop-grid -> ' + IntToStr(Code));
    Sc('delete ' + ServiceName);
    PowerShell('Remove-NetFirewallRule -DisplayName ''DreamGrid (dotnet *)'' -ErrorAction SilentlyContinue');
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    Msg := 'DreamGrid is removed. Your world''s data is still in ' + DataDir +
      '. Delete that folder if you no longer need it.';
    Log(Msg);
    SuppressibleMsgBox(Msg, mbInformation, MB_OK, IDOK);
  end;
end;
