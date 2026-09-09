#define MyAppName "LinkWeaver"
#define MyAppVersion "2.2.7"
#define MyAppPublisher "Abse2001"
#define MyAppExeName "LinkWeaver.exe"

[Setup]
AppId={{D4EE322A-80CF-4377-B7A9-FBE079E2BA32}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
SetupIconFile=..\src\DualLink.App\Assets\AppIcon.ico
DefaultDirName={autopf}\LinkWeaver
DefaultGroupName=LinkWeaver
PrivilegesRequired=admin
UsePreviousAppDir=yes
UsePreviousGroup=no
CloseApplications=force
RestartApplications=no
CloseApplicationsFilter=DualLink.exe,LinkWeaver.exe
CreateUninstallRegKey=yes
Uninstallable=yes
OutputDir=..\artifacts
OutputBaseFilename=LinkWeaver-Setup-x64
Compression=zip
SolidCompression=no
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\DualLink.exe"
Type: files; Name: "{autoprograms}\DualLink.lnk"
Type: files; Name: "{autodesktop}\DualLink.lnk"

[Icons]
Name: "{autoprograms}\LinkWeaver"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\LinkWeaver"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch LinkWeaver"; Flags: nowait postinstall skipifsilent
