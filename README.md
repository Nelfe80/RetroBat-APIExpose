# APIExpose pour RetroBat

APIExpose ajoute des automatismes a RetroBat et EmulationStation : medias,
gamelists, packs de ROMs, scraping, themes, collections, donnees de jeux et API
locale.

Version courante : `1.0.0+20260701.104500`.

Documentation technique avancee :

- [README_tech.md](README_tech.md)
- [README_tech.fr.md](README_tech.fr.md)
- [docs/DOCS_INDEX.md](docs/DOCS_INDEX.md)
- [docs/09_MAME_CONTROL_MAPPING.md](docs/09_MAME_CONTROL_MAPPING.md) pour les
  exports MAME-CFG, RetroArch remaps et la compatibilite Start/Coin.

## A Lire Avant

APIExpose est puissant et peut modifier des fichiers RetroBat : gamelists,
medias, settings EmulationStation, collections et fichiers de theme.

Avant de l'utiliser sur une installation importante, faites une sauvegarde de
votre dossier RetroBat ou testez sur une copie.

## Installation Simple

Le dossier doit etre installe ici :

```text
E:\RetroBat\plugins\APIExpose
```

Il doit contenir au minimum :

```text
RetroBat.Api.exe
install-es-start-hook.bat
uninstall-es-start-hook.bat
start.bat
start-log.bat
stop.bat
resources/
tools/
wrapper/
```

Si RetroBat est ailleurs que `E:\RetroBat`, gardez le meme principe :

```text
<dossier RetroBat>\plugins\APIExpose
```

## Premiere Installation Du Hook

Pour que APIExpose se lance automatiquement quand EmulationStation demarre :

1. Fermez RetroBat.
2. Allez dans `RetroBat\plugins\APIExpose`.
3. Double-cliquez sur `install-es-start-hook.bat`.
4. Une fenetre confirme l'installation du hook.
5. Ensuite, lancez RetroBat normalement, comme avant.

Apres cette installation, vous n'avez normalement plus besoin de lancer
APIExpose a la main. Le hook EmulationStation lance `RetroBat.Api.exe`, attend
que APIExpose ait fini ses traitements de demarrage, puis laisse
EmulationStation continuer.

Le hook installe ce fichier cote EmulationStation :

```text
emulationstation\.emulationstation\scripts\start\APIExpose-start-wait.bat
```

Il ne modifie pas le script RetroBat `updatestores.bat`.

## Lancement Au Quotidien

Mode recommande apres installation du hook :

```text
Lancez simplement RetroBat.
```

APIExpose demarre tout seul au lancement d'EmulationStation.

Autres scripts utiles :

| Script | A quoi ca sert |
| --- | --- |
| `install-es-start-hook.bat` | Installe le lancement automatique APIExpose au demarrage ES. |
| `uninstall-es-start-hook.bat` | Retire uniquement le hook APIExpose. |
| `start.bat` | Lance APIExpose puis RetroBat, sans passer par le hook. Utile pour tester. |
| `start-log.bat` | Lance APIExpose en console visible pour diagnostiquer. Ne lance pas RetroBat. |
| `stop.bat` | Arrete le process APIExpose de ce plugin. |

## Verifier Que Ca Marche

Quand APIExpose est lance, ouvrez dans un navigateur :

```text
http://127.0.0.1:12345/api/v1/health
```

Reponse attendue :

```json
{
  "status": "healthy",
  "version": "1.0.0+20260603.235640"
}
```

Swagger, pour voir les endpoints disponibles :

```text
http://127.0.0.1:12345/swagger/index.html
```

Etat du demarrage :

```text
http://127.0.0.1:12345/api/v1/startup/ready
```

## Ce Que APIExpose Peut Faire

### Medias De Jeux

APIExpose peut trouver, ranger et projeter les medias des jeux :

- screenshots ;
- logos et wheels ;
- boxarts ;
- fanarts ;
- videos ;
- manuels ;
- magazines ;
- cartes/maps ;
- medias de themes.

Les fichiers utilisateur sont prioritaires. Mettez vos medias ici :

```text
media\user\systems\<systeme>\games\<jeu>\
```

APIExpose les utilise avant les medias telecharges.

### Medias Pour Les Flux WebSocket

Les plugins marquee, topper et instruction-card lisent les medias depuis le
store local. Pour un jeu, placez vos fichiers prioritaires ici :

```text
media\user\systems\<systeme>\games\<jeu>\
```

Exemples utiles :

```text
artwork\marquee\marquee.png
artwork\marquee\screenmarquee.png
artwork\marquee\screenmarquee-small.png
artwork\marquee\dmd.png
artwork\marquee\dmd.gif
artwork\marquee\dmd2.gif
artwork\marquee\topper.jpg
artwork\ic\ic.png
artwork\ic\ic-2.png
artwork\fanart.png
ui\wheels\wheel.png
```

Pour un systeme, les surcharges se placent dans :

```text
media\user\systems\<systeme>\
```

Si aucun media systeme local n'existe, APIExpose cherche dans le theme ES
courant, puis dans `es-theme-carbon`. Il ne parcourt pas tous les themes
installes.

Documentation complete des chemins WebSocket :

- [docs/08_MEDIA_WS_PLACEMENT.md](docs/08_MEDIA_WS_PLACEMENT.md)

### Scraping Automatique

APIExpose peut scraper localement puis, si besoin, demander ScreenScraper.

Il peut mettre a jour la fiche du jeu courant sans recharger toute la liste, via
`/addgames`, seulement quand il y a un vrai changement visible :

- image visible ajoutee ou remplacee ;
- logo/marquee ajoute ou remplace ;
- vignette/thumbnail ajoutee ou remplacee ;
- texte localise utile, dans la bonne langue ;
- video fraichement scrappee pour la fiche courante.

Les metadonnees brutes ou dans une mauvaise langue ne declenchent pas de refresh
live.

### Textes Et Langues

APIExpose gere les textes localises :

- description ;
- genre ;
- date ;
- developpeur ;
- editeur ;
- joueurs ;
- langue ;
- region ;
- famille.

Quand la langue EmulationStation change, APIExpose peut realigner les gamelists
dans la nouvelle langue. Les scrapes distants en cours sont invalides pour eviter
de reutiliser des resultats de l'ancienne langue.

### Packs De ROMs

Deposez vos packs ici :

```text
package-installer\
```

APIExpose peut importer :

- ROMs ;
- medias ;
- gamelist fournie dans le pack ;
- fichiers `.zip`, `.7z`, `.rar`.

Exemples :

```text
package-installer\GX4000 (26 games).7z
package-installer\Megadrive 32x (36 games).7z
```

### ROMs A La Demande

Avec le mode on-the-fly, APIExpose peut afficher les jeux dans ES sans extraire
toutes les ROMs tout de suite.

Le jeu apparait dans RetroBat, puis la ROM est extraite seulement quand vous la
selectionnez.

Pratique pour les gros packs.

### Collections

APIExpose peut creer ou enrichir des collections :

- collections dynamiques par famille de jeu ;
- packs de collections ;
- medias de collections ;
- themes de collections.

Les packs de collections se placent ici :

```text
package-installer\collections\<nom-du-theme>\
```

### Themes

APIExpose peut aider les themes RetroBat/EmulationStation :

- medias de theme ;
- datas pour themes ;
- theme HyperBat quand disponible ;
- assets de collections ;
- donnees de fiche jeu enrichies.

### Roms Manager

APIExpose peut aider a nettoyer les listes de jeux :

- masquer certains clones ;
- gerer les variantes ;
- favoriser une region ;
- favoriser une langue ;
- proteger les favoris ;
- proteger les jeux RetroAchievements ;
- appliquer des profils plus simples selon votre usage.

### Donnees Arcade Et Panneaux De Controle

APIExpose peut exposer des donnees pour bornes, LEDs ou themes :

- boutons ;
- couleurs ;
- fonctions des commandes ;
- layouts CPO ;
- donnees runtime de certains jeux.

Dossiers utiles :

```text
resources\dynpanels\
resources\ram\
```

### High Scores Et Evenements

APIExpose peut exposer :

- high scores ;
- evenements runtime ;
- flux WebSocket ;
- contexte du jeu courant ;
- infos utiles pour overlays, themes ou outils externes.

WebSocket local :

```text
ws://127.0.0.1:12345/ws
```

### Menus Dans RetroBat / EmulationStation

APIExpose ajoute ses options dans les menus ES/RetroBat avec traduction.

Les options importantes se trouvent dans :

```text
EXTENDED OPTIONS
API SETTINGS
AUTO SCRAPING MANAGER
LOCAL MEDIA MANAGER
ROMS PACK MANAGER
THEMES MANAGER
COLLECTIONS PACK MANAGER
```

## Ou Mettre Les Fichiers

| Besoin | Dossier |
| --- | --- |
| Pack ROM | `package-installer\` |
| Pack collection/theme | `package-installer\collections\<theme>\` |
| Media utilisateur prioritaire | `media\user\systems\<systeme>\games\<jeu>\` |
| Definitions boutons/CPO | `resources\dynpanels\` |
| Definitions RAM/events | `resources\ram\` |
| Splash de demarrage | `resources\startup-overlay\` |

## Conseils Simples

- Installez le hook une seule fois avec `install-es-start-hook.bat`.
- Ensuite lancez RetroBat normalement.
- Utilisez `start-log.bat` seulement pour diagnostiquer.
- Ne supprimez pas `media\`, `resources\`, `tools\` ou `wrapper\`.
- Gardez une sauvegarde avant de tester de grosses fonctions.
- Pour ajouter des packs, copiez-les dans `package-installer\`, puis relancez
  RetroBat.

## Desinstaller Le Hook

Pour retirer le lancement automatique :

1. Fermez RetroBat.
2. Lancez `uninstall-es-start-hook.bat`.
3. APIExpose ne demarrera plus automatiquement avec EmulationStation.

Le binaire et les fichiers APIExpose restent dans `plugins\APIExpose`.

## En Cas De Probleme

1. Lancez `stop.bat`.
2. Lancez `start-log.bat`.
3. Regardez les messages dans la fenetre.
4. Verifiez aussi les logs dans :

```text
logs\
```

Les endpoints utiles :

```text
http://127.0.0.1:12345/api/v1/health
http://127.0.0.1:12345/api/v1/startup/ready
http://127.0.0.1:12345/swagger/index.html
```

## Pour Les Details Techniques

Le README simple explique comment utiliser APIExpose. Pour les details de
developpement, de release, de gamelist, de scraping et de versioning, utilisez :

```text
README_tech.md
README_tech.fr.md
docs\
```
