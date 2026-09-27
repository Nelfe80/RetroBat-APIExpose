; ─────────────────────────────────────────────────────────────────────────────
; APIExpose - installeur de BORNE (Inno Setup)
; Installe le moteur dans <RetroBat>\plugins\APIExpose : exe unique, hook de
; démarrage EmulationStation, configuration préservée aux mises à jour.
; Le Data Pack (définitions .MEM + médias) se déploie séparément.
; Build préalable : build.bat (produit RetroBat.Api.exe à la racine du plugin).
; Compilation : ISCC.exe installer\CabinetSetup.iss
; ─────────────────────────────────────────────────────────────────────────────

#define AppName "APIExpose (borne RetroBat)"
#define AppVersion "1.9.7"
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
; PLUSIEURS RETROBAT SUR UN MEME PC (demande user 2026-09-27) : une page propose chacun de ceux
; trouves, et la page de dossier reste accessible par « Un autre dossier ». Par defaut, Inno
; Setup masque la page de dossier lors d'une mise a jour et reinstalle au meme endroit sans rien
; demander : c'est ce que DisableDirPage=no empeche (la page de choix decide de la montrer).
DisableDirPage=no
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

[CustomMessages]
french.RetroBatPageCaption=Choix du RetroBat
french.RetroBatPageDescription=Dans quel RetroBat installer APIExpose ?
french.RetroBatPageSubCaption=Plusieurs RetroBat peuvent cohabiter sur un même PC. APIExpose s'installe dans le dossier plugins de celui que vous choisissez.
french.RetroBatLatest=dernier RetroBat lancé
french.RetroBatHasApi=APIExpose %1 déjà installé
french.RetroBatOther=Un autre dossier (à choisir à l'étape suivante)
english.RetroBatPageCaption=Choose your RetroBat
english.RetroBatPageDescription=Which RetroBat should APIExpose be installed into?
english.RetroBatPageSubCaption=Several RetroBat installations can live on the same PC. APIExpose is installed in the plugins folder of the one you choose.
english.RetroBatLatest=last RetroBat launched
english.RetroBatHasApi=APIExpose %1 already installed
english.RetroBatOther=Another folder (chosen on the next step)

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

// ── LE CHOIX DU RETROBAT ─────────────────────────────────────────────────────────────────────
// Beaucoup de joueurs ont plusieurs RetroBat. La page liste ceux que retrobat-detect.iss trouve,
// avec ce qu'on sait de chacun (dernier lance, APIExpose deja installe et sa version), et une
// derniere option pour un dossier quelconque. En installation silencieuse elle n'apparait pas :
// /DIR= decide, sinon l'installation precedente, sinon le RetroBat prefere (DefaultDirName).
var
  RetroBatPage: TInputOptionWizardPage;
  RetroBatRoots: TArrayOfString;

// La version d'APIExpose deja installee dans ce RetroBat (« 1.9.7 »), « ? » si illisible, vide si absente.
function ApiExposeInstalledVersion(Root: String): String;
var
  Exe: String;
begin
  Result := '';
  Exe := AddBackslash(Root) + 'plugins\APIExpose\RetroBat.Api.exe';
  if not FileExists(Exe) then
    Exit;
  if not GetVersionNumbersString(Exe, Result) then
    Result := '?'
  else if (Length(Result) > 2) and (Copy(Result, Length(Result) - 1, 2) = '.0') then
    Result := Copy(Result, 1, Length(Result) - 2);
end;

function RetroBatLabel(Root, Latest: String): String;
var
  Notes, Version: String;
begin
  Notes := '';
  if (Latest <> '') and (CompareText(Root, Latest) = 0) then
    Notes := CustomMessage('RetroBatLatest');
  Version := ApiExposeInstalledVersion(Root);
  if Version <> '' then
  begin
    if Notes <> '' then
      Notes := Notes + ', ';
    Notes := Notes + FmtMessage(CustomMessage('RetroBatHasApi'), [Version]);
  end;
  Result := Root;
  if Notes <> '' then
    Result := Result + '   (' + Notes + ')';
end;

<event('InitializeWizard')>
procedure RetroBatInitializeWizard;
var
  I, Choix: Integer;
  Latest, Precedent: String;
begin
  RetroBatRoots := DetectRetroBatRoots();
  Latest := LatestKnownRetroBatRoot();
  // Une mise a jour : le RetroBat de l'installation precedente est propose, meme s'il vit a un
  // endroit que le scan ne parcourt pas, et il est coche par defaut.
  Precedent := '';
  if WizardForm.PrevAppDir <> '' then
  begin
    Precedent := RemoveBackslashUnlessRoot(ExtractFilePath(RemoveBackslashUnlessRoot(
      ExtractFilePath(RemoveBackslashUnlessRoot(WizardForm.PrevAppDir)))));
    AddRetroBatRoot(RetroBatRoots, Precedent);
  end;
  for I := 0 to GetArrayLength(RetroBatRoots) - 1 do
    Log('RetroBat trouve : ' + RetroBatLabel(RetroBatRoots[I], Latest));
  if GetArrayLength(RetroBatRoots) = 0 then
  begin
    Log('Aucun RetroBat trouve : page de dossier seule.');
    Exit;
  end;

  RetroBatPage := CreateInputOptionPage(wpWelcome, CustomMessage('RetroBatPageCaption'),
    CustomMessage('RetroBatPageDescription'), CustomMessage('RetroBatPageSubCaption'), True, False);
  Choix := 0;
  for I := 0 to GetArrayLength(RetroBatRoots) - 1 do
  begin
    RetroBatPage.Add(RetroBatLabel(RetroBatRoots[I], Latest));
    if (Precedent <> '') and (CompareText(RetroBatRoots[I], Precedent) = 0) then
      Choix := I;
  end;
  RetroBatPage.Add(CustomMessage('RetroBatOther'));
  RetroBatPage.SelectedValueIndex := Choix;
end;

// Vrai quand la page de choix designe un RetroBat trouve (et non « un autre dossier »).
function RetroBatChoisi(): Boolean;
begin
  Result := (RetroBatPage <> nil) and (RetroBatPage.SelectedValueIndex >= 0)
    and (RetroBatPage.SelectedValueIndex < GetArrayLength(RetroBatRoots));
end;

<event('NextButtonClick')>
function RetroBatNextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  // EN SILENCIEUX, JAMAIS : Inno appelle ce « Suivant » meme pour les pages qu'il n'affiche pas,
  // et le choix par defaut ecrasait /DIR= (essai du 2026-09-27 : installe dans E:\RetroBat au
  // lieu du dossier demande). /DIR=, l'installation precedente ou DefaultDirName decident seuls.
  if WizardSilent() then
    Exit;
  if (RetroBatPage <> nil) and (CurPageID = RetroBatPage.ID) and RetroBatChoisi() then
  begin
    WizardForm.DirEdit.Text := AddBackslash(RetroBatRoots[RetroBatPage.SelectedValueIndex]) + 'plugins\APIExpose';
    Log('RetroBat choisi : ' + WizardForm.DirEdit.Text);
  end;
end;

// La page de dossier ne sert que pour « un autre dossier », ou quand aucun RetroBat n'a ete trouve.
<event('ShouldSkipPage')>
function RetroBatShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = wpSelectDir) and RetroBatChoisi();
end;
