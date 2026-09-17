#ifndef PackageDir
  #error PackageDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif

[Setup]
AppId={{7C73BD4C-BFC6-482E-94E2-757650B823A2}
AppName=ControllerBridge
AppVersion={#AppVersion}
AppPublisher=ControllerBridge
DefaultDirName={localappdata}\Programs\ControllerBridge
DefaultGroupName=ControllerBridge
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=ControllerBridge-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\BridgeManager.Modern.App.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
DisableProgramGroupPage=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ControllerBridge"; Filename: "{app}\BridgeManager.Modern.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\ControllerBridge"; Filename: "{app}\BridgeManager.Modern.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\BridgeManager.Modern.App.exe"; Description: "{cm:LaunchProgram,ControllerBridge}"; Flags: nowait postinstall skipifsilent
