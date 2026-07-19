# Audit du workflow de scraping — « Fiche mise à jour » sans changement visible

> Audit du 2026-07-18. Analyse statique des sources APIExpose + EmulationStation
> (`projects-source\batocera-emulationstation-master`), croisée avec les logs runtime de la nuit
> (`.log\media-update-audit.jsonl`, `.log\refresh-tracking.jsonl`). Aucun code modifié.
> Complète [audit.md](audit.md) (latence) et [Implement.md](Implement.md) (phases A→E2).
>
> **Contraintes de cadrage (utilisateur)** : le contenu du payload `/addgames` est **intouchable**
> (il fonctionne ; mal paramétré il crashe ES — démontré §D) ; `/reloadgames` est **disqualifié**
> comme mécanisme de rafraîchissement (démontré §B.3). Les leviers autorisés : **quand** pousser,
> **si** le push se justifie, **quoi** communiquer à l'utilisateur.

## Verdict en une phrase

La notification « Fiche mise à jour » est déclenchée par le **transport** (ES a répondu 2xx au POST
`/addgames` pour le jeu sélectionné) et jamais par le **rendu** ; or de nombreux pushes ne peuvent
rien changer à l'écran (cache de textures ES par chemin sans mtime, métadonnées non visibles,
passe texte avalée par la règle one-per-card, données complètes piégées dans les gamelists étendues
jamais fusionnées) — l'utilisateur voit donc le toast sans voir de changement.

---

## 1. Le workflow complet (chaîne séquentielle)

```
game-selected (watcher, gate de rafale phase E)
 └─ MediaPrefetchService.PrefetchForSelectionAsync            [MediaPrefetchService.cs:103]
     ├─ projection locale (plan → ApplyProjectionAsync)       [:345, :500]
     ├─ staging gamelist (StageExtendedEntriesAsync si live)  [:176]
     ├─ décision remote (EvaluateAfterLocalAsync)             [:240 → RemoteScrapingService.cs:79]
     └─ push live si delta local                              [:296-308]

File de scrape (RemoteScrapeQueueService, workers multiples LIFO)
 └─ ProcessAsync                                              [RemoteScrapeQueueService.cs:~288]
     ├─ ScreenScraper : catalog + médias (énumération régions) [ScreenScraperRemoteProvider.cs:420-644]
     ├─ Push live #1 vidéo   (allowCurrentVideoRefresh)       [:302]  ┐
     ├─ Push live #2 médias  (visibles seulement)             [:314]  ├ PushLiveGameUpdateToEsAsync
     ├─ Push live #3 texte   (allowLocalizedMetadataRefresh)  [:325]  ┘ [GamelistUpdateService.cs:1261]
     └─ StageExtendedEntriesAsync (gamelist étendue pending)  [:337]

PushLiveGamelistFragmentToEsAsync                              [GamelistUpdateService.cs:~2400]
 ├─ dirtyBatch (autres jeux du système) + relatedBatch (clones MAME)   [:2430-2433]
 ├─ filtre delta HasLiveGamelistRefreshDelta                   [:2434 → :3452]
 ├─ filtre signature média (taille+mtime, mémoire process)     [:2468, :3537]
 ├─ sémaphore LiveEsAddGamesGate + re-vérifications            [:2579-2652]
 ├─ POST /addgames/{system} → ES                               [:2655]
 └─ succès : ClearDirty + MarkLiveAddGamesPushedForSelection   [:2717-2724]
     └─ write-behind gamelist DÉLIBÉRÉMENT SAUTÉ               [:2733-2744]
        (raison : « es-addgames-updates-gamelist » — ES écrit lui-même gamelist.xml)

ReloadGamesHostedService (boucle 500 ms)
 └─ TryConsumeReloadGamesReady : bloqué tant que lastFrontendEvent=="game-selected"
    sans bypass sur le chemin scrape                           [MediaRuntimeState.cs:534-591, garde :573]
    → ApplyPendingExtendedGamelistsAsync JAMAIS appelé tant qu'on navigue
```

Comptage observé (logs de la nuit) : par jeu scrapé, ~20 lignes `download-miss` (énumération
région×kind normale — `ExpandLocalizedMediaTypes`, [ScreenScraperRemoteProvider.cs:1364] : chaque
kind visible est tenté dans 4-6 variantes régionales avant de trouver un candidat), 1-3 pushes live,
1 staging étendu, et une boucle `reloadgames pending` toutes les 500 ms qui ne converge jamais.

---

## 2. A — Pourquoi la notification ment

Deux canaux, tous deux `POST 127.0.0.1:1234/notify` (toast `GuiInfoPopup` ~10 s côté ES,
[Window.cpp:294-330]) :
- `EmulationStationNotificationService.NotifyAsync` — notifications de scrape ;
- `GamelistUpdateService.NotifyEsAsync` [:1579] — « Fiche mise à jour ».

**Conditions d'émission de « Fiche mise à jour »** ([PushLiveGameUpdateToEsAsync:1389-1410]) :
1. `LiveEsMediaPushEnabled` ;
2. le POST `/addgames` a répondu **2xx** (pas 204) ;
3. `ShouldNotifyLiveAddGamesUpdate` [:1428] : le path == sélection courante. **C'est tout.**

Aucun contrôle qu'un pixel va changer. Pire : le texte détaillé (`ResolveRemoteScrapeLiveUpdateMessage`
[:1872]) diffe le payload contre le nœud gamelist ; **si aucun label de changement visible n'est
trouvé, le fallback générique « Mise à jour de la fiche » est envoyé quand même** [:1401-1404]
(`interface-texts.json:295-306`). Autrement dit : quand APIExpose lui-même constate qu'il n'a rien de
visible à annoncer, il annonce quand même.

S'y ajoutent les notifications « Scraping terminé » émises pour des scrapes **exact-local** qui ne
poussent jamais d'addgames ([RemoteScrapingService.cs:503-510], `RefreshCurrentGameAfterSuccess`
forcé false [:338]) : variante régionale importée, pas forcément celle affichée.

---

## 3. B — Pourquoi rien ne change à l'écran (côté ES, sources vérifiées)

### 3.1 Ce que /addgames fait réellement
[HttpServerThread.cpp:619-690] : parse du fragment ([Gamelist.cpp:109]), mise à jour des métadonnées
EN MÉMOIRE ([Gamelist.cpp:186]), réécriture de `gamelist.xml` ([Gamelist.cpp:352]), puis
`onFileChanged(rootFolder, FILE_METADATA_CHANGED)` sur le thread UI [:684-690] →
`reloadGameListView` ([ViewController.cpp:1039]) : la vue est **reconstruite, curseur préservé**
(c'est aussi l'origine des re-fires `game-selected` traités en phase E2).
→ **Le texte (nom, description, éditeur, note) se met bien à jour.**

### 3.2 Le cache de textures : la cause n°1 côté image
- `TextureResource::get` : cache par **chemin**, **aucun contrôle de mtime** ([TextureResource.cpp:112-159]).
- `ImageComponent::setImage` : court-circuit si le chemin est identique ([ImageComponent.cpp:292]).

Conséquences :
- payload changeant le **chemin** d'un média → nouvelle clé → image mise à jour ✔
- fichier **remplacé sur disque à chemin constant** (le cas nominal du media store APIExpose, chemins
  canoniques stables) → tant qu'une référence vive garde la texture (autre vue, snapshot vidéo
  [DetailedContainer.cpp:830], chargement async), **l'ancienne image reste affichée indéfiniment**.
  La reconstruction de vue peut suffire à faire expirer la texture (destruction avant recréation,
  [ViewController.cpp:1065-1070]) — mais **sans aucune garantie**. ES ne détecte JAMAIS un
  remplacement de fichier à chemin constant.

### 3.3 /reloadgames : disqualifié (verdict UX factuel)
[ViewController.cpp:1335-1378] : splash « Loading… » plein écran, **re-parse de TOUS les
gamelist.xml** de tous les systèmes ([:1373]), toutes les GUI ouvertes fermées [:1348-1362].
Restauré : le système courant et le mode de vue **seulement** [:1342-1343, :1375]. **Perdus :
position du curseur dans la liste, pile de dossiers, saut alphabétique.** L'utilisateur atterrit en
haut de la gamelist après plusieurs secondes d'écran bloqué. → À ne jamais utiliser comme mécanisme
de rafraîchissement pendant la navigation. (Ironiquement, l'exécution auto du reloadgames scrape ne
se produit jamais — voir §4 — ce qui est un moindre mal ; le vrai bug est ailleurs.)

---

## 4. C — Pourquoi la donnée complète n'arrive jamais

1. **La fusion des gamelists étendues est couplée au reloadgames.** Les scrapes déposent la fiche
   COMPLÈTE (toutes métadonnées + tous médias) dans un pending `{system}.xml`
   (`StageExtendedEntriesAsync` [GamelistUpdateService.cs:569-626], 5 chemins d'appel). La fusion dans
   le vrai `gamelist.xml` (`ApplyPendingExtendedGamelistsAsync` [:628-665]) n'est appelée QUE par le
   scheduler reloadgames ([ReloadGamesHostedService.cs:90,145]). Or le reloadgames d'origine scrape
   est bloqué sans fin par la garde `lastFrontendEvent=="game-selected"` sans bypass
   ([MediaRuntimeState.cs:573] ; les bypass n'existent que pour RomSetManager/collections/startup).
   → tant qu'on navigue, **rien n'est jamais fusionné** ; un switch de langue **purge** le pending
   (`DiscardPendingExtendedGamelistsAsync` [:667-704]) sans qu'il ait jamais été montré.
2. **La règle one-per-card avale la passe texte.** 3 pushes séquentiels par scrape (vidéo → médias →
   texte, [RemoteScrapeQueueService.cs:299-331]) ; le push médias arme la porte
   (`MarkLiveAddGamesPushedForSelection` [:2724]) ; le push texte qui suit est
   `skipped-current-selection-already-pushed` ([MediaRuntimeState.cs:774-813]). Les métadonnées
   fraîches ne partent donc ni en live, ni via la fusion (bloquée, point 1).
3. **Angles morts du filtre delta** (`HasLiveGamelistRefreshDelta` [:3452-3492]) : seuls
   image/thumbnail/marquee/fanart peuvent constituer un delta ([LiveVisibleMediaTags:3671]) ; les
   métadonnées ne sont pas comparées sur le chemin médias ; le paramètre `dirtyBatch` n'est jamais
   utilisé par la fonction ; nœud absent de la gamelist → false.
4. **Cache négatif sans expiration** : `exact-local-no-retry`
   (`.log\remote-exact-local-noretry-cache.json`, [RemoteScrapingService.cs:1516-1546]) n'expire
   JAMAIS — un média régional publié plus tard sur ScreenScraper ne sera plus jamais retenté (les
   autres cooldowns : média 12 h en mémoire, texte 720 min avec expiration — corrects).
5. **Slots pilotés par les réglages scraper ES, pas par le thème** : le fragment remplit
   `<image>/<thumbnail>/<marquee>` selon `ImageSource/ThumbSource/LogoSource/WheelStyle`
   (`sstitle→image`, [NormalizeSelectionSourceToKind:7191]) ; si le thème affiche une autre œuvre
   (boxart, mix…), la mise à jour est réelle mais invisible.

**Scénarios concrets « toast sans changement »** (tous observés ou reproductibles) :
métadonnée localisée seule → push texte → toast générique, rien de visible en grille ; fanart mise à
jour mais non rendue par le thème ; média ré-importé visuellement identique (variante régionale) ;
2e addgames « vidéo » sur un thème qui n'affiche pas la vidéo ; fichier remplacé à chemin constant →
texture ES inchangée ; scrape exact-local notifié sans push.

---

## 5. D — Fragilité d'ES : pourquoi le payload est un contrat gelé

Vérifié dans les sources (justifie l'interdiction de toucher au format qui marche) :
- **Aucun verrou côté ES** : le handler `/addgames` (thread HTTP) mute l'arbre `FileData`/
  `MetaDataList` et réécrit `gamelist.xml` PENDANT que le thread UI lit ces mêmes structures pour le
  rendu ([HttpServerThread.cpp:649-672]) — course de données réelle, crashs aléatoires non
  reproductibles. Un payload « anormal » (plus gros, plus lent, ou empruntant `findOrCreateFile` qui
  insère des nœuds dans l'arbre vivant [Gamelist.cpp:76-99]) élargit la fenêtre → crash immédiat
  plausible, conforme à l'expérience utilisateur.
- **Dangling pointer** : la lambda postée capture un `SystemData*` brut ; si un reload s'intercale
  avant son exécution UI, le pointeur est mort ([HttpServerThread.cpp:686-689] vs
  [ViewController.cpp:1364-1373]).
- **`TRYCATCH` re-throw** ([Log.h:10-12]) : toute exception dans une fonction postée au thread UI
  remonte → `std::terminate` → crash dur.

**Invariants que tout payload DOIT respecter** (contrat gelé) : XML valide à racine `<gameList>`
unique ; chaque `<path>` relatif au romdir, existant sur disque, extension déclarée par le système ;
ne cibler que des jeux déjà connus d'ES (mise à jour en place, jamais de création) ; métadonnées aux
clés/types attendus. Le payload actuel d'APIExpose respecte ces invariants — ne pas y toucher.

---

## 6. E — Multi-thread APIExpose : verdict

**Globalement bien géré** : file de scrape à workers multiples avec plafond dynamique
([RemoteScrapeQueueService.cs:166-192], pop LIFO sous lock, requeue/discard propres) ; POST /addgames
sérialisé par `LiveEsAddGamesGate` (SemaphoreSlim 1,1) avec re-vérifications de fraîcheur À
L'INTÉRIEUR du sémaphore ([GamelistUpdateService.cs:2579-2652]) — un seul POST à la fois vers ES,
essentiel vu §D ; écriture `gamelist.xml` atomique et blindée (tmp + validation XML + garde
anti-réécriture suspecte, [SaveGamelistDocument:6670]) ; `MediaRuntimeState` sous lock unique
cohérent ; journaux d'audit et caches localisés derrière des SemaphoreSlim statiques.

**Trois trous précis :**
1. **La course du double-addgames (observée : bowlrama ×2 à 2 s d'intervalle)** — la porte
   one-per-card est armée par `MarkLiveAddGamesPushedForSelection` [:2724] **après**
   `LiveEsAddGamesGate.Release()` [:2663]. Un push concurrent entre dans le sémaphore et re-teste
   `ShouldSuppressLiveAddGamesForSelection` [:2612] avant l'armement → 2e POST identique.
   Fenêtre : entre Release et Mark (quelques ms, élargie par les délais min-interval).
2. **Deux écrivains sur `gamelist.xml` — développé.** Prouvé mais subtil :
   - **ES réécrit `gamelist.xml` à CHAQUE addgames réussi** ([HttpServerThread.cpp:672]) — et le
     handler marque dirty **tous les jeux préexistants du système** (l.665-670), pas seulement ceux
     du payload. `updateGamelist` ([Gamelist.cpp:352-459]) relit le fichier, supprime chaque nœud
     dirty et le **réécrit depuis la mémoire d'ES** (l.421-441), puis sauve en **écriture directe non
     atomique** (`doc.save_file` l.452, pas de tmp+rename).
   - **APIExpose n'écrit PAS `gamelist.xml` sur le chemin live** ([MediaPrefetchService.cs:150-193] :
     suppression active → staging vers le pending `{system}.xml` séparé ; write-behind post-addgames
     sauté, raison `es-addgames-updates-gamelist`). Ses écritures directes (atomiques, verrou par
     chemin `GetGamelistLock`) n'arrivent que hors-live : application du pending, endpoints manuels
     (generate/normalize/update-local/consolidate), Setup, maintenance de démarrage.
   - **Le risque réel n'est donc pas la collision simultanée (rare) mais le LOST-UPDATE
     systématique** : tout ce qu'APIExpose fusionne sur disque APRÈS le boot d'ES (pending-extended
     appliqué, normalize…) et qu'ES n'a pas en mémoire est **écrasé au prochain addgames du même
     système** (ES régénère les nœuds depuis sa mémoire). Indice empirique : gamelist arcade passée
     de 2 655 693 à 2 607 589 octets (~48 Ko perdus) entre deux traces addgames de la même nuit.
   - Fenêtres de collision résiduelles : écriture ES non atomique pendant une lecture APIExpose
     (lecture partielle possible — les gardes snapshot/validation d'APIExpose l'absorbent) ; rename
     atomique d'APIExpose pouvant échouer si ES tient le fichier ; pending appliqué au moment où
     l'utilisateur quitte la gamelist vs addgames retardataire ; scrape Setup pendant le live.
3. **La boucle reloadgames pending** tourne à 500 ms indéfiniment en journalisant
   (`refresh-tracking.jsonl` en atteste sur des minutes) — pas un bug de concurrence, mais du bruit
   et du réveil CPU inutiles.

---

## 7. Recommandations (conformes aux contraintes : jamais le contenu des payloads, jamais reloadgames)

| # | Levier | Recommandation | Effet |
|---|---|---|---|
| R1 | **Quoi communiquer** | N'émettre « Fiche mise à jour » que si le push change un élément réellement rendu : label de changement visible non vide ([:1872]) ET au moins un chemin de média visible du payload ≠ chemin actuel du nœud gamelist. Sinon : silence. Supprimer la notif « Scraping terminé » des scrapes exact-local sans push. | Le toast redevient fiable ; fin du symptôme perçu. |
| R2 | **Le push se justifie-t-il ?** | Sauter l'addgames quand le fragment ne changerait aucun chemin de média visible ni métadonnée affichée : ES reconstruira la vue pour rien (et ne rechargera pas les textures à chemin constant). La *décision* change ; quand on pousse, le payload reste octet pour octet identique. | Moins de POST vers ES (moins de fenêtres de course §D), moins de re-fires fantômes (phase E2), zéro toast mensonger. |
| R3 | **Quand (one-per-card par cycle)** | Armer la porte one-per-card par CYCLE de scrape (vidéo+médias+texte du même jeu) et non par push : la passe texte du même cycle passe, ordre et contenus des 3 payloads inchangés. Et déplacer l'armement SOUS le sémaphore (avant `Release`) pour fermer la course n°1. | Les métadonnées fraîches arrivent en live ; plus de double-push. |
| R4 | **Fusion pending découplée du reloadgames** | Appliquer `ApplyPendingExtendedGamelistsAsync` sur détection de calme réel de navigation, SANS appeler `/reloadgames`. **Attention (cf. trou n°2)** : tant qu'ES tourne, un addgames ultérieur du même système réécrira les nœuds depuis SA mémoire et écrasera la fusion. Deux stratégies sûres : (a) fusionner à l'**arrêt d'ES** (le watcher détecte déjà la fin de session) — les données sont lues au prochain démarrage ; (b) fusionner au calme PUIS considérer les nœuds fusionnés comme dirty côté APIExpose pour que le prochain cycle addgames de ce jeu réaligne la mémoire d'ES (payload inchangé, seul le moment change). Ne plus purger le pending sur switch de langue sans l'avoir fusionné. Backoff sur la boucle pending (500 ms → 5 s). | La fiche complète finit par exister sur disque sans être écrasée ; plus de perte silencieuse ; plus de boucle de logs. |
| R5 | **TTL sur `exact-local-no-retry`** | Ajouter une expiration (ex. 7-30 jours) comme pour le cooldown texte. | Les médias publiés plus tard sur ScreenScraper redeviennent atteignables. |
| R6 | **Contrat gelé documenté** | Consigner les invariants payload (§5) et le verdict reloadgames (§3.3) comme règles d'architecture (wiki interne / commentaires en tête de GamelistUpdateService). | Personne ne « répare » le payload ou ne réintroduit un reloadgames auto par inadvertance. |

Ordre conseillé : R1+R2 (le symptôme perçu, faible risque), R3 (texte + course), R4 (complétude des
données), R5, R6. Chaque item est indépendant et rollbackable individuellement.

---

## 8. Régression upstream : /addgames est CASSÉ sur les ES récents (découverte utilisateur, vérifiée)

`loadGamelistFile` commence par une garde inconditionnelle :
```cpp
if (Utils::String::toLower(Utils::FileSystem::getExtension(xmlpath)) != ".xml" || !Utils::FileSystem::isRegularFile(xmlpath))
    return ret;
```
Appelée par le handler `/addgames` avec `xmlpath = req.body` (le XML brut) et `fromFile=false`, cette
garde rejette systématiquement le body (pas d'extension `.xml`, pas un fichier) → liste vide → **204
« No game added »** : le addgames live est un no-op silencieux.

**État vérifié le 2026-07-18 :**
| Version | Garde | /addgames body |
|---|---|---|
| Sources locales `projects-source` (= l'ES ancien utilisé volontairement sur cette machine) | absente ([Gamelist.cpp:109-116] : `fromFile ? load_file : load_string` direct) | **fonctionne** (confirmé par les mesures de la nuit : 2xx, gamelist réécrite, re-fires) |
| `batocera-linux/batocera-emulationstation` master (GitHub) | **inconditionnelle**, avant la branche fromFile | **cassé** |
| `RetroBat-Official/emulationstation` master (GitHub) | **inconditionnelle** | **cassé** |

Correctif identifié (une ligne, sans risque — la garde reste active pour les vrais fichiers) :
```cpp
if (fromFile && (Utils::String::toLower(Utils::FileSystem::getExtension(xmlpath)) != ".xml" || !Utils::FileSystem::isRegularFile(xmlpath)))
```

**Conséquences sur les ES récents** : plus aucun refresh live de fiche (204 systématique — au moins
APIExpose n'envoie pas de notification dans ce cas, le 204 retourne false) ; le scrape continue de
remplir le media store (dont profitent marquee/panel, indépendants d'ES) et de stager les gamelists
étendues… qui ne sont jamais fusionnées (§4.1). L'auto-scrap perd donc tout effet visible dans ES.

**Stratégie recommandée (compatible avec toutes les versions) :**
1. **Détection de capacité au démarrage d'APIExpose** : `GET /caps` (version ES) + table de
   correspondance ; en cas de doute, probe fonctionnel unique (fragment du jeu courant à données
   identiques : ES sain → 200, ES cassé → 204). Mémoriser `AddGamesSupported` pour la session.
2. **Mode dégradé propre si non supporté** : désactiver les pushes live ET leurs notifications
   (zéro POST inutile, zéro toast mensonger), et basculer la persistance sur la **fusion du pending à
   l'arrêt d'ES** (stratégie R4a — le watcher détecte déjà la fin de session) : tout le scrape de la
   session apparaît au démarrage suivant. L'auto-scrap garde ainsi son sens sur toutes les versions :
   médias immédiats pour marquee/panel, fiche ES complète à la prochaine session.
3. **Patch upstream** : proposer le one-liner aux deux repos (batocera-linux + RetroBat-Official) —
   seul correctif qui restaure le refresh live sur les versions récentes.
4. **Matrice de compatibilité documentée** (wiki APIExpose) : version ES ↔ refresh live disponible.
