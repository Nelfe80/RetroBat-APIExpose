Nomenclature d'origine :

p     = parent du groupe
n     = nom affichable recommandé
pref  = ROM préférée du groupe
r     = toutes les ROMs du groupe
cl    = clones
pr    = prototypes
bt    = bootlegs / hacks
adu   = jeux adultes
cas   = casino / gambling
mah   = mahjong
ng    = non-jeux
np    = non-arcade
reg   = régions trouvées
cat   = catégories trouvées
pl    = nombre maximum de joueurs
p3    = groupe compatible 3 joueurs ou plus
y     = au moins une version horizontale
t     = au moins une version verticale

Résumé des options couvertes à cibler :
Option	Couverture	Champ
Jeux cachés	via gamelist	h à gérer <hidden> via gamelist
Dernière version	oui	p, c
Mahjong / Casino	oui	mah, cas
Adulte	oui	adu
Pré-installés	oui 	à gérer par date d'installation ?
3+ joueurs	oui	p3, pl
Yoko horizontal	oui	y
Tate vertical	oui	t
Non-jeux	oui	ng
Non-arcade	oui	np
Nom de fichier	oui	id
Nom descriptif	oui	n
Un jeu par rom groupé	oui	p, r, c
Région prioritaire	à gérer selon l'option de l'utilisateur (langue)

# APIExpose - Base locale de groupes de jeux RetroBat

## Objectif

Cette base sert a fournir a APIExpose une lecture rapide, locale et homogene des jeux RetroBat.

Elle permet de regrouper les variantes d un meme jeu, d identifier une version recommandee, de filtrer les prototypes, hacks, jeux adultes, casino, mahjong, non-jeux, ordinateurs, orientations d ecran, nombre de joueurs et regions disponibles.

Le but est d avoir une base exploitable directement par l API, sans refaire un parsing lourd a chaque demarrage.

## Structure du dossier

Le dossier genere contient par exemple :

system_groups/
  aliases.json
  manifest.json
  nes.json
  nes.jsonl
  snes.json
  snes.jsonl
  megadrive.json
  megadrive.jsonl
  amiga.json
  amiga.jsonl

Chaque systeme peut produire deux fichiers :

- system.json
- system.jsonl

## Role de manifest.json

manifest.json est le fichier de resume global.

Il sert a verifier combien de systemes ont ete generes, identifier les systemes non couverts, connaitre les fichiers disponibles, controler le format attendu et debugger la generation.

Champs importants :

- generated_at : date de generation
- format : description des champs
- retrobat_systems_count : nombre de systemes RetroBat detectes
- canonical_count : nombre de fichiers canoniques generes
- alias_count : nombre d alias systeme
- missing_systems_count : nombre de systemes sans source locale
- missing_systems : liste des systemes non couverts
- canonical : details des fichiers generes
- aliases_file : nom du fichier d alias

## Role de aliases.json

aliases.json est le fichier a utiliser en premier.

Il permet de savoir quel fichier ouvrir pour un systeme RetroBat.

Plusieurs systemes RetroBat peuvent partager la meme base canonique.

Exemple logique :

megadrive -> genesis.jsonl
genesis   -> genesis.jsonl
segacd    -> megacd.jsonl
tg16      -> pcengine.jsonl
amiga1200 -> amiga.jsonl

APIExpose ne doit pas supposer que le fichier porte toujours le nom exact du systeme RetroBat. Il faut toujours passer par aliases.json.

## Resolution d un systeme

Pseudo-code :

1. Recevoir un systeme RetroBat, par exemple megadrive.
2. Chercher megadrive dans aliases.json.
3. Lire le champ jsonl.
4. Ouvrir le fichier indique.
5. Parser les lignes de jeux.

Exemple Python :

    import json
    from pathlib import Path

    BASE = Path("retrobat_all_system_groups")

    with (BASE / "aliases.json").open("r", encoding="utf-8") as f:
        aliases = json.load(f)

    def resolve_system(system_name):
        alias = aliases.get(system_name)
        if not alias:
            return None

        return {
            "canonical": alias["canonical"],
            "json": BASE / alias["file"],
            "jsonl": BASE / alias["jsonl"],
            "source": alias.get("source")
        }

## Difference entre json et jsonl

### system.json

Le fichier json est lisible et indente. Il est pratique pour le debug.

Il contient un objet indexe par parent de groupe.

Exemple simplifie :

    {
      "super_mario_world": {
        "p": "super_mario_world",
        "n": "Super Mario World",
        "pref": "Super Mario World Europe",
        "r": [
          "Super Mario World Europe",
          "Super Mario World USA"
        ],
        "cl": [
          "Super Mario World USA"
        ],
        "reg": [
          "Europe",
          "USA"
        ],
        "p3": 0,
        "y": 1,
        "t": 0
      }
    }

### system.jsonl

Le fichier jsonl est le format recommande pour APIExpose.

Une ligne = un groupe de jeu.

Avantages :

- lecture rapide
- lecture ligne par ligne
- facile a streamer
- une entree par jeu logique
- adapte aux gros systemes

Exemple :

    {"p":"super_mario_world","n":"Super Mario World","pref":"Super Mario World Europe","r":["Super Mario World Europe","Super Mario World USA"],"cl":["Super Mario World USA"],"reg":["Europe","USA"],"p3":0,"y":1,"t":0}

## Format d un groupe

Chaque ligne represente un jeu logique, avec ses variantes.

Exemple :

    {"p":"10yard","n":"10-Yard Fight World set 1","pref":"10yard","r":["10yard","10yard85","10yardj","vs10yard","vs10yardj","vs10yardu"],"cl":["10yard85","10yardj","vs10yard","vs10yardu"],"reg":["Japan","USA","World"],"cat":["Sports / Football"],"pl":2,"p3":0,"y":1,"t":0}

## Champs disponibles

- p : parent du groupe
- n : nom affichable recommande
- pref : ROM preferee du groupe
- r : toutes les ROMs ou variantes du groupe
- cl : clones ou variantes
- pr : prototypes
- bt : bootlegs, hacks, versions pirate, unlicensed, homebrew
- adu : jeux adultes
- cas : casino ou gambling
- mah : mahjong
- ng : non-jeux
- np : non-arcade ou computer
- reg : regions trouvees
- cat : categories trouvees
- pl : nombre maximum de joueurs
- p3 : groupe compatible 3 joueurs ou plus
- y : au moins une version horizontale
- t : au moins une version verticale

## Champs toujours presents

Les champs suivants doivent etre consideres comme toujours presents :

- p
- n
- pref
- r
- p3
- y
- t

Exemple minimal :

    {"p":"metroid","n":"Metroid","pref":"Metroid Europe","r":["Metroid Europe"],"p3":0,"y":1,"t":0}

## Champs optionnels

Les champs suivants apparaissent uniquement quand ils sont utiles :

- cl
- pr
- bt
- adu
- cas
- mah
- ng
- np
- reg
- cat
- pl

Regle :

Champ absent = information absente, fausse ou non applicable.

Exemples :

- pas de champ adu : aucun jeu adulte identifie dans ce groupe
- pas de champ cas : aucun casino identifie
- pas de champ mah : aucun mahjong identifie
- pas de champ cl : aucune variante connue
- pas de champ pr : aucun prototype identifie
- pas de champ bt : aucun bootleg ou hack identifie

## Lire un fichier jsonl

Exemple Python :

    def iter_groups_jsonl(path):
        with path.open("r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                yield json.loads(line)

Utilisation :

    resolved = resolve_system("snes")

    if resolved:
        for group in iter_groups_jsonl(resolved["jsonl"]):
            print(group["p"], group["n"], group["pref"])

## Trouver le groupe d une ROM

Exemple Python :

    def find_group_by_rom(system_name, rom_id):
        resolved = resolve_system(system_name)
        if not resolved:
            return None

        for group in iter_groups_jsonl(resolved["jsonl"]):
            if rom_id in group.get("r", []):
                return group

        return None

Utilisation :

    group = find_group_by_rom("snes", "Super Mario World USA")

    if group:
        print(group["p"])
        print(group["pref"])

## Construire un index rapide par ROM

Pour eviter de reparcourir le fichier a chaque recherche :

    def build_rom_index(system_name):
        resolved = resolve_system(system_name)
        if not resolved:
            return {}

        index = {}

        for group in iter_groups_jsonl(resolved["jsonl"]):
            for rom in group.get("r", []):
                index[rom] = group

        return index

Utilisation :

    snes_index = build_rom_index("snes")
    group = snes_index.get("Super Mario World USA")

## Utilisation des filtres

### Jeux adultes

Si show_adult vaut false et que le champ adu est present, masquer le groupe.

    if not show_adult and "adu" in group:
        skip = True

### Casino et mahjong

Si show_casino_mahjong vaut false et que cas ou mah est present, masquer le groupe.

    if not show_casino_mahjong and ("cas" in group or "mah" in group):
        skip = True

### Non-jeux

Si show_non_games vaut false et que ng est present, masquer le groupe.

    if not show_non_games and "ng" in group:
        skip = True

### Non-arcade ou computers

Si show_non_arcade vaut false et que np est present, masquer le groupe.

    if not show_non_arcade and "np" in group:
        skip = True

### Prototypes

Si show_prototypes vaut false et que pr est present, masquer le groupe.

    if not show_prototypes and "pr" in group:
        skip = True

### Bootlegs et hacks

Si show_bootlegs vaut false et que bt est present, masquer le groupe.

    if not show_bootlegs and "bt" in group:
        skip = True

### 3 joueurs ou plus

    if only_3plus and group.get("p3", 0) != 1:
        skip = True

### Horizontal uniquement

    if only_yoko and group.get("y", 0) != 1:
        skip = True

### Vertical uniquement

    if only_tate and group.get("t", 0) != 1:
        skip = True

## Fonction complete de filtrage

    def keep_group(group, options):
        if not options.get("show_adult", True) and "adu" in group:
            return False

        if not options.get("show_casino_mahjong", True) and ("cas" in group or "mah" in group):
            return False

        if not options.get("show_non_games", True) and "ng" in group:
            return False

        if not options.get("show_non_arcade", True) and "np" in group:
            return False

        if not options.get("show_prototypes", True) and "pr" in group:
            return False

        if not options.get("show_bootlegs", True) and "bt" in group:
            return False

        if options.get("only_3plus", False) and group.get("p3", 0) != 1:
            return False

        if options.get("only_yoko", False) and group.get("y", 0) != 1:
            return False

        if options.get("only_tate", False) and group.get("t", 0) != 1:
            return False

        return True

## Un jeu par groupe

Le champ pref indique la ROM recommandee.

Si l option un jeu par groupe est active :

    rom_to_display = group["pref"]
    name_to_display = group["n"]

Si l option est desactivee :

    roms_to_display = group["r"]

## Gestion des clones et variantes

Le champ r contient toutes les variantes connues.

Exemple :

    "r": ["game_europe", "game_usa", "game_japan"]

Le champ cl contient toutes les variantes sauf la version preferee.

Exemple :

    "cl": ["game_usa", "game_japan"]

Pour afficher toutes les versions :

    for rom in group["r"]:
        print(rom)

Pour afficher uniquement la version preferee :

    print(group["pref"])

## Region prioritaire

Le champ reg indique les regions trouvees dans le groupe.

Exemple :

    "reg": ["Europe", "USA", "Japan"]

Le champ pref est choisi par une priorite par defaut au moment de la generation.

APIExpose peut recalculer la preference selon la region ou la langue utilisateur.

Exemple de priorite :

    region_priority = ["France", "Europe", "World", "USA", "Japan"]

Le format actuel indique les regions du groupe, mais ne garantit pas toujours la region exacte de chaque ROM.

Pour un choix regional plus fin, on pourra ajouter plus tard un champ optionnel rr :

    "rr": {
      "game_europe": "Europe",
      "game_usa": "USA",
      "game_japan": "Japan"
    }

## Jeux caches

Les jeux caches ne sont pas stockes dans cette base.

Ils doivent etre geres depuis la gamelist.xml.

Champ attendu :

    hidden = true

Dans APIExpose :

    si show_hidden = false :
        masquer les jeux marques hidden dans gamelist.xml

## Favoris

Les favoris ne sont pas stockes dans cette base.

Ils doivent etre geres depuis la gamelist.xml.

Champ attendu :

    favorite = true

Dans APIExpose :

    si favorites_only = true :
        afficher uniquement les jeux favoris

## Pre-installes

Les pre-installes ne doivent pas etre melanges avec cette base.

Ils dependent de l installation locale.

Recommandation :

    installed_state.json

Exemple :

    {
      "nes:super_mario_bros": {
        "pre": 1,
        "installed_at": "2026-05-10T12:00:00"
      }
    }

Regle possible :

    si le fichier etait present lors de la premiere indexation :
        pre = 1
    sinon :
        pre = 0

## Ordre recommande dans APIExpose

1. Charger aliases.json.
2. Resoudre le systeme RetroBat demande.
3. Ouvrir le fichier jsonl associe.
4. Lire les groupes ligne par ligne.
5. Appliquer les filtres globaux.
6. Appliquer les filtres gamelist.xml : hidden et favorite.
7. Appliquer l etat local : pre-installe ou installe.
8. Choisir group.pref ou group.r selon l option de groupement.
9. Afficher group.n ou le nom fichier selon l option utilisateur.

## Exemple complet

    def load_visible_games(system_name, options):
        resolved = resolve_system(system_name)

        if not resolved:
            return []

        visible = []

        for group in iter_groups_jsonl(resolved["jsonl"]):
            if not keep_group(group, options):
                continue

            if options.get("group_games", True):
                visible.append({
                    "id": group["pref"],
                    "name": group["n"],
                    "group": group["p"],
                    "variants": group.get("r", [])
                })
            else:
                for rom in group.get("r", []):
                    visible.append({
                        "id": rom,
                        "name": rom if options.get("show_filename", False) else group["n"],
                        "group": group["p"]
                    })

        return visible

## Bonnes pratiques

Toujours utiliser aliases.json pour resoudre un systeme.

Utiliser jsonl pour l API et json pour le debug.

Ne pas considerer l absence d un champ comme une erreur.

Ne pas melanger cette base canonique avec les etats utilisateur.

Separer clairement :

- base de groupes : variantes, regions, categories, preferences
- gamelist.xml : hidden, favorite, rating local, lastplayed
- installed_state.json : pre-installe ou installe localement
- options utilisateur : filtres actifs

## Resume

Cette base permet a APIExpose de repondre rapidement a une question simple :

Pour ce systeme RetroBat, quels sont les jeux logiques disponibles, quelles variantes existent, laquelle afficher par defaut, et quels filtres appliquer ?

Elle ne remplace pas la gamelist.xml.

Elle sert de couche locale de resolution, de groupement et de filtrage.
