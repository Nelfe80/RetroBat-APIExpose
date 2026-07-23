; ─────────────────────────────────────────────────────────────────────────────
; APIExpose — installeur de BORNE (Inno Setup)
; Installe le moteur dans <RetroBat>\plugins\APIExpose : exe unique, hook de
; démarrage EmulationStation, configuration préservée aux mises à jour.
; Le Data Pack (définitions .MEM + médias) se déploie séparément.
; Build préalable : build.bat (produit RetroBat.Api.exe à la racine du plugin).
; Compilation : ISCC.exe installer\CabinetSetup.iss
; ─────────────────────────────────────────────────────────────────────────────

#define AppName "APIExpose (borne RetroBat)"
#define AppVersion "1.3.6"
#define AppExe "RetroBat.Api.exe"

[Setup]
AppId={{4E9A11C2-0B77-4A0D-9A55-APIEXPOSE001}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=NelfeTech
AppPublisherURL=https://www.nelfetech.com
; La cible est le dossier plugins du RetroBat de la borne.
DefaultDirName=C:\RetroBat\plugins\APIExpose
DirExistsWarning=no
AppendDefaultDirName=no
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=APIExpose-Cabinet-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=yes
CloseApplications=yes
WizardStyle=modern

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
french.SelectDirDesc=Choisissez le dossier plugins\APIExpose de VOTRE RetroBat (ex. D:\RetroBat\plugins\APIExpose).

[Files]
Source: "..\RetroBat.Api.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\install-es-start-hook.bat"; DestDir: "{app}"; Flags: ignoreversion
; La configuration de la borne n'est JAMAIS écrasée (clé API, options overlay)
Source: "..\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[Dirs]
; état local (sessions RA, sauvegardes de config) — préservé à la désinstallation
Name: "{app}\state"; Flags: uninsneveruninstall

[Run]
Filename: "{app}\install-es-start-hook.bat"; WorkingDir: "{app}"; Description: "Démarrer APIExpose avec RetroBat (hook EmulationStation)"; Flags: postinstall skipifsilent
Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Description: "Démarrer APIExpose maintenant"; Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
Filename: "taskkill"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "StopApi"
