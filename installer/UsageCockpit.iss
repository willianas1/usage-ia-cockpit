; Instalador do Usage IA Cockpit (Inno Setup 6). Gerado pelo build.ps1 -Installer.
; Instala por usuário (sem pedir administrador) em %LOCALAPPDATA%\Programs\UsageIACockpit.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\app"
#endif
#define AppExe "UsageIACockpit.exe"

[Setup]
; AppId fixo: novas versões atualizam a instalação existente em vez de criar outra.
AppId={{6E4C2B5A-8F1D-4B7E-9C3A-2D5F7A1B9E40}
AppName=Usage IA Cockpit
AppVersion={#AppVersion}
AppVerName=Usage IA Cockpit {#AppVersion}
AppPublisher=willianas1
AppPublisherURL=https://github.com/willianas1/usage-ia-cockpit
DefaultDirName={localappdata}\Programs\UsageIACockpit
DefaultGroupName=Usage IA Cockpit
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=UsageIACockpit-Setup-{#AppVersion}
SetupIconFile=..\UsageCockpit\Assets\app.ico
UninstallDisplayName=Usage IA Cockpit
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
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

[InstallDelete]
; Versões até a 0.2.0 eram um exe único com outro nome; a pasta inteira é substituída.
Type: files; Name: "{app}\UsageCockpit.exe"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Usage IA Cockpit"; Filename: "{app}\{#AppExe}"
Name: "{group}\Desinstalar Usage IA Cockpit"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Usage IA Cockpit"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Mesmo valor usado pela opção "Iniciar com o Windows" das configurações do app.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "UsageCockpit"; ValueData: """{app}\{#AppExe}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir o Usage IA Cockpit agora"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
Type: dirifempty; Name: "{app}"
