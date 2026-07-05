# APIExpose pour RetroBat

> **Avertissement beta**
>
> APIExpose est encore en beta et peut modifier les settings EmulationStation,
> les gamelists, les projections media, les collections et les fichiers de
> themes. Selon les options activees, il va ecrire ou mettre a jour des
> gamelists et des fichiers media. Avant de l'utiliser sur votre installation
> principale, faites vraiment une sauvegarde complete de votre dossier RetroBat
> ou testez sur une copie de RetroBat.

APIExpose est un compagnon local pour RetroBat. Il ajoute des automatismes
pratiques autour d'EmulationStation : mise a jour des medias et gamelists,
installation de packs ROMs, collections dynamiques, donnees de themes, donnees
CPO panel, flux WebSocket et API locale.

README utilisateur : [README.md](README.md)

Version technique anglaise : [README_tech.md](README_tech.md)

Release courante : `1.0.0+20260626.221125`, publiee en runtime `win-x64`
single-file framework-dependent. Elle utilise le runtime .NET 8 installe sur la
machine RetroBat et evite d'embarquer le runtime complet dans
`RetroBat.Api.exe`.

Les assemblies managees privees compatibles sont integrees au single-file.
Les DLL natives et outils prives restent dans les dossiers du plugin; ils ne
sont jamais installes dans Windows, le GAC ou le `PATH` global.

Procedure release locale :

```powershell
Get-Process RetroBat.Api -ErrorAction SilentlyContinue | Stop-Process -Force
powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-framework-dependent.ps1
```

Toujours couper le process APIExpose avant de publier ou de remplacer le runtime
racine. Le script publie d'abord hors arbre source pour eviter de re-inclure
les anciens artefacts dans la release suivante.

## Demarrage

Lancez uniquement `start.bat` depuis `E:\RetroBat\plugins\APIExpose`.
Ne lancez pas RetroBat avant, et ne relancez pas RetroBat a la main apres :
`start.bat` ferme les anciens processus `RetroBat.Api`, `emulationstation` et
`RetroBat`, demarre APIExpose, attend que l'endpoint de sante APIExpose local
sur `127.0.0.1:12345` soit pret, puis lance RetroBat. Cette attente ne depend
pas de l'API EmulationStation `127.0.0.1:1234` : ES est lance par RetroBat
ensuite. La barriere laisse le temps a APIExpose de mettre a jour les settings
ES, d'installer ses entrees de menu, de lancer les migrations de demarrage et de
preparer les gamelists/medias des packs ROMs a la demande avant qu'ES lise ses
fichiers.

Pour diagnostiquer l'API en console, utilisez `start-log.bat`. Ce mode applique
le meme nettoyage de depart, mais laisse APIExpose au premier plan et ne lance
pas RetroBat automatiquement.

### Mode test non interactif

Pour les mesures, les tests de flux WebSocket ou les simulations via
`events.ini`, APIExpose peut etre lance en mode test :

```powershell
E:\RetroBat\plugins\APIExpose\RetroBat.Api.exe --urls http://127.0.0.1:12345 --test-mode
```

Le meme mode peut etre force avec la variable d'environnement
`APIEXPOSE_TEST_MODE=1`.

En mode test, APIExpose demarre sans les workflows lourds de demarrage, mais
garde les flux runtime utiles : HTTP/WebSocket, watcher
EmulationStation/`events.ini`, projection panel, providers runtime dont
RetroArch wrapper et outputs arcade si les options correspondantes sont
activees. La migration canonique des medias `roms/<system>/images`, `videos`,
`manuals`, `themehb` et `themes` est donc ignoree avant toute boite de
confirmation Windows, et les installateurs, caches, normalisations gamelist et
deploiements startup ne bloquent pas l'ouverture du port HTTP. Ce mode sert aux
bancs d'essai et ne doit pas etre utilise comme mode d'exploitation permanent si
l'on veut que les migrations/maintenances de demarrage normales s'executent.

## Exports MAME-CFG Et RetroArch

Les fichiers MAME-CFG generes par `panel_curator_ultimate.py` doivent marcher a
la fois avec MAME64 standalone et avec RetroArch `mame_libretro`. Pour Start et
Coin, le generateur ecrit donc les deux conventions dans la meme sequence :

```text
START1 = KEYCODE_1 OR JOYCODE_1_BUTTON9 OR JOYCODE_1_START
COIN1  = KEYCODE_5 OR JOYCODE_1_BUTTON10 OR JOYCODE_1_SELECT
```

Ne pas supprimer cette double forme : MAME64 utilise plutot `BUTTON9/BUTTON10`,
tandis que `mame_libretro` expose Start/Coin sous `START/SELECT`. La reference
complete est dans `docs/09_MAME_CONTROL_MAPPING.md`.

## Ce Que Peut Faire APIExpose

- Auto-scraper les medias locaux et distants, puis mettre a jour la fiche du jeu
  courant quand un changement utile est trouve.
- Maintenir un store media local-first pour organiser vos fichiers, les medias
  telecharges et les medias de packs avant projection dans ES.
- Installer automatiquement des packs ROMs, gamelists et medias depuis des
  archives `.zip`, `.7z` ou `.rar`.
- Utiliser l'installation ROM a la demande pour afficher de gros packs dans ES
  sans extraire toutes les ROMs tout de suite.
- Generer ou reparer des gamelists depuis des packs ROMs, des packs medias ou
  des medias locaux.
- Construire des collections dynamiques depuis les valeurs `family` des
  gamelists, afin que les collections s'enrichissent quand de nouveaux jeux
  correspondants sont ajoutes.
- Installer des packs de collections et deployer leurs assets de theme.
- Appliquer un theme de collection aux jeux quand aucun theme jeu dedie
  n'existe.
- Deployer des medias de theme via `ThemeDeployments` et
  `MediaDeploymentRules`, configurables sans recompilation.
- Exposer les donnees CPO/control panel pour les themes, LEDs et clients
  externes.
- Exposer des evenements runtime ingame et arcade via WebSocket.
- Exporter les high scores pour les donnees de theme modernes et les anciens
  flux `.hiscore`.
- Envoyer aux clients WebSocket les donnees marquee, logo, fanart et contexte
  jeu.
- Ajouter les options APIExpose directement dans les menus RetroBat /
  EmulationStation avec support des locales natives.
- Fournir une API HTTP locale et Swagger pour les outils, dashboards ou futurs
  workflows hub.

## Philosophie

APIExpose part d'une idee simple : RetroBat reste le frontend jouable, pendant
qu'APIExpose prepare proprement les donnees locales dont RetroBat et
EmulationStation ont besoin.

La piece la plus importante est le local media store. APIExpose essaie d'abord
de centraliser les medias dans `media/`, puis de les projeter vers les formats
RetroBat et EmulationStation seulement quand c'est necessaire. Cela evite les
ecritures hasardeuses directement dans les gamelists et donne une source de
verite plus claire :

```text
media/user -> vos fichiers prioritaires
media      -> store local canonique
roms/...   -> projection EmulationStation
gamelist   -> vue finale ES du jeu
```

Cette approche local-first est importante parce que les packs, le scraping, les
themes de collection et les corrections manuelles parlent ensuite le meme
langage. Si un theme ou une gamelist change plus tard, APIExpose peut
reconstruire la projection visible depuis les medias locaux au lieu de deviner a
partir de ce qui etait deja ecrit dans une gamelist.

## Derniers ajouts utiles

- Les packs ROMs peuvent etre installes depuis `package-installer/` au demarrage
  de l'API.
- Le mode ROM a la demande peut creer les gamelists et medias d'abord, puis
  extraire uniquement le jeu selectionne quand il manque.
- Les packs de collections se deposent dans
  `package-installer/collections/<theme-name>/`.
- Un theme systeme de collection peut etre applique aux jeux de la meme famille
  si aucun theme jeu dedie n'existe.
- Les deploiements de themes sont configurables dans `appsettings.json` via
  `ThemeDeployments` et `MediaDeploymentRules`.
- Les metadonnees locales peuvent etre normalisees depuis Swagger sans scraping
  distant. Cela nettoie les valeurs de locale polluees et, si demande, reecrit
  les champs gamelist depuis la valeur locale qualifiee.
- Les textes localises peuvent rafraichir la fiche ES courante via `/addgames`
  quand ils ciblent la langue effective et modifient un champ visible comme le
  titre, la description, le genre, la date, le developpeur, l'editeur ou les
  joueurs.
- Les projections Local Media Manager sont strictes : `LOGO` accepte les logos
  ou wheels simples, `WHEEL HD` accepte seulement la wheel carbon ou steel
  selectionnee, et un media source manquant laisse le slot ES visible vide.
- Auto Scraping Manager peut notifier la fin du scraping de medias lourds via
  `NOTIFIER LE SCRAP MEDIA`. La notification n'est envoyee que si un manuel,
  magazine ou video est reellement importe, et seulement pour la fiche de jeu
  active sauf exception video.
- Quand la langue ES change, APIExpose invalide les scrapes distants en cours
  et abandonne les jobs de queue obsoletes au lieu de reutiliser des resultats
  qui peuvent appartenir a l'ancienne langue. La synchro gamelist langue
  normalise ensuite les textes avec la nouvelle langue effective.
- `themehb` est local-first. Une archive canonique dans `media/systems/<system>/`
  bloque le scraping distant ScreenScraper, tandis que l'installation locale
  dans le theme HyperBat actif ne tourne que si le theme ES courant est un
  dossier HyperBat installe.
- Les menus APIExpose utilisent les locales natives RetroBat
  `es_features.locale`. Les libelles APIExpose sont completes dans les locales
  supportees, tandis que les termes techniques media comme `SCREENSHOT`,
  `TITLESHOT`, `THUMBNAIL` et `MARQUEE` restent volontairement stables.
- Les notifications runtime et les libelles live passent par
  `resources/locales/interface-texts.json`, avec une couverture alignee sur les
  locales de menus ES supportees.
- `es_settings.cfg` est surveille une seule fois par un bus central APIExpose.
  Les modules s'y abonnent au lieu de creer chacun leur propre watcher fichier.
- Les logs runtime de diagnostic peuvent etre remis a zero au demarrage depuis
  `appsettings.json`; les traces XML lourdes `/addgames` sont desactivees par
  defaut et restent derriere `ApiExpose:Scraping:TraceLiveAddGamesPayloads`.

## Ou deposer les fichiers

| Objectif | Dossier | Ce que produit APIExpose |
| --- | --- | --- |
| Installer un pack ROMs | `package-installer/` | Installe ROMs, medias et entrees gamelist au demarrage. |
| Utiliser les ROMs a la demande | `package-installer/` | Prepare la gamelist et les medias au demarrage, puis extrait la ROM manquante a la demande. |
| Installer un pack collection/theme | `package-installer/collections/<theme-name>/` | Cree les collections et deploie les medias/theme de collection. |
| Ajouter des medias locaux | `media/user/systems/<system>/games/<game-slug>/` | Utilise vos fichiers avant les medias caches ou telecharges. |
| Ajouter des medias pour les flux marquee/topper/DMD/cards | `media/user/systems/<system>/games/<game-slug>/artwork/` et `ui/wheels/` | Expose les fichiers dans `/ws/marquee`, `/ws/topper` et `/ws/instruction-card`. |
| Ajouter des definitions CPO panel | `resources/dynpanels/systems/` ou `resources/dynpanels/games/` | Expose couleurs, fonctions et types de controllers aux themes, LEDs et clients WebSocket. |
| Ajouter des definitions RAM/events | `resources/ram/<system>/` | Permet a l'ecouteur ingame d'exposer des signaux runtime. |
| Ajouter des images de splash | `resources/startup-overlay/` | Affiche une image numerotee au demarrage pendant les traitements. |
| Ajouter des traductions de menus | `resources/config-ESmenus/locales/<locale>/es-features.po` | Merge les traductions APIExpose dans le dossier RetroBat `es_features.locale`. |

Medias physiques reconnus par les flux WebSocket :

```text
media/user/systems/<system>/games/<game-slug>/artwork/marquee/marquee.*
media/user/systems/<system>/games/<game-slug>/artwork/marquee/dmd.png
media/user/systems/<system>/games/<game-slug>/artwork/marquee/dmd*.gif
media/user/systems/<system>/games/<game-slug>/artwork/marquee/topper.*
media/user/systems/<system>/games/<game-slug>/artwork/ic/ic.*
media/user/systems/<system>/games/<game-slug>/ui/wheels/wheel.*
media/user/systems/<system>/artwork/marquee/marquee.*
media/user/systems/<system>/artwork/marquee/generated-system-dmd.*
```

Pour les visuels systeme, APIExpose cherche d'abord `media/user`, puis
`media`, puis le theme ES courant, puis `es-theme-carbon`. Les autres themes ne
sont pas utilises en fallback.

Reference complete : [docs/08_MEDIA_WS_PLACEMENT.md](docs/08_MEDIA_WS_PLACEMENT.md).

## Roms Pack Manager

Deposez les packs `.zip`, `.7z` ou `.rar` directement dans :

```text
package-installer/
```

Exemples :

```text
package-installer/GX4000 (26 games).7z
package-installer/Megadrive 32x (36 games).7z
```

Au demarrage, si `ROMS PACK MANAGER > ROM PACK INSTALLER` ou
`ROMS PACK MANAGER > ON-THE-FLY ROM INSTALLER` est actif, APIExpose scanne le
dossier et compare son index de packs. Le dossier n'est pas surveille en
continu : un pack depose pendant que l'API tourne est traite au prochain
demarrage API, apres un changement d'option installateur, ou via
`POST /api/v1/rom-packs/rescan`.

Un pack peut contenir :

```text
gx4000/
  roms/
  images/
  videos/
  manuals/
  gamelist.xml
```

ou une structure plus simple :

```text
gx4000/
  *.zip
  *.7z
  images/
```

APIExpose detecte le systeme cible depuis le nom du pack ou depuis le dossier
racine quand c'est possible.

Regles media dans les packs :

```text
*-screenshot.* -> SCREENSHOT
*-image.*      -> SCREENTITLE
*-marquee.*    -> logo / wheel simple, pas wheel-carbon/steel
*-thumb.*      -> BOX 3D
*-thumbnail.*  -> BOX 3D
*-fanart.*     -> fanart
*-flyer.*      -> flyer
*-video.*      -> video
*-manual.*     -> manual
```

L'installateur importe les medias dans le store canonique `media/`, puis le
Local Media Manager choisit ce qui alimente `<image>`, `<marquee>` et
`<thumbnail>` selon les options ES.

## Extraction ROM a la demande

Activez `ROMS PACK MANAGER > ON-THE-FLY ROM INSTALLER` si vous voulez rendre
les jeux visibles sans installer toutes les ROMs tout de suite. Dans ce mode,
le demarrage prepare seulement la gamelist, les medias canoniques et l'index du
pack ; il n'installe pas les vraies ROMs.

Au demarrage APIExpose :

1. indexe le pack,
2. cree ou enrichit les entrees gamelist,
3. importe les medias,
4. force `ParseGamelistOnly=true` dans les settings ES,
5. garde l'archive ROM disponible pour extraction plus tard.

Quand un jeu reste selectionne assez longtemps et que sa ROM manque, APIExpose
affiche une messagebox demandant de ne pas encore lancer le jeu, extrait
l'archive, puis notifie que le jeu est installe.

`UNZIP ROMS` est une option transverse : si elle est active, les archives ROMs
internes sont aussi extraites quand un jeu est materialise depuis un pack.

## Collections Pack Manager

Deposez les packs de collections/themes dans :

```text
package-installer/collections/<theme-name>/
```

Exemples :

```text
package-installer/collections/hyperbat/sonic.zip
package-installer/collections/hyperspin/mario.7z
```

Au demarrage, si `COLLECTIONS PACK MANAGER > COLLECTION PACK INSTALLER` est
actif, APIExpose installe le pack, cree les collections et deploie les medias
de theme selon `appsettings.json`.

Les collections dynamiques sont basees sur la balise `family`. APIExpose
consolide les familles des gamelists et cree des filtres `.xcc` pour que la
collection puisse s'alimenter automatiquement quand de nouveaux jeux
correspondants sont ajoutes.

Exemple de collection par familles :

```xml
<?xml version="1.0"?>
<filter name="mario">
  <family>MARIO</family>
  <family>MARIO KART, MARIO</family>
  <family>MARIO, SUPER MARIO BROS</family>
</filter>
```

Si `ENABLE FOR THEMES` est actif, un theme systeme de collection peut etre
converti en theme jeu pour les jeux de cette collection quand aucun theme jeu
dedie ni theme canonical parent/clone n'existe.

## Local Media Manager

Les medias utilisateur se deposent dans :

```text
media/user/systems/<system>/games/<game-slug>/
```

Exemple :

```text
media/user/systems/nes/games/super-mario-bros-3/
  artwork/screens/ingame.png
  artwork/box/front.png
  ui/wheels/wheel.png
  documents/manuals/manual.pdf
```

Les medias utilisateur sont prioritaires sur les medias caches ou telecharges.
APIExpose peut les projeter vers les chemins RetroBat et mettre a jour la
gamelist sans melanger les familles de medias.

Les slots visibles ES sont pilotes par les options :

```text
MAIN IMAGE      -> <image>
LOGO / MARQUEE  -> <marquee>
THUMBNAIL       -> <thumbnail>
WHEEL HD STYLE  -> wheel-carbon ou wheel-steel selon le choix
```

`LOGO` peut utiliser un logo simple ou une wheel ScreenScraper simple, car les
deux sont des assets de type logo pour `<marquee>`. `WHEEL HD` reste separe et
utilise uniquement la variante carbon ou steel selectionnee. Si la source
choisie n'existe pas, APIExpose laisse le slot vide au lieu de prendre une autre
famille media.

Avant une reallocation globale ou un changement Roms Pack Manager qui impacte
les jeux, revenez sur l'ecran systeme. Si ES est encore dans une fiche jeu,
APIExpose affiche une messagebox : le systeme courant peut ne pas etre rafraichi
et les modifications peuvent ne pas y etre prises en compte tant que vous ne
sortez pas de la liste des jeux.

## Auto Scraping Manager

L'auto scraping se configure dans `EXTENDED OPTIONS > AUTO SCRAPING MANAGER`.
APIExpose suit les options sauvegardees dans `appsettings.json` et exposees dans
ce menu : les flags scraper natifs ES sont synchronises pour l'interface, mais
ils ne bloquent pas la politique de scraping local/distant d'APIExpose.

Options importantes :

```text
ENABLE AUTO SCRAPING MANAGER
ENABLE SCREENSCRAPER
ENABLE SCRAPING QUEUE
REMOTE SCRAPE AFTER LOCAL
REFRESH GAME VIEW AFTER SCRAPE
NOTIFIER LE SCRAP MEDIA
SCRAPER LES MARQUEES
SCRAPER LES SCREEN MARQUEES
SCRAPER LES SMALL SCREEN MARQUEES
SCRAPER LES STEAMGRID
SCRAPER LES MIX
SCRAPER LES MANUELS
SCRAPER LES MAGAZINES
SCRAPER LES VIDEOS
SCRAPER LES VIDEOS NORMALISEES
SCRAPER LES BEZELS
BEZEL ASPECT
BEZEL ORIENTATION
```

`NOTIFIER LE SCRAP MEDIA` pilote seulement la notification de fin pour les
medias distants lourds : manuels, magazines, videos et videos normalisees.
Cette option ne force pas un refresh live `/addgames` a elle seule. Les
notifications de fin pour medias lourds sont limitees a la fiche active; les
videos importees sont l'exception explicite qui peut encore etre annoncee apres
navigation. Une `video` fraichement scrappee est aussi la seule exception media
lourd pour le refresh live : si elle correspond encore a la fiche
`game-selected` courante, APIExpose peut pousser un `/addgames` live pour que ES
l'affiche.

## Theme Deployments

Les deploiements de themes sont configures dans `appsettings.json`.

Sections utiles :

```json
"ThemeDeployments": [],
"MediaDeploymentRules": []
```

Cela permet d'ajouter une nouvelle cible de theme sans recompiler l'API. Une
regle peut definir :

- le media a scraper ou deployer,
- le theme ES actif concerne,
- le dossier d'installation,
- des copies annexes vers des dossiers bezels, marquee ou propres au theme.

Pour `themehb`, l'archive canonique est :

```text
media/systems/<system>/games/<game-slug>/themes/themehb.zip
```

Les systemes arcade-like comme `mame`, `fbneo`, `fba` et `hbmame` partagent le
store canonique `arcade`. Le zip canonique empeche un nouveau telechargement
distant ScreenScraper, mais il peut toujours etre installe localement dans le
theme HyperBat actif quand ce theme est selectionne et present dans le dossier
`themes/` d'EmulationStation. Si le theme ES courant n'est pas HyperBat,
APIExpose ignore le scrape live `themehb`, l'extraction locale et le refresh F5.

## Donnees CPO Control Panel

Definitions panel systeme :

```text
resources/dynpanels/systems/<system>.json
```

Overrides jeu arcade :

```text
resources/dynpanels/games/<rom>.json
```

Quand le module est actif, APIExpose expose :

- la couleur des boutons,
- la fonction des boutons,
- le type de controller,
- la configuration panel resolue dans le flux WebSocket.

Les fichiers panel par jeu concernent surtout l'arcade. Les consoles et
ordinateurs utilisent en general la definition panel du systeme.

## Events Ingame Et Definitions RAM

Les definitions runtime se deposent dans :

```text
resources/ram/<system>/<game>.MEM
resources/ram/<system>/alias.json
```

Exemple :

```text
resources/ram/megadrive/sonic-the-hedgehog.MEM
resources/ram/megadrive/alias.json
```

Ces fichiers permettent a l'ecouteur ingame d'exposer des signaux utiles aux
clients WebSocket, overlays, panels ou outils de score.

## Splash De Demarrage

Les images de demarrage sont dans :

```text
resources/startup-overlay/
```

Utilisez des fichiers numerotes :

```text
splashscreen0.png
splashscreen1.png
splashscreen2.png
```

APIExpose peut en choisir une au hasard au demarrage. La splash affiche aussi
les etapes d'initialisation, dont le controle et l'installation des packs ROMs.

## API Et WebSocket

Endpoints par defaut :

```text
API:       http://127.0.0.1:12345
Swagger:   http://127.0.0.1:12345/swagger/index.html
WebSocket: ws://127.0.0.1:12345/ws
Health:    GET /api/v1/health
```

Reponse attendue :

```json
{
  "status": "healthy",
  "version": "1.0.0+20260603.235640"
}
```

Endpoints de maintenance utiles dans Swagger :

```text
POST /api/v1/media/gamelist/refresh-selections
POST /api/v1/media/metadata/normalize
```

`refresh-selections` reapplique les choix du Local Media Manager sur un ou
plusieurs systemes. `metadata/normalize` nettoie les pollutions de metadonnees
locales, repare les textes mojibake et peut, seulement si demande explicitement,
normaliser les champs texte deja presents dans les `gamelist.xml` via
`normalizeExistingGamelists: true`. Cette passe nettoie aussi les genres
multi-langues colles dans une meme balise. Aucun de ces endpoints ne lance un
scraping distant tout seul.

Pendant la navigation live, APIExpose ne reecrit pas directement
`roms/<systeme>/gamelist.xml` apres un `/addgames` : Batocera ES le fait deja.
Les champs extended utiles aux themes sont prepares dans
`media/aliases/shared/gamelist-extended-pending/`, puis exposes au demarrage API
ou juste avant un `reloadgames`.

Exception controlee : quand la migration canonique des anciens dossiers
`roms/<systeme>/images`, `videos` et `manuals` est acceptee dans la fenetre
Windows, APIExpose met aussi a jour les gamelists pour remplacer les anciens
liens legacy par les chemins `media/` canoniques. Sans cette passe, ES pourrait
continuer a pointer vers des fichiers qui viennent d'etre deplaces ou supprimes.

Un `/addgames` live n'est envoye que si la fiche ES courante a vraiment besoin
d'un changement visible : media visible manquant au moment de la selection,
media visible local qui vient d'etre projete/modifie, ou texte localise de la
langue effective qui change un champ ES visible. Les metadonnees brutes, les
textes d'une mauvaise langue et les fallbacks ambigus ne peuvent pas ouvrir le
chemin live. Un simple ecart du `gamelist.xml` disque apres un changement global
de filtres ne declenche pas de refresh par fiche si ES affiche deja une fiche
complete. Une `video` qui vient d'etre scrappee est la seule exception qui peut
produire un deuxieme `/addgames` sur la meme fiche selectionnee ; cette
exception video ne peut etre consommee qu'une fois et aucun autre media ne
rouvre le gate.

## MAME Standalone Ingame RAM

APIExpose distingue deux flux MAME :

- les outputs MAME (`lamp`, compteurs, sorties natives) restent lus via le
  reseau MAME `output network` et publies comme evenements `mame.*` sur
  `/ws/arcade` ;
- les evenements ingame RAM de MAME standalone passent par un plugin Lua
  `apiexpose_ingame` et publient des evenements `ingame.*` sur `/ws/ingame`.

Le plugin source est conserve dans :

```text
resources/ram/tools/mame_apiexpose_ingame/
```

Au demarrage, si `GameEventsManager.MameLuaIngameEnabled` et
`MameLuaIngamePluginDeploymentEnabled` sont actifs, APIExpose le deploie vers :

```text
E:\RetroBat\bios\mame\plugins\apiexpose_ingame\
E:\RetroBat\bios\mame\ini\plugin.ini
```

La ligne suivante active le plugin dans MAME :

```ini
apiexpose_ingame          1
```

Un miroir peut aussi etre ecrit dans `E:\RetroBat\emulators\mame\plugins\` pour
les lancements manuels hors RetroBat. Le Lua reste volontairement minimal : il
annonce la ROM (`HELLO|1944|...`), recoit une watchlist (`WATCH|id|address|type`)
et renvoie les valeurs changees (`VALUE|id|value`). APIExpose garde la logique
metier : resolution `resources/ram/arcade/<rom>.MEM`, aliases, conditions,
deltas score, actions et publication WebSocket.

## Garde-Fous

APIExpose reste conservateur :

- il ne supprime pas automatiquement les ROMs,
- il sauvegarde les gamelists avant remplacement,
- il valide le XML avant ecriture,
- il utilise des index de packs pour eviter les reinstallations inutiles,
- il debounce le scraping live, l'extraction et les refreshs UI,
- il envoie un seul F5 de stabilisation quelques secondes apres que l'API ES reponde au demarrage,
- il espace les POST `/addgames` live pour laisser ES terminer ses refreshs internes,
- il differe les balises gamelist extended hors `game-selected`,
- il retire son integration temporaire `es_features` quand ES s'arrete.

## Documentation Detaillee

```text
docs/DOCS_INDEX.md
docs/00_POINT_PROJET_WORKFLOW.md
docs/01_DEVOPS_VERSIONING.md
docs/02_CORE_NOMENCLATURE.md
docs/03_FEAT_SCRAPING.md
docs/04_FEAT_MEDIA_PIPELINE.md
docs/05_FEAT_UI_THEMES.md
docs/06_INTERFACES_CODE_BACKLOG.md
resources/config-ESmenus/README_APIExpose_RetroBat_Options.txt
```
