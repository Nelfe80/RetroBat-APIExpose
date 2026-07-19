# Plan d'implémentation — affichage immédiat de l'état courant (marquee + panel)

> Fait suite à [audit.md](audit.md). Objectif : quand on navigue dans EmulationStation, le marquee et
> le panel reflètent le **dernier état courant** en < 500 ms, sans jamais rejouer d'états périmés,
> quelle que soit la durée/vitesse de la navigation. Latence nominale par sélection inchangée.

## État d'avancement (2026-07-17)

| Phase | État | Commit | Rollback sans rebuild | Rollback git |
|---|---|---|---|---|
| A — MarqueeManager coalescing + DMD | **FAIT** (A1+A2 ; caches A4 et annulation A3 reportés) | `5bb431e` (repo MarqueeManager) | `config.ini` → `[Settings] CoalesceStateStreams=false` | `git revert 5bb431e` |
| A' — LedManager coalescing | **FAIT** (slot + garde avant enrichissement ; cache A'3 reporté) | `b79770f` (repo LedManager) | `LedManager.ini` → `[APIExpose] CoalescePanelStates=false` | `git revert b79770f` |
| B — APIExpose publication immédiate | **FAIT** (B1+B2 ; B3 timeouts et B4 différé reload reportés) | `1737cf9c` (repo APIExpose) | `appsettings.json` → `EmulationStationWatcher:PublishSelectionBeforeDetails=false` | `git revert 1737cf9c` |
| C — Hooks events.ini atomiques + lecture tolérante | **FAIT** (2026-07-18) | `570300c8` (repo APIExpose) | — (pas de flag ; comportement transparent) | `git revert 570300c8` + recopier les hooks dans le dossier scripts d'ES |
| C-bis — MarqueeManager en priorité CPU BelowNormal | **FAIT** (2026-07-18) | `a1e3d1b` (repo MarqueeManager) | `config.ini` → `ProcessPriority=normal` | `git revert a1e3d1b` |
| E — Gate de rafale game-selected + poller de secours FSW | **FAIT** (2026-07-18 ~01:00, déployé) | `d35a81f9` (repo APIExpose) | `appsettings` → `EmulationStationWatcher:CoalesceSelectionBursts=false` (fenêtres réglables : `SelectionBurstQuietMs=600`, `SelectionBurstProgressMs=800`) | `git revert d35a81f9` |
| E2 — Filtre des re-fires fantômes post-addgames/reloadgames | **FAIT** (2026-07-18 ~01:35, déployé ; validation terrain en attente d'un vrai cycle de scrape) | `7f1d16c0` (repo APIExpose) | `CoalesceSelectionBursts=false` désactive aussi la fenêtre/le filtre | `git revert 7f1d16c0` |
| D — Nettoyages | non commencé | — | — | — |

**Phase E2 — le fantôme post-push (banc réel taps via `/api/v1/es/controller/tap`).** Quand APIExpose
pousse `/addgames` ou `/reloadgames`, ES re-émet des `game-selected` pour chaque vue rafraîchie :
d'abord les curseurs périmés des vues non visibles (mesuré : `88games` rejoué ~30 s après chaque
arrêt de navigation), en dernier le curseur réel de la vue visible (parfois dédupliqué par ES si
inchangé — le fantôme arrive alors seul). Correctifs : horodatage des push UI dans MediaRuntimeState
(`RecordEsUiRefreshPush`/`IsWithinPostEsRefreshWindow`), leading edge différé pendant 4 s post-push
(la paire fantôme→réel se réduit au dernier), et filtre : dans la fenêtre, sélection ≠ contexte
courant ET ∈ historique LRU (48) → ignorée avec log INFO « post-refresh ghost selection ignored ».
Compromis : une navigation réelle vers un jeu récemment visité pendant ces 4 s peut être ignorée une
fois (auto-corrigé au mouvement suivant). Ce banc a aussi montré : en cadence moyenne (~330 ms/tap),
le drain de la file ES suit à ~1:1 avec 2-3 états rejoués dans les ~3 s suivant l'arrêt (espacement
> fenêtre quiet — limite structurelle de la file ES, binaire figé) ; en cadence rapide, ES coalesce
lui-même via `isScrolling` et le résultat est propre. Limite du banc : le backend clavier plafonne à
~330-640 ms/tap ; la manette réelle est plus rapide (davantage coalescée par ES, donc plus favorable).

**Phase E — pourquoi et mesures.** La file de scripts d'ES (Scripting.cpp : FIFO, 1 spawn cmd.exe
par jeu survolé en navigation par pressions ; binaire figé, pas de patch possible ; aucune route
HTTP ES n'expose le curseur) draine APRÈS l'arrêt de l'utilisateur : events.ini rejouait chaque
sélection intermédiaire, espacée au-delà de tous les debounces (CpoPanel 15 ms, WS 30-220 ms,
consommateurs), et le panel rejouait le journal ~10 s. Correctif dans le watcher (premier étage
contrôlé) : leading edge immédiat, coalescing latest-wins sous `SelectionBurstQuietMs`, échantillon
de progression toutes les `SelectionBurstProgressMs`, purge du slot sur lifecycle. Les intermédiaires
n'entrent plus dans ProcessEventAsync → plus de fetch ES/prefetch/scrape par jeu survolé → le drain
ES lui-même s'accélère (la charge par événement était sa cause de lenteur). Mesures WS `/ws/panel` :
rafale 45 ms ×12 → 2 états (9 avant) ; rafale 300 ms ×10 → 5 états, final +570 ms (10 avant) ;
sélection isolée : 165 ms.

**Découverte phase E — pertes FileSystemWatcher.** Mesuré : 6 remplacements atomiques sur 10
(espacés de 300 ms) perdus par le FSW sur ce répertoire actif → un « goto » (saut direct vers un
jeu) pouvait ne jamais s'afficher (symptôme utilisateur : « le goto ne marche pas, haut/bas oui »).
Filet : poller mtime/taille toutes les 150 ms qui rappelle `HandleEventsIniChanged` si le fichier a
changé ; la dédup par signature absorbe les recouvrements avec le FSW. Coût : 1 stat fichier/150 ms.

Chaque flag runtime restaure exactement le comportement historique : un simple redémarrage du
process concerné suffit après l'avoir changé.

**Déployé le 2026-07-17 à 23:28** : les trois exe racine (RetroBat.Api.exe, LedManager.exe,
MarqueeManager.exe) ont été republiés (mêmes flags que les build.bat) et les process relancés avec
les recettes exactes des hooks ES, session EmulationStation en cours conservée.

**Mesure post-déploiement (test de rafale synthétique)** : 25 écritures `game-selected` dans
`events.ini` à 45 ms d'intervalle (simulation d'un scroll rapide MAME). Résultat : le marquee suit
la rafale en direct (intermédiaires coalescés) et affiche le jeu final **126 ms** après la dernière
écriture (23:30:24.541 → 23:30:24.667), zéro rattrapage d'états périmés. Objectif < 500 ms atteint.
Reste identifié pendant le test : le moteur lighting CPU sature en rafale (render scale abaissé
100 % → 50 % par l'autorégulation) — c'est le chantier GPU/cache LRU (A4/Phase 0 lighting), sans
impact sur la latence de sélection.

**Découverte pendant le test** : 4 écritures sur 25 ont été rejetées (« fichier en cours
d'utilisation ») — la lecture du watcher verrouille brièvement `events.ini`, donc le hook cmd `>`
peut lui aussi échouer silencieusement en rafale = sélections perdues. La phase C (écriture `.tmp`
+ `move /y`, qui est un rename sans double écriture des données) corrige précisément cette course ;
à faire dans une prochaine passe.

**Phase C réalisée (2026-07-18)** — deux volets complémentaires :
1. **Hooks atomiques** : `game-selected`, `system-selected`, `game-end`, `game-start` (les deux
   variantes) écrivent un tmp privé unique (`events.%RANDOM%%RANDOM%.tmp`) puis `move /y` (rename =
   métadonnées, pas de double écriture des données — la lenteur vue autrefois venait d'une variante
   copy/type ou du scan AV). Le FSW du watcher (filtre `events.ini`) voit le `Renamed`, jamais les
   tmp. Copies live ET sources `.installer` synchronisées ; les sources `-wait` de game-selected /
   system-selected ont été renommées en `APIExpose.bat` (déployées telles quelles, elles auraient
   rendu les hooks synchrones et bloquants pour ES — piège levé, validé par l'utilisateur).
2. **Lecture tolérante** : les 4 lectures d'events.ini (watcher, EsControllerService,
   GamelistUpdateService, RomPackInstallerService) passent par `EventsIniFile.ReadAllLines`
   (`FileShare.ReadWrite|Delete`) — un hook qui écrit pendant une lecture ne peut plus échouer
   (cause mesurée : 4 écritures rejetées sur 25 en rafale).

**Diagnostic terrain 2026-07-18 (« events.ini reste sur jaguar quand MarqueeManager est ouvert »)** :
symptôme de famine CPU — le raster lighting (CPU, cf. chantier Phase 0/GPU) sature la machine, le
thread de scripts d'ES (1 spawn cmd.exe par sélection, file FIFO) draine au ralenti, `events.ini`
prend des minutes de retard ; MarqueeManager fermé → tout redevient fluide. Mitigation livrée :
**MarqueeManager démarre en priorité CPU BelowNormal** (`ProcessPriority=belownormal`, rollback
`=normal`) — sous charge, c'est le rendu du marquee qui dégrade en premier (l'autorégulation du
RenderScale absorbe), jamais ES ni la chaîne d'événements. Le fond du sujet reste le portage GPU du
lighting (hors périmètre latence).

**Exe redéployés le 2026-07-18 ~00:01** : RetroBat.Api.exe et MarqueeManager.exe à la racine des
plugins (tous les process étaient arrêtés) ; prochains lancements via RetroBat/hooks ES.
>
> Principe unique appliqué partout : **au plus UN état en attente à chaque étage, le plus récent**
> (slot capacité 1, drop-oldest) + garde de fraîcheur AVANT tout travail coûteux + annulation des
> travaux obsolètes.
>
> Ordre conseillé : **A (MarqueeManager) → A' (LedManager) → B (APIExpose) → C (hooks) → D (nettoyages)**.
> Chaque phase est livrable et testable seule. La phase A seule borne déjà le retard cumulatif.

---

## Phase A — MarqueeManager : ingestion drain-to-latest (gain principal)

Fichier pivot : `src\RetroBatMarqueeManager\Application\Services\WebSocketListenerService.cs`

### A1. Découpler réception et traitement, coalescer les streams d'état

- Dans `ReceiveAsync` (l.114-139), remplacer le `await ProcessAsync(...)` inline (l.132) par un dépôt
  non bloquant dans une boîte aux lettres par stream, + un worker par stream qui consomme.
- Implémentation : `Channel.CreateBounded<JsonDocument>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest })`
  — même motif que la queue panel de LedManager ([LedManager\Program.cs:1298-1304]), déjà éprouvé.
- **Classification des streams** :
  - **Coalescés (état, cap-1 drop-oldest)** : `marquee`, `topper`, `instruction-card`, `panel`,
    `frontend` — un snapshot remplace le précédent, seul le dernier compte.
  - **FIFO conservé (événementiels, bounded large + drop-oldest de sécurité)** : `retroachievements`,
    `hiscore`, `score`, `timer`, `ingame`, `arcade` — ce sont des notifications ponctuelles, pas des
    états ; les perdre changerait le comportement (toasts, effets).
- La boucle de réception ne fait plus que : lire le socket → parse → `TryWrite` → relire. Elle ne peut
  plus prendre de retard ; le backlog TCP disparaît.
- Attention à la durée de vie des `JsonDocument` (IDisposable) : transférer la propriété au worker
  (dispose après traitement, et dispose de l'élément éjecté par DropOldest — utiliser le callback
  d'éjection ou cloner en `JsonElement.Clone()` si plus simple).

### A2. Sortir le décode DMD du thread de traitement + no-op sans matériel

- `HandleMarqueeAsync` l.240 : `await _dmd.SetBaseMediaAsync(...)` exécute
  `DmdFrameRenderer.RenderImage` (`Image.FromFile` GDI+) en ligne. Le déplacer derrière son propre
  slot cap-1 + worker dédié dans `DmdService`.
- Court-circuit total : si le wrapper DMD n'a trouvé aucun renderer (cas actuel, DmdDevice.log
  « No renderers found »), exposer un flag `IsAvailable` sur `DmdService` et **ne rien décoder du
  tout** (ni base media, ni miroir).

### A3. Annulation par sélection

- Un `CancellationTokenSource` par sélection courante dans `WebSocketListenerService` (renouvelé à
  chaque snapshot d'état accepté) ; passer son token à la résolution de chaîne, au lighting
  (`StartGeneration`) et au DMD. Un travail obsolète s'arrête au premier point de contrôle au lieu
  d'aller au bout.

### A4. Caches (rendre le coût unitaire négligeable)

- `CompositionChainResolver` : mémoïser le résultat de résolution par clé `(system, rom, catégorie)`
  avec invalidation sur mtime d'`assignments.json` (le cache mtime existe déjà pour ce fichier,
  l.141-189 — l'étendre au résultat final des `FirstExisting`).
- `MarqueeLightingRenderer` : cache LRU (ex. 32 entrées) des cartes générées par chemin d'artwork —
  revisiter un jeu ne régénère plus 12-118 ms de CPU.

### Critère de fin de phase A

Scroll rapide de 50+ jeux puis arrêt : le marquee affiche le jeu final < 500 ms après le relâchement,
log sans aucun « Displaying image » intermédiaire post-relâchement.

---

## Phase A' — LedManager : même traitement à l'ingestion

Fichier pivot : `src\LedManager\Program.cs`

### A'1. Slot cap-1 pour les panel.state

- Dans `ListenWebSocketAsync` (l.254-273), ne plus faire `await HandleJsonAsync` inline (l.271) pour
  le stream `panel` : déposer le JSON brut dans un `Channel` cap-1 drop-oldest + worker. Les autres
  streams (`mem`/`ingame`, `frontend` game-start/end, `arcade`, `hiscore`) restent FIFO (événements).
- Symétrie parfaite avec la queue sender déjà en place (l.1298-1304) : l'invariant « au plus un panel
  en attente » devient vrai de bout en bout.

### A'2. Garde de fraîcheur AVANT le coût

- Extraire `Sequence` (et system/rom) par une lecture ciblée du JSON (chemins connus, pas de balayage
  récursif) **avant** `PanelState.FromPanelEvent().EnrichFromDynpanel().ApplyUserOverrides()`
  (l.318-320). Si périmé → drop immédiat, zéro I/O.
- Supprimer le `root.Clone()` systématique de l'arbre complet (`LedEvent.cs:105`) pour le chemin
  panel : ne cloner que ce qui survit au traitement.

### A'3. Cache dynpanel/overrides

- Mémoïser `LoadDynpanelOutputs` (`PanelState.cs:250-286`) et `ApplyUserOverrides`
  (`PanelOverrides.cs:21-53`) par clé `(system, rom)` avec invalidation mtime — en navigation on
  survole souvent les mêmes jeux, l'I/O disque par survol tombe à ~0.

### Critère de fin de phase A'

Même test de rafale : le panel affiche la config du jeu final < 500 ms après l'arrêt ; log `[panel]`
sans traitement d'intermédiaires post-relâchement.

---

## Phase B — APIExpose : publier immédiatement l'état courant

Fichier pivot : `src\RetroBat.Providers.EmulationStation\EmulationStationWatcherProvider.cs`

### B1. Publication immédiate, enrichissement asynchrone

- Dans `ProcessEventAsync` (game-selected) : dès le contexte de base posé (l.362-368), publier
  **immédiatement** `ui.game.selected.raw` + `ui.game.selected` avec les infos mémoire
  (system/path/name — c'est tout ce dont la projection marquee et le panel ont besoin pour afficher).
- Déplacer `FetchSystemDetailsAsync`/`FetchGameDetailsAsync` (l.387, 394) **après** la publication,
  en tâche d'enrichissement ; à la fin, ré-émettre l'événement enrichi (soit re-publier
  `ui.game.selected.raw` avec Details, soit un type dédié `ui.game.selected.updated`). Le coalescing
  latest-wins du `WebSocketConnectionManager` et les slots cap-1 de la phase A absorbent la
  ré-émission sans coût.
- Garder les gardes `IsLatestFrontendEvent` autour de l'enrichissement : un enrichissement périmé est
  simplement abandonné.
- Vérifier que `PhysicalMediaWebSocketProjectionService` (abonné à `…raw`, l.77-82) fonctionne avec un
  contexte non enrichi — il résout les médias par system/slug locaux, donc oui a priori ; ajuster si
  un champ Details y est lu.

### B2. Ne plus avaler de sélections

- Fenêtre de suppression live-addgames (l.375-385) : le `return` doit devenir « publier quand même
  l'événement UI, ne supprimer QUE le scrape/projection lourde ». Le marquee/panel ne doit jamais
  rater une sélection.

### B3. Dompter le chemin chaud vers l'API ES

- Pour les fetches d'enrichissement déclenchés par la navigation : timeout court (500 ms), **1 seule
  tentative** (pas les 3 retries de `GetEsApiWithRetryAsync`), abandon immédiat si
  `!IsLatestFrontendEvent`. Les 3 retries × 2 s restent acceptables pour les chemins froids
  (démarrage, game-start).

### B4. Sortir reloadgames/addgames du chemin de navigation

- Différer `reloadgames`/`addgames` tant que des `game-selected` arrivent (fenêtre de calme, ex.
  ≥ 2 s sans sélection) — ils n'ont « pas le temps de se faire » pendant les rafales de toute façon,
  autant les programmer explicitement après la rafale au lieu de les laisser dégrader l'API ES pendant.

### Critère de fin de phase B

Timestamp `events.ini` → émission WS `marquee.snapshot` < 100 ms même pendant un reload ES ; aucune
sélection non publiée dans les logs.

---

## Phase C — Hooks ES / events.ini

Dossier : `.installer\scripts\` (+ scripts déployés dans `emulationstation\.emulationstation\scripts\`)

- **C1.** Rendre atomiques `game-selected\APIExpose.bat`, `system-selected\APIExpose.bat`,
  `game-end\APIExpose.bat` : écrire dans `events.ini.tmp` puis `move /y events.ini.tmp events.ini`
  — modèle déjà utilisé par `game-start\APIExpose-wait.bat`. Élimine les lectures partielles et les
  sélections abandonnées par la garde de complétude du watcher (batch pur, conforme aux contraintes
  antivirus du projet).
- **C2.** Ne PAS toucher aux suffixes `-wait`/`-nowait` : les hooks `*-selected` sont déjà asynchrones
  (Scripting.cpp l.84+120), ES n'est jamais bloqué. Documenter la sémantique dans le README des hooks
  pour éviter les fausses pistes futures.
- **C3.** (Optionnel) Simplifier la garde de complétude du watcher une fois C1 déployé (les 4
  tentatives × 5 ms deviennent superflues) — à ne faire qu'après validation terrain de C1.

---

## Phase D — Nettoyages secondaires (opportunistes)

- **D1.** `GamelistStore`/`GamelistsController` : cache du `XDocument` par `(path, mtime, taille)` —
  même motif que `LoadCachedGamelistDocument` du watcher (l.1486-1517).
- **D2.** Amplification ×2 : après B1, mesurer si le couple brut/enrichi reste utile par sélection ;
  si le snapshot enrichi n'apporte rien au marquee, n'émettre qu'un seul `marquee.snapshot` par état.
- **D3.** Projection médias : mémoïser les `FindAssets` par `(system, slug)` avec invalidation mtime
  des racines — facultatif, le debounce 35 ms suffit peut-être après A+B.
- **D4.** Pico : ne rien changer aux garde-fous série (réinit 9 s, reconnexion) — pics rares et
  matériels ; consigner simplement dans la doc qu'une réinit pendant la navigation gèle P1 ~9 s.

---

## Vérification globale (après chaque phase, puis en fin de chantier)

1. **Instrumentation** : ajouter un log horodaté commun aux 3 process : côté APIExpose au moment du
   FSW event et de l'émission WS ; côté MarqueeManager à l'affichage effectif ; côté LedManager au
   write stdin vers PicoCommandSender. Corréler par (system, rom).
2. **Test de rafale** : navigation rapide maintenue (50+ jeux, y compris MAME), puis arrêt net.
   Mesurer : délai arrêt → marquee final, délai arrêt → panel final, nombre d'états intermédiaires
   traités après l'arrêt (doit être 0 côté affichage, ≤ 1 côté traitement).
3. **Test navigation systèmes** : carrousel rapide entre 10+ systèmes, mêmes mesures.
4. **Test pendant reload** : provoquer un scrape/addgames puis naviguer — la publication doit rester
   < 100 ms (phase B), aucune sélection avalée.
5. **Non-régression** : latence nominale sélection unique (~100-400 ms) inchangée ; toasts
   RetroAchievements/hiscore toujours tous délivrés (streams FIFO préservés) ; game-start/game-end
   (chemins `-wait`) inchangés ; effets MEM en jeu inchangés.

## Récapitulatif effort / impact

| Phase | Impact latence | Effort | Risque |
|---|---|---|---|
| A (MarqueeManager) | Énorme — borne le backlog à 1 message | Moyen | Faible (motif éprouvé dans LedManager aval) |
| A' (LedManager) | Fort | Faible-moyen | Faible |
| B (APIExpose) | Fort — supprime les 13-20 s de pire cas | Moyen | Moyen (ordre des événements, abonnés à vérifier) |
| C (hooks) | Modéré — fiabilité (plus de sélections perdues) | Trivial | Faible |
| D | Confort/robustesse | Faible | Faible |
