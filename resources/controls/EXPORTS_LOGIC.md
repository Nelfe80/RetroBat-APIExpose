# Export Logic

Ce document decrit la logique actuelle des exports generes par `panel_curator_ultimate.py`.

Les trois exports concernes sont :
- `MAME-CFG` : fichier MAME `ctrlr`
- `FBNEO-RMP` : remap RetroArch pour `FinalBurn Neo`
- `RB-XML` : mapping XML RetroBat pour MAME

Les donnees dynamiques du projet sont maintenant separees en trois couches :
- `resources\dynpanels\games`
- `resources\dynpanels\systems`
- `resources\dynpanels\cores`

Le bouton `SAVE` simple met a jour :
- le JSON du curator
- le preview LED panel via `POST /api/v1/Panels/preview` quand APIExpose est
  actif

Les exports techniques (`MAME-CFG`, `FBNEO-RMP`, `RB-XML`,
`LIBRETRO-YML`) sont produits par leurs boutons dedies ou par la generation
batch.

## Source de verite

La source de verite n'est pas un fichier externe MAME ou FBNeo.

La source de verite est :
- les donnees editees dans `panel_curator_ultimate.py`
- les conventions universelles de `profiles_db.py`
- les mappings MAME deja associes a chaque bouton/joystick dans le curator

Principe general :
- on reconstruit d'abord le panel logique/origine
- ensuite chaque export traduit ce panel vers le format technique cible
- on ne laisse pas un mapping externe "confort manette" imposer sa logique au panel

## Dynpanels

### Games

`resources\dynpanels\games` contient les JSON jeu par jeu produits par le curator.

### Systems

`resources\dynpanels\systems` contient les JSON systeme generes depuis `resources\panels\systems`.

### Cores

`resources\dynpanels\cores` contient les JSON core generes depuis `resources\panels\libreto`.

Cette couche ne remplace pas `systems`.
Elle sert a decrire :
- le vocabulaire Retropad d'un core
- les groupes/presets documentes dans le markdown du core
- la base de recoupement future entre `systems` et `cores`

## 1. Export CFG

### Objectif

Generer un fichier MAME de type `ctrlr` par ROM :
- destination principale : `E:\RetroBat\saves\mame\ctrlr\<rom>.cfg`
- copie locale : `resources\controls\mame\<rom>.cfg`

### Source des donnees

Le `CFG` est construit a partir :
- des boutons de chaque joueur
- des joysticks de panel
- des metadonnees MAME de chaque entree :
  - `tag`
  - `type`
  - `mask`
  - `defvalue`

Le layout exporte depend de `self.export_layout_var`.

### Logique de generation

Le code passe par :
- `collect_mame_cfg_assignments()`
- `build_mame_cfg_tree()`
- `save_mame_cfg_export()`

La logique est la suivante :
- chaque entree MAME est identifiee par `(tag, type, mask, defvalue)`
- pour chaque entree, on accumule une ou plusieurs sequences :
  - `standard`
  - `increment`
  - `decrement`
- les boutons de panel sont traduits en `KEYCODE_*` via les slots du layout choisi
- les directions de joystick sont traduites selon leur nature :
  - bouton/direction simple -> `standard`
  - axe analogique -> `increment` / `decrement`

### Format produit

Le fichier genere est un XML MAME :

```xml
<mameconfig version="10">
  <system name="rom">
    <input>
      <port tag="..." type="..." mask="..." defvalue="...">
        <newseq type="standard">KEYCODE_A OR KEYCODE_B</newseq>
      </port>
    </input>
  </system>
</mameconfig>
```

### Remarques

- si aucune entree MAME exploitable n'est trouvee, aucun `CFG` n'est ecrit
- plusieurs touches peuvent etre accumulees sur la meme entree MAME avec `OR`
- Start/Coin doit rester compatible avec MAME64 standalone et RetroArch
  `mame_libretro` dans le meme fichier jeu. Le generateur combine donc les
  deux conventions :
  - `START1 = KEYCODE_1 OR JOYCODE_1_BUTTON9 OR JOYCODE_1_START`
  - `COIN1 = KEYCODE_5 OR JOYCODE_1_BUTTON10 OR JOYCODE_1_SELECT`
- La regle vit dans `profiles_db.py`, constante `MAME_SYSTEM_JOYCODES`.
  Voir aussi `docs/09_MAME_CONTROL_MAPPING.md`.

## 2. Export FBNEO-RMP

### Objectif

Generer un remap RetroArch par ROM pour FinalBurn Neo uniquement :
- `E:\RetroBat\emulators\retroarch\config\remaps\FinalBurn Neo\<rom>.rmp`

Copie locale :
- `resources\controls\retroarch\fbneo\<rom>.rmp`

Le curator n'exporte plus de `.rmp` dans
`E:\RetroBat\emulators\retroarch\config\remaps\MAME`. Pour MAME, la logique
principale passe par les fichiers MAME-CFG et, si besoin, par les exports
`LIBRETRO-YML`.

### Source des donnees

Le `RMP` est construit a partir :
- des slots panel attribues aux boutons
- des conventions libretro definies dans `profiles_db.py`
- des correspondances `RMP_SLOT_BUTTONS_BY_LAYOUT`
- des correspondances systeme `RMP_SYSTEM_BUTTON_MAP`

Le principe ici est :
- on traduit le panel logique vers des boutons RetroArch/libretro
- on ne suit pas `fbneo.yml` comme source prioritaire

### Logique de generation

Le code passe par :
- `collect_retroarch_rmp_assignments()`
- `build_retroarch_rmp_text()`
- `save_retroarch_rmp_exports()`

La logique est la suivante :
- on calcule les remaps par joueur
- on genere des lignes `input_playerX_btn_* = "N"`
- on force un minimum de 4 joueurs dans le fichier, pour rester stable
- on ajoute :
  - `input_libretro_device_pX = "1"`
  - `input_playerX_analog_dpad_mode = "0"`
  - `input_remap_port_pX = "<player-1>"`
- on applique l'ordre de sortie `RMP_BUTTON_OUTPUT_ORDER`
- s'il y a plusieurs affectations impossibles a representer sur la meme cle, elles sont remontees comme `conflicts`

### Format produit

Le fichier genere est un texte `.rmp`, par exemple :

```ini
input_libretro_device_p1 = "1"
input_player1_analog_dpad_mode = "0"
input_player1_btn_b = "0"
input_player1_btn_a = "8"
input_remap_port_p1 = "0"
```

### Remarques

- le contenu RMP reste construit depuis la matrice panel universelle
- la destination runtime est uniquement `FinalBurn Neo`
- si un jour MAME redevient une cible RMP, il faudra le remettre comme export
  explicite separe, pas comme effet de bord du bouton FBNeo

## 3. Export RB-XML

### Objectif

Generer un XML RetroBat par ROM :
- destination principale : `E:\RetroBat\user\inputmapping\mame\<rom>.xml`
- copie locale : `resources\controls\retrobat\mame\<rom>.xml`

### Source des donnees

Le `RB-XML` est construit a partir :
- des boutons reconnus dans le curator
- des entrees joystick reconstruites dans le curator
- des identites MAME de chaque entree :
  - `tag`
  - `type`
  - `mask`
  - `defvalue`
- des conventions de mapping RetroBat definies dans le code

### Variantes de layout generees

Le panel RetroBat de reference pour les slots est :

```text
4 3 5 7
1 2 6 8
```

Le XML contient 4 layouts :
- `default`
- `modern8`
- `6alternative`
- `8alternative`

Le mapping actuel est :

`default`
- `1 -> JOY_west`
- `2 -> JOY_south`
- `3 -> JOY_east`
- `4 -> JOY_north`
- `5 -> JOY_l1`
- `6 -> JOY_r1`
- `7 -> JOY_l2trigger`
- `8 -> JOY_r2trigger`

`modern8`
- `1 -> JOY_west`
- `2 -> JOY_north`
- `3 -> JOY_r1`
- `4 -> JOY_south`
- `5 -> JOY_east`
- `6 -> JOY_r2trigger`
- `7 -> JOY_l1`
- `8 -> JOY_l2trigger`

`6alternative`
- `1 -> JOY_west`
- `2 -> JOY_north`
- `3 -> JOY_l1`
- `4 -> JOY_south`
- `5 -> JOY_east`
- `6 -> JOY_r1`

`8alternative`
- `1 -> JOY_south`
- `2 -> JOY_east`
- `3 -> JOY_west`
- `4 -> JOY_north`
- `5 -> JOY_l1`
- `6 -> JOY_r1`
- `7 -> JOY_l2trigger`
- `8 -> JOY_r2trigger`

### Logique de generation

Le code passe par :
- `retrobat_xml_player_buttons()`
- `retrobat_xml_joystick_entries()`
- `retrobat_xml_button_slot()`
- `retrobat_xml_append_button()`
- `build_retrobat_xml_tree()`
- `save_retrobat_xml_export()`

La logique est la suivante :
- on recupere les boutons numerotes du jeu (`game_button`)
- on conserve uniquement ceux qui ont une identite MAME valable
- on determine un `source_layout` d'apres le nombre de boutons :
  - `2-Button`
  - `4-Button`
  - `6-Button`
  - `8-Button`
- pour chaque variante RetroBat, on parcourt les joueurs puis :
  - on ajoute les directions joystick
  - on ajoute les boutons de jeu dans l'ordre logique `Button 1`, `Button 2`, etc.
- chaque bouton XML contient :
  - l'identite MAME
  - une `mapping` RetroBat `JOY_*`
  - une couleur
  - une fonction
  - un `slot`

### Slot au lieu de X/Y

Le XML n'exporte plus `x` et `y`.

A la place :
- les boutons de gameplay exportent `slot="1"` a `slot="8"` selon le layout source
- les directions joystick exportent un slot textuel :
  - `LEFT`
  - `RIGHT`
  - `UP`
  - `DOWN`

Le but est :
- de garder une logique semantique
- d'eviter une pseudo-position graphique figee dans le XML

### Format produit

Exemple simplifie :

```xml
<game name="rom" rom="rom">
  <layouts>
    <layout type="modern8" joystickcolor="Red">
      <button type="P1_JOYSTICK_LEFT" tag=":P1" mask="2" defvalue="2" slot="LEFT" color="Red" function="JOYSTICK_LEFT">
        <mapping type="standard" name="JOY_left OR JOY_lsleft" />
      </button>
      <button type="P1_BUTTON1" tag=":P1" mask="16" defvalue="16" slot="1" color="Orange" function="BUTTON1">
        <mapping type="standard" name="JOY_west" />
      </button>
    </layout>
  </layouts>
</game>
```

## Sauvegarde automatique

Quand on clique sur `SAVE`, le curator fait maintenant dans cet ordre :
1. ecriture du JSON du jeu dans `resources\dynpanels\games`
2. synchronisation du LED panel via APIExpose actif si disponible

Les autres exports sont declenches par leurs boutons :
- `MAME-CFG` genere le cfg MAME runtime et sa copie locale
- `FBNEO-RMP` genere uniquement le remap RetroArch FinalBurn Neo
- `RB-XML` genere le mapping RetroBat
- `LIBRETRO-YML` genere les mappings user libretro

Le preview LED ne doit pas lancer un second LedManager quand APIExpose est
actif. Le curator poste un `panel.state` transitoire sur
`/api/v1/Panels/preview`; APIExpose le rebroadcast sur `/ws/panel`, et le
LedManager deja lance l'applique par son pipeline normal. Si APIExpose est
absent, l'ancien fallback LedManager/stdin reste disponible pour debug.

## Fichiers de reference utiles

Dans `resources\controls` :
- `controls.ini`
- `controls.json`
- `restructured-controls.json`
- `fbneo.yml`

Dans le code :
- `panel_curator_ultimate.py`
- `profiles_db.py`

## Exposition API

L'API expose les fichiers generes dans `resources\controls` sans supposer qu'ils existent deja.
Cela permet a un plugin comme `ledmanager` de savoir quels fichiers sont disponibles, mais aussi quels fichiers il doit generer ou deployer.

Endpoint par ROM :

```text
GET /api/v1/Panels/controls/{rom}
```

Chaque reponse contient les slots connus :
- `mame-cfg`
- `retroarch-rmp-mame`
- `retroarch-rmp-fbneo`
- `retrobat-xml-mame`

Pour chaque slot, l'API renvoie :
- `ApiPath` : chemin relatif dans le plugin, par exemple `resources/controls/mame/llander.cfg`
- `ApiFilePath` : chemin local complet de la copie API
- `TargetPath` : chemin cible RetroBat attendu
- `ExistsInApi` : indique si la copie locale existe dans `resources\controls`
- `ExistsAtTarget` : indique si le fichier est deja deploye dans RetroBat
- `DownloadUrl` : URL de telechargement si `ExistsInApi` vaut `true`

Telechargement d'un fichier existant :

```text
GET /api/v1/Panels/controls/{rom}/files/{id}/content
```

Le flux WebSocket `panel.state` expose aussi ce catalogue dans le bloc `ControlFiles` quand le contexte courant est un jeu.

## Intention globale

L'intention du projet est :
- reconstruire le panel d'origine le plus fidelement possible
- conserver une logique universelle par slots
- decliner ensuite cette logique dans plusieurs formats techniques

Autrement dit :
- le panel logique prime
- les exports ne sont que des traductions de ce panel
