# Arcade et panels

APIExpose expose des **données arcade** pour bornes, LEDs et thèmes : les plugins comme [LedManager](https://nelfe80.github.io/RetroBat-Led-Manager/) s'en servent pour colorer vos boutons, et [MarqueeManager](https://nelfe80.github.io/RetroBat-Marquee-Manager/) pour animer lampes et scores.

## Les panels (dynpanels)

Le dossier `resources\dynpanels\` contient les définitions des panneaux de contrôle : boutons, couleurs, fonctions des commandes, layouts CPO — par système et par jeu. C'est grâce à elles que vos boutons prennent les couleurs des contrôles réels du jeu sélectionné.

## Les définitions RAM

Le dossier `resources\ram\` contient les définitions mémoire des jeux (fichiers `.MEM`) : elles permettent de détecter en temps réel les événements d'une partie — score, vies, power-ups — directement dans la RAM du jeu.

!!! note "Le Data Pack"
    `dynpanels`, `ram`, gamelists et autres données de `resources\` constituent l'**APIExpose Data Pack**, fruit d'un long travail de curation. Il est inclus dans l'archive `full` des releases et protégé par sa propre licence (`DATA-LICENSE.md`) — voir [Licences](licences.md).

## Le wrapper RetroArch

Pour lire la RAM des jeux, APIExpose s'appuie sur `wrapper\wrapper.dll`, une DLL proxy libretro qui s'intercale entre RetroArch et le cœur d'émulation, sans modifier RetroArch.

Chaque version publiée est accompagnée de son empreinte dans `wrapper\WRAPPER_VERSION.txt` :

```powershell
Get-FileHash wrapper\wrapper.dll -Algorithm SHA256
```

Le hash obtenu doit correspondre à celui du fichier `WRAPPER_VERSION.txt` et des notes de release. S'il diffère, n'utilisez pas la DLL.

## High scores et sorties MAME

APIExpose expose aussi :

- les **high scores** des jeux arcade (via hi2txt) ;
- les **sorties natives MAME** (`READY_LAMP`, `TORP_LAMP_1`…) sur le flux `/ws/arcade`, pour que lampes et LEDs revivent comme sur la borne d'origine ;
- le contexte du jeu courant et les événements runtime, pour overlays, thèmes ou outils externes.

Le détail des flux temps réel est dans [API locale](api.md).
