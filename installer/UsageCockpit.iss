; Instalador do Usage IA Cockpit (Inno Setup 6). Gerado pelo build.ps1 -Installer.
; Instala por usuário (sem pedir administrador) em %LOCALAPPDATA%\Programs\UsageCockpit.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist"
#endif

[Setup]
AppId={{6E4C2B5A-8F1D-4B7E-9C3A-2D5F7A1B9E40}
AppName=Usage IA Cockpit
AppVersion={#AppVersion}
AppPublisher=monitor-uso-ia
DefaultDirName={localappdata}\Programs\UsageCockpit
DefaultGroupName=Usage IA Cockpit
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=UsageCockpit-Setup-{#AppVersion}
SetupIconFile=..\UsageCockpit\Assets\app.ico
UninstallDisplayIcon={app}\UsageCockpit.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Iniciar com o Windows"; GroupDescription: "Opções:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\UsageCockpit.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Usage IA Cockpit"; Filename: "{app}\UsageCockpit.exe"
Name: "{group}\Desinstalar Usage IA Cockpit"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Usage IA Cockpit"; Filename: "{app}\UsageCockpit.exe"; Tasks: desktopicon

[Registry]
; Mesmo valor usado pela opção "Iniciar com o Windows" das configurações do app.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "UsageCockpit"; ValueData: """{app}\UsageCockpit.exe"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\UsageCockpit.exe"; Description: "Abrir o Usage IA Cockpit agora"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM UsageCockpit.exe /F"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
Type: dirifempty; Name: "{app}"
