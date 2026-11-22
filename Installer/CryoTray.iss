; CryoTray installer script (Inno Setup 6)
; Build from the repository root with build-installer.ps1, or directly:
;   ISCC.exe /DAppVersion=0.0.103 Installer\CryoTray.iss

#ifndef AppVersion
#define AppVersion "0.0.103"
#endif

[Setup]
AppId={{8F3B9A12-4D6E-4C2B-9A1F-7E5D3C1B8A90}
AppName=CryoTray
AppVersion={#AppVersion}
AppVerName=CryoTray {#AppVersion}
AppPublisher=Aryan
AppPublisherURL=https://github.com/hackerlemonaru/CryoTray
AppSupportURL=https://github.com/hackerlemonaru/CryoTray/issues
AppUpdatesURL=https://github.com/hackerlemonaru/CryoTray/releases
DefaultDirName={autopf}\CryoTray
DefaultGroupName=CryoTray
; Installed copies must be elevated from a location standard users cannot modify.
DisableDirPage=yes
UsePreviousAppDir=no
LicenseFile=..\LICENSE
SetupIconFile=..\Images\CryoTray.ico
UninstallDisplayIcon={app}\CryoTray.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
PrivilegesRequired=admin
OutputDir=Output
OutputBaseFilename=CryoTray-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; LICENSE, NOTICE and VERSION are copied explicitly below, so exclude them from the recursive copy to avoid duplicate destinations.
; Keep the full publish output for local debugging. The installer excludes framework language satellites
; CryoTray does not use and debugger-only runtime files. Remove a language folder from this list when it becomes supported.
; Do not add createallsubdirs: it recreates the excluded satellite folders as empty directories.
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs; Excludes: "*.pdb,PawnIO_setup.exe,LICENSE,NOTICE,VERSION,\cs\*,\de\*,\es\*,\fr\*,\it\*,\ja\*,\ko\*,\pl\*,\pt-BR\*,\ru\*,\tr\*,\zh-Hans\*,\zh-Hant\*,\mscordaccore.dll,\mscordaccore_amd64_amd64_*.dll,\mscordbi.dll,\Microsoft.DiaSymReader.Native.amd64.dll"

Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NOTICE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\VERSION"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Resources\PawnIO_setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Dirs]
Name: "{app}"; Permissions: admins-full system-full users-readexec

[InstallDelete]
; Remove empty language folders left by earlier installers. dirifempty leaves non-empty folders alone.
Type: files; Name: "{app}\PawnIO_setup.exe"
Type: dirifempty; Name: "{app}\cs"
Type: dirifempty; Name: "{app}\de"
Type: dirifempty; Name: "{app}\es"
Type: dirifempty; Name: "{app}\fr"
Type: dirifempty; Name: "{app}\it"
Type: dirifempty; Name: "{app}\ja"
Type: dirifempty; Name: "{app}\ko"
Type: dirifempty; Name: "{app}\pl"
Type: dirifempty; Name: "{app}\pt-BR"
Type: dirifempty; Name: "{app}\ru"
Type: dirifempty; Name: "{app}\tr"
Type: dirifempty; Name: "{app}\zh-Hans"
Type: dirifempty; Name: "{app}\zh-Hant"

[Icons]
Name: "{group}\CryoTray"; Filename: "{app}\CryoTray.exe"
Name: "{autodesktop}\CryoTray"; Filename: "{app}\CryoTray.exe"; Tasks: desktopicon

[Run]
Filename: "{tmp}\PawnIO_setup.exe"; Parameters: "-install -silent"; StatusMsg: "Installing PawnIO hardware sensor driver..."; Flags: runhidden waituntilterminated; Check: not PawnIOInstalled
Filename: "{sys}\icacls.exe"; Parameters: """{app}"" /setowner *S-1-5-32-544 /T /C /Q"; StatusMsg: "Protecting the CryoTray application folder..."; Flags: runhidden waituntilterminated
Filename: "{app}\CryoTray.exe"; Description: "{cm:LaunchProgram,CryoTray}"; Flags: shellexec nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""CryoTray"" /F"; Flags: runhidden; RunOnceId: "DeleteStartupTask"

[Code]
var
  PreviousAppDir: String;

function GetFileAttributesW(lpFileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function IsReparsePointPath(const Path: String): Boolean;
var
  Attrs: Cardinal;
begin
  Attrs := GetFileAttributesW(Path);
  Result := (Attrs <> $FFFFFFFF) and ((Attrs and $00000400) <> 0);
end;

function PawnIOInstalled: Boolean;
begin
  Result := RegKeyExists(HKEY_LOCAL_MACHINE, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

function GetPreviousInstallLocation: String;
var
  Value: String;
begin
  Result := '';
  if IsWin64 and
     RegQueryStringValue(HKLM64,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F3B9A12-4D6E-4C2B-9A1F-7E5D3C1B8A90}_is1',
       'InstallLocation', Value) then
  begin
    Result := RemoveBackslash(Value);
    Exit;
  end;

  if IsWin64 and
     RegQueryStringValue(HKLM32,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F3B9A12-4D6E-4C2B-9A1F-7E5D3C1B8A90}_is1',
       'InstallLocation', Value) then
  begin
    Result := RemoveBackslash(Value);
    Exit;
  end;

  if RegQueryStringValue(HKLM,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F3B9A12-4D6E-4C2B-9A1F-7E5D3C1B8A90}_is1',
       'InstallLocation', Value) then
  begin
    Result := RemoveBackslash(Value);
  end;
end;

function IsPathInside(const ChildPath, ParentPath: String): Boolean;
var
  Child, Parent: String;
begin
  Child := RemoveBackslash(ChildPath);
  Parent := RemoveBackslash(ParentPath);
  if Length(Child) <= Length(Parent) then
  begin
    Result := False;
    Exit;
  end;

  Result := CompareText(Copy(Child, 1, Length(Parent)), Parent) = 0;
  if Result then
    Result := Child[Length(Parent) + 1] = '\';
end;

function IsSafePreviousInstall(const Path: String): Boolean;
var
  Candidate, AppDir, WindowsDir, SystemDir: String;
begin
  Result := False;
  if Path = '' then
    Exit;

  Candidate := RemoveBackslash(ExpandFileName(Path));
  if (Candidate = '') or (Length(Candidate) <= 3) or (Pos('..', Path) > 0) then
    Exit;

  if IsReparsePointPath(Candidate) then
  begin
    Log('Previous location rejected: reparse point: ' + Candidate);
    Exit;
  end;

  if not FileExists(AddBackslash(Candidate) + 'CryoTray.exe') or
     (not FileExists(AddBackslash(Candidate) + 'unins000.exe') and
      not FileExists(AddBackslash(Candidate) + 'unins001.exe')) then
  begin
    Log('Previous location rejected: executable or uninstaller missing: ' + Candidate);
    Exit;
  end;

  AppDir := RemoveBackslash(ExpandFileName(ExpandConstant('{app}')));
  WindowsDir := RemoveBackslash(ExpandFileName(ExpandConstant('{win}')));
  SystemDir := RemoveBackslash(ExpandFileName(ExpandConstant('{sys}')));

  if (CompareText(Candidate, AppDir) = 0) or
     (CompareText(Candidate, WindowsDir) = 0) or
     (CompareText(Candidate, SystemDir) = 0) or
     IsPathInside(Candidate, WindowsDir) or
     IsPathInside(Candidate, SystemDir) or
     IsPathInside(AppDir, Candidate) or
     IsPathInside(Candidate, AppDir) then
  begin
    Log('Previous location rejected: protected, root, or overlapping path: ' + Candidate);
    Exit;
  end;

  Log('Previous location accepted for cleanup: ' + Candidate);
  Result := True;
end;

procedure StopCryoTray;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM CryoTray.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function IsUpdateInstall: Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    if CompareText(ParamStr(Index), '/PRESERVEUPDATE') = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  PreviousAppDir := GetPreviousInstallLocation;
  Log('Previous CryoTray install location: ' + PreviousAppDir);
  StopCryoTray;
  if not IsUpdateInstall then
  begin
    DeleteFile(ExpandConstant('{commonappdata}\CryoTray\Update\last-update.json'));
  end;
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    Log('Post-install previous location: ' + PreviousAppDir);
    if IsSafePreviousInstall(PreviousAppDir) then
    begin
      Log('Removing previous CryoTray installation: ' + PreviousAppDir);
      DelTree(PreviousAppDir, True, True, True);
    end;
  end;
end;

function InitializeUninstall: Boolean;
begin
  StopCryoTray;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  ExePath: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    ExePath := ExpandConstant('{app}\CryoTray.exe');
    if not FileExists(ExePath) or
       not Exec(ExePath, '/uninstall-cleanup', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or
       (ResultCode <> 0) then
    begin
      Exec(ExpandConstant('{sys}\powercfg.exe'), '/setactive 381b4222-f694-41f0-9685-ff5bb260df2e', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;
