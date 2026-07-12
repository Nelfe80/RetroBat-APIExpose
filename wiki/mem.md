# Créer ses fichiers .MEM

Un fichier `.MEM` apprend à APIExpose à **lire la mémoire d'un jeu pendant que vous y jouez** : où se trouvent les vies, le score, l'état du personnage… C'est grâce à lui que vos LEDs flashent quand Sonic perd ses rings et que le score s'affiche en direct sur le marquee — sans modifier ni le jeu ni l'émulateur.

Cette page vous apprend à écrire le vôtre. Aucun outil spécial requis : un `.MEM` est un simple fichier texte.

## Comment ça marche

```text
RetroArch exécute le jeu
   → le wrapper APIExpose lit la RAM décrite par le .MEM
      → les changements deviennent des événements (« Player loses a life »)
         → LedManager, MarqueeManager et vos outils y réagissent
```

## Où placer le fichier

```text
plugins\APIExpose\resources\ram\<système>\<nom-du-jeu>.MEM
```

Par exemple `resources\ram\nes\super-mario-bros.MEM`. Le `<système>` est le **nom du dossier RetroBat** (`nes`, `snes`, `megadrive`, `mame`…). Regardez les fichiers existants du système pour suivre le même style de nommage. Un fichier `alias.json` dans le dossier système peut faire pointer plusieurs noms de ROM (régions, romhacks) vers le même `.MEM`.

!!! tip "Partez d'un existant"
    Le Data Pack contient déjà des milliers de `.MEM`. Ouvrez celui d'un jeu proche du vôtre : c'est le meilleur modèle de départ.

## La structure : quatre blocs

Un `.MEM` est une table Lua avec quatre sections, toujours dans cet ordre :

```lua
return {
  game = { ... },      -- qui est ce jeu
  rom = { ... },       -- quelles ROMs correspondent
  memory = { ... },    -- les variables observées
  events = { ... }     -- les événements qui en découlent
}
```

### 1. `game` — l'identité du jeu

```lua
game = {
  title = "Super Mario Bros.",
  system = "nes",                 -- nom du dossier RetroBat, obligatoire
  system_name = "NES/Famicom",    -- nom lisible, recommandé
  game_id = 1446                  -- id base de données, optionnel
}
```

### 2. `rom` — les ROMs compatibles

```lua
rom = {
  name = "super_mario_bros",      -- snake_case, minuscules, sans extension
  hashes = {
    { hash = "8e3630186e35d477231bf8fd50e54cdd",
      label = "Super Mario Bros. (World).nes",
      tags = { "nointro" } }
  }
}
```

Les hashes permettent de reconnaître les variantes (régions, versions) sans dupliquer le fichier.

### 3. `memory.variables` — ce qu'on observe

Chaque variable décrit **une valeur**, jamais un changement :

```lua
memory = {
  variables = {
    lives = { address = 0x075A, type = "u8", desc = "Player lives" },
    world = { address = 0x075F, type = "u8", desc = "Current world" }
  }
}
```

- ✅ `desc = "Player lives"` — décrit la valeur
- ❌ `desc = "Player loses a life"` — ça, c'est un événement (bloc suivant)

**Où trouver les adresses ?** Avec le cheat engine de RetroArch, un débogueur d'émulateur, ou les bases communautaires (Data Crystal, guides de romhacking). Les adresses des cheat codes existants sont souvent un excellent point de départ.

### 4. `events` — ce qui déclenche les effets

Un événement = une adresse + un type + une **condition** + une description :

```lua
events = {
  resources = {
    lives = {
      { address=0x075A, type="u8", condition="decrease", desc="Player loses a life" }
    }
  },
  scoring = {
    score = {
      { address=0x0840, type="u24be", condition="increase", desc="Score increased", is_score=true }
    }
  }
}
```

## Les types

| Type | Signification |
|---|---|
| `u8` / `s8` | 1 octet, non signé / signé — **le choix par défaut** |
| `u16le` / `u16be` | 2 octets, little / big endian |
| `u24le` / `u24be` | 3 octets (fréquent pour les scores) |
| `u32le` / `u32be`, `s16*`, `s32*` | 4 octets et variantes signées |

Ne devinez pas l'endianness d'une valeur multi-octets : en cas de doute, restez en `u8`.

## Les conditions

| Condition | Quand l'utiliser | Exemples |
|---|---|---|
| `decrease` | La valeur baisse de façon signifiante | vies, santé, timer, munitions |
| `increase` | La valeur monte de façon signifiante | score, rings, expérience, combo |
| `change` | Ça change, sans direction particulière | niveau courant, salle, état du joueur |
| `equal` | Une valeur précise est atteinte (avec `min`/`max`) | écran titre actif, invincibilité, boss vaincu |
| `any` | Dernier recours, observation non directionnelle | |

## Les huit familles d'événements

Chaque événement se range dans une famille, avec des sous-clés normalisées (minuscules, `snake_case`) :

| Famille | Contenu | Sous-clés typiques |
|---|---|---|
| `flow` | Où en est le jeu | `title_screen`, `in_game`, `pause`, `game_over`, `credits` |
| `progression` | L'avancement | `world`, `level`, `stage`, `room`, `lap`, `checkpoint` |
| `resources` | Ce qui se gagne/perd | `lives`, `health`, `ammo`, `oxygen`, `timer` |
| `inventory` | Les objets | `items`, `keys`, `weapon`, `held_object` |
| `combat` | Les affrontements | `boss_hit`, `damage_taken`, `enemy_state` |
| `scoring` | La performance | `score`, `coins_rings`, `currency`, `experience`, `combo` |
| `state` | Formes et effets | `player_state`, `powerup_state`, `temporary_state`, `status_effect` |
| `system` | Le technique utile | `memory`, `prng`, `flags` |

Utilisez les noms canoniques : `rings` et `coins` deviennent `coins_rings`, `gold`/`rupees` deviennent `currency`, `XP` devient `experience`. Une valeur valide mais inclassable va dans `system.memory` — on ne jette rien.

## Traduire les valeurs : `map`

```lua
powerup_state = {
  { address=0x0756, type="u8", condition="change", desc="Player powerup state",
    map={ [0]="small", [1]="big", [2]="fire" } }
}
```

Le `map` transforme un nombre brut en mot stable — c'est ce que les effets lumineux exploitent (« fire » → panel rouge).

## Piloter les effets : `action` et `action_map`

Le runtime traduit automatiquement vos familles en commandes universelles : une baisse de `lives` émet `ACTION: DEAD`, une hausse de `scoring` émet `ACTION: SCORE`, un `flow` vers le titre émet `STATE: TITLE_SCREEN`… Pour forcer un comportement précis, utilisez les verbes génériques officiels dans `action`/`action_map` : `INVINCIBILITY_START`/`STOP`, `SPEED_START`/`STOP`, `SHIELD_GAIN`/`LOST`, `RING_GAIN`/`LOSE`, `TREASURE`, `BOSS_DEFEATED`, `LAP_COMPLETE`, `TURBO_BOOST`, `CRASH`, `DOOR_OPENED`, `SECRET_REVEALED`, `NIGHT_TIME`… Ainsi les Speed Shoes de Sonic et l'étoile de Mario allument les mêmes effets sur toutes les bornes.

## Éviter le spam : `no_log` et `no_survey`

- `no_log=true` ou `no_survey=true` : le runtime **ignore l'entrée dès le chargement** — l'adresse n'est pas surveillée et ne coûte rien en jeu.
- Ces entrées restent volontairement dans les fichiers du Data Pack : pour réactiver une adresse, passez son flag à `false` (ou supprimez-le — l'absence vaut `false`), aucun outil n'est nécessaire.
- Un anti-spam automatique protège de toute façon le runtime : un événement non-score qui se déclenche en boucle est coupé définitivement pour la session.

## Les règles d'or des descriptions

En anglais, courtes, orientées gameplay, sans point final, sans adresse dans le texte :

- ✅ `Player lives`, `Collected rings`, `Invincibility active`
- ❌ `ram address for number of lives`, `0x075A - Lives`

## Checklist avant de partager

- [ ] Les trois blocs dans l'ordre `game` → `rom` → `events`
- [ ] `game.system` = nom du dossier RetroBat
- [ ] Familles canoniques uniquement (`flow.lifecycle`, `scoring.points`…), `desc` en dernier champ, sans `=` ni le mot « address »
- [ ] `condition` reconnue : `change`, `eq`, `neq`, `increase`, `decrease`, `bit_true`, `bit_false`
- [ ] `no_log=true` sur les valeurs qui changent à chaque frame (réactivable en le passant à `false`)
- [ ] Testé en jeu : les événements apparaissent sur `ws://127.0.0.1:12345/ws/ingame` (avec leur `family`, et `color` pour les deltas score arcade)

!!! question "Un doute ?"
    Le modèle complet commenté est `resources\ram\<système>\template.MEM` quand il existe, et les fichiers du Data Pack sont autant d'exemples conformes. Les fichiers `.MEM` sont couverts par la [DATA-LICENSE](licences.md) — vos créations personnelles restent les vôtres, le partage communautaire est bienvenu.
