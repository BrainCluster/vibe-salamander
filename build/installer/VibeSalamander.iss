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
