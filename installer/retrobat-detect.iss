[Code]
// retrobat-detect.iss - DETECTION + VALIDATION du RetroBat cible, partagee par les
// installeurs de plugins RetroBat (APIExpose, MarqueeManager, LedManager, HubManager,
// RetroCreator). Un plugin DOIT vivre dans <RetroBat>\plugins\<Plugin> : le runtime
// resout RetroBat comme le grand-parent du dossier plugin, sans quoi les listes de
// jeux/systemes sont vides. Ce fichier :
//   - trouve TOUS les RetroBat de la machine (DetectRetroBatRoots), parce que beaucoup de
//     joueurs en ont plusieurs et que l'installeur doit leur laisser le choix ;
//   - pre-remplit DefaultDirName sous le RetroBat prefere (installation silencieuse) ;
//   - fournit AppParentIsRetroBat() pour AVERTIR si le dossier choisi n'est pas un RetroBat.
//
// IMPORTANT : commence DIRECTEMENT par [Code], uniquement des commentaires « // »
// (il peut etre #inclus apres un autre include finissant en [Code]). Ne definit AUCUNE
// procedure d'evenement (CurStepChanged reste propre a chaque installeur).

function RetroBatGetDriveType(lpRootPathName: String): Cardinal;
  external 'GetDriveTypeW@kernel32.dll stdcall';

// Un dossier est un RetroBat s'il porte son lanceur, ou au moins EmulationStation.
function IsRetroBatRoot(Root: String): Boolean;
begin
  Root := RemoveBackslashUnlessRoot(Root);
  Result := (Root <> '') and (FileExists(Root + '\retrobat.exe')
    or FileExists(Root + '\emulationstation\emulationstation.exe'));
end;

// Ajoute Root a la liste s'il est valide et pas deja present (comparaison sans casse).
procedure AddRetroBatRoot(var Roots: TArrayOfString; Root: String);
var
  I: Integer;
begin
  Root := RemoveBackslashUnlessRoot(Trim(Root));
  if not IsRetroBatRoot(Root) then
    Exit;
  for I := 0 to GetArrayLength(Roots) - 1 do
    if CompareText(Roots[I], Root) = 0 then
      Exit;
  SetArrayLength(Roots, GetArrayLength(Roots) + 1);
  Roots[GetArrayLength(Roots) - 1] := Root;
end;

// Le dernier RetroBat installe ou lance, tel que RetroBat le note lui-meme.
function LatestKnownRetroBatRoot(): String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, 'Software\RetroBat', 'LatestKnownInstallPath', Result) then
    Result := RemoveBackslashUnlessRoot(Trim(Result))
  else
    Result := '';
end;

// Les dossiers ou l'on cherche un RetroBat un niveau plus bas : D:\Games\RetroBat,
// E:\Jeux\RetroBat2... Pas plus profond : un scan complet des disques serait trop long.
function IsGamesFolderName(Name: String): Boolean;
begin
  Name := Lowercase(Name);
  Result := (Name = 'games') or (Name = 'jeux') or (Name = 'emulation') or (Name = 'emulators')
    or (Name = 'emulateurs') or (Name = 'emu') or (Name = 'retrogaming') or (Name = 'retro')
    or (Name = 'arcade') or (Pos('retrobat', Name) > 0);
end;

// Cherche dans les dossiers de premier niveau d'un lecteur, et un niveau plus bas sous les
// dossiers de jeux.
procedure ScanDriveForRetroBat(var Roots: TArrayOfString; Drive: String);
var
  Rec, Sub: TFindRec;
  Dir: String;
begin
  AddRetroBatRoot(Roots, Drive + 'RetroBat');
  if FindFirst(Drive + '*', Rec) then
  try
    repeat
      if ((Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Rec.Name <> '.') and (Rec.Name <> '..') then
      begin
        Dir := Drive + Rec.Name;
        AddRetroBatRoot(Roots, Dir);
        if IsGamesFolderName(Rec.Name) and FindFirst(Dir + '\*', Sub) then
        try
          repeat
            if ((Sub.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Sub.Name <> '.') and (Sub.Name <> '..') then
              AddRetroBatRoot(Roots, Dir + '\' + Sub.Name);
          until not FindNext(Sub);
        finally
          FindClose(Sub);
        end;
      end;
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

// Tous les RetroBat de la machine, le dernier connu de RetroBat en tete. Seuls les disques
// locaux et amovibles sont parcourus : un lecteur reseau ou optique ferait attendre l'installeur.
function DetectRetroBatRoots(): TArrayOfString;
var
  DriveNum: Integer;
  Drive: String;
  Kind: Cardinal;
begin
  SetArrayLength(Result, 0);
  AddRetroBatRoot(Result, LatestKnownRetroBatRoot());
  for DriveNum := Ord('C') to Ord('Z') do
  begin
    Drive := Chr(DriveNum) + ':\';
    Kind := RetroBatGetDriveType(Drive);
    // 2 = amovible, 3 = disque local ; le reste (reseau, CD, RAM disk, absent) est ignore.
    if (Kind = 2) or (Kind = 3) then
      ScanDriveForRetroBat(Result, Drive);
  end;
end;

// Le RetroBat prefere : le dernier connu de RetroBat, sinon le premier trouve, sinon vide.
function DetectRetroBatRoot(): String;
var
  Roots: TArrayOfString;
begin
  Roots := DetectRetroBatRoots();
  if GetArrayLength(Roots) > 0 then
    Result := Roots[0]
  else
    Result := '';
end;

// Dossier d'installation par defaut du plugin : <RetroBat prefere>\plugins\<Param>.
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
