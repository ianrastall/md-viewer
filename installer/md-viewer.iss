; md-viewer installer (Inno Setup 6). Built by scripts\release.ps1, which passes the version and folders.
; Installs for the current user without administrator rights: Start Menu entry, optional desktop icon,
; "Open with" for Markdown files (the default app is not changed), and an uninstaller in Settings > Apps.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\release\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif

[Setup]
AppId={{89FAEFD2-19DC-47F0-B634-BDA46A141433}
AppName=md-viewer
AppVersion={#AppVersion}
AppVerName=md-viewer {#AppVersion}
AppPublisher=Ian Rastall
AppPublisherURL=https://github.com/ianrastall/md-viewer
AppSupportURL=https://github.com/ianrastall/md-viewer/issues
AppUpdatesURL=https://github.com/ianrastall/md-viewer/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\md-viewer
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=md-viewer-{#AppVersion}-setup
SetupIconFile=..\src\MdViewer\Assets\AppIcon.ico
UninstallDisplayIcon={app}\MdViewer.exe
UninstallDisplayName=md-viewer
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
#ifdef Sign
; scripts\release.ps1 defines the "mdviewer" sign tool when a code-signing certificate is available.
SignTool=mdviewer
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\md-viewer"; Filename: "{app}\MdViewer.exe"; Comment: "Markdown reader and converter"
Name: "{autodesktop}\md-viewer"; Filename: "{app}\MdViewer.exe"; Tasks: desktopicon

[Registry]
; "Open with" entries for Markdown files. HKA is the current user (or the machine, for an all-users install).
Root: HKA; Subkey: "Software\Classes\md-viewer.Markdown"; ValueType: string; ValueData: "Markdown document"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\md-viewer.Markdown\DefaultIcon"; ValueType: string; ValueData: "{app}\MdViewer.exe,0"
Root: HKA; Subkey: "Software\Classes\md-viewer.Markdown\shell\open\command"; ValueType: string; ValueData: """{app}\MdViewer.exe"" ""%1"""
Root: HKA; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "md-viewer.Markdown"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.markdown\OpenWithProgids"; ValueType: string; ValueName: "md-viewer.Markdown"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.mdown\OpenWithProgids"; ValueType: string; ValueName: "md-viewer.Markdown"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.mkd\OpenWithProgids"; ValueType: string; ValueName: "md-viewer.Markdown"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\MdViewer.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "md-viewer"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\MdViewer.exe\shell\open\command"; ValueType: string; ValueData: """{app}\MdViewer.exe"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\MdViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".md"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\MdViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".markdown"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\MdViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".mdown"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\MdViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".mkd"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\MdViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".txt"; ValueData: ""

[Run]
Filename: "{app}\MdViewer.exe"; Description: "{cm:LaunchProgram,md-viewer}"; Flags: nowait postinstall skipifsilent
