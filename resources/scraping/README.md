# Scraping Resources

Ce dossier contient les ressources de reference utilisees par `APIExpose` pour son moteur de scrap et de projection vers EmulationStation.

## Positionnement

- `APIExpose` reste autonome a l'execution ;
- les fichiers de `reference/` sont internalises dans ce projet ;
- ils servent de bootstrap de normalisation et de compatibilite ;
- la logique metier de resolution, projection et scrap reste propre a `APIExpose`.

## Table ScreenScraper

- `reference/systems_screenscraper.json` est la table statique prioritaire utilisee par `APIExpose` pour resoudre `system -> systemid` ;
- elle est figee localement pour eviter toute requete supplementaire au runtime ;
- elle consolide la base historique `systems_screenscraper.txt` et les aliases utiles provenant du projet `RetroBat-Marquee-Manager - Aynshe` ;
- `systems_screenscraper.txt` est conserve comme fallback legacy et comme source d'audit.

## Credits

Une partie du corpus de reference de `reference/` provient du projet **ARRM** et de son ecosysteme de configuration.

Credits et remerciements :

- **ARRM** pour le travail de normalisation des systemes, des medias et des conventions de scrap
- **Jean-Philippe Delattre / Selph** pour la conception et l'evolution de ARRM
- les contributeurs et mainteneurs du projet ARRM
- les ecosystemes relies exploites historiquement par ARRM, notamment **ScreenScraper** et les bases de mapping associees

## Intention

Ces references sont conservees ici a titre de base de compatibilite et d'amorcage.

L'objectif du projet reste :

- de s'abstraire d'ARRM en finalite ;
- de stabiliser des ressources natives a `APIExpose` ;
- puis de faire evoluer le moteur independamment.
