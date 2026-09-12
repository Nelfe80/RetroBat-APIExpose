; ─────────────────────────────────────────────────────────────────────────────
; APIExpose - installeur de BORNE (Inno Setup)
; Installe le moteur dans <RetroBat>\plugins\APIExpose : exe unique, hook de
; démarrage EmulationStation, configuration préservée aux mises à jour.
; Le Data Pack (définitions .MEM + médias) se déploie séparément.
; Build préalable : build.bat (produit RetroBat.Api.exe à la racine du plugin).
; Compilation : ISCC.exe installer\CabinetSetup.iss
; ─────────────────────────────────────────────────────────────────────────────

#define AppName "APIExpose (borne RetroBat)"
#define AppVersion "1.8.4"
#define AppExe "RetroBat.Api.exe"

[Setup]
AppId={{4E9A11C2-0B77-4A0D-9A55-APIEXPOSE001}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=NelfeTech
AppPublisherURL=https://www.nelfetech.com
; La cible est le dossier plugins du RetroBat de la borne. DefaultDirName est resolu
; par [Code] (retrobat-detect.iss) : RetroBat detecte sur les lecteurs, sinon C:\RetroBat.
DefaultDirName={code:GetPluginInstallDir|APIExpose}
DirExistsWarning=no
AppendDefaultDirName=no
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=APIExpose-Cabinet-Setup
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
; APIExpose COMPLET (= contenu de full.7z) : moteur + .installer (hooks ES) +
; resources (packs gamelist « Data Pack » + lighting) + wrapper + tools utilisés
; (imagemagick pour la generation de marquees, translateLocally pour les descriptions).
; PAS ffmpeg : 98 Mo qu'aucune ligne du code n'appelle - verifie sur src/**/*.cs,
; appsettings.json et les .ini. Retire tant qu'un usage reel ne le ramene pas. JAMAIS media (bibliothèque
; média locale, construite sur la borne), ni src/docs/wiki/state/.env/secrets/
; sources de curation. Excludes calqués sur release.ps1 (+ appsettings à part).
; Les scripts .ps1/.py de tools/ sont de l'outillage interne (curation, exploitation,
; sondes) : ni le runtime ni l'utilisateur n'en a besoin, et ils ne sont pas publics.
Source: "..\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; \
    Excludes: "\src\*,\tests\*,\docs\*,\wiki\*,\media\*,\state\*,\artifacts\*,\dist\*,\installer\*,\.git\*,\.github\*,\.log\*,\.temp\*,\.cache\*,\.archive\*,\.versioning\*,\site\*,\package-installer\*,\projects-source\*,\resources\outputs\*,\resources\panels\*,\resources\controls\retroarch\mame\*,\resources\ra\*,\resources\ram\.user\*,\resources\history\history.db,\resources\colors\colors.ini,\resources\command\command.dat,\tools\mem-curator\*,\tools\libretro-probe\*,\.env,\wrapper\.env,\events.ini,\appsettings.json,\mkdocs.yml,\build.bat,\release.ps1,\CabinetSetup.iss,\publish-tmp\*,\panel_curator*,\profiles_db*,*.log,*.pdb,*.g.cs,__pycache__\*,*.pyc,*.ps1,*.py,*.bak,ScreenScraper.html,\tools\ffmpeg\*,\tools\translateLocally\*.exe,\tools\translateLocally\models\*"
; L'etat de la borne n'est JAMAIS ecrase : ni appsettings.json (cle API, options), ni
; wrapper\.env (drapeaux DISCOVERY et PERSO - PERSO=1 = test de .MEM perso en cours ;
; APIExpose recree ce fichier au demarrage). Meme regle que l'archive .7z.
; La configuration de la borne n'est JAMAIS écrasée (clé API, options overlay)
Source: "..\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[Dirs]
; état local (sessions RA, sauvegardes de config) - préservé à la désinstallation
Name: "{app}\state"; Flags: uninsneveruninstall

[Run]
Filename: "{app}\install-es-start-hook.bat"; WorkingDir: "{app}"; Description: "Démarrer APIExpose avec RetroBat (hook EmulationStation)"; Flags: postinstall skipifsilent
Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Description: "Démarrer APIExpose maintenant"; Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
Filename: "taskkill"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "StopApi"

; Detection/validation du RetroBat cible (DefaultDirName + avertissement si mauvais dossier).
#include "retrobat-detect.iss"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    WarnIfNotRetroBat();
end;
