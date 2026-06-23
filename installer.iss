; Imprime+ — instalador tradicional (Inno Setup)
; Instala en Archivos de programa, con icono, accesos directos y menu contextual
; "Imprimir con Imprime+" en imagenes.

#define MyApp "Imprime+"
#define MyVer "2.3.2"
#define MyExe "ImprimePlus.exe"

[Setup]
AppId={{8F3A1C2E-9B4D-4E6A-A1F2-3C5D7E9B0A11}
AppName={#MyApp}
AppVersion={#MyVer}
AppPublisher=UTP Honduras
AppPublisherURL=https://imprime.utp.hn
DefaultDirName={autopf}\Imprime+
DefaultGroupName=Imprime+
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyExe}
UninstallDisplayName=Imprime+ {#MyVer}
OutputDir=installer-out
OutputBaseFilename=ImprimePlus-Setup-{#MyVer}
SetupIconFile=Assets\AppIcon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos:"
Name: "ctxmenu"; Description: "Agregar 'Imprimir con Imprime+' al menu de clic derecho en imagenes"; GroupDescription: "Integracion:"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Imprime+"; Filename: "{app}\{#MyExe}"
Name: "{group}\Desinstalar Imprime+"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Imprime+"; Filename: "{app}\{#MyExe}"; Tasks: desktopicon

[Registry]
; Menu contextual "Imprimir con Imprime+" en tipos de imagen (no cambia la app por defecto).
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\ImprimePlus"; ValueType: string; ValueData: "Imprimir con Imprime+"; Flags: uninsdeletekey; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\ImprimePlus"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyExe},0"; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\ImprimePlus\command"; ValueType: string; ValueData: """{app}\{#MyExe}"" ""%1"""; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\ImprimePlus"; ValueType: string; ValueData: "Imprimir con Imprime+"; Flags: uninsdeletekey; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\ImprimePlus"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyExe},0"; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\ImprimePlus\command"; ValueType: string; ValueData: """{app}\{#MyExe}"" ""%1"""; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\ImprimePlus"; ValueType: string; ValueData: "Imprimir con Imprime+"; Flags: uninsdeletekey; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\ImprimePlus"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyExe},0"; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\ImprimePlus\command"; ValueType: string; ValueData: """{app}\{#MyExe}"" ""%1"""; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\ImprimePlus"; ValueType: string; ValueData: "Imprimir con Imprime+"; Flags: uninsdeletekey; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\ImprimePlus\command"; ValueType: string; ValueData: """{app}\{#MyExe}"" ""%1"""; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.gif\shell\ImprimePlus"; ValueType: string; ValueData: "Imprimir con Imprime+"; Flags: uninsdeletekey; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.gif\shell\ImprimePlus\command"; ValueType: string; ValueData: """{app}\{#MyExe}"" ""%1"""; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.tif\shell\ImprimePlus"; ValueType: string; ValueData: "Imprimir con Imprime+"; Flags: uninsdeletekey; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.tif\shell\ImprimePlus\command"; ValueType: string; ValueData: """{app}\{#MyExe}"" ""%1"""; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.tiff\shell\ImprimePlus"; ValueType: string; ValueData: "Imprimir con Imprime+"; Flags: uninsdeletekey; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.tiff\shell\ImprimePlus\command"; ValueType: string; ValueData: """{app}\{#MyExe}"" ""%1"""; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.webp\shell\ImprimePlus"; ValueType: string; ValueData: "Imprimir con Imprime+"; Flags: uninsdeletekey; Tasks: ctxmenu
Root: HKLM; Subkey: "Software\Classes\SystemFileAssociations\.webp\shell\ImprimePlus\command"; ValueType: string; ValueData: """{app}\{#MyExe}"" ""%1"""; Tasks: ctxmenu

[Run]
Filename: "{app}\{#MyExe}"; Description: "Abrir Imprime+ ahora"; Flags: nowait postinstall skipifsilent
