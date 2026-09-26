#ifndef MyAppName
  #define MyAppName "K.netagent"
#endif
#ifndef MyAppDisplayName
  #define MyAppDisplayName "K.netagentV0.1"
#endif
#define MyAppPublisher "kirisue"
#ifndef MyAppExeName
  #define MyAppExeName "KNetAgent.exe"
#endif
#ifndef MyAppId
  #define MyAppId "{{74C0F60B-2D53-4E3A-A71F-3980323A2D6A}"
#endif
#ifndef MyInstallDirectory
  #define MyInstallDirectory "K.netagent"
#endif
#ifndef MyOutputFlavor
  #define MyOutputFlavor ""
#endif

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef MyVersionInfo
  #define MyVersionInfo "0.1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif
#ifndef RepoRoot
  #define RepoRoot ".."
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppDisplayName}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyVersionInfo}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Windows-native desktop Agent installer
VersionInfoProductName={#MyAppName}
DefaultDirName={userpf}\{#MyInstallDirectory}
DefaultGroupName={#MyAppDisplayName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=k-netagent-{#MyAppVersion}{#MyOutputFlavor}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile={#RepoRoot}\LICENSE
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
#ifdef MyMinVersion
MinVersion={#MyMinVersion}
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppDisplayName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppDisplayName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
#ifdef MyWindowsPageShortcuts
Name: "{autoprograms}\{#MyAppDisplayName} - Windows Events"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--page events"; WorkingDir: "{app}"
Name: "{autoprograms}\{#MyAppDisplayName} - Windows Services"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--page services"; WorkingDir: "{app}"
#endif

[Run]
Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Description: "Launch {#MyAppDisplayName}"; Flags: nowait postinstall skipifsilent
