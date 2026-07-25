; Inno Setup script for Soundstage.
;
; Built against the single-file publish output in ..\publish\. The app is self-contained, so this
; installs one executable plus its shortcuts — no runtime prerequisite, no component tree.
;
;   dotnet publish src/Soundstage.Shell -c Release -r win-x64 --self-contained ^
;     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
;   iscc installer\Soundstage.iss
;
; PrivilegesRequired=lowest on purpose: Soundstage writes only to the user's own profile, and asking
; for admin to install an audio utility is the kind of thing that makes people close the window.

#define AppName "Soundstage"
#define AppVersion "1.1.1"
#define AppExe "Soundstage.exe"
#define AppPublisher "Soundstage"
#define AppUrl "https://github.com/random12one0/SoundStage"

[Setup]
AppId={{8B6E2D1C-7A94-4A14-9D30-5C51A1E64B10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=Soundstage-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\assets\soundstage.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
CloseApplicationsFilter=*.exe
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

; The audio plugin and its installer. Shipped as plain files beside the exe rather than bundled into
; it, because install-apo.ps1 looks for the DLL in its own directory, and because turning plugin mode
; on is a separate elevated step the user takes later from Settings - not part of this install.
Source: "..\native\soundstage-apo\SoundstageApo.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\native\soundstage-apo\install-apo.ps1"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup"; Description: "Start {#AppName} when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Registry]
; Matches what the app's own "Launch at login" toggle writes, so the two agree rather than fighting.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The app's settings live here; leaving them behind after an uninstall is just litter.
Type: filesandordirs; Name: "{userappdata}\{#AppName}"
