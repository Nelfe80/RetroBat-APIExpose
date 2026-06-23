# Dynamic Panel Driver Pico — GPIO, WS2812B, boutons, joysticks, bandeaux et matrices

Ce firmware MicroPython pour Raspberry Pi Pico permet de piloter un panel arcade lumineux sans figer le câblage dans `main.py`.

Le principe est simple : au démarrage, le PC, RetroBat, MAME ou Thonny envoie une configuration matérielle au Pico. Le Pico crée alors des **pointeurs de sorties** : boutons, START, SELECT, joysticks, bandeaux LED, circles/rings, matrices WS2812B, etc.

Le firmware supporte deux familles de LEDs :

- **GPIO direct** : une LED simple ou un bouton RGB câblé directement sur des GPIO du Pico.
- **Adressable WS2812B / NeoPixel** : boutons adressables, bandeaux, circles, joysticks lumineux, matrices 8x8 / 16x16 / 32x32.

---

## 1. Fichiers nécessaires sur le Pico

Copier ces fichiers à la racine du Pico :

```text
main.py
hardware_profiles.py
profiles_db.py
```

- `main.py` : firmware dynamique qui reçoit les commandes.
- `hardware_profiles.py` : profils matériels génériques initialisables avec une seule commande `HW`.
- `profiles_db.py` : profils de couleurs par système ou panel, par exemple NeoGeo, SNES, MAME, etc.

---

## 2. Démarrage rapide

Dans Thonny, lancer `main.py`. Le Pico doit répondre :

```text
READY DYNAMIC PANEL DRIVER
```

Pour ton panel actuel :

```text
HW GPIO_8B_SS_GPIO
```

Cela initialise :

```text
8 boutons RGB en GPIO direct
START en GPIO ON/OFF
SELECT en GPIO ON/OFF
```

Ensuite tu peux tester :

```text
SET B1 RED
SET B2 BLUE
START ON
SELECT ON
GET
```

Pour tout éteindre :

```text
CLEAR
```

Pour tester toutes les sorties :

```text
DEMOOUTPUTS 400
```

---

## 3. Grande nouveauté : polarité HIGH / LOW par sortie

Les sorties ON/OFF ne sont pas toutes câblées pareil selon les panels.

Le firmware supporte donc une option de polarité par sortie :

```text
HIGH = actif haut : ON met le GPIO à 1
LOW  = actif bas  : ON met le GPIO à 0
```

Depuis la dernière correction :

```text
RGB GPIO  = LOW par défaut
ON/OFF GPIO = LOW par défaut

Donc `START ON` met GP27 à `0` dans le profil standard. Si ton montage actif haut allume avec `1`, utilise `POLARITY START HIGH`.
```

C’est le comportement attendu pour ton cas actuel :

```text
START ON  -> GP27 à 1
START OFF -> GP27 à 0
SELECT ON -> GP28 à 1
SELECT OFF -> GP28 à 0
```

Si un utilisateur a un montage inversé, il peut le déclarer directement dans le `PTR` :

```text
PTR START START ONOFF START 27 LOW
PTR SELECT SELECT ONOFF SELECT 28 LOW
```

Ou modifier après initialisation :

```text
POLARITY START LOW
POLARITY SELECT LOW
```

Pour revenir au comportement normal actif haut :

```text
POLARITY START HIGH
POLARITY SELECT HIGH
```

Raccourci équivalent :

```text
INVERT START ON
INVERT START OFF
```

- `INVERT ... ON` force la sortie en actif bas.
- `INVERT ... OFF` force la sortie en actif haut.

---

## 4. Commandes principales

### Diagnostic

```text
PING
SCAN
GET
GPIO
BUSES
HWLIST
HWDESC GPIO_8B_SS_GPIO
MATRIXINFO
```

### Pilotage simple

```text
SET B1 RED
SET B2 BLUE
SLOT 4 GREEN
START ON
SELECT OFF
JOY1 CYAN
STRIP1 PURPLE
CIRCLE1 ORANGE
ALL WHITE
CLEAR
```

### Profils de couleurs système

```text
PROFILE NEOGEO_MINI
PROFILE SNES_DEFAULT
PROFILE MAME
PANELS
```

Les profils peuvent contenir des entrées `START` et `SELECT`. Si ces sorties sont RGB, la couleur est appliquée. Si elles sont ON/OFF, toute couleur autre que `BLACK/OFF` devient `ON`.

---

## 5. Protocole d’initialisation manuel

Le protocole bas niveau est :

```text
INIT
BUS <nom_bus> NEOPIXEL <gpio_data> <nombre_pixels> [luminosité] [ordre_couleur]
PTR <nom> <type> <mode> <slot> <gpio... ou bus:index> [HIGH|LOW|SHARED]
COMMIT
```

Exemple minimal :

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2 LOW
PTR B2 BUTTON RGB 2 3,4,5 LOW
PTR START START ONOFF START 27 LOW
PTR SELECT SELECT ONOFF SELECT 28 LOW
COMMIT
```

Dans cet exemple :

- `B1` est un bouton RGB direct sur GP0, GP1, GP2, actif bas.
- `B2` est un bouton RGB direct sur GP3, GP4, GP5, actif bas.
- `START` est une LED simple sur GP27, active bas par défaut.
- `SELECT` est une LED simple sur GP28, active bas par défaut.

---

## 6. Types et modes disponibles

### Types logiques

```text
BUTTON  = bouton joueur, B1 à B8
START   = bouton START
SELECT  = bouton SELECT / COIN
JOY     = joystick lumineux
STRIP   = bandeau LED
CIRCLE  = ring / circle LED
MATRIX  = matrice de pixels
AUX     = sortie libre
```

### Modes électriques

```text
ONOFF = 1 GPIO, marche / arrêt
RGB   = 3 GPIO directs, un canal par couleur
ADDR  = pixels adressables sur bus WS2812B / NeoPixel
```

### Règle importante

Dans un même profil, les boutons `B1` à `B8` ne mélangent pas GPIO et adressable.

Valide :

```text
8 boutons RGB GPIO + START/SELECT GPIO
8 boutons adressables + START/SELECT GPIO
8 boutons adressables + START/SELECT adressables
```

Évité volontairement :

```text
B1 à B4 GPIO + B5 à B8 adressables
```

Cela simplifie la logique côté RetroBat / MAME / configurateur.

---

## 7. Profils matériels prêts à utiliser

Utiliser :

```text
HW <nom_du_profil>
```

Lister les profils :

```text
HWLIST
```

Décrire un profil :

```text
HWDESC ADDR_8B_SS_GPIO_2JOY_ADDR
```

### 7.1 Boutons GPIO directs

```text
HW GPIO_2B
HW GPIO_4B
HW GPIO_6B
HW GPIO_8B
```

Ces profils déclarent uniquement les boutons `B1` à `Bn` en RGB GPIO direct.

### 7.2 Boutons GPIO + START/SELECT ON/OFF

```text
HW GPIO_2B_SS_GPIO
HW GPIO_4B_SS_GPIO
HW GPIO_6B_SS_GPIO
HW GPIO_8B_SS_GPIO
```

Exemple pour ton panel actuel :

```text
HW GPIO_8B_SS_GPIO
```

Équivalent logique :

```text
B1 à B8 = RGB GPIO, actif LOW
START   = ON/OFF GPIO, actif LOW
SELECT  = ON/OFF GPIO, actif LOW
```

### 7.3 Boutons GPIO + START/SELECT RGB

```text
HW GPIO_2B_SS_RGB
HW GPIO_4B_SS_RGB
HW GPIO_6B_SS_RGB
```

`GPIO_8B_SS_RGB` n’est pas proposé car 8 boutons RGB consomment déjà 24 GPIO, et START/SELECT RGB ajouteraient 6 GPIO supplémentaires. Le Pico n’a pas assez de GPIO libres pour cela.

### 7.4 Boutons GPIO + joysticks GPIO ou RGB

```text
HW GPIO_2B_SS_GPIO_1JOY_GPIO
HW GPIO_2B_SS_GPIO_2JOY_GPIO
HW GPIO_4B_SS_GPIO_1JOY_GPIO
HW GPIO_4B_SS_GPIO_2JOY_GPIO
HW GPIO_6B_SS_GPIO_1JOY_GPIO
HW GPIO_6B_SS_GPIO_2JOY_GPIO
```

Pour joystick RGB direct :

```text
HW GPIO_2B_SS_GPIO_1JOY_RGB
HW GPIO_2B_SS_GPIO_2JOY_RGB
HW GPIO_4B_SS_GPIO_1JOY_RGB
HW GPIO_4B_SS_GPIO_2JOY_RGB
HW GPIO_6B_SS_GPIO_1JOY_RGB
HW GPIO_6B_SS_GPIO_2JOY_RGB
```

Attention : les joysticks RGB consomment 3 GPIO chacun.

---

## 8. Profils adressables WS2812B / NeoPixel

Par défaut, les profils adressables utilisent un bus `PANEL` sur GP22, sauf certains profils mixtes GPIO + adressable qui réservent un autre GPIO disponible.

### 8.1 Boutons adressables seuls

```text
HW ADDR_2B
HW ADDR_4B
HW ADDR_6B
HW ADDR_8B
```

Par convention, chaque bouton adressable reçoit 4 pixels.

### 8.2 Boutons adressables + START/SELECT adressables

```text
HW ADDR_2B_SS_ADDR
HW ADDR_4B_SS_ADDR
HW ADDR_6B_SS_ADDR
HW ADDR_8B_SS_ADDR
```

Tout passe par le bus adressable : boutons, START et SELECT.

### 8.3 Boutons adressables + START/SELECT GPIO

```text
HW ADDR_2B_SS_GPIO
HW ADDR_4B_SS_GPIO
HW ADDR_6B_SS_GPIO
HW ADDR_8B_SS_GPIO
```

C’est utile si l’utilisateur a des boutons adressables mais deux LEDs simples pour START/SELECT.

Exemple :

```text
HW ADDR_8B_SS_GPIO
```

Logique :

```text
B1 à B8 = adressables WS2812B
START   = GP27 ON/OFF actif LOW
SELECT  = GP28 ON/OFF actif LOW
```

### 8.4 Boutons adressables + joysticks adressables

```text
HW ADDR_2B_SS_ADDR_1JOY_ADDR
HW ADDR_2B_SS_ADDR_2JOY_ADDR
HW ADDR_4B_SS_ADDR_1JOY_ADDR
HW ADDR_4B_SS_ADDR_2JOY_ADDR
HW ADDR_6B_SS_ADDR_1JOY_ADDR
HW ADDR_6B_SS_ADDR_2JOY_ADDR
HW ADDR_8B_SS_ADDR_1JOY_ADDR
HW ADDR_8B_SS_ADDR_2JOY_ADDR
```

Variante avec START/SELECT en GPIO :

```text
HW ADDR_8B_SS_GPIO_1JOY_ADDR
HW ADDR_8B_SS_GPIO_2JOY_ADDR
```

---

## 9. Bandeaux LED, circles/rings et effets lumineux

### Bandeaux seuls

```text
HW ADDR_1STRIP
HW ADDR_2STRIP
HW ADDR_3STRIP
HW ADDR_4STRIP
```

Chaque bandeau fait 30 pixels par défaut.

### Circles / rings seuls

```text
HW ADDR_1CIRCLE
HW ADDR_2CIRCLE
HW ADDR_3CIRCLE
HW ADDR_4CIRCLE
```

Chaque circle fait 16 pixels par défaut.

### Bandeaux + circles

```text
HW ADDR_1STRIP_1CIRCLE
HW ADDR_2STRIP_2CIRCLE
HW ADDR_3STRIP_3CIRCLE
HW ADDR_4STRIP_4CIRCLE
```

### Exemples de pilotage

```text
STRIP1 RED
STRIP2 BLUE
CIRCLE1 GREEN
CIRCLE2 OFF
ALL CYAN
CLEAR
```

---

## 10. Matrices WS2812B flexibles 8x8, 16x16 et 32x32

Le firmware supporte les panneaux :

```text
WS2812B RGB Flexible 8x8
WS2812B RGB Flexible 16x16
WS2812B RGB Flexible 32x32
```

Profils dédiés :

```text
HW ADDR_MATRIX_8X8
HW ADDR_MATRIX_16X16
HW ADDR_MATRIX_32X32
```

Après initialisation, tu peux vérifier :

```text
MATRIXINFO
```

### Configuration explicite de la matrice

Si besoin, tu peux forcer la taille et le type de câblage :

```text
MATRIXCFG MATRIX1 8 8 SERPENTINE
MATRIXCFG MATRIX1 16 16 SERPENTINE
MATRIXCFG MATRIX1 32 32 SERPENTINE
```

Modes :

```text
SERPENTINE = une ligne sur deux est inversée, très courant sur panneaux flexibles
LINEAR     = toutes les lignes dans le même sens
```

### Commandes matrice

Remplir :

```text
MATRIXFILL MATRIX1 RED
MATRIXFILL MATRIX1 #00FF00
MATRIXCLEAR MATRIX1
```

Allumer un pixel :

```text
MATRIXPIXEL MATRIX1 0 0 BLUE
MATRIXPIXEL MATRIX1 15 15 #FF00FF
```

Dessiner un rectangle :

```text
MATRIXRECT MATRIX1 0 0 8 2 YELLOW
```

Afficher un score :

```text
MATRIXSCORE MATRIX1 12345 GREEN
```

Raccourci prévu pour MAME outputs :

```text
MAME SCORE 12345
MAME SCORE 98765 MATRIX1 RED
```

Afficher un texte simple :

```text
MATRIXTEXT MATRIX1 WHITE READY
MATRIXTEXT MATRIX1 GREEN 12345
```

Envoyer une image complète en hexadécimal :

```text
MATRIXIMAGE MATRIX1 FF000000FF000000FF...
```

Envoyer une ligne :

```text
MATRIXROW MATRIX1 0 FF000000FF000000FF000000FF000000FF
```

Le format image est en blocs `RRGGBB`, ligne par ligne.

---

## 11. Ordre des couleurs RGB / GRB

Certains WS2812B utilisent l’ordre logique GRB au lieu de RGB. Si tu demandes `RED` et que tu obtiens du vert, change l’ordre du bus :

```text
BUSORDER PANEL GRB
```

Autres valeurs possibles :

```text
RGB
GRB
BRG
BGR
RBG
GBR
```

Tu peux aussi le déclarer dès l’initialisation manuelle :

```text
BUS PANEL NEOPIXEL 22 256 0.35 GRB
```

---

## 12. Configurations mixtes utiles

### 8 boutons RGB GPIO + START/SELECT simples

```text
HW GPIO_8B_SS_GPIO
```

C’est ton cas actuel.

### 6 boutons RGB GPIO + START/SELECT simples + 1 joystick RGB + 2 bandeaux

```text
HW GPIO_6B_SS_GPIO_1JOY_RGB_2STRIP
```

### 8 boutons adressables + START/SELECT GPIO + 2 joysticks adressables

```text
HW ADDR_8B_SS_GPIO_2JOY_ADDR
```

### 8 boutons adressables + START/SELECT GPIO + matrice 16x16

```text
HW ADDR_8B_SS_GPIO_MATRIX16X16
```

### 8 boutons adressables + START/SELECT adressables + matrice 32x32

```text
HW ADDR_8B_SS_ADDR_MATRIX32X32
```

### 8 boutons adressables + START/SELECT GPIO + 2 joysticks + 2 bandeaux + 2 circles

```text
HW ADDR_8B_SS_GPIO_2JOY_ADDR_2STRIP_2CIRCLE
```

### 8 boutons adressables + START/SELECT adressables + 2 joysticks + 4 bandeaux + 4 circles

```text
HW ADDR_8B_SS_ADDR_2JOY_ADDR_4STRIP_4CIRCLE
```

---

## 13. GPIO disponibles sur Raspberry Pi Pico

Le firmware considère comme sûrs :

```text
GP0 à GP22
GP26, GP27, GP28
```

GP25 est réservé à la LED interne du Pico pour le heartbeat.

Rappel de consommation GPIO :

```text
ONOFF GPIO = 1 GPIO
RGB GPIO   = 3 GPIO
Bus WS2812B = 1 GPIO data, quel que soit le nombre de pixels
```

Exemples :

```text
8 boutons RGB GPIO = 24 GPIO
+ START ON/OFF     = 1 GPIO
+ SELECT ON/OFF    = 1 GPIO
Total              = 26 GPIO
```

C’est quasiment le maximum exploitable du Pico.

Avec de l’adressable :

```text
8 boutons adressables + START/SELECT + joysticks + matrice
= 1 seul GPIO data pour toute la chaîne si tout est sur le même bus
```

Mais attention : on gagne des GPIO, pas de la puissance électrique.

---

## 14. Conseils de câblage GPIO direct

### Boutons RGB SJ@JX validés

Sur les boutons RGB SJ@JX que tu as testés, la logique RGB directe est active bas :

```text
LOW  = canal allumé
HIGH = canal éteint
```

C’est pour cela que les boutons RGB sont déclarés en `LOW` :

```text
PTR B1 BUTTON RGB 1 0,1,2 LOW
```

### LED simple START/SELECT

Pour une LED simple câblée de manière classique avec résistance :

```text
GPIO -> résistance -> LED -> GND
```

la logique est active haut :

```text
HIGH = ON
LOW  = OFF
```

Donc :

```text
PTR START START ONOFF START 27 LOW
PTR SELECT SELECT ONOFF SELECT 28 LOW
```

Si ton câblage est :

```text
3.3V -> résistance -> LED -> GPIO
```

alors c’est actif bas :

```text
PTR START START ONOFF START 27 LOW
```

### Résistances

Pour une LED simple, prévoir une résistance série, typiquement :

```text
220 Ω à 470 Ω
```

Ne jamais brancher une LED simple directement entre un GPIO et le GND sans résistance.

---

## 15. Conseils de câblage WS2812B / NeoPixel

### Connexions minimales

Pour un bandeau, un circle ou une matrice WS2812B :

```text
Pico GND  -> GND alimentation LEDs
Pico GPIO -> DIN du premier pixel
Alim 5V   -> +5V LEDs
Alim GND  -> GND LEDs
```

La masse doit être commune :

```text
GND Pico = GND alimentation LEDs = GND panel
```

Sans masse commune, les données seront instables ou ne fonctionneront pas.

### Ne pas alimenter les LEDs par le Pico

Ne pas alimenter une matrice ou un long bandeau WS2812B depuis le 5V USB du Pico.

Utiliser une alimentation externe 5V adaptée.

### Résistance sur DATA

Ajouter idéalement une résistance sur la ligne DATA :

```text
330 Ω à 470 Ω entre le GPIO Pico et DIN
```

Elle limite les pics et les rebonds sur le signal.

### Condensateur d’entrée

Ajouter un condensateur entre +5V et GND au début du ruban ou de la matrice :

```text
1000 µF / 6.3V ou plus
```

Pour une petite installation, 470 µF peut suffire, mais 1000 µF est une bonne pratique.

### Level shifter recommandé

Le Pico sort un signal DATA en 3.3V. Les WS2812B alimentés en 5V préfèrent souvent un signal proche de 5V.

Souvent, cela fonctionne directement en 3.3V, mais pour un montage fiable :

```text
GPIO Pico 3.3V -> level shifter 5V -> DIN WS2812B
```

Un 74AHCT125 ou 74HCT245 est adapté.

### Injection de puissance

Pour des bandeaux longs ou une matrice 16x16 / 32x32, injecter le 5V et le GND à plusieurs endroits.

Une seule arrivée en début de bande peut provoquer :

```text
baisse de luminosité en fin de chaîne
couleurs qui virent au rouge
clignotements
reset des LEDs
```

---

## 16. Estimation de courant WS2812B

Un pixel WS2812B peut consommer jusqu’à environ 60 mA en blanc plein à pleine puissance.

Estimation théorique maximale :

```text
8x8   = 64 pixels   -> 3.84 A max
16x16 = 256 pixels  -> 15.36 A max
32x32 = 1024 pixels -> 61.44 A max
```

En pratique, le firmware limite souvent la luminosité à `0.35` dans les profils adressables, et les animations ne sont pas toujours en blanc plein. Mais il faut dimensionner l’alimentation avec marge.

Recommandations pratiques :

```text
8x8   -> alimentation 5V 4A confortable
16x16 -> alimentation 5V 10A à 20A selon usage
32x32 -> alimentation 5V très sérieuse, injections multiples, câblage dimensionné
```

Pour une borne, il vaut mieux éviter le blanc plein à 100% sur une 32x32.

---

## 17. Exemple d’intégration MAME outputs

Un script PC peut lire les outputs MAME et envoyer des commandes série au Pico.

Exemples :

```text
MAME SCORE 12500
```

Le Pico affiche le score sur la première matrice disponible.

Pour des lampes :

```text
START ON
SELECT OFF
B1 RED
B2 BLUE
JOY1 GREEN
```

Pour un jeu 6 boutons sur panel 8 boutons :

```text
BATCH B1 RED;B2 BLUE;B3 GREEN;B4 YELLOW;B5 WHITE;B6 WHITE;B7 OFF;B8 OFF
```

---

## 18. Exemple d’intégration RetroBat / PC

Côté PC, on peut initialiser le panel au lancement :

```python
import serial
import time

ser = serial.Serial("COM5", 115200, timeout=1)
time.sleep(2)

for line in [
    "HW GPIO_8B_SS_GPIO",
    "PROFILE MAME",
    "START ON",
]:
    ser.write((line + "\n").encode("utf-8"))
    time.sleep(0.05)
```

Avec `hardware_profiles.py`, on peut aussi générer les lignes côté PC :

```python
from hardware_profiles import get_init_commands

for line in get_init_commands("ADDR_8B_SS_GPIO_2JOY_ADDR"):
    ser.write((line + "\n").encode("utf-8"))
```

Mais le plus simple si `hardware_profiles.py` est sur le Pico reste :

```text
HW ADDR_8B_SS_GPIO_2JOY_ADDR
```

---

## 19. Dépannage rapide

### START ON éteint la LED

La polarité est inversée.

Tester :

```text
POLARITY START LOW
```

ou :

```text
INVERT START ON
```

Si ça corrige, modifier le profil ou l’init pour mettre `LOW` sur cette sortie.

### START ON ne fait rien

Vérifier :

```text
GET
GPIO
```

Puis vérifier le câblage, la résistance, la masse et le GPIO déclaré.

### Les boutons RGB affichent de mauvaises couleurs

Pour GPIO direct SJ@JX, vérifier l’ordre des fils déclarés dans `PTR`.

Exemple :

```text
PTR B1 BUTTON RGB 1 0,1,2 LOW
```

Si rouge et bleu sont inversés, il faut inverser les GPIO correspondants dans le `PTR`.

### Les WS2812B affichent rouge quand je demande vert

Changer l’ordre du bus :

```text
BUSORDER PANEL GRB
```

### Les WS2812B clignotent ou reset

Vérifier :

```text
masse commune
alimentation 5V suffisante
résistance DATA
condensateur d’entrée
injection 5V/GND
level shifter si nécessaire
```

### La matrice affiche l’image en zigzag ou miroir

Tester :

```text
MATRIXCFG MATRIX1 16 16 SERPENTINE
```

ou :

```text
MATRIXCFG MATRIX1 16 16 LINEAR
```

---

## 20. Commandes mémo

```text
HWLIST
HWDESC GPIO_8B_SS_GPIO
HW GPIO_8B_SS_GPIO
SCAN
GET
GPIO
BUSES
MATRIXINFO

SET B1 RED
SLOT 4 BLUE
START ON
SELECT OFF
JOY1 GREEN
STRIP1 CYAN
CIRCLE1 ORANGE
ALL WHITE
CLEAR

POLARITY START HIGH
POLARITY START LOW
INVERT START OFF
INVERT START ON

MATRIXFILL MATRIX1 RED
MATRIXPIXEL MATRIX1 0 0 BLUE
MATRIXTEXT MATRIX1 WHITE READY
MATRIXSCORE MATRIX1 12345 GREEN
MAME SCORE 12345
```

---

## 21. Profil conseillé pour les tests actuels

Pour ton montage actuel :

```text
HW GPIO_8B_SS_GPIO
```

Puis :

```text
START ON
SELECT ON
SET B1 RED
SET B2 BLUE
GET
```

Si START/SELECT sont encore inversés malgré la correction :

```text
POLARITY START LOW
POLARITY SELECT LOW
```

Avec le `hardware_profiles.py` mis à jour, START et SELECT sont déclarés en `LOW` par défaut dans les profils ON/OFF, ce qui correspond au montage arcade à + commun.
