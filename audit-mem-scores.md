# Audit — captation des scores .MEM (cas Zool 6700→101)

Date : 2026-07-22. Déclencheur : en jeu l'écran affichait **6700**, l'app a
enregistré **101** (Zool SNES) ; et des scores partaient pendant l'attract
mode (« je n'ai même pas fait de start »).

## 1. Faits établis (sur pièces)

### 1.1 Le 101 est un octet BCD lu en binaire avec poids 1

Base du hub (`state/hub.db`, table `score`, session du 2026-07-21 13:35) :
valeurs enregistrées 72, 73, 80, 82…101, 102, 103, 104, 105, 112, 304, puis
oscillation 228/236 (attract). Le `.MEM` SNES de Zool décrit le score en
**paires de chiffres BCD, un octet = deux rangs décimaux** :

```
{ address=0X0202, ... desc="current score - tens, units" },
{ address=0X0203, ... desc="current score - hundreds, thousands" },
{ address=0X0204, ... desc="current score - ten thousands, hundred thousands" },
```

`LiveScoreAggregatorProvider.InferWeightFromWords` ne reconnaissait que les
libellés à **une seule** place (« Tens », « (Ten Thousands) ») ancrés en fin
de description — les paires « hundreds, thousands » ne matchaient pas → poids
1, valeur binaire brute. Affiché 6700 = octet `0x67` au rang
centaines/milliers → somme publiée `0x67` = **103**. CQFD (les 101/102/104 de
la base sont les scores intermédiaires 6500…6900).

### 1.2 Ampleur flotte (scan du 2026-07-22)

- 14 362 fichiers `.MEM` ;
- **81 fichiers / 239 entrées** avec des paires de places BCD dans un
  `SCORE_STATE` (megadrive 14, nes 13, snes 13, arcade 8, atari2600 6…) ;
- **14 fichiers / 41 entrées** avec un score 32 bits éclaté en deux moitiés
  u16 (`upper 16` / `lower 16`, poids 1 chacun → somme absurde) ;
- **7 798 fichiers sans aucune définition de score** (ni `SCORE_STATE` ni
  `is_score`) — ex. Zool **Game Boy** : `events = {}` vide. Ils rendaient
  pourtant le jeu « scorable » (l'index ne testait que l'existence du .MEM) →
  promesse de record impossible à tenir.
- Doublons de représentation sur la **même adresse** : `asteroids.MEM` porte
  `0X52` (u16be) **et** `0X0052` (u8) — deux clés de part différentes, la
  somme comptait l'octet deux fois.

## 2. Correctifs livrés (2026-07-22, runtime — aucune régénération .MEM)

Tous côté APIExpose, déployés et poussés :

1. **Paires BCD** (`LiveScoreAggregatorProvider.TryResolvePlacePair`) : une
   description finissant par deux places séparées par `,` / `and` / `/`
   (« tens, units », « Hundreds, Tens », « ten thousands, hundred
   thousands ») → poids = la **plus petite** place nommée, valeur =
   **décodage BCD** de l'octet. Zool : 0 + 67×100 + 0 = **6700** ✔. L'ordre
   des places dans le libellé est indifférent (asteroids écrit la grande
   d'abord).
2. **Moitiés u16** : `upper 16` → poids 65 536, `lower 16` → poids 1
   (encodage `u32-split`).
3. **Déduplication par adresse** : clé de part = adresse **normalisée**
   (`0X52` et `0X0052` → même part, plus de double comptage).
4. **Scorable honnête** (`RomCanonicalResolver.LoadScoreIndex`) : un jeu
   n'est « scorable » que si son .MEM contient `SCORE_STATE` ou `is_score`.
   Les 7 798 .MEM sans score n'allument plus la promesse de record (l'app
   affiche le ⚠ AVANT de lancer).

Garde-fous conservés : `high score` / leaderboard exclus du score courant
(`ReferenceScoreRegex`), formule `value * N` prioritaire, masques
`score_mask` inchangés.

## 3. Reste à faire (validation nécessaire avant tout dev)

### R1 — Gating attract mode / démo
Le RAM-watch capte aussi les scores de la démo (Zool : oscillation 228/236
toutes les ~3 s à 13:36, personne ne jouait). Options par ordre de coût :
- **(a) borne côté hub** : n'insérer un score que si la valeur a fait un
  cycle « remise à ~0 puis progression » depuis le lancement — fragile, les
  démos font pareil ;
- **(b) events lifecycle .MEM** (GAME_START/CREDITS) : couverture inégale
  d'un jeu à l'autre, exploitable là où présent ;
- **(c) activité manette** : la source la plus fiable (une démo ne touche
  pas aux inputs) — demande d'exposer l'état des inputs côté wrapper
  RetroArch (non trivial) ou côté MAME Lua (facile, `manager.machine.input`).
  Recommandation : commencer par MAME (arcade = le gros des salles), puis
  statuer.

### R2 — Curation des définitions fausses au cas par cas
Le fix §2 corrige les **classes** systémiques ; certaines définitions restent
fausses individuellement (adresses d'une autre région de ROM, unités
différentes). Process outillé existant : `tools/mem-curator`
(`validate_mem_repo.py`, `mame_batch_validate.py`) + retours terrain des
salles pilotes. À prioriser par les jeux réellement joués (table `score` du
hub = télémétrie gratuite).

### R3 — Régénération + déploiement flotte
Le curator v5 devrait émettre `score_mask`/`score_encoding="bcd"` sur ces
entrées (le masque est déjà géré au runtime) plutôt que de compter sur le
parsing des descriptions. À faire lors de la prochaine régénération globale —
mémoire : 157 wrappers obsolètes à redéployer par la même occasion.

### R4 — Doublon megadrive Zool (représentation triple)
`megadrive/zool-....MEM` : paire u16 (EC54/EC56) **plus** un u8 `READONLY`
(EC5A) — trois entrées pour le même score, adresses différentes donc non
dédupliquées par §2.3. La somme reste légèrement gonflée sur ce motif
(pair + octet dupliqué). Motif rare ; à traiter en curation R2/R3.

## 4. Vérification

- Build Release 0 erreur ; déployé sur la borne dev, `health` 200.
- La preuve Zool est arithmétique (données §1.1) ; la validation en jeu réel
  se fera à la prochaine session (relancer Zool SNES, comparer écran/app).
- Non-régression : les chemins masque/formule/BCD contigus n'ont pas changé ;
  les nouveaux chemins ne s'activent que sur des motifs de description
  aujourd'hui non reconnus (poids 1 par défaut = comportement corrigé, pas
  déplacé).
