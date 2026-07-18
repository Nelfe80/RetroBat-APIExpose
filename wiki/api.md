# API locale

APIExpose expose une API REST et des WebSockets **en local uniquement** (`127.0.0.1`) — rien ne sort de votre machine.

## Les bases

| Quoi | Adresse |
|---|---|
| **État des services** (API, WebSocket, ES, managers) | `http://127.0.0.1:12345/api/v1/status` |
| Santé du service | `http://127.0.0.1:12345/api/v1/health` |
| État du démarrage | `http://127.0.0.1:12345/api/v1/startup/ready` |
| **Swagger** (tous les endpoints, testables) | `http://127.0.0.1:12345/swagger/index.html` |
| Découverte des flux WebSocket | `http://127.0.0.1:12345/api/v1/ws/streams` |
| WebSocket généraliste | `ws://127.0.0.1:12345/ws` |

!!! tip "Swagger est votre ami"
    L'interface Swagger documente et permet de tester chaque endpoint depuis le navigateur, avec des **exemples pré-remplis qui répondent vraiment** (megadrive/sonic pour les consoles, mame/1943 pour l'arcade). C'est la référence à jour de l'API — toujours synchronisée avec la version installée, et publiée en artefact (`swagger.json`) avec chaque release.

## Comment l'API est organisée

Les endpoints sont regroupés selon la **même logique que le menu EmulationStation** : chaque manager est la porte d'entrée de son domaine, et un manager OFF éteint toutes les fonctions de sa branche (voir le schéma de la page [Menus et options](menus.md)). `GET /api/v1/status` montre l'état effectif de chaque porte.

| Groupe Swagger | Domaine |
|---|---|
| System & Health | santé, démarrage, statut des services, options effectives |
| Context & Navigation | jeu/système courant, pilotage d'ES (tap, combo, launch) |
| Local Media Manager | médias servis, opérations gamelists |
| Auto Scraping Manager | état et actions du scraping |
| Roms Manager | filtres du romset, installeurs de packs, gamelists |
| Control Panel Manager | panels résolus, déploiements de contrôles |
| Themes & Collections | exports thèmes (.gameinfos) |
| Game Events | scores, sorties arcade, RetroAchievements, hiscores |
| Notifications & UI | toasts, notifications ES natives |
| Real-time (WebSocket) | découverte des flux |
| Internal & Prototype | maintenance, ingestion, surfaces futures |

!!! warning "Endpoints expérimentaux"
    `es/controller/goto-system` et `goto-game` ne sont pas encore fiables sur tous les thèmes/vues — préférez des séquences `tap`/`combo`. `reloadgames` ne doit jamais servir de rafraîchissement automatique (le curseur de l'utilisateur est perdu).

## Les flux WebSocket

Chaque flux ne délivre que les types d'événements de ses préfixes ; certains rejouent leur dernier instantané à la connexion (*retained*). La liste complète, toujours synchronisée avec la version installée : `GET /api/v1/ws/streams`. La spécification formelle (canaux, enveloppe, exemples réels) : [asyncapi.yaml](asyncapi.yaml), publiée aussi avec chaque release.

| Flux | Contenu |
|---|---|
| `/ws/frontend` | Navigation ES, démarrage/fin de jeu (`ui.*`) |
| `/ws/marquee`, `/ws/topper`, `/ws/instruction-card`, `/ws/screen` | Instantanés médias des surfaces physiques |
| `/ws/panel` | Panel résolu du contexte courant (`panel.state` retenu) |
| `/ws/ingame` | Événements mémoire in-game (RetroArch, MAME Lua) |
| `/ws/arcade` | Sorties MAME natives (lampes), sessions |
| `/ws/score`, `/ws/timer` | Score et timer temps réel normalisés (retenus) |
| `/ws/retroachievements` | Session RA : rich presence, unlocks, défis, leaderboards |
| `/ws/hiscore` | Captures et mises à jour de high scores |
| `/ws/media`, `/ws/roms`, `/ws/system`, `/ws/control`, `/ws/esevent` | Store médias, Roms Manager, service, commandes, hooks ES bruts |

Toute l'enveloppe est commune : `{ "Type": "ui.game.selected", "Ts": "...", "NodeId": "...", "CorrelationId": "...", "Payload": { ... } }` — le contrat est additif (les champs s'ajoutent, jamais retirés).

## Construire votre propre outil

Un overlay pour navigateur, un rich presence, un tableau de scores maison : tout consommateur WebSocket/REST peut se brancher. Commencez par :

1. `GET /api/v1/status` pour vérifier que tout est vert ;
2. ouvrir Swagger et essayer les endpoints (les exemples répondent) ;
3. vous connecter à `ws://127.0.0.1:12345/ws` et observer les événements en naviguant dans EmulationStation ;
4. passer aux flux spécialisés (`/api/v1/ws/streams`) quand vous savez ce que vous cherchez.

!!! note "Stabilité"
    Les flux listés ici sont ceux que consomment MarqueeManager et LedManager — ils sont maintenus en priorité et évoluent de façon rétrocompatible.
