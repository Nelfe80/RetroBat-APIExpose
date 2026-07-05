# API locale

APIExpose expose une API REST et des WebSockets **en local uniquement** (`127.0.0.1`) — rien ne sort de votre machine.

## Les bases

| Quoi | Adresse |
|---|---|
| Santé du service | `http://127.0.0.1:12345/api/v1/health` |
| État du démarrage | `http://127.0.0.1:12345/api/v1/startup/ready` |
| **Swagger** (tous les endpoints, testables) | `http://127.0.0.1:12345/swagger/index.html` |
| WebSocket généraliste | `ws://127.0.0.1:12345/ws` |

!!! tip "Swagger est votre ami"
    L'interface Swagger documente et permet de tester chaque endpoint depuis le navigateur. C'est la référence à jour de l'API — toujours synchronisée avec la version installée.

## Les flux WebSocket spécialisés

Chaque flux pousse un instantané à la connexion, puis les changements en temps réel. Les reconnexions se font naturellement (les plugins officiels retentent après 5 secondes).

| Flux | Contenu |
|---|---|
| `/ws/marquee` | Média marquee/DMD du jeu ou système sélectionné |
| `/ws/topper` | Média topper |
| `/ws/instruction-card` | Cartes d'instructions |
| `/ws/frontend` | Démarrage/fin de jeu, navigation ES |
| `/ws/arcade` | Sorties MAME natives (lampes) et signaux RAM |
| `/ws/retroachievements` | Session RA : rich presence, unlocks, défis, leaderboards |
| `/ws/score` | Score temps réel normalisé, toutes sources |
| `/ws/timer` | Timer temps réel normalisé |
| `/ws/hiscore` | Notifications de high score |

## Construire votre propre outil

Un overlay OBS, un Discord rich presence, un tableau de scores maison : tout consommateur WebSocket/REST peut se brancher. Commencez par :

1. ouvrir Swagger pour explorer les endpoints REST ;
2. vous connecter à `ws://127.0.0.1:12345/ws` et observer les événements en naviguant dans EmulationStation ;
3. passer aux flux spécialisés quand vous savez ce que vous cherchez.

!!! note "Stabilité"
    Les flux listés ici sont ceux que consomment MarqueeManager et LedManager — ils sont maintenus en priorité et évoluent de façon rétrocompatible autant que possible.
