# APIExpose pour RetroBat

APIExpose est une API locale et une couche d'automatisation intelligente conçue pour enrichir l'écosystème de RetroBat et EmulationStation. Elle permet de synchroniser les états de jeu en temps réel via WebSockets, de gérer les profils de ROMs, de manipuler les fichiers de métadonnées gamelist, d'automatiser le scraping local et de piloter des panneaux LED dynamiques.

Cette version du dépôt est destinée aux utilisateurs finaux et aux intégrateurs, incluant les fichiers d'exécution opérationnels ainsi que le code source propre pour la compilation.

---

## ⚠️ Protection et Licences (IMPORTANT)

APIExpose et ses fichiers de données associés sont protégés par des licences spécifiques :

1.  **Logiciel / Code Source** : Distribué sous licence **personnelle et non-commerciale** (voir `LICENSE.md` et `PERSONAL-LICENSE.md`). L'utilisation est limitée à un usage personnel ou éducatif. Toute redistribution commerciale ou intégration dans des produits payants (bornes d'arcade commerciales, mini-PCs préconfigurés, cartes SD/disques durs vendus garnis, etc.) est strictement interdite sans un accord de licence commerciale écrit préalable (voir `COMMERCIAL-LICENSE.md`).
2.  **Pack de Données (Data Pack)** : Les fichiers de définitions de mémoire RAM (`resources/ram/`), les fichiers de configuration de boutons/CPO (`resources/dynpanels/`), les métadonnées de référence de jeux et les fichiers de mapping sont protégés par la licence de données **`DATA-LICENSE.md`**. Aucun droit d'utilisation commerciale de ces bases de données n'est accordé par défaut.

---

## 🚀 Fonctionnalités Principales

*   **Gestion des Profils de ROMs (Roms Manager)** : Nettoyage automatique des listes de jeux (masquage de clones inutiles, filtrage par langue, région, protection des favoris ou des jeux à succès/RetroAchievements).
*   **Gestion de Médias et Scraping** : Scraping local enrichi, intégration à chaud de nouveaux médias et d'images/vidéos sur la fiche active sans nécessiter de rechargement complet de la liste.
*   **Flux WebSocket d'Événements** : Exposition en continu sur le port local `ws://127.0.0.1:12345/ws` de l'état de jeu actuel (lancement, fin, appui bouton, modification de score, état de vie, etc.) pour interagir avec des outils tiers, des dalles LED ou des overlays.
*   **Panneaux de Contrôle Dynamiques (CPO)** : Exposition des configurations physiques de boutons d'arcade (Layouts, couleurs logiques par jeu) via `resources/dynpanels/`.
*   **Liaison EmulationStation** : Script de hook automatique lançant et arrêtant le service de l'API de manière transparente au démarrage et à la fermeture de RetroBat.

---

## 📁 Structure du Dépôt

Le dépôt contient uniquement les éléments fonctionnels nécessaires et exclut tous les fichiers temporaires de développement :

*   `RetroBat.Api.exe` : Exécutable principal précompilé (Windows x64).
*   `install-es-start-hook.bat` / `uninstall-es-start-hook.bat` : Outils d'installation du lancement automatique.
*   `stop.bat` : Arrêt manuel du service.
*   `appsettings.json` : Configuration de l'API (ports, bases de données, journaux).
*   `docs/` : Manuels utilisateur, guides d'installation et spécifications techniques détaillées.
*   `resources/` : Définitions de mémoire (`ram/`), spécifications de layouts de contrôles (`dynpanels/`), fichiers de menus ES, thèmes et overlays.
*   `tools/` : Dépendances utilitaires indispensables (`ffmpeg`, `imagemagick`, `translateLocally`).
*   `wrapper/` : Module d'accès mémoire bas niveau (`wrapper.dll`) et sa documentation.
*   `src/` : Fichiers sources C# propres de l'application pour les utilisateurs souhaitant recompiler l'API par eux-mêmes.

---

## 🔧 Installation Standard

Le dossier doit être installé à cet emplacement dans votre installation de RetroBat :
```text
<votre dossier RetroBat>\plugins\APIExpose\
```

### Activation du lancement automatique (Hook) :
1.  Fermez RetroBat s'il est ouvert.
2.  Allez dans le dossier `<RetroBat>\plugins\APIExpose\`.
3.  Double-cliquez sur `install-es-start-hook.bat`.
4.  Lancez RetroBat. L'API démarrera automatiquement en arrière-plan.

Pour désinstaller le hook, lancez simplement `uninstall-es-start-hook.bat`.

---

## 🛠️ Compilation à partir des Sources

Si vous souhaitez recompiler l'exécutable vous-même au lieu d'utiliser le binaire fourni :

1.  Assurez-vous d'avoir installé le **SDK .NET 8.0** ou supérieur sur votre machine.
2.  Ouvrez une invite de commande ou PowerShell dans le dossier racine du dépôt.
3.  Compilez le projet principal à l'aide de la commande suivante :
    ```powershell
    dotnet build src\RetroBat.Api\RetroBat.Api.csproj -c Release
    ```
4.  Pour générer un exécutable unique (Single File) optimisé comme celui fourni :
    ```powershell
    dotnet publish src\RetroBat.Api\RetroBat.Api.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true -o build_output
    ```
    Copiez ensuite l'exécutable généré dans `build_output\RetroBat.Api.exe` vers la racine du plugin.
