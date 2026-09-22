; ─────────────────────────────────────────────────────────────────────────────
; APIExpose - installeur de BORNE (Inno Setup)
; Installe le moteur dans <RetroBat>\plugins\APIExpose : exe unique, hook de
; démarrage EmulationStation, configuration préservée aux mises à jour.
; Le Data Pack (définitions .MEM + médias) se déploie séparément.
; Build préalable : build.bat (produit RetroBat.Api.exe à la racine du plugin).
; Compilation : ISCC.exe installer\CabinetSetup.iss
; ─────────────────────────────────────────────────────────────────────────────

#define AppName "APIExpose (borne RetroBat)"
#define AppVersion "1.8.24"
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
    Excludes: "\src\*,\tests\*,\docs\*,\wiki\*,\media\*,\state\*,\artifacts\*,\dist\*,\installer\*,\.git\*,\.github\*,\.log\*,\.temp\*,\.cache\*,\.archive\*,\.versioning\*,\site\*,\package-installer\*,\projects-source\*,\resources\outputs\*,\resources\panels\*,\resources\gamelist\localized\*,\resources\controls\retroarch\mame\*,\resources\ra\*,\resources\ram\.user\*,\resources\history\history.db,\resources\colors\colors.ini,\resources\command\command.dat,\tools\mem-curator\*,\tools\libretro-probe\*,\.env,\wrapper\.env,\wrapper\certified.txt,\events.ini,\appsettings.json,\mkdocs.yml,\build.bat,\release.ps1,\CabinetSetup.iss,\publish-tmp\*,\panel_curator*,\profiles_db*,*.log,*.pdb,*.g.cs,__pycache__\*,*.pyc,*.ps1,*.py,*.bak,ScreenScraper.html,\tools\ffmpeg\*,\tools\translateLocally\*.exe,\tools\translateLocally\models\*"
; L'etat de la borne n'est JAMAIS ecrase : ni appsettings.json (cle API, options), ni
; wrapper\.env (drapeaux DISCOVERY et PERSO - PERSO=1 = test de .MEM perso en cours ;
; APIExpose recree ce fichier au demarrage). Meme regle que l'archive .7z.
; La configuration de la borne n'est JAMAIS écrasée (clé API, options overlay)
; La configuration livree vient du DEPOT, pas de la machine qui compile. Le fichier a la
; racine est celui de la borne de developpement : sa langue, ses collections, ses reglages
; d'essai, et les reecritures du service de synchro. Le livrer revenait a installer la
; configuration d'une machine particuliere chez tous ceux qui installent le plugin.
; `appsettings.default.json` est produit par tools\build-default-settings.ps1, qui le tire de
; HEAD : il ne peut donc pas deriver en silence.
Source: "appsettings.default.json"; DestDir: "{app}"; DestName: "appsettings.json"; Flags: onlyifdoesntexist uninsneveruninstall

[Dirs]
; état local (sessions RA, sauvegardes de config) - préservé à la désinstallation
Name: "{app}\state"; Flags: uninsneveruninstall

[Run]
; Le hook EmulationStation n'est plus pose par install-es-start-hook.bat : lance ici avec
; skipifsilent, il etait saute en /VERYSILENT et finissait sur une pause. [Code] le copie.
Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Description: "Démarrer APIExpose maintenant"; Flags: postinstall nowait skipifsilent unchecked
; L'outil de diagnostic (depot APIExposeDiagnostic) : il dit pourquoi APIExpose ne demarre pas.
Filename: "{app}\RetroBat.Api.Diagnostic.exe"; WorkingDir: "{app}"; Description: "Diagnostiquer le démarrage d'APIExpose"; Flags: postinstall nowait skipifsilent unchecked; Check: DiagnosticToolPresent

[UninstallRun]
Filename: "taskkill"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "StopApi"

; Detection/validation du RetroBat cible (DefaultDirName + avertissement si mauvais dossier).
#include "retrobat-detect.iss"

// Prerequis .NET 8 (ASP.NET Core + Desktop, x64) : detectes, telecharges et installes au besoin.
// (Commentaire en // : apres l'include de retrobat-detect.iss, on est deja dans [Code].)
#include "dotnet-runtime.iss"

[Code]
// La case « Diagnostiquer le demarrage » n'apparait que si l'outil a ete livre.
function DiagnosticToolPresent(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\RetroBat.Api.Diagnostic.exe'));
end;

// Dossier des scripts de demarrage d'EmulationStation du RetroBat cible.
function EsStartHookDir(): String;
begin
  Result := ExtractFilePath(RemoveBackslashUnlessRoot(ExtractFilePath(RemoveBackslashUnlessRoot(ExpandConstant('{app}')))))
    + 'emulationstation\.emulationstation\scripts\start';
end;

// Le hook qui fait attendre EmulationStation au demarrage : pose par l'installeur lui-meme,
// y compris en installation silencieuse, et jamais hors d'un RetroBat.
procedure InstallEsStartHook();
var
  Source, Target: String;
begin
  if not AppParentIsRetroBat() then
  begin
    Log('Hook EmulationStation non pose : le dossier n''est pas dans un RetroBat.');
    Exit;
  end;
  Source := ExpandConstant('{app}\.installer\scripts\start\APIExpose-start-wait.bat');
  Target := EsStartHookDir() + '\APIExpose-start-wait.bat';
  if not ForceDirectories(EsStartHookDir()) then
    Log('Hook EmulationStation : dossier impossible a creer : ' + EsStartHookDir())
  else if CopyFile(Source, Target, False) then
    Log('Hook EmulationStation pose : ' + Target)
  else
    Log('Hook EmulationStation NON pose : copie impossible vers ' + Target);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    WarnIfNotRetroBat();
  if CurStep = ssPostInstall then
    InstallEsStartHook();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Target: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    Target := EsStartHookDir() + '\APIExpose-start-wait.bat';
    if FileExists(Target) and DeleteFile(Target) then
      Log('Hook EmulationStation retire : ' + Target);
  end;
end;
