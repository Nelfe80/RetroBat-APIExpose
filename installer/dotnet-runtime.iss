[Code]
// dotnet-runtime.iss - PREREQUIS .NET 8 de RetroBat.Api.exe : ASP.NET Core Runtime 8 et
// .NET Desktop Runtime 8, en x64 (l'exe est framework-dependent, serveur web + Windows Forms).
//
// IMPORTANT : commence DIRECTEMENT par [Code], uniquement des commentaires « // » (voir
// retrobat-detect.iss). Les procedures d'evenement sont declarees par attributs <event(...)> :
// l'installeur qui inclut ce fichier garde les siennes.
//
// Detection : une version 8.x inscrite dans le registre (vue 32 bits : c'est la que les
// installeurs Microsoft l'ecrivent, une valeur nommee par version, mesure du 15/09/2026 :
// « 8.0.24 ») OU un dossier 8.* sous Program Files\dotnet\shared\<framework>. L'existence de
// la cle seule ne prouve rien : un .NET 9 seul la cree aussi.
//
// Installation : telechargement depuis les liens permanents aka.ms de Microsoft (page de
// progression en installation normale, sans interface en /VERYSILENT), puis lancement ELEVE
// (cet installeur tourne sans droits administrateur) et nouvelle detection. ShellExec ne rend
// pas le code de sortie, et un runtime peut s'installer en demandant un redemarrage (3010) :
// la presence du runtime est la seule preuve qui compte.

const
  DotNetMajorPrefix = '8.';
  DotNetFxCount = 2;

var
  DotNetDownloadPage: TDownloadWizardPage;

function DotNetFxName(Index: Integer): String;
begin
  if Index = 0 then
    Result := 'Microsoft.AspNetCore.App'
  else
    Result := 'Microsoft.WindowsDesktop.App';
end;

function DotNetFxLabel(Index: Integer): String;
begin
  if Index = 0 then
    Result := 'ASP.NET Core Runtime 8 (x64)'
  else
    Result := '.NET Desktop Runtime 8 (x64)';
end;

function DotNetFxUrl(Index: Integer): String;
begin
  if Index = 0 then
    Result := 'https://aka.ms/dotnet/8.0/aspnetcore-runtime-win-x64.exe'
  else
    Result := 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
end;

function DotNetFxFileName(Index: Integer): String;
begin
  if Index = 0 then
    Result := 'aspnetcore-runtime-8-win-x64.exe'
  else
    Result := 'windowsdesktop-runtime-8-win-x64.exe';
end;

function DotNetFxInRegistry(const Fx: String): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\' + Fx, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos(DotNetMajorPrefix, Names[I]) = 1 then
        Result := True;
end;

function DotNetFxOnDisk(const Fx: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not IsWin64 then
    Exit;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\') + Fx + '\' + DotNetMajorPrefix + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          Result := True;
      until Result or not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function DotNetFxPresent(Index: Integer): Boolean;
begin
  Result := DotNetFxInRegistry(DotNetFxName(Index)) or DotNetFxOnDisk(DotNetFxName(Index));
end;

function DotNetAllPresent(): Boolean;
var
  I: Integer;
begin
  Result := True;
  for I := 0 to DotNetFxCount - 1 do
    if not DotNetFxPresent(I) then
      Result := False;
end;

<event('InitializeWizard')>
procedure DotNetInitializeWizard;
begin
  DotNetDownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing),
    'Telechargement de Microsoft .NET 8, necessaire a APIExpose.', nil);
  DotNetDownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

// Installation normale : on telecharge au clic sur « Installer », avec une barre de progression,
// avant que quoi que ce soit ne change sur le disque. Un echec laisse l'utilisateur sur la page
// pour reessayer.
<event('NextButtonClick')>
function DotNetNextButtonClick(CurPageID: Integer): Boolean;
var
  I: Integer;
begin
  Result := True;
  if CurPageID <> wpReady then
    Exit;
  if DotNetAllPresent() then
    Exit;

  DotNetDownloadPage.Clear;
  for I := 0 to DotNetFxCount - 1 do
    if not DotNetFxPresent(I) then
      DotNetDownloadPage.Add(DotNetFxUrl(I), DotNetFxFileName(I), '');
  DotNetDownloadPage.Show;
  try
    try
      DotNetDownloadPage.Download;
    except
      if DotNetDownloadPage.AbortedByUser then
        Log('.NET 8 : telechargement annule par l''utilisateur.')
      else
        SuppressibleMsgBox('Impossible de telecharger Microsoft .NET 8 (' + DotNetDownloadPage.LastBaseNameOrUrl + ') :'#13#10
          + GetExceptionMessage + #13#10#13#10
          + 'Verifiez la connexion Internet, puis cliquez de nouveau sur Installer.', mbCriticalError, MB_OK, IDOK);
      Result := False;
    end;
  finally
    DotNetDownloadPage.Hide;
  end;
end;

// Installe un runtime deja telecharge (ou le telecharge sans interface en installation
// silencieuse). Rend un message d'erreur, ou une chaine vide si le runtime est la ensuite.
function DotNetInstallOne(Index: Integer): String;
var
  FilePath: String;
  ErrorCode: Integer;
begin
  Result := '';
  FilePath := ExpandConstant('{tmp}\') + DotNetFxFileName(Index);
  if not FileExists(FilePath) then
  begin
    try
      DownloadTemporaryFile(DotNetFxUrl(Index), DotNetFxFileName(Index), '', nil);
    except
      Result := 'Impossible de telecharger ' + DotNetFxLabel(Index) + ' : ' + GetExceptionMessage;
      Exit;
    end;
  end;

  Log('.NET 8 : installation de ' + DotNetFxLabel(Index) + ' depuis ' + FilePath);
  if not ShellExec('runas', FilePath, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode) then
  begin
    Result := 'L''installation de ' + DotNetFxLabel(Index) + ' n''a pas pu etre lancee : ' + SysErrorMessage(ErrorCode);
    Exit;
  end;

  if not DotNetFxPresent(Index) then
    Result := DotNetFxLabel(Index) + ' n''est toujours pas detecte apres son installation.';
end;

<event('PrepareToInstall')>
function DotNetPrepareToInstall(var NeedsRestart: Boolean): String;
var
  I: Integer;
begin
  Result := '';
  for I := 0 to DotNetFxCount - 1 do
  begin
    if DotNetFxPresent(I) then
      Log('.NET 8 : ' + DotNetFxLabel(I) + ' present')
    else if Result = '' then
    begin
      Result := DotNetInstallOne(I);
      if Result <> '' then
      begin
        Log('.NET 8 : ' + Result);
        Result := Result + #13#10#13#10 + 'APIExpose a besoin de ' + DotNetFxLabel(I)
          + '. Installez-le depuis ' + DotNetFxUrl(I) + ' puis relancez cette installation.';
      end
      else
        Log('.NET 8 : ' + DotNetFxLabel(I) + ' installe');
    end;
  end;
end;
