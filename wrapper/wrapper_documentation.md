# Documentation : Arcade Wrapper (RetroBat / Libretro) - V4 Industrial Edition
Copyright (C) Nelfe 2026

## 1. Présentation Générale
L'**Arcade Wrapper** est une bibliothèque dynamique (DLL) haute performance conçue pour s'interfacer entre un frontend/émulateur Libretro (RetroArch, RetroBat) et un Core système (FBNeo, Genesis Plus GX, Snes9x). 

Son but est d'extraire de la télémétrie en temps réel (60 FPS) sans impact de performance, afin de piloter des matériels d'Arcade physiques (Vibrations, LEDs, Solénoïdes) via un flux UDP asynchrone acheminé par un **Named Pipe Windows**.

---

## 2. Architecture et Interception (Le "Proxy")
Le Wrapper fonctionne selon un modèle de **Man-in-the-Middle (Proxy DLL)** :

1. **Substitution :** Le fichier `wrapper.dll` remplace le core original (ex: `fbneo_libretro.dll`).
2. **Chargement du Vrai Cœur :** Le Wrapper charge la DLL originale (renommée avec le suffixe `_real.dll` ou placée dans un dossier `\cores_real\`).
3. **Pontage Transparent :** Toutes les commandes (Vidéo, Audio, Input) sont redirigées vers le vrai core, **sauf** l'interception de la RAM et le lancement de jeux.
4. **Protection "Antivirus" RetroBat :** Un fichier de ressource `.rc` inscrit un "ProductVersion" élevé (ex: 0.300.0.0) pour empêcher RetroBat de mettre à jour et d'écraser notre DLL par erreur.

---

## 3. Le Moteur de Parsing MEM (V4)

### A. Chargement et Alias
Lors du lancement, le Wrapper extrait le nom de la ROM et cherche une correspondance dans `alias.json`. S'il trouve un alias (ex: `sonic1` -> `sonic-the-hedgehog`), il charge le fichier `.MEM` correspondant.

### B. Hiérarchie par Indentation (Automatique)
Nouveauté majeure de la V4 : le Wrapper déduit la structure `category.event` directement de l'indentation des accolades dans le fichier Lua. 
Exemple : 
```lua
scoring = {
    collectibles = {
        { address=0x123, ... }  -- Deviendra "scoring.collectibles"
    }
}
```
Cela permet une organisation infinie des événements sans code supplémentaire.

### C. Tolérance et Standardisation
Le parser V4 est **insensible à la casse**. Vous pouvez écrire `address`, `Address`, `0x123`, `0X123`, `value`, `Value`, etc. Cela facilite la génération automatique des fichiers `.MEM` via scripts.

---

## 4. Types de Données et Endianness (Universel)
Le Wrapper supporte nativement l'alignement des données selon l'architecture du core émulé :

| Type | Dimensions | Ordre des octets |
| :--- | :--- | :--- |
| **u8** | 8 bits (1 octet) | Fixe |
| **u16le / u16be** | 16 bits (2 octets) | Little / Big Endian |
| **u24le / u24be** | 24 bits (3 octets) | Little / Big Endian |
| **u32le / u32be** | 32 bits (4 octets) | Little / Big Endian |

*Note sur la Mega Drive :* Pour les cores comme Genesis Plus GX, le Wrapper détecte automatiquement le "byteswap" et l'inverse au vol (XOR 1) pour lire les adresses correctement.

---

## 5. Logique de Déclenchement (Filtres Stricts)

Le Wrapper opère une vérification chirurgicale avant tout envoi :

- **Condition `eq` (ou `equal`) :** Déclenche l'action **uniquement** quand l'adresse mémoire devient exactement égale à `value`. Idéal pour les effets sonores ponctuels (ex: un bruit de cascade à 160 ne déclenchera pas l'anneau à 181).
- **Condition `change` :** Déclenche si la valeur change. Si une `value` est spécifiée, elle agit comme un filtre : l'action ne part que si la nouvelle valeur est celle-là.
- **Conditions `increase` / `decrease` :** Suivi des scores ou des stocks de munitions.
- **Conditions `bit_true` / `bit_false` :** Pour surveiller un bit spécifique via un `mask`.

### Protections Anti-Spam (V4)

Les compteurs d'animation et les timers changent de valeur 60 fois par seconde. Pour éviter le spam UDP et protéger le matériel physique, le Wrapper incorpore un bouclier heuristique en amont sans aucune configuration nécessaire :

**Le "Circuit Breaker" automatique (Mutation Définitive) :**
Le Wrapper analyse en temps réel la signature des changements de mémoire de chaque adresse. Si la valeur change avec un **écart de temps très court** (moins de 10 frames) ET **très régulier** (écart quasi-identique 3 fois de suite), l'adresse est qualifiée de "Boucle d'Animation ou Timer". 
Dès lors (en moins d'une fraction de seconde après le début du jeu), le Wrapper met définitivement l'adresse sous silence pour le reste de la session et prévient via la console :
`[WARNING] AUTO-MUTE DEFINITIF pour [category.event]. Signature d'animation detectee... Blocage permanent.`

---

## 6. Logs d'Initialisation (LISTENING)
Le démarrage (INIT) affiche dans la console un état détaillé pour chaque adresse surveillée **active** (items ayant `SURVEY:YES`) :
`[HH:MM:SS.mmm] [ADDR:0xADDR] [VAL:0x00] [DOF API] LISTENING: [category.event] -> Adresse (Condition) [LOG:YES/NO, SURVEY:YES/NO] - Description`

- **Filtrage :** Seuls les éléments dont la surveillance matérielle est active (`SURVEY:YES`) sont listés au démarrage pour ne pas encombrer les logs.
- **LOG:YES/NO :** Détermine si le changement doit être écrit dans la console. (ex: `no_log=true` pour les positions X/Y incessantes).
- **SURVEY:YES/NO :** Détermine si l'action doit être envoyée au matériel Arcade. (Les items à `NO` ne sont pas affichés).

---

## 7. Format de Sortie UDP (Named Pipe)
Le flux réseau envoyé vers `\\.\pipe\RetroBatArcadePipe` suit ce format normalisé et horodaté :
`[HH:MM:SS.mmm] [ADDR:0xADDR] [VAL:0xVAL] [UDP_OUT] TYPE:ACTION | SOURCE:Description | VALUE:X | RATE:Y (Nom [Détail])`

- **HORODATAGE :** Format `[HH:MM:SS.mmm]` permettant un suivi temporel précis.
- **MÉTADONNÉES :** Rappel de l'adresse et de la valeur brute en hexadécimal au tout début de la ligne.
- **TYPE :** `ACTION`, `STATE ` (avec un espace) ou `SCORE `.
- **RATE :** Représente le "Delta" (variation numérique) entre deux lectures.

---

## 8. Étude de Cas : Analyse de `sonic_ref.MEM`

Pour illustrer le fonctionnement du Wrapper V4, analysons une ligne critique du fichier de Sonic :

```lua
scoring = {
  collectibles = {
    { address=0XFFF00A, type="u8", condition="eq", value=0XB5, action="COIN_GAIN", desc="Active sound effect (Ring collected)" },
  }
}
```

### Analyse de l'Initialisation
Lors du lancement, le Wrapper repère que cette ligne est à l'intérieur de `scoring` et `collectibles`. Il génère alors le log :
`[DOF API] LISTENING: [scoring.collectibles] -> FFF00A (==181) [LOG:YES, SURVEY:YES] - Active sound effect (Ring collected)`

### Comportement en Jeu
1. **L'Adresse `0XFFF00A` :** C'est l'emplacement RAM où Sonic écrit l'ID du dernier effet sonore déclenché.
2. **La Cascade (Silence) :** Quand Sonic s'approche d'une cascade, le jeu écrit `160` (0xA0) à cette adresse. Le Wrapper V4 voit le changement, mais comme `condition="eq"` et `value=0XB5` (181), il ignore l'événement. **Le solénoïde ne bouge pas.**
3. **L'Anneau (Action) :** Dès que Sonic touche un anneau, le jeu écrit `181` (0xB5). Le Wrapper valide le match immédiat et envoie la trame :
`[UDP_OUT] SCORE :COIN_GAIN | SOURCE:Active sound effect (Ring collected) | VALUE:181 | RATE:21`
4. **Conclusion :** Votre borne réagit instantanément au son de l'anneau tout en restant silencieuse devant les chutes d'eau. C'est la puissance du filtrage V4.

---
*Initialisé par Nelfe / Antigravity (Modèle de Télémétrie Arcade 2026)*
