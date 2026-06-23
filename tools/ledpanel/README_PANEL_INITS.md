# Pico Dynamic Panel Driver — README des initialisations de panels

Ce README regroupe des configurations de base à envoyer au Raspberry Pi Pico au démarrage pour déclarer le panel réel de l'utilisateur : boutons, START, SELECT, joysticks lumineux, bandes LED, circles/rings LED et boutons adressables.

L'idée générale : le firmware démarre sans panel figé. Le PC, RetroBat, ton launcher ou Thonny lui pousse une configuration matérielle avec des pointeurs logiques.

```text
INIT
PTR <nom_logique> <type_objet> <mode_led> <slot_logique> <gpio... ou bus:index>
COMMIT
```

Ensuite, les commandes de pilotage restent simples :

```text
SET B1 RED
SLOT 4 BLUE
START ON
SELECT OFF
JOY1 GREEN
PROFILE NEOGEO_MINI
CLEAR
```

---

## 1. Notions importantes

### GPIO disponibles sur Raspberry Pi Pico

Le firmware utilise les GPIO externes suivants :

```text
GP0 à GP22
GP26, GP27, GP28
```

Soit **26 GPIO utiles**.

À éviter pour les sorties externes :

```text
GP23, GP24, GP25
```

GP25 est gardé pour la LED interne de heartbeat.

### Coût GPIO selon le type de LED

```text
LED ON/OFF directe  = 1 GPIO
LED RGB directe     = 3 GPIO
Bus adressable      = 1 GPIO pour toute la chaîne
```

Exemples :

```text
8 boutons RGB directs = 8 × 3 = 24 GPIO
START + SELECT simples = 2 GPIO
Total = 26 GPIO, donc Pico plein
```

```text
6 boutons RGB directs = 18 GPIO
START RGB = 3 GPIO
SELECT RGB = 3 GPIO
JOY simple = 1 GPIO
Total = 25 GPIO
```

```text
2 joysticks adressables + 8 boutons adressables + bandes LED = 1 seul GPIO si tout est sur le même bus NeoPixel
```

### Modes de LED

```text
ONOFF = LED simple ON/OFF sur 1 GPIO
RGB   = LED RGB directe sur 3 GPIO
ADDR  = LED adressable type WS2812 / NeoPixel
```

### Types d'objets

Les types servent à décrire ce que représente la sortie.

```text
BUTTON  = bouton joueur
START   = bouton START
SELECT  = bouton SELECT / COIN
JOY     = joystick lumineux
STRIP   = bande LED
CIRCLE  = cercle/ring LED
AUX     = sortie auxiliaire
MARQUEE = éclairage marquee ou déco
```

Le type n'allume rien par lui-même. C'est un repère logique.

### Slots logiques

Pour les boutons principaux, on garde la convention actuelle :

```text
Rangée du haut : 4 3 5 7
Rangée du bas  : 1 2 6 8
```

Donc :

```text
SLOT 1 RED = bouton bas gauche
SLOT 4 RED = bouton haut gauche
```

START, SELECT et JOY peuvent aussi avoir des slots logiques :

```text
START
SELECT
JOY1
JOY2
STRIP1
CIRCLE1
```

---

## 2. Protocole d'initialisation

### Réinitialiser la configuration

```text
INIT
```

ou :

```text
RESETCFG
```

### Déclarer une LED GPIO directe ON/OFF

```text
PTR START START ONOFF START 27
```

Format :

```text
PTR <nom> <type> ONOFF <slot> <gpio>
```

### Déclarer une LED RGB GPIO directe

```text
PTR B1 BUTTON RGB 1 0,1,2
```

Format :

```text
PTR <nom> <type> RGB <slot> <gpio_r,gpio_g,gpio_b>
```

Dans le cas des boutons SJ@JX validés sur ton câblage, l'ordre correspond à la logique déjà testée dans le firmware.

### Déclarer un bus adressable NeoPixel / WS2812

```text
BUS PANEL NEOPIXEL 28 80 0.35
```

Format :

```text
BUS <nom_bus> NEOPIXEL <gpio_data> <nombre_leds> <luminosité>
```

Exemple :

```text
BUS PANEL NEOPIXEL 28 80 0.35
```

signifie :

```text
Nom du bus     = PANEL
GPIO data      = GP28
Nombre de LED  = 80 pixels
Luminosité     = 35 %
```

### Déclarer une sortie adressable

```text
PTR JOY1 JOY ADDR JOY1 PANEL:0-11
```

Format :

```text
PTR <nom> <type> ADDR <slot> <bus>:<index ou plage>
```

Exemples :

```text
PTR B1 BUTTON ADDR 1 PANEL:24-27
PTR START START ADDR START PANEL:32
PTR STRIP1 STRIP ADDR STRIP1 PANEL:40-79
```

### Valider la configuration

```text
COMMIT
```

### Diagnostic

```text
SCAN
GET
GPIO
BUSES
DEMOOUTPUTS
```

---

## 3. Configurations GPIO directes de base

Ces exemples concernent des panels câblés directement sur GPIO, sans LED adressable.

---

### A. Panel 8 boutons RGB + START/SELECT ON/OFF

C'est la configuration maximale en GPIO direct : **26 GPIO utilisés**.

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2
PTR B2 BUTTON RGB 2 3,4,5
PTR B3 BUTTON RGB 3 6,7,8
PTR B4 BUTTON RGB 4 9,10,11
PTR B5 BUTTON RGB 5 12,13,14
PTR B6 BUTTON RGB 6 15,16,17
PTR B7 BUTTON RGB 7 18,19,20
PTR B8 BUTTON RGB 8 21,22,26
PTR START START ONOFF START 27
PTR SELECT SELECT ONOFF SELECT 28
COMMIT
```

Commandes de test :

```text
SET B1 RED
SET B8 BLUE
START ON
SELECT ON
GET
CLEAR
```

Limite : il ne reste plus de GPIO pour un joystick lumineux direct.

---

### B. Panel 8 boutons RGB sans START/SELECT lumineux

Utile si START/SELECT ne sont pas éclairés ou sont branchés ailleurs.

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2
PTR B2 BUTTON RGB 2 3,4,5
PTR B3 BUTTON RGB 3 6,7,8
PTR B4 BUTTON RGB 4 9,10,11
PTR B5 BUTTON RGB 5 12,13,14
PTR B6 BUTTON RGB 6 15,16,17
PTR B7 BUTTON RGB 7 18,19,20
PTR B8 BUTTON RGB 8 21,22,26
COMMIT
```

---

### C. Panel 6 boutons RGB + START/SELECT ON/OFF + joystick RGB

Très bon compromis arcade 6 boutons avec joystick lumineux RGB.

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2
PTR B2 BUTTON RGB 2 3,4,5
PTR B3 BUTTON RGB 3 6,7,8
PTR B4 BUTTON RGB 4 9,10,11
PTR B5 BUTTON RGB 5 12,13,14
PTR B6 BUTTON RGB 6 15,16,17
PTR START START ONOFF START 18
PTR SELECT SELECT ONOFF SELECT 19
PTR JOY1 JOY RGB JOY1 20,21,22
COMMIT
```

Commandes de test :

```text
SLOT 1 RED
SLOT 2 YELLOW
SLOT 3 BLUE
SLOT 4 GREEN
JOY1 WHITE
START ON
SELECT OFF
```

GPIO libres : GP26, GP27, GP28.

---

### D. Panel 6 boutons RGB + START/SELECT RGB + joystick ON/OFF

START et SELECT profitent des couleurs de profil, mais le joystick reste simple.

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2
PTR B2 BUTTON RGB 2 3,4,5
PTR B3 BUTTON RGB 3 6,7,8
PTR B4 BUTTON RGB 4 9,10,11
PTR B5 BUTTON RGB 5 12,13,14
PTR B6 BUTTON RGB 6 15,16,17
PTR START START RGB START 18,19,20
PTR SELECT SELECT RGB SELECT 21,22,26
PTR JOY1 JOY ONOFF JOY1 27
COMMIT
```

GPIO libre : GP28.

---

### E. Panel 6 boutons RGB + START/SELECT RGB sans joystick

Configuration propre pour panels console/arcade sans joystick lumineux.

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2
PTR B2 BUTTON RGB 2 3,4,5
PTR B3 BUTTON RGB 3 6,7,8
PTR B4 BUTTON RGB 4 9,10,11
PTR B5 BUTTON RGB 5 12,13,14
PTR B6 BUTTON RGB 6 15,16,17
PTR START START RGB START 18,19,20
PTR SELECT SELECT RGB SELECT 21,22,26
COMMIT
```

GPIO libres : GP27, GP28.

---

### F. Panel 4 boutons RGB + START/SELECT RGB + joystick RGB

Bon panel simple type 4 boutons, avec éclairage complet.

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2
PTR B2 BUTTON RGB 2 3,4,5
PTR B3 BUTTON RGB 3 6,7,8
PTR B4 BUTTON RGB 4 9,10,11
PTR START START RGB START 12,13,14
PTR SELECT SELECT RGB SELECT 15,16,17
PTR JOY1 JOY RGB JOY1 18,19,20
COMMIT
```

GPIO libres : GP21, GP22, GP26, GP27, GP28.

---

### G. Panel 4 boutons RGB + START/SELECT ON/OFF + deux joysticks RGB

Utile pour panel deux joueurs minimal, chacun avec un joystick lumineux.

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2
PTR B2 BUTTON RGB 2 3,4,5
PTR B3 BUTTON RGB 3 6,7,8
PTR B4 BUTTON RGB 4 9,10,11
PTR START START ONOFF START 12
PTR SELECT SELECT ONOFF SELECT 13
PTR JOY1 JOY RGB JOY1 14,15,16
PTR JOY2 JOY RGB JOY2 17,18,19
COMMIT
```

GPIO libres : GP20, GP21, GP22, GP26, GP27, GP28.

---

### H. Panel 2 boutons RGB + START/SELECT RGB + deux joysticks RGB

Panel très flexible pour borne simple ou borne enfant.

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2
PTR B2 BUTTON RGB 2 3,4,5
PTR START START RGB START 6,7,8
PTR SELECT SELECT RGB SELECT 9,10,11
PTR JOY1 JOY RGB JOY1 12,13,14
PTR JOY2 JOY RGB JOY2 15,16,17
COMMIT
```

GPIO libres : GP18, GP19, GP20, GP21, GP22, GP26, GP27, GP28.

---

### I. Panel 8 boutons ON/OFF + START/SELECT ON/OFF + deux joysticks RGB

Cas où les boutons ne sont pas RGB, mais les joysticks le sont.

```text
INIT
PTR B1 BUTTON ONOFF 1 0
PTR B2 BUTTON ONOFF 2 1
PTR B3 BUTTON ONOFF 3 2
PTR B4 BUTTON ONOFF 4 3
PTR B5 BUTTON ONOFF 5 4
PTR B6 BUTTON ONOFF 6 5
PTR B7 BUTTON ONOFF 7 6
PTR B8 BUTTON ONOFF 8 7
PTR START START ONOFF START 8
PTR SELECT SELECT ONOFF SELECT 9
PTR JOY1 JOY RGB JOY1 10,11,12
PTR JOY2 JOY RGB JOY2 13,14,15
COMMIT
```

GPIO libres nombreux pour bande, coin, service, etc.

---

### J. Panel 8 boutons RGB + START/SELECT ON/OFF en active-high

Si les LEDs sont câblées dans l'autre sens et s'allument avec GPIO à 1, ajoute `HIGH` en fin de ligne.

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2 HIGH
PTR B2 BUTTON RGB 2 3,4,5 HIGH
PTR B3 BUTTON RGB 3 6,7,8 HIGH
PTR B4 BUTTON RGB 4 9,10,11 HIGH
PTR B5 BUTTON RGB 5 12,13,14 HIGH
PTR B6 BUTTON RGB 6 15,16,17 HIGH
PTR B7 BUTTON RGB 7 18,19,20 HIGH
PTR B8 BUTTON RGB 8 21,22,26 HIGH
PTR START START ONOFF START 27 HIGH
PTR SELECT SELECT ONOFF SELECT 28 HIGH
COMMIT
```

Par défaut, le firmware est en active-low, ce qui correspond aux boutons où le commun est au +3.3 V et où le GPIO absorbe le courant.

---

## 4. Configurations mixtes GPIO direct + adressable

Ces exemples combinent les boutons GPIO directs déjà validés avec des LEDs adressables pour économiser les GPIO.

---

### K. 8 boutons RGB directs + START/SELECT ON/OFF + bande adressable impossible sur Pico seul

Avec 8 boutons RGB directs + START/SELECT simples, les 26 GPIO sont déjà utilisés.

```text
8 × RGB = 24 GPIO
START + SELECT = 2 GPIO
Total = 26 GPIO
```

Donc pour ajouter une bande adressable, il faut au choix :

```text
- passer START ou SELECT sur un bus adressable
- passer un ou plusieurs boutons en adressable
- réduire à 6 boutons RGB directs
- utiliser un second Pico
```

---

### L. 6 boutons RGB directs + START/SELECT ON/OFF + bande adressable sur GP28

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2
PTR B2 BUTTON RGB 2 3,4,5
PTR B3 BUTTON RGB 3 6,7,8
PTR B4 BUTTON RGB 4 9,10,11
PTR B5 BUTTON RGB 5 12,13,14
PTR B6 BUTTON RGB 6 15,16,17
PTR START START ONOFF START 18
PTR SELECT SELECT ONOFF SELECT 19
BUS CAB NEOPIXEL 28 60 0.30
PTR STRIP1 STRIP ADDR STRIP1 CAB:0-59
COMMIT
```

Commandes :

```text
STRIP1 BLUE
STRIP1 BLACK
ALL RED
CLEAR
```

---

### M. 6 boutons RGB directs + deux joysticks adressables

Deux rings/circles de joystick sur un même bus NeoPixel.

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2
PTR B2 BUTTON RGB 2 3,4,5
PTR B3 BUTTON RGB 3 6,7,8
PTR B4 BUTTON RGB 4 9,10,11
PTR B5 BUTTON RGB 5 12,13,14
PTR B6 BUTTON RGB 6 15,16,17
PTR START START ONOFF START 18
PTR SELECT SELECT ONOFF SELECT 19
BUS JOYS NEOPIXEL 28 24 0.35
PTR JOY1 JOY ADDR JOY1 JOYS:0-11
PTR JOY2 JOY ADDR JOY2 JOYS:12-23
COMMIT
```

Commandes :

```text
JOY1 RED
JOY2 BLUE
START ON
SELECT ON
```

---

### N. 6 boutons RGB directs + START/SELECT adressables + deux joysticks adressables

START, SELECT et les joysticks sont sur une chaîne NeoPixel.

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2
PTR B2 BUTTON RGB 2 3,4,5
PTR B3 BUTTON RGB 3 6,7,8
PTR B4 BUTTON RGB 4 9,10,11
PTR B5 BUTTON RGB 5 12,13,14
PTR B6 BUTTON RGB 6 15,16,17
BUS PANEL NEOPIXEL 28 26 0.35
PTR JOY1 JOY ADDR JOY1 PANEL:0-11
PTR JOY2 JOY ADDR JOY2 PANEL:12-23
PTR START START ADDR START PANEL:24
PTR SELECT SELECT ADDR SELECT PANEL:25
COMMIT
```

---

### O. 4 boutons RGB directs + boutons système directs + grande ambiance adressable

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2
PTR B2 BUTTON RGB 2 3,4,5
PTR B3 BUTTON RGB 3 6,7,8
PTR B4 BUTTON RGB 4 9,10,11
PTR START START ONOFF START 12
PTR SELECT SELECT ONOFF SELECT 13
BUS AMBIANCE NEOPIXEL 28 120 0.25
PTR STRIP_LEFT STRIP ADDR STRIP_LEFT AMBIANCE:0-39
PTR STRIP_RIGHT STRIP ADDR STRIP_RIGHT AMBIANCE:40-79
PTR MARQUEE MARQUEE ADDR MARQUEE AMBIANCE:80-119
COMMIT
```

Commandes :

```text
STRIP_LEFT BLUE
STRIP_RIGHT BLUE
MARQUEE WHITE
```

---

## 5. Configurations full adressable

Ici, presque tout est sur un ou plusieurs bus NeoPixel. C'est le plus flexible.

---

### P. 8 boutons adressables + START/SELECT + deux joysticks + bande LED sur un seul bus

Exemple avec :

```text
JOY1  = pixels 0-11
JOY2  = pixels 12-23
B1-B8 = 4 pixels par bouton
START = 1 pixel
SELECT = 1 pixel
STRIP = reste de la bande
```

```text
INIT
BUS PANEL NEOPIXEL 28 90 0.30
PTR JOY1 JOY ADDR JOY1 PANEL:0-11
PTR JOY2 JOY ADDR JOY2 PANEL:12-23
PTR B1 BUTTON ADDR 1 PANEL:24-27
PTR B2 BUTTON ADDR 2 PANEL:28-31
PTR B3 BUTTON ADDR 3 PANEL:32-35
PTR B4 BUTTON ADDR 4 PANEL:36-39
PTR B5 BUTTON ADDR 5 PANEL:40-43
PTR B6 BUTTON ADDR 6 PANEL:44-47
PTR B7 BUTTON ADDR 7 PANEL:48-51
PTR B8 BUTTON ADDR 8 PANEL:52-55
PTR START START ADDR START PANEL:56
PTR SELECT SELECT ADDR SELECT PANEL:57
PTR STRIP1 STRIP ADDR STRIP1 PANEL:58-89
COMMIT
```

Avantage : un seul GPIO utilisé, GP28.

---

### Q. Boutons adressables individuels + rings joystick + marquee adressable

Même principe, mais avec des zones mieux nommées.

```text
INIT
BUS CTRL NEOPIXEL 28 64 0.35
PTR B1 BUTTON ADDR 1 CTRL:0-3
PTR B2 BUTTON ADDR 2 CTRL:4-7
PTR B3 BUTTON ADDR 3 CTRL:8-11
PTR B4 BUTTON ADDR 4 CTRL:12-15
PTR B5 BUTTON ADDR 5 CTRL:16-19
PTR B6 BUTTON ADDR 6 CTRL:20-23
PTR B7 BUTTON ADDR 7 CTRL:24-27
PTR B8 BUTTON ADDR 8 CTRL:28-31
PTR START START ADDR START CTRL:32
PTR SELECT SELECT ADDR SELECT CTRL:33
PTR JOY1 JOY ADDR JOY1 CTRL:34-45
PTR JOY2 JOY ADDR JOY2 CTRL:46-57
PTR SERVICE AUX ADDR SERVICE CTRL:58
PTR COIN AUX ADDR COIN CTRL:59
PTR MARQUEE MARQUEE ADDR MARQUEE CTRL:60-63
COMMIT
```

---

### R. Deux bus adressables séparés : panel et ambiance

Utile si tu veux séparer physiquement :

```text
GP27 = panel boutons/joysticks
GP28 = ambiance meuble/marquee
```

```text
INIT
BUS PANEL NEOPIXEL 27 64 0.35
BUS AMBIANCE NEOPIXEL 28 100 0.20
PTR JOY1 JOY ADDR JOY1 PANEL:0-11
PTR JOY2 JOY ADDR JOY2 PANEL:12-23
PTR B1 BUTTON ADDR 1 PANEL:24-27
PTR B2 BUTTON ADDR 2 PANEL:28-31
PTR B3 BUTTON ADDR 3 PANEL:32-35
PTR B4 BUTTON ADDR 4 PANEL:36-39
PTR B5 BUTTON ADDR 5 PANEL:40-43
PTR B6 BUTTON ADDR 6 PANEL:44-47
PTR START START ADDR START PANEL:48
PTR SELECT SELECT ADDR SELECT PANEL:49
PTR STRIP_LEFT STRIP ADDR STRIP_LEFT AMBIANCE:0-49
PTR STRIP_RIGHT STRIP ADDR STRIP_RIGHT AMBIANCE:50-99
COMMIT
```

---

### S. Un ring joystick partagé en zones

Si un joystick ring a 24 pixels et que tu veux pointer vers des zones distinctes.

```text
INIT
BUS JOY NEOPIXEL 28 24 0.40
PTR JOY1 JOY ADDR JOY1 JOY:0-23
PTR JOY1_TOP CIRCLE ADDR JOY1_TOP JOY:0-5
PTR JOY1_RIGHT CIRCLE ADDR JOY1_RIGHT JOY:6-11
PTR JOY1_BOTTOM CIRCLE ADDR JOY1_BOTTOM JOY:12-17
PTR JOY1_LEFT CIRCLE ADDR JOY1_LEFT JOY:18-23
COMMIT
```

Si tu veux autoriser plusieurs pointeurs sur les mêmes pixels, utilise `SHARED` :

```text
INIT
BUS JOY NEOPIXEL 28 24 0.40
PTR JOY1 JOY ADDR JOY1 JOY:0-23
PTR JOY1_TOP CIRCLE ADDR JOY1_TOP JOY:0-5 SHARED
PTR JOY1_RIGHT CIRCLE ADDR JOY1_RIGHT JOY:6-11 SHARED
PTR JOY1_BOTTOM CIRCLE ADDR JOY1_BOTTOM JOY:12-17 SHARED
PTR JOY1_LEFT CIRCLE ADDR JOY1_LEFT JOY:18-23 SHARED
COMMIT
```

---

## 6. Exemples orientés panels utilisateurs

### Utilisateur A : panel arcade standard 8 boutons RGB directs

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2
PTR B2 BUTTON RGB 2 3,4,5
PTR B3 BUTTON RGB 3 6,7,8
PTR B4 BUTTON RGB 4 9,10,11
PTR B5 BUTTON RGB 5 12,13,14
PTR B6 BUTTON RGB 6 15,16,17
PTR B7 BUTTON RGB 7 18,19,20
PTR B8 BUTTON RGB 8 21,22,26
PTR START START ONOFF START 27
PTR SELECT SELECT ONOFF SELECT 28
COMMIT
```

### Utilisateur B : panel 6 boutons + joystick RGB

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2
PTR B2 BUTTON RGB 2 3,4,5
PTR B3 BUTTON RGB 3 6,7,8
PTR B4 BUTTON RGB 4 9,10,11
PTR B5 BUTTON RGB 5 12,13,14
PTR B6 BUTTON RGB 6 15,16,17
PTR START START ONOFF START 18
PTR SELECT SELECT ONOFF SELECT 19
PTR JOY1 JOY RGB JOY1 20,21,22
COMMIT
```

### Utilisateur C : panel 6 boutons + deux joysticks adressables

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2
PTR B2 BUTTON RGB 2 3,4,5
PTR B3 BUTTON RGB 3 6,7,8
PTR B4 BUTTON RGB 4 9,10,11
PTR B5 BUTTON RGB 5 12,13,14
PTR B6 BUTTON RGB 6 15,16,17
PTR START START ONOFF START 18
PTR SELECT SELECT ONOFF SELECT 19
BUS JOYS NEOPIXEL 28 24 0.35
PTR JOY1 JOY ADDR JOY1 JOYS:0-11
PTR JOY2 JOY ADDR JOY2 JOYS:12-23
COMMIT
```

### Utilisateur D : tout en adressable

```text
INIT
BUS PANEL NEOPIXEL 28 90 0.30
PTR JOY1 JOY ADDR JOY1 PANEL:0-11
PTR JOY2 JOY ADDR JOY2 PANEL:12-23
PTR B1 BUTTON ADDR 1 PANEL:24-27
PTR B2 BUTTON ADDR 2 PANEL:28-31
PTR B3 BUTTON ADDR 3 PANEL:32-35
PTR B4 BUTTON ADDR 4 PANEL:36-39
PTR B5 BUTTON ADDR 5 PANEL:40-43
PTR B6 BUTTON ADDR 6 PANEL:44-47
PTR B7 BUTTON ADDR 7 PANEL:48-51
PTR B8 BUTTON ADDR 8 PANEL:52-55
PTR START START ADDR START PANEL:56
PTR SELECT SELECT ADDR SELECT PANEL:57
PTR STRIP1 STRIP ADDR STRIP1 PANEL:58-89
COMMIT
```

### Utilisateur E : déco cabinet adressable, boutons directs

```text
INIT
PTR B1 BUTTON RGB 1 0,1,2
PTR B2 BUTTON RGB 2 3,4,5
PTR B3 BUTTON RGB 3 6,7,8
PTR B4 BUTTON RGB 4 9,10,11
PTR B5 BUTTON RGB 5 12,13,14
PTR B6 BUTTON RGB 6 15,16,17
PTR START START ONOFF START 18
PTR SELECT SELECT ONOFF SELECT 19
BUS CAB NEOPIXEL 28 120 0.20
PTR MARQUEE MARQUEE ADDR MARQUEE CAB:0-39
PTR SIDE_LEFT STRIP ADDR SIDE_LEFT CAB:40-79
PTR SIDE_RIGHT STRIP ADDR SIDE_RIGHT CAB:80-119
COMMIT
```

---

## 7. Commandes utiles après initialisation

### Tester une sortie

```text
SET B1 RED
SET B2 BLUE
SET START ON
SET SELECT OFF
SET JOY1 GREEN
```

Les raccourcis marchent aussi pour les noms logiques :

```text
START ON
SELECT OFF
JOY1 BLUE
STRIP1 CYAN
```

### Tester par slot physique

```text
SLOT 1 RED
SLOT 2 YELLOW
SLOT 3 BLUE
SLOT 4 GREEN
```

### Tout allumer / tout éteindre

```text
ALL WHITE
CLEAR
```

### Appliquer un profil

```text
PROFILE NEOGEO_MINI
PROFILE SNES_DEFAULT
PROFILE PSX_DEFAULT
```

Si START et SELECT sont déclarés, ils peuvent recevoir les couleurs du profil. S'ils sont en ONOFF, toute couleur non noire devient simplement ON.

### Lister les profils disponibles

```text
PANELS
```

### Diagnostic

```text
SCAN
GET
GPIO
BUSES
DEMOOUTPUTS 400
```

---

## 8. Notes de câblage

### GPIO direct active-low

Par défaut, le firmware considère les LED GPIO directes en **active-low** :

```text
ON  = GPIO à 0
OFF = GPIO à 1
```

C'est adapté aux boutons câblés avec un commun au +3.3 V et les canaux couleur tirés vers les GPIO.

Si une LED fonctionne à l'envers, ajoute `HIGH` à la fin du pointeur :

```text
PTR START START ONOFF START 27 HIGH
```

ou pour du RGB :

```text
PTR B1 BUTTON RGB 1 0,1,2 HIGH
```

### Alimentation des LEDs adressables

Pour les bandes LED, circles et boutons adressables :

```text
- alimentation externe recommandée si beaucoup de pixels
- masse commune obligatoire entre alimentation LED et Pico
- data depuis le GPIO déclaré dans BUS
- luminosité limitée conseillée : 0.20 à 0.40
```

Exemple prudent :

```text
BUS PANEL NEOPIXEL 28 100 0.25
```

---

## 9. Stratégie recommandée

### Panel simple et robuste

```text
8 boutons RGB directs + START/SELECT ONOFF
```

Avantage : pas de bus adressable, câblage direct, comportement prévisible.

### Panel évolutif

```text
6 boutons RGB directs + START/SELECT ONOFF + adressable pour joysticks ou bandes LED
```

Avantage : bon équilibre entre arcade classique et effets lumineux.

### Panel premium

```text
boutons, joysticks, START/SELECT et déco en adressable
```

Avantage : très peu de GPIO utilisés, beaucoup d'effets possibles, plus facile à étendre.

---

## 10. Checklist avant test

1. Copier `main.py` sur le Pico.
2. Copier `profiles_db.py` sur le Pico si les profils sont utilisés.
3. Lancer le Pico dans Thonny.
4. Attendre :

```text
READY DYNAMIC PANEL DRIVER
```

5. Envoyer une configuration :

```text
INIT
...
COMMIT
```

6. Vérifier :

```text
SCAN
GPIO
GET
```

7. Tester :

```text
DEMOOUTPUTS 400
```

8. Appliquer un profil :

```text
PROFILE NEOGEO_MINI
```
