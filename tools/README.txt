APIExpose tools / Outils APIExpose
==================================

EN
--
This directory contains local helper tools and third-party binaries used by
APIExpose during maintenance, media processing, diagnostics, or optional local
translation workflows. Its contents are intentionally not tracked by Git, except
for this README.

Expected local layout when those tools are needed:

- ffmpeg/
- imagemagick/
- translateLocally/
- any temporary diagnostic scripts used locally

Each restored tool folder can keep its own README.txt. Those README files are
tracked so the installation notes stay with the folder they describe.

Installation:

1. Restore the tool folders from your local backup, release package, or the
   original vendor archives.
2. Place them under APIExpose/tools with the names above.
3. Keep large binaries, downloaded models, caches, and ad-hoc scripts in this
   folder; Git will ignore them.
4. If a tool becomes required to build or run APIExpose, document it in the main
   README and package it in the installer instead of committing it here.

FR
--
Ce dossier contient des outils locaux et des binaires tiers utilises par
APIExpose pour la maintenance, le traitement des medias, le diagnostic ou les
traductions locales optionnelles. Son contenu n'est volontairement pas suivi par
Git, sauf ce README.

Structure locale attendue lorsque ces outils sont necessaires :

- ffmpeg/
- imagemagick/
- translateLocally/
- scripts de diagnostic temporaires utilises localement

Chaque dossier d'outil restaure peut garder son propre README.txt. Ces README
sont suivis pour que les notes d'installation restent avec le dossier concerne.

Installation :

1. Restaurer les dossiers d'outils depuis une sauvegarde locale, un package de
   release ou les archives officielles des fournisseurs.
2. Les placer dans APIExpose/tools avec les noms indiques ci-dessus.
3. Garder dans ce dossier les gros binaires, modeles telecharges, caches et
   scripts ponctuels ; Git les ignorera.
4. Si un outil devient indispensable au build ou au lancement d'APIExpose, le
   documenter dans le README principal et le distribuer via l'installeur plutot
   que le committer ici.
