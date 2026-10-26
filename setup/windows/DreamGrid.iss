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

[Icons]
; Runs as administrator (AfterInstall): only the service account, SYSTEM and Administrators can read the settings.
Name: "{autoprograms}\DreamGrid admin page"; Filename: "{app}\dotnet-{#RuntimeVersion}\dotnet.exe"; Parameters: """{app}\launcher\DreamGrid.Launcher.dll"" admin-link --open"; WorkingDir: "{app}\launcher"; Comment: "Opens your world's admin page in the browser."; AfterInstall: SetElevationBit('{autoprograms}\DreamGrid admin page.lnk')

[Run]
Filename: "{app}\dotnet-{#RuntimeVersion}\dotnet.exe"; Parameters: """{app}\launcher\DreamGrid.Launcher.dll"" admin-link --data ""{commonappdata}\DreamGrid"" --wait 120 --open"; WorkingDir: "{app}\launcher"; Description: "Open the setup page"; Flags: postinstall nowait runhidden runascurrentuser skipifsilent

[UninstallDelete]
; Runtime folders kept by an upgrade because a running process still used them.
Type: filesandordirs; Name: "{app}\dotnet-*"
Type: filesandordirs; Name: "{app}\mariadb-*"

[Code]
const
  ServiceName = 'DreamGrid';
  DefaultWebPort = 8001;

var
  WebPortParam: String; { the /WEBPORT value, '' when not given }

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

function RunHidden(const Filename, Params: String): Integer;
var
  Code: Integer;
begin
  if not Exec(Filename, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Log(Filename + ' ' + Params + ' -> ' + IntToStr(Code));
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

{ Stops the service (only the launcher: the world keeps running) and waits up to 60 s; true once it is stopped or absent. }
function StopService: Boolean;
begin
  Result := True;
  if not ServiceExists then
    exit;
  Sc('stop ' + ServiceName);
  Result := PowerShell('$s = Get-Service ' + ServiceName + ' -ErrorAction SilentlyContinue; ' +
    'if ($s) { try { $s.WaitForStatus(''Stopped'', ''00:01:00'') } catch { exit 1 } }; exit 0') = 0;
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

function EndsWith(const S, Suffix: String): Boolean;
begin
  Result := (Length(S) >= Length(Suffix)) and (Copy(S, Length(S) - Length(Suffix) + 1, Length(Suffix)) = Suffix);
end;

{ True when a TCP socket listens on Port. Reads netstat -ano by columns (protocol, local, foreign address): a listening
  socket's foreign address is 0.0.0.0:0 or [::]:0, which, unlike the state column, is not translated. }
function PortInUse(Port: Integer): Boolean;
var
  OutFile, Line, Proto, Local, Foreign: String;
  Lines: TArrayOfString;
  I, Code: Integer;
begin
  Result := False;
  OutFile := GenerateUniqueName(GetTempDir, '.txt');
  if not Exec(ExpandConstant('{cmd}'), '/S /C ""' + ExpandConstant('{sys}\netstat.exe') + '" -ano > "' + OutFile + '""',
    '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) or not LoadStringsFromFile(OutFile, Lines) then
  begin
    Log('netstat -ano could not be read; the web port is not checked.');
    DeleteFile(OutFile);
    exit;
  end;
  DeleteFile(OutFile);
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[I];
    Proto := NextToken(Line);
    Local := NextToken(Line);
    Foreign := NextToken(Line);
    if (CompareText(Proto, 'TCP') = 0) and EndsWith(Local, ':' + IntToStr(Port)) and
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

{ --- Install ------------------------------------------------------------------------------------------------------ }

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  NeedsRestart := False;
  Result := '';
  if not StopService then
    Result := 'The DreamGrid service did not stop within 60 seconds. Please try again in a minute.';
end;

{ Sets "Run as administrator" on a shortcut (bit 0x20 of the .lnk header's byte 21, in LinkFlags). }
procedure SetElevationBit(Filename: String);
var
  Buffer: AnsiString;
  Stream: TStream;
begin
  Filename := ExpandConstant(Filename);
  Stream := TFileStream.Create(Filename, fmOpenReadWrite);
  try
    Stream.Seek(21, soFromBeginning);
    SetLength(Buffer, 1);
    Stream.ReadBuffer(Buffer, 1);
    Buffer[1] := Chr(Ord(Buffer[1]) or $20);
    Stream.Seek(-1, soFromCurrent);
    Stream.WriteBuffer(Buffer, 1);
  finally
    Stream.Free;
  end;
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
    if EndsWith(Names[I], '.removing') then
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
  Common: String;
begin
  Common := ServiceName + ' start= auto obj= "NT SERVICE\' + ServiceName + '" DisplayName= "DreamGrid" binPath= ' +
    ScQuoted(ServiceCommand);
  { Created before its first start, so the launcher gives NT SERVICE\DreamGrid access to the data folder. }
  if ServiceExists then
    Sc('config ' + Common)
  else if Sc('create ' + Common) <> 0 then
    SuppressibleMsgBox('Windows did not accept the DreamGrid service. The setup log in ' + GetTempDir + ' says why.',
      mbCriticalError, MB_OK, IDOK);
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

  Sc('start ' + ServiceName);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SetUpService;
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
