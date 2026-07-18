# Audit des endpoints APIExpose — inventaire, manques, restructuration (2026-07-18)

Périmètre : la surface HTTP (`/api/v1/*`, 96 paths / 99 opérations / 27 controllers) et WebSocket
(`/ws` + 18 streams) de RetroBat.Api 1.3.3, croisée avec les besoins réels des consommateurs
(RetroCreator, LedManager, MarqueeManager, SDK JS public). Sources : swagger.json généré (post-fix),
lecture des controllers/services, et audit d'intégration des trois plugins (fichiers:lignes cités).

---

## 1. Résumé exécutif

1. **Swagger était cassé depuis la 1.3.1** (500 sur swagger.json) — réparé (commit `c13669b4`) :
   collision de schemaId entre `MameCfgDeployService.Report/Item` et `FbneoRmpDeployService.Report/Item`.
2. **La surface existante est saine dans l'ensemble** : peu de routes à déprécier, mais un contrat
   peu exploitable (32 opérations sans documentation, 30 sans réponse typée, aucune étiquette
   interne/prototype/déprécié, surface WS documentée nulle part).
3. **Le vrai déficit est en manques** : 10 états/actions du moteur sans endpoint, et 3 plugins qui
   contournent l'API en lisant les dossiers d'APIExpose sur disque ou en pollant.
4. **Proposition structurante** : réorganiser la présentation (tags Swagger) et la convention des
   FUTURES routes sur la **logique des managers du menu ES** — même modèle mental que le menu, le
   wiki et le schéma de dépendances — sans casser une seule route existante.

---

## 2. P0 réglé — Swagger 500

- Symptôme : `GET /swagger/v1/swagger.json` → 500 corps vide, UI « Failed to load API definition ».
- Cause : schemaId Swashbuckle = nom simple ; deux paires de records imbriqués homonymes
  (`Report`, `Item`) référencées par `ProducesResponseType` (PanelsController:323,343,361).
  Introduit en 1.3.1 avec `fbneormp/deploy`.
- Fix : `CustomSchemaIds` (type imbriqué → `DeclaringTypeNom`), Program.cs. Vérifié 200 / 235 Ko.
- Leçon (voir §9) : contrôle « swagger.json → 200 » en checklist de release ; jamais deux DTO
  exposés du même nom simple.

---

## 3. État des lieux et verdicts par route

Légende verdicts : **G** = garder tel quel · **G+doc** = garder, documenter/typer ·
**INT** = garder, étiqueter Interne · **PROTO** = garder, étiqueter Prototype ·
**DEP** = marquer déprécié (grace period, pas de suppression) · **CONS** = consolider.

### Système & santé
| Route | Verdict | Note |
|---|---|---|
| GET Health, GET Version | G+doc | réponses non typées |
| GET startup/ready, startup/gamelists | G+doc | 503 avant readiness à documenter |
| GET Config/local-options | G | riche et déjà documenté |

### Contexte & navigation
| Route | Verdict | Note |
|---|---|---|
| GET Context/state | **G+doc — canonique** | surface SDK ; non documentée, non typée |
| GET Context/current-game, current-system | CONS | doublons de state ; garder en compat, documenter « préférer state » ; RetroCreator les utilise (ApiExposeGateway.cs:366-471) |
| GET Context | CONS | triplon ; même traitement |
| es/controller/* (14 routes) | G+doc | `reloadgames` : à étiqueter « jamais en refresh auto » (doctrine UX, curseur perdu) |
| commands/launch, retroarch/command, retroarch/status | G+doc | loopback-only (403 cross-origin) à documenter |
| POST Intent/pushView | PROTO | auto-déclaré prototype |

### Local Media Manager
| Route | Verdict | Note |
|---|---|---|
| GET Media/{*path} | G+doc | **ajouter HEAD** (§6) |
| POST Media/rescrape/local, local-scrape/preview | G+doc | |
| POST Media/gamelist/generate, update-local, consolidate, refresh-selections, metadata/normalize | G+doc | refresh-selections déclenche reloadgames : le dire dans la doc |
| POST Media/rescrape/remote | **DEP** | tombstone archivé 2026-05-09, visible sans étiquette → `[Obsolete]` |

### Auto Scraping Manager
| Route | Verdict | Note |
|---|---|---|
| GET Media/scraping/status | G+doc | à enrichir (§5.1) : file, capacité addgames, caches |

### Roms Manager
| Route | Verdict | Note |
|---|---|---|
| rom-set-manager/options, audit, apply, apply-current-and-check, restore | G | le domaine le mieux couvert |
| rom-packs/on-the-fly/ensure-launch-rom, rescan | G+doc | |
| GET Gamelists/{system}/games | G+doc | utilisé par LedManager Setup (GamesView.cs:386) |

### Control Panel Manager
| Route | Verdict | Note |
|---|---|---|
| Panels (14 routes : catalog, system, game, definition, current, controls, deploys, preview) | G+doc | 5 POST deploy sans doc ; 409 « MAME en cours » à documenter |
| POST Panels/current/export-theme-xml | **DEP** | legacy auto-déclaré, supersédé par theme-datas |

### Themes & Collections
| Route | Verdict | Note |
|---|---|---|
| theme-datas/current/xml, current/export, export, audit | G | |
| *(aucune route collections)* | — | gap majeur, §5.6 |

### Game Events (scores, outputs, RA)
| Route | Verdict | Note |
|---|---|---|
| GET Outputs/mame, retroarch, retroarch/definition | G+doc | definition à enrichir (§6.1) |
| GET Hiscores | G+doc | non documentée, non typée |
| GET mamelua/sessions | INT | diagnostic |
| retroachievements/* (6 routes) | G+doc | |
| GET+POST /dorequest.php | INT | proxy transparent RetroArch — étiqueter, sinon incompréhensible pour un dev |

### Live Contest
| Route | Verdict | Note |
|---|---|---|
| livecontest/enroll, check, status + overlay/livecontest | G+doc | gating origine `*.nelfetech.com` à documenter |

### Notifications & UI
| Route | Verdict | Note |
|---|---|---|
| POST toast-notifications, es-notifications, es-notifications/messagebox | G+doc | distinction toast APIExpose vs notify ES natif à documenter |
| POST Toasts | CONS | doublon de toast-notifications — garder en compat, pointer la canonique |
| POST Toasts/task-progress/test | INT | test overlay |

### Interne / Prototype / Maintenance
| Route | Verdict | Note |
|---|---|---|
| POST ingest/Es | INT | ingestion hooks ES → bus |
| Maintenance/* (6 routes installer / retroarch-wrapper / es cache) | INT | dry-run à documenter |
| Hub/register, Hub/nodes ; Rules/active, compile | PROTO | auto-déclarés « future » |

**Bilan** : 2 dépréciations franches (déjà tombstonées, à étiqueter), 3 consolidations douces
(current-game/current-system/Context vs state ; Toasts vs toast-notifications), ~10 étiquettes
INT/PROTO, et l'écrasante majorité en « garder + documenter ».

---

## 4. Qualité du contrat (mesures sur le swagger.json réel)

- **32 opérations sans summary/description** — dont le cœur public : Context/state, current-game,
  current-system, Gamelists, Health, Hiscores, livecontest, Maintenance, les 5 deploys de contrôles,
  rom-packs, startup, Toasts, Version.
- **30 opérations sans réponse typée** (aucun schéma) — Context/*, Panels/preview,
  mamecfg/current, Hub/Intent/Rules, startup, dorequest.php…
- **0 étiquette** : pas d'`[Obsolete]`, pas d'`ApiExplorerSettings`, pas de tags de cycle de vie —
  l'interne et le prototype apparaissent au même niveau que le public.
- **Surface WS invisible** : 18 streams, préfixes d'événements par stream, messages retenus à la
  connexion (panel.state, score/timer, catalog RA…) — documentés nulle part côté API.

---

## 5. Manques côté moteur (état/action sans endpoint) — priorisés

1. **File de scraping** : profondeur, pending-persistence, cooldowns de `RemoteScrapeQueueService` —
   invisibles ; `scraping/status` ne les inclut pas. → enrichir `GET scraping/status`.
2. **Capacité `/addgames`** (`AddGamesSupported`, MediaRuntimeState — recommandé par audit-scrap §8) :
   l'état « cette version d'ES supporte-t-elle la mise à jour live » n'a pas de GET. → même
   enrichissement de `scraping/status`.
3. **Fusion pending-extended** (`ApplyPendingExtendedGamelistsAsync` / `Discard…`,
   GamelistUpdateService.cs:649,688) : déclenchée uniquement à l'arrêt d'ES. → `GET
   gamelist/pending-extended` (compteur par système) + `POST …/apply|discard`.
4. **Cache `exact-local-no-retry`** (TTL 14 j) : ni inspection ni purge — un média publié plus tard
   sur ScreenScraper reste bloqué sans recours opérateur. → `GET/DELETE scraping/no-retry-cache`.
5. **`RomSetHiddenLedger`** (romset-hidden-ledger.json, RomSetManagerService.cs:2765+) : « ce que le
   Roms Manager a caché et restaurerait » est invisible. → `GET rom-set-manager/ledger`.
6. **Collections sans controller** : `CollectionPackInstallerService` (index des packs installés,
   familles, install/apply) n'a AUCUNE route — asymétrie totale avec RomPacksController. →
   `GET collections/packs`, `POST collections/rescan`, `POST collections/apply-theme`.
7. **Autogen marquee/DMD** : `MarqueeAutogenService.GenerateForSelectedGameAsync` uniquement interne.
   → `POST marquee/autogen` (jeu/système ciblé) — utile MarqueeManager Setup et debug.
8. **populate_all** : action uniquement via écriture es_settings. → `POST local-media/populate`
   (+ GET progression) — cohérent avec le pattern « switch surveillé » documenté mais plus propre.
9. **Observabilité** : media-update-audit.jsonl, refresh-tracking.jsonl, addgames-payloads,
   game-session/ES-flow — aucun endpoint de lecture. → `GET diagnostics/{log}?recent=N` (INT).
10. **es-features menu** : pas de paire audit/deploy alors que MaintenanceController l'offre pour
    installer et retroarch-wrapper. → `GET/POST Maintenance/es-features/audit|deploy` (INT).

---

## 6. Manques côté consommateurs (workarounds mesurés dans leur code)

### 6.1 RetroCreator (ApiExposeGateway.cs)
- **Lit le `.mem` sur disque + SHA-256 local** (:686-694) — l'API ne renvoie que le chemin
  (`outputs/retroarch/definition`). → **endpoint n°1 en valeur** : `GET
  outputs/retroarch/definition/content` (signaux parsés + hash) — indispensable Live Contest
  (les viewers doivent partager la même définition de score).
- **Poll REST 2 s de current-system** (:405-463) car l'événement système n'est pas fiable. →
  fiabiliser `ui.system.selected` (et documenter la garantie).
- **Sondes médias en GET** (:238-246) — « le media controller ne répond pas à HEAD ». → HEAD.
- **Capabilities hard-codées true** (:766-774) alors que le contrat (docs/05) et le SDK
  (`getCapabilities`) les spécifient. → `GET capabilities` réel (mame, retroArch, hiscore, RA,
  memoryEvents, arcadeOutputs, ledManager, dynPanels + AddGamesSupported).

### 6.2 LedManager
- **Runtime lit `..\APIExpose\resources\dynpanels\games\*.json` sur disque** (PanelState.cs:280-284,
  GamePanelCatalog) alors que `GET panels/game/{system}/{rom}/definition` existe (seul le Setup
  l'utilise). → soit assumer le contrat « data pack local » (le documenter comme officiel), soit
  compléter le snapshot `panel.state` pour supprimer la lecture disque. Recommandation : documenter
  le data pack comme contrat (latence LED critique) ET compléter le snapshot — les deux sont
  légitimes.
- **Poll 1 s du processus emulationstation** (Program.cs:276) pour le lifecycle. → événement WS
  `ui.frontend.started/stopped` (le moteur sait déjà quand ES démarre/quitte — merge pending
  s'appuie dessus) + `GET Context/frontend`.
- **`mem.action` sans player id** (routé DefaultPlayer, Program.cs:419-421). → enrichissement
  additif du payload `mem.action` avec `player` quand la définition le porte.

### 6.3 MarqueeManager
- **Tous les médias résolus via le dossier frère `..\APIExpose`** (ResolveLocal:940-945) et
  **video.mp4 introuvable dans le snapshot** → parcours disque en dur (ResolveGameVideo:503-516).
  → ajouter `video` (et les absents) dans les snapshots `marquee.snapshot`/`screen.snapshot` sous
  forme d'**URL `/api/v1/media/...`** en plus du chemin relatif (additif, opt-in côté client).

### 6.4 Convergence SDK ↔ natifs
Le SDK public documente `context/state`, `panels/current`, 18 streams ; les consommateurs natifs
utilisent current-game/current-system + polls + disque. La cible : les natifs consomment la même
surface que le SDK (state + WS fiables), le SDK gagne `capabilities` réel et la découverte
`GET /ws/streams`.

---

## 7. Restructuration proposée — la logique des managers (alignée menu ES)

**Principe retenu** : le menu ES, le wiki, le schéma de dépendances et les gates du code partagent
déjà la hiérarchie des managers. L'API adopte le même modèle mental :

| Groupe (tag Swagger) | Manager ES correspondant | Routes actuelles rattachées |
|---|---|---|
| Système & santé | — (transverse) | Health, Version, startup, Config |
| Contexte & navigation | — (transverse) | Context, es/controller, commands, Intent |
| Local Media Manager | LOCAL MEDIA MANAGER | Media/* (serve + gamelist ops) |
| Auto Scraping Manager | AUTO SCRAPING MANAGER | Media/scraping/*, rescrape, (futur no-retry-cache) |
| Roms Manager | ROMS MANAGER | rom-set-manager, rom-packs, Gamelists |
| Control Panel Manager | CONTROL PANEL MANAGER | Panels/* |
| Marquee Manager | MARQUEE MANAGER | (futur marquee/autogen ; snapshots WS) |
| Themes & Collections | THEMES + COLLECTIONS PACK | theme-datas, (futur collections/*) |
| Game Events | GAME EVENTS MANAGER | Outputs, Hiscores, mamelua, retroachievements |
| Live Contest | (RetroCreator) | livecontest, overlay |
| Notifications & UI | API SETTINGS | toast-notifications, es-notifications, Toasts |
| Temps réel | — | /ws + (futur GET /ws/streams) |
| Interne & Prototype | — | ingest, Maintenance, Hub, Rules, dorequest.php, tests |

**Règles de mise en œuvre (rien ne casse)** :
1. Phase immédiate = **présentation seulement** : `[Tags]` sur les controllers existants — aucune
   URL ne change. L'UI Swagger raconte la même histoire que le menu ES.
2. **Toute nouvelle route naît dans la convention de son manager** (ex. les manques du §5 :
   `collections/*`, `marquee/autogen`, `rom-set-manager/ledger`…).
3. Les routes historiques mal rangées (ex. scraping sous `Media/…`) ne migrent PAS : on crée au
   besoin un alias canonique plus tard, l'ancienne reste en compat documentée. Dépréciation =
   étiquette + période de grâce d'au moins une version majeure, jamais de suppression sèche.
4. Le wiki (page API) adopte le même sommaire que la page Menus — un dev retrouve un endpoint là où
   un utilisateur retrouve l'option.

---

## 8. Recommandations « Swagger exploitable développeurs »

1. Documenter les 32 opérations nues (XML docs — pipe déjà branché).
2. Typer les 30 réponses anonymes (`ProducesResponseType`/`ActionResult<T>`) + erreurs
   `ProblemDetails` ; documenter 409 (MAME en cours), 403 (origine), 404 (module off), 503 (startup).
3. Exemples de payloads réalistes (package `Swashbuckle.AspNetCore.Filters`, IExamplesProvider)
   pour les POST complexes : es/controller/tap|combo|goto, panels/preview, rom-set-manager/apply,
   toast-notifications, commands/launch.
4. `[Tags]` par manager (§7) + `DocExpansion(None)` + `DisplayRequestDuration()`.
5. Cycle de vie : `[Obsolete]` → `deprecated:true` (rescrape/remote, export-theme-xml) ; filtre
   « Interne »/« Prototype » ; option : deux documents (public vs interne) via GroupName.
6. `OpenApiInfo` complet : version = Directory.Build.props, description avec doctrine de contrat,
   externalDocs → wiki.
7. Enums en chaînes (`JsonStringEnumConverter`) + `SupportNonNullableReferenceTypes()`.
8. Documenter le WS : `GET /ws/streams` (streams, préfixes, retained) + tableau dans la description
   Swagger et le wiki.
9. Checklist release : swagger.json → 200 vérifié à chaque build (CI `dotnet swagger tofile` —
   aurait attrapé la régression 1.3.1) ; publier swagger.json en artefact versionné des releases.
10. HEAD sur `/media/{*path}`.

---

## 9. Plan de phases proposé

| Phase | Contenu | Bénéficiaire | Rollback |
|---|---|---|---|
| E1 | **FAIT (`ea658195`)** : tags managers ordonnés menu ES, `[Obsolete]` ×2, OpenApiInfo (doctrine + version + wiki), UI DocExpansion/duration, `GET /api/v1/status` (services : API, WS+clients, ES, gates managers), `GET /api/v1/ws/streams` (catalogue dérivé de la source de vérité du hub), exemples réels (tap/combo/goto/toast), **0 op sans doc / 98 paths**. Découverte annexe : le XML de doc était déployé périmé depuis le 4 juillet — le déploiement copie désormais exe + xml. Reste d'E1 : enums-as-strings (attention : change la sérialisation des réponses, à traiter comme évolution de contrat). | tous les devs | revert commit |
| E2 | Typage des ~30 réponses anonymes restantes + exemples sur les autres POST complexes | SDK, intégrateurs | revert |
| E3 | Quick wins GET : scraping/status enrichi (file+capacité addgames), rom-set-manager/ledger, gamelist/pending-extended, no-retry-cache, HEAD média | opérateur + RetroCreator | flags/revert |
| E4 | Endpoints consommateurs : definition/content (.mem parsé+hash), capabilities réel, video/URLs dans snapshots, player dans mem.action, lifecycle frontend | RetroCreator, LedManager, MarqueeManager | additif → inoffensif |
| E5 | Controller collections + marquee/autogen + populate + es-features audit/deploy + diagnostics | parité interne | revert |
| E6 | **FAIT (`560258f3`)** : release.ps1 vérifie swagger.json→200 (bloquant) et publie swagger.json + asyncapi.yaml en artefacts de release ; `wiki/asyncapi.yaml` (AsyncAPI 3.0, 17 canaux, enveloppe réelle capturée) ; pages wiki API FR/EN restructurées managers (status + ws/streams en tête). Aussi : fbneormp/deploy hors Swagger + Obsolete (route conservée, non fonctionnelle), goto-system/goto-game marqués EXPERIMENTAL (préférer tap/combo). E6 restant : vrai job CI (dotnet swagger tofile au build, hors release manuelle). | écosystème | revert |

Chaque phase = commit dédié, JSON additif uniquement, payload addgames intouchable, reloadgames
jamais automatisé.

---

## 10. Doctrine de contrat (rappel, à inscrire dans l'OpenApiInfo)

- Contrat **additif** : on ajoute des champs, on n'en retire ni n'en renomme.
- Le payload `/addgames` vers ES est **gelé** (un payload anormal crashe ES).
- `/reloadgames` n'est **jamais** un refresh automatique (curseur perdu).
- Dépréciation = étiquette Swagger + période de grâce ; jamais de suppression sèche.
- Les managers OFF font répondre leurs domaines proprement (404/409 documentés), pas silencieusement.
