# ROMs et collections

## Importer un pack de ROMs

Déposez vos archives dans le dossier du plugin, puis relancez RetroBat :

```text
package-installer\Megadrive 32x (36 games).7z
package-installer\GX4000 (26 games).7z
```

APIExpose importe les ROMs, les médias et la gamelist fournie dans le pack. Formats acceptés : `.zip`, `.7z`, `.rar`. Le pilotage fin se fait dans le menu ES `ROMS PACK MANAGER`.

!!! warning "Vos ROMs restent vos ROMs"
    APIExpose n'embarque ni ne télécharge aucune ROM : le dossier `package-installer\` est le vôtre, alimenté par vos propres sauvegardes de jeux.

## ROMs à la demande (on-the-fly)

Pour les gros packs, le mode à la demande affiche les jeux dans EmulationStation **sans tout extraire tout de suite** : le jeu apparaît dans la liste, et sa ROM n'est extraite qu'au moment où vous le lancez. Idéal pour explorer un pack complet sans saturer le disque.

## Collections

APIExpose peut créer ou enrichir des collections : collections dynamiques par famille de jeu, packs de collections avec leurs médias et leurs thèmes. Les packs de collections se déposent ici :

```text
package-installer\collections\<nom-du-thème>\
```

Puis se gèrent dans le menu ES `COLLECTIONS PACK MANAGER`.

## Le Roms Manager : des listes propres

Le Roms Manager nettoie vos listes de jeux selon des profils adaptés à votre usage :

- masquer certains clones ;
- gérer les variantes d'un même jeu ;
- favoriser une région ou une langue ;
- **protéger vos favoris** et les jeux RetroAchievements ;
- appliquer des profils simples (du plus permissif au plus strict).

Tout se pilote depuis les menus ES, sans éditer de fichier.

## Thèmes

APIExpose alimente aussi les thèmes RetroBat/EmulationStation : médias de thème, données de fiche enrichies, assets de collections, et le thème HyperBat quand il est disponible. Voir le menu `THEMES MANAGER`.
