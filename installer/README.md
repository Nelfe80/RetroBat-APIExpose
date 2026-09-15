# Installeur de borne APIExpose

`CabinetSetup.iss` produit `dist/APIExpose-Cabinet-Setup.exe` (Inno Setup 7). Ce document explique ce que fait l'installeur, comment ses fichiers s'articulent, et comment le modifier puis le vérifier sans casser une borne.

## Ce que fait l'installeur

| Étape | Où | Détail |
|---|---|---|
| Dossier cible | `retrobat-detect.iss` | Cherche `<lettre>:\RetroBat\retrobat.exe` sur les lecteurs et propose `<RetroBat>\plugins\APIExpose`. Avertit si le dossier choisi n'est pas dans un RetroBat. |
| Prérequis .NET 8 | `dotnet-runtime.iss` | Détecte ASP.NET Core Runtime 8 et .NET Desktop Runtime 8 (x64) ; télécharge et installe celui qui manque. Détail plus bas. |
| Fichiers | `CabinetSetup.iss`, `[Files]` | Le dossier du plugin, moins les exclusions (sources, docs, état local, secrets, outils internes). `appsettings.json` n'est posé que s'il n'existe pas ; `wrapper\.env` jamais. |
| Hook EmulationStation | `CabinetSetup.iss`, `[Code]` | Copie `.installer\scripts\start\APIExpose-start-wait.bat` dans `<RetroBat>\emulationstation\.emulationstation\scripts\start\`, y compris en installation silencieuse. Le retire à la désinstallation. |
| Fin d'installation | `[Run]` | Case « Démarrer APIExpose maintenant », décochée, ignorée en silencieux. |

L'installeur tourne **sans droits administrateur** (`PrivilegesRequired=lowest`) : il écrit dans le dossier de RetroBat et inscrit sa désinstallation dans `HKCU`. Seule l'installation d'un runtime .NET demande une élévation, ponctuelle.

## Les fichiers

| Fichier | Rôle | Réutilisable |
|---|---|---|
| `CabinetSetup.iss` | Installeur d'APIExpose : `[Setup]`, `[Files]`, `[Run]`, et son propre `[Code]` (hook ES, `CurStepChanged`, `CurUninstallStepChanged`). | non |
| `retrobat-detect.iss` | Détection et validation du RetroBat cible (`GetPluginInstallDir`, `AppParentIsRetroBat`, `WarnIfNotRetroBat`). | oui, par tous les installeurs de plugins |
| `dotnet-runtime.iss` | Prérequis .NET 8, accroché aux événements par attributs `<event(...)>`. | oui, par tout installeur qui pose `RetroBat.Api.exe` |
| `apiexpose-bootstrap.iss` | `ApiExposeInstalled()` pour les installeurs des autres plugins, qui avertissent si APIExpose manque. | oui |
| `obs-check.iss` | `ObsInstalled()` pour les installeurs qui dépendent d'OBS Studio. | oui |

### Règles des fichiers inclus

- Un fichier inclus commence **directement par `[Code]`** et n'utilise que des commentaires `//`. Après un `#include` qui finit en `[Code]`, on est toujours dans `[Code]` : une ligne `;` y est lue comme du Pascal et la compilation s'arrête sur « 'BEGIN' expected ». Cela vaut aussi pour un commentaire écrit dans `CabinetSetup.iss` entre deux `#include`.
- Un fichier inclus ne définit **pas** de procédure d'événement ordinaire (`CurStepChanged`, `InitializeWizard`...) : l'installeur qui l'inclut garde les siennes. S'il doit réagir à un événement, il le déclare par attribut, la ligne **avant** la déclaration :

```pascal
<event('PrepareToInstall')>
function DotNetPrepareToInstall(var NeedsRestart: Boolean): String;
```

## Prérequis .NET 8 (`dotnet-runtime.iss`)

`RetroBat.Api.exe` est un exe unique « framework-dependent » : serveur web ASP.NET Core et Windows Forms. Il lui faut les deux runtimes 8 en x64. `RetroBat.Api.Update.exe` n'a besoin que du runtime de base, compris dans chacun.

| Runtime | Lien de téléchargement (permanent, Microsoft) |
|---|---|
| ASP.NET Core Runtime 8 (x64) | `https://aka.ms/dotnet/8.0/aspnetcore-runtime-win-x64.exe` |
| .NET Desktop Runtime 8 (x64) | `https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe` |

### Détection

Un runtime est présent si l'une des deux preuves existe :

1. une valeur dont le nom commence par `8.` sous `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\<framework>`, lue dans la **vue 32 bits** du registre (`HKLM32`), là où les installeurs Microsoft l'écrivent ;
2. un dossier `8.*` sous `Program Files\dotnet\shared\<framework>` (64 bits).

La seule existence de la clé ne suffit pas : un .NET 9 installé seul crée la même clé.

### Déroulé

| Moment | Installation normale | Installation silencieuse (`/VERYSILENT`) |
|---|---|---|
| Clic sur « Installer » (`NextButtonClick`, page `wpReady`) | Page de progression, téléchargement de ce qui manque vers `{tmp}`. Un échec affiche la cause et laisse l'utilisateur réessayer. | page non affichée |
| Préparation (`PrepareToInstall`) | Installation de ce qui a été téléchargé. | Téléchargement sans interface (`DownloadTemporaryFile`), puis installation. |
| Installation d'un runtime | `ShellExec` avec le verbe `runas` : une demande UAC s'affiche, puis `/install /quiet /norestart`. | idem |
| Vérification | Nouvelle détection. Si le runtime manque encore, `PrepareToInstall` rend un message : l'installation s'arrête avant de copier les fichiers et donne le lien à suivre. | idem, code de sortie non nul |

`ShellExec` ne rend pas le code de sortie du programme lancé, et un runtime peut s'installer en signalant un redémarrage (`3010`). C'est pourquoi seule la nouvelle détection décide. Le journal d'installation (`/LOG=<fichier>`) contient une ligne par runtime : `.NET 8 : <runtime> present`, `installe`, ou la raison de l'échec.

## Hook de démarrage d'EmulationStation (`.installer/scripts/start/APIExpose-start-wait.bat`)

EmulationStation exécute les scripts de `scripts\start` **avant** de s'initialiser, et attend leur fin. Le hook démarre l'API puis attend qu'elle réponde `ready`, pour qu'EmulationStation affiche des listes et des menus à jour. Il ne doit jamais bloquer EmulationStation :

| Situation | Comportement |
|---|---|
| API déjà prête | sortie immédiate |
| `RetroBat.Api.exe` absent | journalisé, sortie |
| Runtime .NET 8 absent (dossiers `dotnet\shared\...\8.*`) | l'API est lancée (Windows affiche son propre message), EmulationStation n'attend pas |
| L'API s'arrête pendant l'attente | sortie dès qu'elle n'est plus active |
| Démarrage long | 40 essais au plus (de 40 s à 2 min), puis EmulationStation continue ; l'API finit de démarrer seule |

Délai mesuré entre le lancement et `ready` : de 7 à 23 secondes. Le hook sort toujours avec le code 0 et écrit dans `plugins\APIExpose\.log\es-start-hook.log`.

Le script reste en **batch pur avec `curl.exe`** : une commande PowerShell qui fait une requête web est classée Trojan:Win32/ClickFix par Microsoft Defender. Il doit être enregistré avec des fins de ligne **CRLF** : `cmd` gère mal les étiquettes et les `goto` d'un fichier en LF seul.

Le hook n'est posé que par l'installeur. La mise à jour automatique (`RetroBat.Api.Update.exe`) remplace le fichier source dans `.installer`, pas la copie d'EmulationStation. `install-es-start-hook.bat` et `uninstall-es-start-hook.bat` restent disponibles pour une remise en place manuelle.

## Modifier puis vérifier

### 1. Contrôle rapide du code (quelques secondes)

Compiler un installeur minuscule qui reprend le `[Code]` réel, sans le dossier complet : une erreur Pascal apparaît tout de suite, au lieu de la fin d'une compilation de douze minutes. Générer un `.iss` temporaire avec un `[Setup]` minimal (AppId différent de celui d'APIExpose), un `[Files]` qui ne contient que `.installer\scripts\start\APIExpose-start-wait.bat` (destination `{app}\.installer\scripts\start`), puis la fin de `CabinetSetup.iss` à partir de `#include "retrobat-detect.iss"`, avec des chemins d'include absolus. Le compiler avec `ISCC.exe /Q`.

Ce petit installeur sert aussi au test de comportement : l'installer en silencieux dans une fausse arborescence RetroBat avec `/LOG`, lire les lignes `.NET 8 :` et `Hook EmulationStation pose`, vérifier que la copie du hook est identique à la source, lancer `unins000.exe /VERYSILENT` et vérifier que le hook a disparu. Retirer ensuite la fausse arborescence et l'entrée `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{<AppId>}_is1` de ce test.

### 2. Compilation réelle

```powershell
& "C:\Program Files\Inno Setup 7\ISCC.exe" installer\CabinetSetup.iss
```

Environ 12 minutes pour 200 Mo. Lancer en arrière-plan. Un exe deux fois plus petit que la version précédente signale une compilation interrompue.

### 3. Installation de test dans une fausse arborescence

L'installeur ne se lit ni avec 7-Zip ni avec `innounp` : seule une installation réelle montre ce qu'il contient.

1. Arrêter l'API (`CloseApplications=yes` la fermerait sans prévenir).
2. Créer une fausse arborescence : un `retrobat.exe` vide et un dossier `emulationstation`. Sans eux, `WarnIfNotRetroBat` affiche une boîte que l'installation silencieuse attend indéfiniment. Jamais le vrai RetroBat.
3. `APIExpose-Cabinet-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS "/DIR=<faux RetroBat>\plugins\APIExpose" /LOG=<fichier>`
4. Contrôler : version de `RetroBat.Api.exe`, empreinte de `wrapper\wrapper.dll` égale à `wrapper\WRAPPER_VERSION.json`, aucun fichier sous `src`, `docs`, `media`, `tests`, `resources\ram\.user`, aucun `.env`, `.pem`, `.ps1`, `.py` (des dossiers vides peuvent exister, seuls les fichiers comptent), lignes `.NET 8 :` et hook posé dans le journal.
5. Supprimer la fausse arborescence et l'entrée de désinstallation `{4E9A11C2-0B77-4A0D-9A55-APIEXPOSE001}_is1` de `HKCU`, puis relancer l'API.

Le chemin « .NET absent » ne se teste que sur une machine sans runtime .NET 8 (machine virtuelle ou PC neuf).

### 4. Publication

`AppVersion` (`#define` en tête de `CabinetSetup.iss`) suit la version d'APIExpose (`Directory.Build.props`). L'installeur se joint à la release existante :

```powershell
gh release upload vX.Y.Z dist\APIExpose-Cabinet-Setup.exe --repo Nelfe80/RetroBat-APIExpose --clobber
```

Toute nouvelle exclusion de fichiers se reporte aux deux endroits : `Excludes` de `CabinetSetup.iss` et exclusions de `release.ps1`.
