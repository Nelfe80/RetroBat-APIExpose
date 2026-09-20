# Médias et scraping

## Vos médias sont toujours prioritaires

APIExpose trouve, range et projette les médias des jeux : screenshots, logos et wheels, boxarts, fanarts, vidéos, manuels, magazines, cartes et médias de thèmes. Mais **vos fichiers passent d'abord** - placez-les ici et APIExpose les utilisera avant tout média téléchargé :

```text
media\user\systems\<système>\games\<jeu>\
```

## Médias pour marquee, topper et carte d'instructions

Les plugins d'affichage (MarqueeManager…) lisent leurs médias dans le store local. Pour personnaliser un jeu, les emplacements utiles :

```text
artwork\marquee\marquee.png
artwork\marquee\screenmarquee.png
artwork\marquee\dmd.png          (ou dmd.gif, dmd2.gif)
artwork\marquee\topper.jpg
artwork\ic\ic.png                (carte d'instructions)
artwork\fanart.png
ui\wheels\wheel.png
```

Pour surcharger les médias d'un **système** entier :

```text
media\user\systems\<système>\
```

Si aucun média système local n'existe, APIExpose cherche dans le thème EmulationStation courant, puis dans `es-theme-carbon` - il ne parcourt pas tous les thèmes installés.

## Le scraping automatique

APIExpose scrape **localement d'abord**, puis interroge ScreenScraper seulement si nécessaire. Le pilotage se fait dans le menu ES `AUTO SCRAPING MANAGER`.

La fiche du jeu courant peut se mettre à jour **sans recharger toute la liste**, mais seulement quand il y a un vrai changement visible : image, logo ou vignette ajoutés/remplacés, texte localisé dans la bonne langue, vidéo fraîchement scrapée. Les métadonnées brutes ou dans une mauvaise langue ne déclenchent pas de rafraîchissement.

## Un jeu d'arcade n'est scrapé qu'une fois

Le même jeu d'arcade est souvent installé sous plusieurs dossiers : `mame`, `fbneo`, `neogeo`, `cps2`… Ses médias sont les mêmes, alors APIExpose les partage : quand une fiche cherche une image, il regarde d'abord dans le store de son système, puis dans celui des autres systèmes d'arcade. S'il la trouve, il l'utilise là où elle est, sans la retélécharger ni la recopier.

Cela vaut aussi pour les textes : description, genre, éditeur, nombre de joueurs. Si un autre dossier d'arcade connaît déjà ce jeu, la fiche s'affiche complète tout de suite au lieu de montrer des « Inconnu » le temps d'aller chercher au loin. APIExpose continue quand même à demander la version propre à ce système, en arrière-plan : elle prendra la place à votre prochain passage.

Après un scrap, l'inverse se produit aussi : si le même jeu est présent sous un autre dossier d'arcade, sa fiche est préparée pour lui. Elle se mettra à jour la prochaine fois que ce système rafraîchira son affichage, sans rien recharger ni faire clignoter l'interface.

Vos médias gardent la priorité, et un média scrapé spécialement pour un système passe toujours devant celui d'un autre.

## Textes et langues

APIExpose gère les textes localisés des fiches : description, genre, date, développeur, éditeur, joueurs, langue, région, famille.

!!! tip "Changez la langue d'EmulationStation sans crainte"
    Quand la langue ES change, APIExpose réaligne les gamelists dans la nouvelle langue, et invalide les scrapes distants en cours pour ne pas réutiliser des résultats de l'ancienne langue.
