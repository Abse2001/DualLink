#define MyAppName "DualLink"
#define MyAppVersion "2.2.2"
#define MyAppPublisher "Abse2001"
#define MyAppExeName "DualLink.exe"

[Setup]
AppId={{D4EE322A-80CF-4377-B7A9-FBE079E2BA32}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
SetupIconFile=..\src\DualLink.App\Assets\AppIcon.ico
DefaultDirName={autopf}\DualLink
DefaultGroupName=DualLink
PrivilegesRequired=admin
UsePreviousAppDir=yes
UsePreviousGroup=yes
CloseApplications=force
RestartApplications=no
CloseApplicationsFilter=DualLink.exe
CreateUninstallRegKey=yes
Uninstallable=yes
OutputDir=..\artifacts
OutputBaseFilename=DualLink-Setup-x64
Compression=zip
SolidCompression=no
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\DualLink"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\DualLink"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch DualLink"; Flags: nowait postinstall skipifsilent
