# Audit de latence bout-en-bout — navigation EmulationStation → Marquee & Panel LED

> Audit du 2026-07-17. Analyse statique des sources : APIExpose, MarqueeManager, LedManager,
> et EmulationStation (batocera, `projects-source\batocera-emulationstation-master`).
> Aucun code modifié. Chaque affirmation est référencée `fichier:ligne`.
> Le plan de correction associé est dans [Implement.md](Implement.md).

## Symptôme

Quand on navigue dans EmulationStation (changement de jeu ou de système), le panel LED est lent à se
mettre à jour et le marquee très lent. Le retard **croît avec la durée/vitesse de navigation, jusqu'à
~30 s**, puis se résorbe seulement quand on arrête de naviguer : les consommateurs « rattrapent leur
retard » en affichant un à un des états déjà périmés.

## Verdict en une phrase

Toute la chaîne fonctionne en **journal d'événements FIFO** (chaque jeu survolé est traité
intégralement, dans l'ordre), alors que le besoin est un **état courant** (seul le dernier jeu
sélectionné compte). Le retard s'accumule à deux endroits : (1) l'ingestion WebSocket des deux
consommateurs, qui ne coalesce rien, et (2) APIExpose, qui ne publie la sélection qu'après des appels
HTTP série vers l'API ES, précisément quand celle-ci est ralentie par ses propres reloadgames/addgames.

---

## 1. Architecture réelle (qui lit/écrit quoi)

```
EmulationStation (C++)
  │  fireEvent("game-selected"/"system-selected")            [FileData.cpp:1826, SystemView.cpp:754]
  │  → file FIFO de scripts, 1 thread, 1 spawn cmd.exe/évt   [Scripting.cpp:24-72]
  ▼
hooks .bat  →  events.ini  (écriture NON atomique `>`)       [.installer\scripts\game-selected\APIExpose.bat]
  ▼
APIExpose (RetroBat.Api.exe)
  │  FileSystemWatcher sur events.ini (pas de polling)       [EmulationStationWatcherProvider.cs:103-112]
  │  ProcessEventAsync (fire-and-forget par événement)       [EmulationStationWatcherProvider.cs:203-205]
  │  ← appels HTTP vers l'API ES :1234 (systems, games)      [EmulationStationWatcherProvider.cs:387,394]
  │  → bus interne → WebSocket push (11 streams)             [Program.cs:240-243, 293-302]
  ▼                          ▼
MarqueeManager            LedManager
  clients WS purs — AUCUNE lecture d'events.ini, AUCUN polling HTTP
  [WebSocketListenerService.cs:89-132]   [LedManager\Program.cs:159-170, 238-285]
  ▼                          ▼
Écran marquee (WPF/Skia)   PicoCommandSender.exe (process enfant, stdin) → série → Pico
```

Points importants qui contredisent des intuitions courantes :

- **Personne ne polle `events.ini`** : la détection est un FileSystemWatcher, la distribution est du
  push WebSocket. Le problème n'est pas un intervalle de sondage.
- **LedManager ne lit aucun `.rmp`/`.cfg` en navigation** : la config panel vient du push `panel.state`
  lui-même, plus enrichissement fichier dynpanel/overrides.
- **L'aval de LedManager est déjà bien conçu** : queue capacité 1 drop-oldest vers chaque sender
  ([LedManager\Program.cs:1298-1304]) et coalescing last-state-wins dans PicoCommandSender
  ([PicoCommandSender\Program.cs:192-244]). Le trou est en amont, à l'ingestion.

---

## 2. Étage EmulationStation (sources batocera)

### Points d'émission et cadence

| Événement | Où | Cadence |
|---|---|---|
| `game-selected` | [es-app/src/FileData.cpp:1826] `setSelectedGame()` | à chaque pose du curseur ; en vue basique, supprimé pendant le défilement rapide (`isScrolling()`, [BasicGameListView.cpp:27]) ; en vues détaillées, émis par mouvement ([DetailedContainer.cpp:1387,1403]) |
| `system-selected` | [SystemView.cpp:754] (CURSOR_STOPPED) et [ViewController.cpp:195] (`goToSystemView`) | à l'arrêt du carrousel systèmes + à chaque entrée dans un système |
| `game-start` / `game-end` | lancement/fin d'émulateur | ponctuel |

### Mécanisme d'exécution des scripts ([es-core/src/Scripting.cpp])

- Les événements `game-selected`, `system-selected`, `game-start`, `game-end`… sont déclarés
  **« async »** (l.84).
- Sous Windows, un script d'événement async est **mis en file FIFO** (`mScriptQueue`, l.19) drainée par
  **un seul thread** qui **spawne un process par commande** (`cmd.exe` pour un `.bat`), sans attendre
  la fin (`waitForExit=false`, l.43-47).
- **Déduplication minimale** : seule une commande `*-selected` strictement identique à la précédente est
  ignorée (l.62-63). **Aucun coalescing** : N jeux survolés = N spawns de process, dans l'ordre.
- **Sémantique des suffixes** (l.120) :
  - nom finissant par **`-wait`** → exécution **synchrone bloquante** pour le thread ES qui a émis
    l'événement (utilisé volontairement par `game-start\APIExpose-wait.bat` pour garantir l'état avant
    le lancement du jeu) ;
  - nom finissant par **`-nowait`** → force la mise en file asynchrone pour un événement normalement
    synchrone (sauf `quit`).
  - **Nos hooks `*-selected` n'ont aucun suffixe → déjà asynchrones. ES n'est jamais bloqué par eux.**
    Il n'y a rien à gagner à toucher `-wait`/`-nowait` pour la navigation.
- `fireEvent` fait une énumération de répertoires de scripts à chaque événement (l.169, 196) : I/O
  faible, négligeable.

### Le hook `game-selected`

`.installer\scripts\game-selected\APIExpose.bat` (déployé dans
`emulationstation\.emulationstation\scripts\game-selected\`) écrit 3 lignes :

```
event=game-selected
<system> <path> "<name>"
timestamp=...
```

avec une redirection **`> events.ini` non atomique** (troncature puis écriture). Seul le hook
`game-start` utilise le modèle atomique `.tmp` + `move /y`.

### Coût et verdict pour l'étage ES

~10-30 ms par événement (spawn de process), file drainée vite. **ES n'est pas le goulot.** Ses deux
défauts réels : il produit un *journal* (pas un état), et l'écriture non atomique provoque des lectures
partielles côté watcher (voir §3).

---

## 3. Étage APIExpose

### Détection (`EmulationStationWatcherProvider.cs`)

- FileSystemWatcher sur le dossier d'`events.ini` (l.103-112), traitement immédiat, pas de debounce
  watcher.
- Garde d'écriture partielle : relecture avec snapshot mtime+taille, 4 tentatives × 5 ms (l.302-329) ;
  si le contenu est incomplet (< 2 args pour game-selected, l.270-300), **l'événement est abandonné**
  et on attend le Changed suivant → conséquence directe de l'écriture non atomique du hook : des
  sélections peuvent être perdues (rattrapées seulement au mouvement suivant).
- Anti-doublon par signature `event|args` (l.1001-1016).
- Chaque événement complet part en **`Task.Run(ProcessEventAsync)` fire-and-forget** (l.203-205), avec
  un compteur `_frontendEventSequence` pour le latest-wins aval (l.733-734).

### Le problème central : publication APRÈS les appels ES série

Dans `ProcessEventAsync` pour `game-selected` :

| Ligne | Étape | Coût |
|---|---|---|
| 362-368 | contexte de base **déjà en mémoire** (system, path, name) | 0 |
| 375-385 | fenêtre de suppression live-addgames → **`return` AVANT toute publication** | sélection avalée, marquee/panel jamais mis à jour |
| 387 | `await FetchSystemDetailsAsync` → `GET :1234/systems/{id}` | timeout 2 s × 3 retries ⇒ ≤ ~6,75 s si ES lent |
| 394 | `await FetchGameDetailsAsync` → `GET /systems/{id}/games` (cache 600 s ; +1 appel fallback si jeu introuvable, l.1157-1200) | ≤ ~6,75 s, ×2 si fallback ; gros JSON pour MAME |
| 409 | **enfin** `PublishRawFrontendEventAsync("ui.game.selected.raw")` | — |
| 416 | `PublishFrontendEventAsync` | — |

Le timeout HttpClient vers ES est de 2 s ([EmulationStationWatcherProvider.cs:87]) avec 3 tentatives
espacées de 250 ms (`GetEsApiWithRetryAsync`, l.1272-1291). **Pire cas : ~13-20 s avant que le premier
octet ne parte vers le marquee et le panel** — tout ça pour enrichir des `Details` dont l'affichage
immédiat n'a pas besoin.

### La boucle de rétroaction reloadgames/addgames

APIExpose déclenche `/reloadgames` (`ReloadGamesHostedService`, poll 500 ms, min 5 s) et `/addgames`
(`LiveEsMediaPushDelayMs=1200`). Pendant ces opérations, l'API ES `:1234` est lente ou indisponible —
exactement au moment où le chemin chaud (l.387/394) l'interroge → timeouts + retries qui s'empilent.
Observation utilisateur cohérente : « les addgames et reloadgames n'ont pas le temps de se faire quand
je navigue en rafale » — et pendant ce temps, chaque sélection paie le prix fort.

### Projection médias et diffusion WebSocket (corrects dans l'ensemble)

- `PhysicalMediaWebSocketProjectionService` s'abonne à `ui.game.selected.raw` (l.77-82) et construit
  les snapshots marquee/topper/instruction-card/screen : **~60-100 `Directory.Exists`/`EnumerateFiles`
  par sélection, non cachés** (l.1197-1220 et alentours), atténués par un debounce 35 ms latest-wins.
  Rapide en absolu (media store local) — pas la cause des 30 s.
- `WebSocketConnectionManager` : **coalescing latest-wins côté serveur** par clé
  (`QueueLatestWinsBroadcast`, l.160-234) + throttle adaptatif 30-220 ms. Bon design — mais il ne peut
  pas compenser un consommateur qui drain lentement : une fois le message parti dans le socket, il est
  dans le tampon TCP du client.
- **Amplification ×2** : chaque sélection produit un couple brut + enrichi sur `frontend` et deux
  snapshots `marquee` (visible dans les logs du MarqueeManager). Double le débit que les consommateurs
  doivent drainer.

---

## 4. Étage MarqueeManager — la cause n°1 du symptôme

### Ingestion ([WebSocketListenerService.cs])

- 11 streams WS, une boucle par stream (l.89-90).
- `ReceiveAsync` (l.114-139) : lit un message, puis **`await ProcessAsync` inline** (l.132) — le
  message suivant n'est lu **que lorsque le précédent est intégralement traité**. FIFO strict,
  **aucun coalescing, aucun debounce, aucun saut au dernier**. Les messages en attente s'empilent dans
  le tampon TCP.

### Coût payé pour CHAQUE snapshot (même périmé)

| Étape | Localisation | Coût |
|---|---|---|
| Parse JSON | l.131 | ~ms |
| Résolution chaîne média | `CompositionChainResolver` (l.56-105, 262-278) | rafale de `File.Exists` : jusqu'à 10 extensions × orthographes système × sources de la chaîne, non caché par sélection |
| Contexte effets | `IngameEffectLibrary.SetContext` | 1-3 JSON si le jeu change |
| Fanout affichage | `MarqueeController.DisplayMediaAsync` | `File.Exists` + `BeginInvoke` (non bloquant) |
| **Décode DMD** | `HandleMarqueeAsync` l.240 → `DmdService` → `DmdFrameRenderer.RenderImage` (l.13-26) | **`Image.FromFile` GDI+ synchrone SUR LE THREAD WS**, à chaque snapshot, **même sans matériel DMD** (DmdDevice.log : « No renderers found ») |
| Rendu WPF (aval, latest-wins) | `MarqueeWindow.DisplayImage` l.482-486 | décode `BitmapImage` synchrone sur le thread UI |
| Lighting (aval, latest-wins) | `MarqueeLightingRenderer.StartGeneration` l.879-959 | décode artwork + génération carte **CPU 12-118 ms mesurés**, raster CPU, **aucun cache inter-sélection** ; FPS mesuré s'effondrant à 1-6 pendant les rafales |

Coût inline par snapshot sur le thread WS : **~8-45 ms**, ×2 snapshots par sélection = **15-90 ms par
jeu survolé**, à comparer à une cadence de navigation de ~100-150 ms par touche. Dès que les images
sont lourdes ou que la machine est chargée (lighting CPU en fond), le débit de drainage passe sous le
débit d'arrivée → **le backlog croît sans borne**. Après un scroll de 50-100 jeux, le dernier snapshot
est affiché des dizaines de secondes après le relâchement. **C'est mathématiquement le symptôme
observé.**

Les gardes latest-wins existantes (`_latestImagePath`, single-flight lighting, `_layoutRenderBusy`)
n'agissent qu'**après** le dépilage : elles évitent du travail d'affichage redondant mais ne sautent
aucun message.

Aussi : aucun `CancellationToken` par sélection (le token est celui de la durée de vie du stream) ; un
snapshot obsolète est traité de bout en bout.

---

## 5. Étage LedManager — même défaut, coût moindre

### Ingestion ([LedManager\Program.cs])

- 5 streams WS (l.159-170). Boucle de réception : **`await HandleJsonAsync` inline** (l.271) — même
  motif FIFO strict sans coalescing que le MarqueeManager.

### Coût payé pour CHAQUE panel.state (avant même de savoir s'il est périmé)

| Étape | Localisation | Coût |
|---|---|---|
| Parse + **`Clone()` de l'arbre JSON complet** | `LedEvent.cs:54-107` (clone l.105) | ~ms, proportionnel au payload panel (volumineux) |
| Construction PanelState | `PanelState.cs:19-40` via `ReadStringDeep`/`ReadLongDeep` (l.725-792) | **~10 balayages récursifs plein-arbre** |
| Enrichissement dynpanel | `PanelState.cs:250-286` | 1-2 `File.Exists` + `File.ReadAllText` + parse (`..\APIExpose\resources\dynpanels\...`) |
| Overrides utilisateur | `PanelOverrides.cs:21-53` | jusqu'à 4 `File.Exists` + lecture/parse si présent |
| **Garde de séquence** | `Program.cs:321-333` | placée **APRÈS** tout ce qui précède : un panel périmé a déjà payé toute l'I/O |
| Routage + dispatch | `CommandRouter.cs:61-88` → queue cap-1 drop-oldest (l.1370) | faible, bien conçu |
| Pulse START/SELECT | `Program.cs:738-770` | fond, 140 ms, non bloquant |

L'aval est sain : queue panel capacité 1 drop-oldest ([Program.cs:1298-1304]), pacing 10 ms,
coalescing last-state-wins dans PicoCommandSender, dédup d'état série. **Seul le dernier panel est
jamais envoyé au Pico** — mais l'ingestion a déjà payé le coût de tous les intermédiaires.

### Pics ponctuels (hors navigation, à connaître)

- Réinit firmware Pico : `PostInitDelayMs=9000` tenu sous `initLock` → **~9 s de sends série gelés**
  si le Pico ré-annonce READY pendant la navigation ([PicoCommandSender\Program.cs:47-70],
  [PicoCommandSender.ini:39]).
- Reconnexion série : jusqu'à 10 × 1 s ([PicoCommandSender\Program.cs:1498-1568]).
- Démarrage : `StartupDelayMs=18000` + `PostInitDelayMs=9000` — long premier affichage, mais non
  cumulatif.

Les effets MEM (`default.mem.effects.json`) ne jouent **aucun rôle** en navigation (résolus uniquement
sur les streams `mem`/`ingame`, catalogue chargé une fois au démarrage).

---

## 6. Comptage par sélection de jeu (chaîne complète, régime nominal)

| Étage | Lectures fichiers | Écritures | Requêtes HTTP | Messages WS émis/reçus |
|---|---|---|---|---|
| ES (hook) | énumération dossiers scripts | 1× `events.ini` (non atomique) | 0 | 0 |
| APIExpose watcher | 1-4 relectures `events.ini` | 0 | **2-3 vers ES :1234** (2 s timeout ×3 retries chacun) | 0 |
| APIExpose projection | ~60-100 `Directory.Exists`/`EnumerateFiles` | 0 | 0 | **~8+ messages émis** (frontend ×2, marquee ×2, topper, instruction-card, screen, panel…) |
| MarqueeManager | dizaines de `File.Exists` + 2-3 décodes image (DMD, WPF, lighting) ×2 snapshots | 0 | 0 | ~6 reçus |
| LedManager | jusqu'à 6 `File.Exists` + 2 lectures JSON | 0 | 0 | ~2 reçus |
| PicoCommandSender | 0 | 1 write série | 0 | 0 |

Latence nominale mesurable (caches chauds, ES réactif) : **~100-400 ms** bout-en-bout. Latence
pathologique : **13-20 s côté APIExpose** (appels ES × retries pendant reload) **+ backlog non borné
côté consommateurs** (15-90 ms × nombre de jeux survolés pour le marquee). Les deux effets s'ajoutent
→ les ~30 s observées.

---

## 7. Hiérarchie des causes

1. **[RACINE — consommateurs] Ingestion WS séquentielle sans coalescing.**
   MarqueeManager [WebSocketListenerService.cs:132], LedManager [Program.cs:271]. Chaque état
   intermédiaire est traité intégralement ; rien ne saute au dernier. C'est la cause de la latence
   *cumulative* et du « rattrapage » d'états périmés.
2. **[RACINE — APIExpose] Publication de la sélection après 2-3 appels ES série** (timeout 2 s × 3
   retries) [EmulationStationWatcherProvider.cs:387-416], aggravée par la boucle
   reloadgames/addgames ⇄ API ES, et **suppression pure de publications** dans la fenêtre
   live-addgames (l.375-385).
3. **[AMPLIFICATEUR] ×2 raw+enrichi** par sélection (double le débit à drainer).
4. **[AMPLIFICATEUR] Coût par message élevé et non annulable** : décode DMD GDI+ synchrone sur le
   thread WS (même sans matériel), rafales `File.Exists` non cachées, lighting CPU sans cache LRU,
   clone+balayages plein-arbre LedManager, garde de séquence placée après l'enrichissement.
5. **[FIABILITÉ] `events.ini` non atomique** → lectures partielles → sélections abandonnées.
6. **[PICS] Réinit/reconnexion série Pico** (9-10 s), hors navigation nominale.
7. **[MINEUR] Étage ES** : file FIFO à 1 spawn/événement, dédup limitée — coût réel faible.

## 8. Principe directeur de la correction

**Sémantique « dernier état courant » de bout en bout.** À chaque étage, un seul invariant : *ce qui
est en attente de traitement est au plus UN état, le plus récent*. Concrètement : slot « dernier reçu »
(capacité 1, drop-oldest) à l'ingestion des deux consommateurs, publication immédiate du contexte
mémoire côté APIExpose avec enrichissement asynchrone ré-émis ensuite, garde de fraîcheur avant tout
travail coûteux, annulation des travaux obsolètes, caches pour rendre le coût unitaire négligeable.
Le détail phasé, fichier par fichier, est dans [Implement.md](Implement.md).

Gain attendu : latence nominale inchangée (~100-400 ms) ; en rafale, le retard est borné à **un seul
message en vol** → le jeu final s'affiche en **< 500 ms** après l'arrêt de la navigation, sans aucun
rejeu d'états intermédiaires, quelles que soient la durée et la vitesse du scroll.
