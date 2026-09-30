; Inno Setup 6 script for the per-user installer of YAV Shell.
;
; This file is installer CONFIGURATION. It becomes an installer only when it is compiled:
;     scripts\package.ps1          (compiles it when Inno Setup 6 is installed)
;     iscc /DAppVersion=0.1.1 /DSourceDir=<package directory> /DOutputDir=<dist> installer\yav-shell.iss
;
; The installer it produces is not code-signed unless you sign it yourself (SignTool in Inno Setup).
; It installs for the current user only and never asks for administrator rights.

#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\yav-shell-" + AppVersion + "-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{6D0B6B0C-6C1B-4E0B-9C2E-5A3D2F7B8A41}
AppName=YAV Shell
AppVersion={#AppVersion}
AppVerName=YAV Shell {#AppVersion}
AppPublisher=YAV
AppCopyright=Copyright (c) 2026 Yavinesh Rajagopal. All rights reserved.
LicenseFile={#SourceDir}\LICENSE.txt
DefaultDirName={localappdata}\Programs\YavShell
DefaultGroupName=YAV Shell
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=yav-shell-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes
UninstallDisplayIcon={app}\yav.exe
UninstallDisplayName=YAV Shell
InfoBeforeFile={#SourceDir}\docs\security-boundaries.md
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "addtopath"; Description: "Add YAV Shell to the PATH of my user account, so that 'yav' works in every console"; GroupDescription: "Optional:"; Flags: unchecked
Name: "startmenu"; Description: "Create a Start menu entry"; GroupDescription: "Optional:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\YAV Shell"; Filename: "{app}\yav.exe"; WorkingDir: "{%USERPROFILE}"; Tasks: startmenu

[Run]
Filename: "{app}\yav.exe"; Parameters: "--version"; Flags: runhidden waituntilterminated; StatusMsg: "Checking that YAV Shell starts..."

[UninstallDelete]
; Only what the installation created. The user's data in {localappdata}\YavShell is never removed by the uninstaller.
Type: dirifempty; Name: "{app}"

[Code]
const
  EnvironmentKey = 'Environment';

function Comparable(const Value: string): string;
var
  Text: string;
begin
  Text := Trim(Value);
  if (Length(Text) >= 2) and (Text[1] = '"') and (Text[Length(Text)] = '"') then
    Text := Copy(Text, 2, Length(Text) - 2);
  Text := ExpandConstant(Text);
  while (Length(Text) > 0) and (Text[Length(Text)] = '\') do
    Delete(Text, Length(Text), 1);
  Result := Lowercase(Text);
end;

// The PATH of the user is changed as text: parts are neither reordered nor rewritten.
function WithEntry(const Path, Entry: string; Add: Boolean): string;
var
  Rest, Part, Kept: string;
  Position: Integer;
  Found: Boolean;
begin
  Rest := Path;
  Kept := '';
  Found := False;
  while Length(Rest) > 0 do
  begin
    Position := Pos(';', Rest);
    if Position = 0 then
    begin
      Part := Rest;
      Rest := '';
    end
    else
    begin
      Part := Copy(Rest, 1, Position - 1);
      Rest := Copy(Rest, Position + 1, Length(Rest));
    end;

    if Comparable(Part) = Comparable(Entry) then
    begin
      Found := True;
      if Add then
      begin
        if Length(Kept) > 0 then Kept := Kept + ';';
        Kept := Kept + Part;
      end;
    end
    else if Length(Part) > 0 then
    begin
      if Length(Kept) > 0 then Kept := Kept + ';';
      Kept := Kept + Part;
    end;
  end;

  if Add and not Found then
  begin
    if Length(Kept) > 0 then Kept := Kept + ';';
    Kept := Kept + Entry;
  end;

  Result := Kept;
end;

procedure ChangePath(Add: Boolean);
var
  Path, Changed: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, EnvironmentKey, 'Path', Path) then
    Path := '';
  Changed := WithEntry(Path, ExpandConstant('{app}'), Add);
  if Changed <> Path then
    RegWriteExpandStringValue(HKEY_CURRENT_USER, EnvironmentKey, 'Path', Changed);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    ChangePath(True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    ChangePath(False);
end;
