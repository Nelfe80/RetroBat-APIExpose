[Code]
// retrobat-detect.iss - DETECTION + VALIDATION du RetroBat cible, partagee par les
// installeurs de plugins RetroBat (APIExpose, MarqueeManager, LedManager, HubManager,
// RetroCreator). Un plugin DOIT vivre dans <RetroBat>\plugins\<Plugin> : le runtime
// resout RetroBat comme le grand-parent du dossier plugin, sans quoi les listes de
// jeux/systemes sont vides. Ce fichier :
//   - pre-remplit DefaultDirName sous le RetroBat detecte (au lieu de C:\RetroBat en dur) ;
//   - fournit AppParentIsRetroBat() pour AVERTIR si le dossier choisi n'est pas un RetroBat.
//
// IMPORTANT : commence DIRECTEMENT par [Code], uniquement des commentaires « // »
// (il peut etre #inclus apres un autre include finissant en [Code]). Ne definit AUCUNE
// procedure d'evenement (CurStepChanged reste propre a chaque installeur).

// Racine RetroBat detectee : d'abord un scan des lecteurs pour <lettre>:\RetroBat\retrobat.exe,
// sinon chaine vide (l'appelant retombe sur C:\RetroBat).
function DetectRetroBatRoot(): String;
var
  DriveNum: Integer;
  Candidate: String;
begin
  Result := '';
  for DriveNum := Ord('C') to Ord('Z') do
  begin
    Candidate := Chr(DriveNum) + ':\RetroBat';
    if FileExists(Candidate + '\retrobat.exe') then
    begin
      Result := Candidate;
      Exit;
    end;
  end;
end;

// Dossier d'installation par defaut du plugin : <RetroBat detecte>\plugins\<Param>.
// Usage dans le [Setup] : DefaultDirName={code:GetPluginInstallDir|MonPlugin}
function GetPluginInstallDir(Param: String): String;
var
  Root: String;
begin
  Root := DetectRetroBatRoot();
  if Root = '' then
    Root := 'C:\RetroBat';
  Result := AddBackslash(Root) + 'plugins\' + Param;
end;

// Vrai si le grand-parent du dossier d'installation ressemble a un vrai RetroBat
// (retrobat.exe ou dossier emulationstation present).
function AppParentIsRetroBat(): Boolean;
var
  GrandParent: String;
begin
  GrandParent := ExtractFilePath(RemoveBackslashUnlessRoot(
                   ExtractFilePath(RemoveBackslashUnlessRoot(ExpandConstant('{app}')))));
  Result := FileExists(GrandParent + 'retrobat.exe') or DirExists(GrandParent + 'emulationstation');
end;

// Message d'avertissement commun (a appeler depuis CurStepChanged de chaque installeur).
procedure WarnIfNotRetroBat();
begin
  if not AppParentIsRetroBat() then
    // SuppressibleMsgBox et non MsgBox : /SUPPRESSMSGBOXES ne supprime QUE celles-ci. Avec un
    // MsgBox, une installation /VERYSILENT hors d'un RetroBat attendait un clic invisible, sans
    // jamais copier un fichier (vecu en verifiant la 1.8.5).
    SuppressibleMsgBox('Le dossier choisi ne semble pas etre dans un RetroBat :'#13#10
      + ExpandConstant('{app}') + #13#10 + #13#10
      + 'Un plugin doit etre installe dans <RetroBat>\plugins\. Sinon les listes de'#13#10
      + 'jeux et systemes resteront vides. Verifiez l''emplacement (l''installation continue).',
      mbError, MB_OK, IDOK);
end;
