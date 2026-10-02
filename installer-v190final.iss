[Setup]
AppId={{B02D8CE0-6CC0-4B6F-9495-8A57F67B5601}
AppName=Android Multi Game Manager
AppVersion=1.9.0
DefaultDirName={autopf}\AndroidMultiGameManager
DefaultGroupName=Android Multi Game Manager
OutputDir=C:\Temp\AGMMInstaller_v190
OutputBaseFilename=AndroidMultiGameManager-1.9.0-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=force
RestartApplications=no
CloseApplicationsFilter=AndroidMultiGameManager.exe
SetupLogging=yes
UsePreviousAppDir=yes
DirExistsWarning=no

[Files]
Source: "publish-v190final\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Android Multi Game Manager"; Filename: "{app}\AndroidMultiGameManager.exe"
Name: "{autodesktop}\Android Multi Game Manager"; Filename: "{app}\AndroidMultiGameManager.exe"

[Run]
Filename: "{app}\AndroidMultiGameManager.exe"; Description: "프로그램 실행"; Flags: nowait postinstall skipifsilent