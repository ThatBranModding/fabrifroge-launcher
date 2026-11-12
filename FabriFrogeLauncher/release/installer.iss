#define AppName "FabriFrogeLauncher"
#define AppPublisher "FabriFroge"
#define AppExeName "FabriFrogeLauncher.exe"

; AppVersion comes from ISCC /DAppVersion=...
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

; SourceDir comes from ISCC /DSourceDir="...\release\out\launcher"
#ifndef SourceDir
  #define SourceDir ".\out\launcher"
#endif

[Setup]
AppId={{8E6A6E9A-1B6A-4B0F-9C8C-7B2A0F8B2A11}  ; keep constant forever
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\FabriFroge\FabriFrogeLauncher
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=..\..\artifacts\release
OutputBaseFilename=FabriFrogeLauncher-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64 arm64
ArchitecturesInstallIn64BitMode=x64 arm64
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#AppExeName}

[Files]
; Install EXACTLY what dotnet publish produced
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\FabriFroge Launcher"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\FabriFroge Launcher"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch FabriFroge Launcher"; Flags: nowait postinstall skipifsilent
