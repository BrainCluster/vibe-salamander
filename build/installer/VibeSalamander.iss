; SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
; SPDX-License-Identifier: GPL-3.0-or-later
;
; The Windows installer (Inno Setup 6.3 or later), made by the release workflow from the published folder:
;
;   ISCC.exe /DVersion=0.8.0 /DArch=x64 /DSource=publish\VibeSalamander /DOutput=dist build\installer\VibeSalamander.iss
;
; It installs for the current user without asking for administrator rights (into %LocalAppData%\Programs), or for
; all users into Program Files when chosen. Start menu entry, an optional desktop icon, an uninstaller. The settings
; stay in %AppData%\VibeSalamander (the uninstaller leaves them). installed.flag tells the program it was installed,
; so Help → Check for Updates updates it with the next installer, which it runs as:
;
;   VibeSalamander-<version>-win-x64-setup.exe /SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /relaunch=1

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef Source
  #define Source "..\..\publish\VibeSalamander"
#endif
#ifndef Output
  #define Output "..\..\dist"
#endif

[Setup]
AppId={{FD1096E8-9977-4668-B1A2-196FC6091581}
AppName=Vibe Salamander
AppVersion={#Version}
AppVerName=Vibe Salamander {#Version}
AppPublisher=BrainCluster
AppPublisherURL=https://github.com/BrainCluster/vibe-salamander
AppSupportURL=https://github.com/BrainCluster/vibe-salamander/issues
AppUpdatesURL=https://github.com/BrainCluster/vibe-salamander/releases
AppCopyright=Copyright (c) 2026 Vibe Salamander Authors
VersionInfoVersion={#Version}
DefaultDirName={autopf}\Vibe Salamander
DefaultGroupName=Vibe Salamander
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableReadyPage=yes
; for me (no administrator rights) or for everyone (Program Files)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041
WizardStyle=modern
SetupIconFile=..\..\src\VibeSalamander.App\Assets\VibeSalamander.ico
UninstallDisplayIcon={app}\VibeSalamander.exe
UninstallDisplayName=Vibe Salamander
OutputDir={#Output}
OutputBaseFilename=VibeSalamander-{#Version}-win-{#Arch}-setup
Compression=lzma2/max
SolidCompression=yes
; an update closes the running program (it has closed itself already, normally)
CloseApplications=force
RestartApplications=no
; "vibesalamander" on the PATH: Windows is told the environment changed
ChangesEnvironment=yes
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "cs"; MessagesFile: "compiler:Languages\Czech.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "nl"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; Windows integration (roadmap S3): each off by default, each taken away again by the uninstaller
Name: "explorermenu"; Description: "Add ""Open in Vibe Salamander"" to Explorer's menu for folders and drives"; GroupDescription: "Windows integration:"; Flags: unchecked
Name: "addtopath"; Description: "Start it from a command prompt: vibesalamander <folder>"; GroupDescription: "Windows integration:"; Flags: unchecked
Name: "wine"; Description: "Win+E opens Vibe Salamander instead of File Explorer (for this user)"; GroupDescription: "Windows integration:"; Flags: unchecked

[InstallDelete]
; the previous version's program files (a file a new version no longer has must not stay behind)
Type: filesandordirs; Name: "{app}\lib"
; an installed copy is never portable
Type: files; Name: "{app}\portable.flag"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installed.flag"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Vibe Salamander"; Filename: "{app}\VibeSalamander.exe"
Name: "{autodesktop}\Vibe Salamander"; Filename: "{app}\VibeSalamander.exe"; Tasks: desktopicon

[Registry]
; "Open in Vibe Salamander" on a folder, on the background of an open folder, and on a drive (Windows 11 shows it
; under "Show more options"); a running Vibe Salamander opens the folder in a new tab
Root: HKA; Subkey: "Software\Classes\Directory\shell\VibeSalamander"; ValueType: string; ValueName: ""; ValueData: "Open in Vibe Salamander"; Flags: uninsdeletekey; Tasks: explorermenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\VibeSalamander"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\VibeSalamander.exe"; Tasks: explorermenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\VibeSalamander\command"; ValueType: string; ValueName: ""; ValueData: """{app}\VibeSalamander.exe"" ""%V"""; Tasks: explorermenu
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\VibeSalamander"; ValueType: string; ValueName: ""; ValueData: "Open in Vibe Salamander"; Flags: uninsdeletekey; Tasks: explorermenu
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\VibeSalamander"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\VibeSalamander.exe"; Tasks: explorermenu
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\VibeSalamander\command"; ValueType: string; ValueName: ""; ValueData: """{app}\VibeSalamander.exe"" ""%V"""; Tasks: explorermenu
Root: HKA; Subkey: "Software\Classes\Drive\shell\VibeSalamander"; ValueType: string; ValueName: ""; ValueData: "Open in Vibe Salamander"; Flags: uninsdeletekey; Tasks: explorermenu
Root: HKA; Subkey: "Software\Classes\Drive\shell\VibeSalamander"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\VibeSalamander.exe"; Tasks: explorermenu
Root: HKA; Subkey: "Software\Classes\Drive\shell\VibeSalamander\command"; ValueType: string; ValueName: ""; ValueData: """{app}\VibeSalamander.exe"" ""%1"""; Tasks: explorermenu

; Win+E: the user's own override of "This PC"'s "open new window" (Windows' own, for all users, stays untouched);
; the uninstaller removes only what was added here, key by key
Root: HKCU; Subkey: "Software\Classes\CLSID\{{52205fd8-5dfb-447d-801a-d0b52f2e83e1}"; Flags: uninsdeletekeyifempty; Tasks: wine
Root: HKCU; Subkey: "Software\Classes\CLSID\{{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell"; Flags: uninsdeletekeyifempty; Tasks: wine
Root: HKCU; Subkey: "Software\Classes\CLSID\{{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow"; Flags: uninsdeletekeyifempty; Tasks: wine
Root: HKCU; Subkey: "Software\Classes\CLSID\{{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command"; ValueType: string; ValueName: ""; ValueData: """{app}\VibeSalamander.exe"""; Flags: uninsdeletekey; Tasks: wine
Root: HKCU; Subkey: "Software\Classes\CLSID\{{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command"; ValueType: string; ValueName: "DelegateExecute"; ValueData: ""; Tasks: wine

[Run]
; after an interactive install: "Launch Vibe Salamander"
Filename: "{app}\VibeSalamander.exe"; Description: "{cm:LaunchProgram,Vibe Salamander}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; after an update from Help → Check for Updates (silent, /relaunch=1): the program starts again
Filename: "{app}\VibeSalamander.exe"; Flags: nowait runasoriginaluser; Check: Relaunch

[Code]
function Relaunch: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

{ "vibesalamander" on the PATH: the program's folder added to the user's PATH (or everyone's, for an install for
  all users), once; the uninstaller takes it out again. }
function PathRoot: Integer;
begin
  if IsAdminInstallMode then Result := HKEY_LOCAL_MACHINE else Result := HKEY_CURRENT_USER;
end;

function PathKey: String;
begin
  if IsAdminInstallMode then
    Result := 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
  else
    Result := 'Environment';
end;

function PathHas(Paths, Dir: String): Boolean;
begin
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0;
end;

procedure AddToPath;
var
  Paths: String;
begin
  if not RegQueryStringValue(PathRoot, PathKey, 'Path', Paths) then
    Paths := '';
  if PathHas(Paths, ExpandConstant('{app}')) then
    exit;
  if (Paths <> '') and (Copy(Paths, Length(Paths), 1) <> ';') then
    Paths := Paths + ';';
  RegWriteExpandStringValue(PathRoot, PathKey, 'Path', Paths + ExpandConstant('{app}'));
end;

procedure RemoveFromPath;
var
  Paths, Dir: String;
  P: Integer;
begin
  if not RegQueryStringValue(PathRoot, PathKey, 'Path', Paths) then
    exit;
  Dir := ExpandConstant('{app}');
  Paths := ';' + Paths + ';';
  P := Pos(';' + Uppercase(Dir) + ';', Uppercase(Paths));
  if P = 0 then
    exit;
  Delete(Paths, P, Length(Dir) + 1);
  Paths := Copy(Paths, 2, Length(Paths) - 2);
  RegWriteExpandStringValue(PathRoot, PathKey, 'Path', Paths);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddToPath;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveFromPath;
end;
