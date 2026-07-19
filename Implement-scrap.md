# Plan d'implémentation — workflow de scraping fiable et honnête

> Fait suite à [audit-scrap.md](audit-scrap.md). Contraintes gelées : le **contenu** des payloads
> `/addgames` ne change jamais (contrat §5 de l'audit — un payload anormal peut crasher ES) ; aucun
> appel `/reloadgames` automatique (verdict UX §3.3). Leviers autorisés : **quand** pousser, **si**
> le push se justifie, **quoi** communiquer.
>
> Références externes : PR upstream soumis le 2026-07-18 —
> [RetroBat-Official/emulationstation#429](https://github.com/RetroBat-Official/emulationstation/pull/429)
> et [batocera-linux/batocera-emulationstation#2178](https://github.com/batocera-linux/batocera-emulationstation/pull/2178)
> (fix `if (fromFile && ...)` dans `loadGamelistFile`). Tant qu'ils ne sont pas mergés ET livrés dans
> les builds ES distribués, les phases S1/S2 restent le filet pour les utilisateurs.
>
> Ordre conseillé : **S1 → S2 → S3 → S4 → S5**. Chaque phase est livrable, testable et rollbackable
> seule (flag runtime + commit dédié, comme pour Implement.md).

---

## Phase S1 — Détection de capacité /addgames + avertissement utilisateur

**Objectif** : sur un ES récent (bug de la garde, audit §8), le refresh live est un no-op silencieux
(204 systématique). L'utilisateur doit être prévenu **au premier auto-scrap** que le rafraîchissement
live des fiches n'est pas supporté par sa version d'ES — le canal `/notify` (toast) fonctionne, lui,
sur toutes les versions.

### S1.a — Détection

- État : `AddGamesSupported` (nullable : inconnu / vrai / faux) dans `MediaRuntimeState`, durée de vie
  session.
- **Signal principal (aucun trafic ajouté)** : dans `PushLiveGamelistFragmentToEsAsync`
  ([GamelistUpdateService.cs:2668], branche 204) — un 204 sur un push qui a passé toutes les gardes
  APIExpose (delta visible réel, jeu existant côté ES, gameid résolu) est quasi impossible sur un ES
  sain : APIExpose ne pousse que des jeux présents dans la fileMap d'ES. Compter : **2 pushes
  qualifiés consécutifs → 204** ⇒ `AddGamesSupported = false` (le seuil de 2 élimine le faux positif
  résiduel).
- **Signal de confirmation (optionnel, gratuit)** : `GET /caps` au démarrage (HttpApi::getCaps expose
  la version ES) — table de correspondance version ↔ garde présente, pour poser
  `AddGamesSupported=false` dès le boot sans attendre 2 échecs. La table est à construire quand le
  commit fautif upstream sera daté ; en attendant, le signal 204×2 suffit.
- Journal : entrée `refresh-tracking` `addgames status=unsupported-es-version` au moment du basculement.

### S1.b — Avertissement (une fois par session)

- À la bascule `AddGamesSupported=false` : un toast via le canal notification existant
  (`EmulationStationNotificationService.NotifyAsync`), clés à ajouter dans
  `resources\locales\interface-texts.json` (FR/EN), texte validé :
  - FR : « Mise à jour des médias scrapés non supportée par cette version d'EmulationStation. »
  - EN : « Scraped media update is not supported by this EmulationStation version. »
- Une seule émission par session (flag mémoire) ; pas de persistance disque (le rappel une fois par
  session est utile tant que la version d'ES n'a pas changé, et disparaît de lui-même après mise à
  jour d'ES).
- Le scraping lui-même CONTINUE (voir S2) : le message dit ce qui se passe, il n'annonce pas une
  désactivation.

### Rollback S1
`ApiExpose:Scraping:DetectAddGamesSupport=false` (appsettings) → comportement actuel (aucune
détection, aucun toast). Verdict par défaut `AddGamesSupported=true` si détection désactivée.

---

## Phase S2 — Mode dégradé quand /addgames n'est pas supporté

**Objectif** : que l'auto-scrap garde tout son sens sur ES récent : médias immédiatement disponibles
pour marquee/panel (media store APIExpose, indépendant d'ES), fiche ES complète au prochain démarrage.

- **S2.a** — Quand `AddGamesSupported=false` : court-circuiter `PushLiveGameUpdateToEsAsync` en amont
  (avant construction du payload/dirtyBatch) avec un log `skipped-unsupported-es` — zéro POST inutile,
  zéro délai min-interval, zéro notification « Fiche mise à jour ». Le pipeline scrape (téléchargements,
  imports, staging étendu) reste inchangé.
- **S2.b** — **Fusion du pending-extended à l'arrêt d'ES** (stratégie R4a de l'audit, valable pour
  TOUTES les versions — c'est le seul point de fusion sûr vis-à-vis du lost-update §6.2 : ES ne peut
  plus réécrire gamelist.xml quand il est éteint) :
  - déclencheur : la détection de fin de session ES existe déjà (`EmulationStationLifecycle`,
    poll 1 s) ; à l'arrêt détecté → `ApplyPendingExtendedGamelistsAsync` (existant,
    [GamelistUpdateService.cs:628-665]) pour tous les systèmes ayant un pending ;
  - garde : ne pas fusionner si APIExpose s'arrête lui-même en même temps (ordre d'arrêt des hosted
    services — faire la fusion dans le handler d'arrêt AVANT le dispose des services gamelist) ;
  - supprimer la purge du pending au switch de langue sans fusion préalable
    (`DiscardPendingExtendedGamelistsAsync` [:667-704] → fusionner d'abord, purger ensuite si besoin).
- **S2.c** — Backoff de la boucle reloadgames pending (trou n°3) : 500 ms → 5 s quand la seule raison
  est `last-event-game-selected` ([ReloadGamesHostedService.cs:66-275]) ; le reloadgames automatique
  reste de fait inerte (souhaité), mais sans réveil CPU ni logs en boucle.
- **S2.d — F5 ciblé (opt-in)** — Le F5 clavier ([ViewController.cpp:869-883]) est immunisé contre le
  bug addgames (chemin input, pas de loadGamelistFile) et fait exactement ce que /addgames ne peut
  pas : `ResourceManager::unloadAll/reloadAll` (toutes les textures rechargées depuis le disque —
  résout le trou du cache §3.2) + `ViewController::reloadAll` avec **curseur préservé** (cursorMap
  sauvegardé/restauré, l.1148-1153). Il ne relit PAS gamelist.xml (métadonnées/chemins mémoire
  inchangés). L'infrastructure d'envoi existe déjà (startup-f5, backend clavier VK 0x74).
  Usage proposé, valable ES sain ET cassé :
  - déclencheur : au calme réel de navigation, au plus 1×/N minutes, ET seulement si ≥1 média
    visible d'un jeu **déjà référencé dans la gamelist** a été remplacé à chemin constant pendant la
    session (le seul cas où F5 change des pixels) ;
  - coût UX assumé : bref splash « Loading gamelists » (rebuild des vues chargées, amplifié par
    `PreloadUI`) — c'est pour ça que c'est opt-in (`ApiExpose:Scraping:TargetedF5Refresh=false` par
    défaut) et jamais par sélection ;
  - limite sur ES cassé : un jeu jamais scrapé (aucun chemin en gamelist au boot) reste invisible
    jusqu'au prochain démarrage (S2.b) — F5 ne transporte pas de données, seulement des pixels.
    Attention : les noms de fichiers du store sont suffixés par région (`front-us.png`,
    `screentitle-wor.png`, vérifié sur payload réel) — **une variante d'une autre région = un
    NOUVEAU chemin**, donc cas « jamais scrapé » et non « remplacement ». F5 ne couvre que le
    re-scrape à région identique. Pas de rattrapage par le fallback local-art d'ES (il scanne les
    emplacements conventionnels à côté des roms, pas le store APIExpose).
    Règle mémo : **F5 = rafraîchit les re-scrapes ; fusion à l'arrêt (S2.b) = livre les premiers
    scrapes et les nouvelles variantes.**

### Rollback S2
`ApiExpose:Scraping:MergePendingOnEsExit=false` ; le skip S2.a suit le flag S1 ;
`TargetedF5Refresh=false` (défaut) pour S2.d.

---

## Phase S3 — Notification honnête + push justifié (R1 + R2)

**Objectif** : faire disparaître le symptôme « Fiche mise à jour sans changement » sur TOUTES les
versions (y compris l'ES ancien de cette machine, où /addgames fonctionne).

- **S3.a (R2 — le push se justifie-t-il ?)** — Dans `PushLiveGamelistFragmentToEsAsync`, avant le
  sémaphore : calculer `visibleRenderDelta` = au moins un des 4 tags visibles du fragment
  (image/thumbnail/marquee/fanart) dont le **chemin** diffère de la valeur actuelle du nœud gamelist,
  OU métadonnée affichée réellement modifiée (passe `allowLocalizedMetadataRefresh`). Si
  `visibleRenderDelta=false` → skip (`skipped-no-render-delta`) : ES reconstruirait la vue pour rien
  (cache textures par chemin, audit §3.2 — un même chemin ne recharge jamais l'image). Quand on
  pousse, le payload reste octet pour octet celui d'aujourd'hui.
  - Effet de bord bénéfique : moins de POST → moins de rebuilds de vue ES → moins de re-fires
    fantômes (phase E2 de Implement.md) et fenêtre de course §D réduite.
- **S3.b (R1 — quoi communiquer)** — Principe UX validé : **montrer qu'il se passe quelque chose
  prime** — on ne vise pas le silence, on vise des messages véridiques :
  - les notifications d'ACTIVITÉ (« Scraping … » démarré/terminé) restent le canal « il se passe
    quelque chose » — conservées, y compris quand la fiche ne changera pas visuellement ;
  - « Fiche mise à jour » est réservée aux pushes avec `visibleRenderDelta=true` ET label détaillé
    non vide (`ResolveRemoteScrapeLiveUpdateMessage` [:1872]) — suppression du fallback générique
    [:1401-1404] qui annonçait une mise à jour sans rien à annoncer ;
  - cas particulier scrapes exact-local sans push ([RemoteScrapingService.cs:503-510]) : garder la
    notif d'activité mais avec un libellé d'activité (« Scraping terminé »), jamais un libellé de
    mise à jour de fiche.

### Rollback S3
`ApiExpose:Scraping:RequireRenderDeltaForLivePush=false` et
`ApiExpose:Scraping:HonestNotifications=false` (deux flags séparés — S3.a et S3.b indépendants).

---

## Phase S4 — one-per-card par cycle + fermeture de la course (R3 + trou n°1)

- **S4.a** — Cadrage validé avec l'utilisateur — l'intention du design actuel est la bonne et reste :
  **UNE addgames = la fiche complète** (le payload embarque TOUJOURS tout : médias visibles + texte,
  `BuildLiveMetadataPayload` inclus quel que soit le déclencheur — vérifié [:3764-3788]) ; **la vidéo,
  plus lourde, a droit à une SECONDE addgames dédiée** (exception vidéo existante,
  [MediaRuntimeState.cs:798-813]). Les « 3 passes » ([RemoteScrapeQueueService.cs:299-331]) ne sont
  pas 3 envois : ce sont 3 déclencheurs possibles du MÊME envoi complet (vidéo fraîche / delta média
  visible / delta texte), le premier qui aboutit ferme la carte.
  **Pourquoi le texte localisé est en retard (structurel, pas un bug)** : quand ScreenScraper ne
  fournit pas la description dans la langue de l'utilisateur, elle est produite LOCALEMENT par
  `DescriptionTranslationService` (BackgroundService pilotant l'outil **TranslateLocally** — modèle de
  traduction local, avec téléchargement/installation du modèle à la première utilisation,
  [DescriptionTranslationService.cs:104-296]). Elle ne peut donc pas être prête au moment du push
  principal — elle arrive secondes ou minutes plus tard.
  **Règles validées avec l'utilisateur — MAX 2 pushes par carte :**
  1. **Push principal** = la fiche complète (médias visibles + texte disponible à cet instant) ;
  2. **Push vidéo** = l'exception existante (média lourd, arrive après) — et RIEN d'autre.
  3. **Texte tardif (traduction) : JAMAIS de push dédié** → mise à jour silencieuse de la donnée via
     `MarkLiveGamelistDirty` — **mécanisme déjà câblé** ([DescriptionTranslationService.cs:247]) — et
     portée par la **mutualisation des addgames** : tout push addgames embarque le `dirtyBatch` des
     autres jeux modifiés du même système ([CollectDirtyLiveGamelistBatch:3285], prouvé par les
     payloads `gameNodeCount=2` observés). Le texte traduit apparaît donc au prochain addgames du
     système (fréquent en navigation avec scrape actif), sinon à la fusion d'arrêt (S2.b). Aucun toast.
  4. **La passe texte dédiée** ([RemoteScrapeQueueService.cs:325]) est restreinte au seul cas « la
     fiche n'avait AUCUN texte localisé » (première description = vrai gain visible) ; tous les autres
     deltas texte (mise à jour d'un texte existant, champs non affichés) deviennent silencieux-dirty.
     Élimine les faux positifs « texte non visible → refresh ».
  Bilan garanti : 1 push nominal, 2 max (vidéo), zéro push pour le texte tardif. Ordre et format des
  payloads inchangés.
- **S4.b** — Armement de la porte déplacé **avant** `LiveEsAddGamesGate.Release()` (aujourd'hui
  [:2724] après [:2663]) : la fenêtre de course du double-push (bowlrama ×2 observé) disparaît.
  Changement d'ordonnancement pur, aucun payload modifié.

### Rollback S4
`git revert` du commit (pas de flag : c'est un correctif de synchro, le comportement historique est
le bug).

---

## Phase S5 — Nettoyages (R5 + R6)

- **S5.a** — TTL sur `exact-local-no-retry` (`.log\remote-exact-local-noretry-cache.json`,
  [RemoteScrapingService.cs:1516-1546]) : `ExpiresAtUtc` comme le cooldown texte, défaut 14 jours,
  clé de config `RemoteExactLocalNoRetryTtlDays`.
- **S5.b** — Contrat gelé documenté : bloc de commentaires en tête de `GamelistUpdateService.cs`
  (invariants payload §5 de l'audit + interdiction reloadgames + pointeur audit-scrap.md), et page
  wiki interne si souhaité (attention règles de contenu public : généraliser, pas de doctrine interne).

---

## Vérification

1. **S1/S2 (ES récent)** — banc : pas d'ES récent sur cette machine ; simuler le 204 en pointant
   `_esHttpClient` vers un stub local qui répond 204 (variable d'env ou appsettings de test), OU
   temporairement patcher la table /caps. Critères : toast unique en session au premier scrape,
   `skipped-unsupported-es` ensuite, zéro POST addgames, et à l'arrêt d'ES → pending fusionnés
   (comparer gamelist.xml avant/après arrêt).
2. **S2.b (toutes versions)** — scraper un jeu, vérifier `{system}.xml` pending, fermer ES, vérifier
   la fusion dans `roms\{system}\gamelist.xml` (métadonnées + médias non-visibles présents), relancer
   ES et vérifier la fiche complète.
3. **S3 (ES ancien de cette machine)** — re-scraper un jeu déjà à jour : AUCUN toast, AUCUN push
   (`skipped-no-render-delta`) ; scraper un jeu avec nouveau média visible : toast avec label précis,
   fiche réellement changée. Compter les toasts sur une session de navigation longue : ils doivent
   devenir rares et tous véridiques.
4. **S4** — scraper un jeu avec vidéo + médias + texte : vérifier 3 addgames du même cycle dans
   refresh-tracking (et non 1 + skipped), et plus jamais deux `addgames success` du même push-type à
   quelques secondes ; le test de charge : deux scrapes concurrents du même jeu → un seul cycle pousse.
5. **Non-régression** — latence navigation (Implement.md phases A→E2) inchangée : rejouer le test de
   rafale synthétique ; le gate/fantôme de la phase E2 ne doit pas être perturbé par la baisse du
   nombre de pushes (moins de re-fires = mieux).

## État (implémenté et déployé le 2026-07-18 ~12:45)

| Phase | Contenu | État | Commit | Rollback sans rebuild |
|---|---|---|---|---|
| S1 | Détection 204×2 + toast unique/session (`notification.live.addgames_unsupported`, EN/FR + fallback EN) | **FAIT** | `cb0f5a40` | `Scraping:DetectAddGamesSupport=false` |
| S2 | Skip pushes si non supporté (`skipped-unsupported-es`) + fusion pending à l'arrêt d'ES (`es-exit`) + backoff boucle pending 500 ms→5 s | **FAIT** (S2.d F5 ciblé : différé, opt-in documenté) | `cb0f5a40` | `Scraping:MergePendingOnEsExit=false` |
| S3 | Push seulement si delta de rendu (`skipped-no-render-delta`, données re-dirty pour le prochain push mutualisé) + toast « Fiche mise à jour » sans fallback menteur | **FAIT** | `08f1a13d` | `Scraping:RequireRenderDeltaForLivePush=false`, `Scraping:HonestNotifications=false` |
| S4 | Armement one-per-card SOUS le sémaphore POST (fin du double-addgames) ; 2-pushes-max/texte silencieux couverts par S3 | **FAIT** | `71ac743d` | `git revert 71ac743d` (correctif de course) |
| S5 | TTL 14 j sur exact-local-no-retry + contrat payload gelé en tête de GamelistUpdateService | **FAIT** | `94009d19` | `Scraping:RemoteExactLocalNoRetryTtlDays=0` |
| S6 — Bug « Super Mario World » : registre + réclamation des hidden orphelins + balises portées par le fragment | **FAIT** (2026-07-18 ~18:00, déployé, **vérifié sur snes** : SMW USA re-visible, Europe cachées avec raison, registre 36 entrées) | `e99351dc` | F3 : `Scraping:IncludeRomsetTagsInLivePayload=false` (rollback dédié si instabilité addgames) ; F1/F2 : `git revert` | — |
| S7 — Bug systémique « OFF mort » des switches du menu ES | **FAIT** (2026-07-18 ~18:30, déployé — redémarrage ES requis) | hors git (resources/ ignoré) — backup `apiexpose_es_features_blocks_to_add.cfg.pre-switchon.bak` | restaurer le .bak + redémarrer API/ES | — |
| PR upstream | #429 (RetroBat) / #2178 (batocera) soumis 2026-07-18 | en attente de merge | — | — |

**S7 — le bug systémique** : un `preset="switch"` d'ES sauvegarde OFF comme **chaîne vide**
([GuiMenu.cpp:2372]), jamais `"0"` ; or le sync appsettings ignore `""` (TryParseBool échoue,
[ApiExposeAppsettingsSyncService.cs:286-289]) et les lectures directes retombent sur le défaut
appsettings ([RomSetManagerService.ResolveBool:3273]). **Conséquence : sur les 77 switches du menu,
les 47 à défaut appsettings=true avaient un OFF sans aucun effet** (fonction active, menu affichant
OFF) — dont ENABLE LIVE CONTEST (cas déclencheur : consommé par le `LiveContestClientService`
d'APIExpose, passerelle RetroCreator, qui ne refuse que sur `value="0"`), websocket, tous les
managers. Correctif : `preset="switchon"` pour les 47 défaut-vrai (OFF → écrit `"0"` explicite ;
ON → `""` → fallback vrai ; valeur absente → affiché ON — sémantique exacte, [GuiMenu.cpp:2392]) ;
les 30 défaut-faux restent en `switch` (sémantique déjà correcte). Vérifié : es_features.cfg
régénéré avec les 47 presets. Note : `live_contest` n'était PAS une anomalie de câblage (lecture
directe volontaire, hors Mappings/seeder à juste titre) — le P1 du plan est amendé.

**S6 — le bug élucidé** : l'ingestion d'un fragment addgames par ES fait `mUnKnownElements.clear()`
(`loadFromXML`, [MetaData.cpp:155]) → les balises de propriété `apiexpose_romset_*` du jeu poussé
sont effacées de la mémoire d'ES tandis que `<hidden>` (champ connu) survit ; la réécriture
`updateGamelist` (tous les jeux dirty, [HttpServerThread.cpp:665-670]) produit un **hidden orphelin**
que le Roms Manager, par prudence, refusait de toucher — même élu gagnant de son groupe de variantes
(24 orphelins mesurés). Correctifs : **F1** registre de propriété hors gamelist
(`media/aliases/shared/romset-hidden-ledger.json`, synchronisé à chaque hide/restore) ; **F2**
réclamation au run d'un hidden sans tags prouvable nôtre (registre, OU gagnant élu par les raisons
`variant-selected` du run courant, OU visé par une décision hide courante) puis passage par le flux
normal ; **F3** le fragment addgames recopie les balises existantes (jeu poussé + dirtyBatch +
relatedBatch) pour qu'ES les recharge au lieu de les effacer.

Notes de livraison : `resources\locales\interface-texts.json` est hors git (gitignore `resources`) —
la clé ajoutée vit dans le fichier déployé et suivra le packaging release habituel. Exe redéployé
(single-file, swap + relance, session ES conservée). Vérifications à faire en session réelle :
scraper un jeu déjà à jour (attendu : `skipped-no-render-delta`, aucun toast) ; un jeu avec nouveau
média visible (toast précis + fiche changée) ; fermer ES (attendu : log « Pending extended gamelists
merged after EmulationStation exit ») ; sur un ES récent cassé : toast unique « Mise a jour des
medias scrapes non supportee… » puis plus aucun POST addgames de la session.

**S8 — Chantier cohérence menus ES (audit-esmenu, 2026-07-18)** : livré et déployé en **1.3.2**
(commits `50424b34` code + `71d5434c` version/wiki). P1 câblage : `romset.unknown_roms_mode`
ajouté au sync (String → RomSetManager.UnknownRomsMode) et au seeder ;
`scraping.description_translation.enabled` semé. P2 défauts : ScrapingOptions et
RomSetManagerOptions alignés sur appsettings.json (source livrée). P3 purge : les 8 clés legacy
mortes (only_retroachievements, show_clones/prototypes/bootlegs_hacks/adult/casino/mahjong/
non_games) retirées du sync, du seeder, du catalogue runtime et du .cfg — les 3 vivantes
(show_non_arcade/horizontal/vertical) intactes ; les propriétés C# restent (recalculées en interne
par le snapshot). Menu : websocket.enabled retiré (feature + sharedFeature), description SCRAPE
VIDEOS corrigée (.cfg + 12 locales .po), exemple es_settings commenté aligné, doctrine documentée
(en-tête du .cfg + README + wiki menus FR/EN). Hors git : backup
`apiexpose_es_features_blocks_to_add.cfg.pre-cleanup.bak`. Rollback : `git revert 50424b34` +
restauration du .bak. **Redémarrage ES requis** pour recharger es_features.cfg.
