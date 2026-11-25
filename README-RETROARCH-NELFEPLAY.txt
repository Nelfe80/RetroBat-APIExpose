RETROARCH NELFEPLAY
===================

(English below)

Ce que c'est
------------
RetroArch NelfePlay n'est PAS une nocturne (version de developpement de RetroArch). C'est la version STABLE 1.22.2 de
RetroArch, celle que RetroBat installe (Git 69a4f0e), recompilee telle quelle avec trois correctifs deja acceptes par
l'equipe de RetroArch mais qui ne sont encore dans aucune version stable officielle. C'est donc une 1.22.2 "patched" par
NelfePlay, en attendant la prochaine stable.

Meme base que la version de RetroBat : meme SDL2, memes manettes, memes DLL (celles de RetroBat, rien d'autre n'est
remplace), memes reglages (retroarch.cfg n'est pas touche). Seul retroarch.exe change. La seule difference voulue :
l'interface de bureau Qt de RetroArch, que RetroBat n'utilise pas, n'est pas compilee.

C'est provisoire : des qu'une version stable officielle de RetroArch contiendra ces correctifs, NelfePlay reviendra a
elle et cette version sera retiree.

Version livree : 1.22.2-nelfeplay.1 (retroarch --version : "1.22.2 (Git 69a4f0e-nelfeplay.1)"), non signee,
sha256 aff5d5747bbaa0e533036d025b757667ee1866417b8cf8e46e803f37ca72d0cf.

Les trois correctifs (tous de l'equipe de RetroArch, repris sans modification)
------------------------------------------------------------------------------
1. Replays des jeux aux gros etats (libretro/RetroArch #19136, commit 8a8cc448b9) : la version officielle 1.22.2 plante
   au debut de l'enregistrement d'un replay sur certains jeux (TGM2, RayForce, Gun Lock, jeux du coeur MAME libretro).
   APIExpose devait donc ne PAS enregistrer leurs replays. Avec cette version, ils s'enregistrent et se relisent.
2. Commande d'etat (#18585, commit 68e8274a31) : RetroArch ne plante plus quand APIExpose lui demande son etat avec un
   coeur dont la fiche n'a pas d'identifiant de systeme.
3. Hote netplay (commit 5fa3573d88) : l'hote ne plante plus quand un joueur se connecte sans compression.

Comment elle s'installe
-----------------------
APIExpose la pose de lui-meme, jamais pendant une partie, et SEULEMENT par-dessus la 1.22.2 officielle de RetroBat
(empreinte verifiee). L'exe d'origine est garde dans emulators\retroarch\.nelfeplay\sauvegarde ; la marque est
emulators\retroarch\.nelfeplay\build.txt. Si RetroBat met RetroArch a jour, APIExpose ne touche pas a la nouvelle
version (jamais de retour en arriere).

retroarch.patched.RETROBAT.exe : c'est une copie de retroarch.exe que fait emulatorLauncher (RetroBat) pour l'hote d'une
partie netplay. APIExpose l'efface quand il pose ou retire sa version : emulatorLauncher la refait depuis le bon exe.

Revenir a la version de RetroBat
-----------------------------------
- Option d'ES : EXTENDED OPTIONS > API SETTINGS > RETROARCH NELFEPLAY (allumee par defaut). Eteinte, la version de
  RetroBat est remise a l'identique au prochain moment ou aucun jeu ne tourne.
- Ou, a la main : uninstall-retroarch-nelfeplay.bat (APIExpose ne la reposera plus d'elle-meme) ;
  install-retroarch-nelfeplay.bat la reprend.

Les scripts a cote de ce fichier
--------------------------------
- install-retroarch-nelfeplay.bat : pose la version NelfePlay (memes gardes que l'API).
- uninstall-retroarch-nelfeplay.bat : remet la version de RetroBat, octet pour octet.
- retirer-nocturne-retroarch.bat : NE CONCERNE PAS cette version. Il nettoie seulement l'essai d'octobre 2026 (une
  nocturne de RetroArch avec SDL3, ancienne marque .nelfeplay\installe.txt), retire depuis. APIExpose le lance au besoin.

Sources et licence
------------------
RetroArch est sous licence GPLv3. Sources exactes de cette version :
https://github.com/Nelfe80/RetroArch/tree/v1.22.2-nelfeplay.1
(la recette de compilation est dans pkg/nelfeplay du meme depot).


ENGLISH
=======

RetroArch NelfePlay is NOT a nightly build. It is the STABLE RetroArch 1.22.2 that RetroBat installs (Git 69a4f0e),
rebuilt as is with three fixes already accepted by the RetroArch team but not yet in any official stable release: a
NelfePlay "patched" 1.22.2, until the next stable comes out. Same SDL2, same controllers, same DLLs, same settings;
only retroarch.exe changes (the Qt desktop interface, unused by RetroBat, is not built). It is temporary: as soon as an
official stable release contains these fixes, NelfePlay goes back to it.

Fixes: replay recording of games with large states (#19136, 8a8cc448b9; TGM2, RayForce, Gun Lock, MAME libretro core),
status command without a system id (#18585, 68e8274a31), netplay host with an uncompressed peer (5fa3573d88).

APIExpose installs it by itself, never during a game, and only over RetroBat's official 1.22.2 (hash checked); the
original exe is kept in emulators\retroarch\.nelfeplay\sauvegarde. To go back: ES option EXTENDED OPTIONS > API
SETTINGS > RETROARCH NELFEPLAY off, or uninstall-retroarch-nelfeplay.bat. retirer-nocturne-retroarch.bat only cleans up
the October 2026 nightly trial and does not concern this build.

Sources (GPLv3): https://github.com/Nelfe80/RetroArch/tree/v1.22.2-nelfeplay.1
