# APIExpose pour RetroBat

**APIExpose** ajoute des automatismes à RetroBat et EmulationStation : médias, gamelists localisées, packs de ROMs, scraping, thèmes, collections, données arcade et API locale temps réel - le moteur dont se nourrissent les plugins [MarqueeManager](https://github.com/Nelfe80/RetroBat-Marquee-Manager) et [LedManager](https://github.com/Nelfe80/RetroBat-Led-Manager).

## 📖 Documentation

**➡ [Wiki en français](https://nelfe80.github.io/RetroBat-APIExpose/)** · **[English wiki](https://nelfe80.github.io/RetroBat-APIExpose/en/)**

Installation pas-à-pas, tous les menus et options expliqués, médias, packs de ROMs, API locale et dépannage.

## ⬇ Installation rapide

1. Téléchargez et lancez **[`APIExpose-Cabinet-Setup.exe`](https://github.com/Nelfe80/RetroBat-APIExpose/releases/latest/download/APIExpose-Cabinet-Setup.exe)** : il installe le plugin dans `RetroBat\plugins\APIExpose\` et enregistre le hook de démarrage EmulationStation.
2. Relancez RetroBat : APIExpose démarre automatiquement.

Vérification : `http://127.0.0.1:12345/api/v1/health` doit répondre `healthy`.

## 🔄 Mise à jour

Lancez **`RetroBat.Api.Update.exe`** à la racine de `RetroBat\plugins\APIExpose\` : il télécharge la dernière version publiée, vérifie son empreinte SHA-256, sauvegarde ce qu'il remplace, relance l'API et remet la sauvegarde si elle ne répond plus. Votre `appsettings.json`, `state\` et `resources\` ne sont jamais touchés. `--check` dit seulement s'il y a une mise à jour, `--yes` applique sans question.

> ⚠️ APIExpose peut modifier gamelists, médias et réglages EmulationStation. **Sauvegardez votre dossier RetroBat** avant la première utilisation.

## 📄 Licences

Usage personnel et non commercial libre. Toute utilisation commerciale nécessite une licence écrite - voir [LICENSE.md](LICENSE.md), [PERSONAL-LICENSE.md](PERSONAL-LICENSE.md), [COMMERCIAL-LICENSE.md](COMMERCIAL-LICENSE.md). Les données (`resources\`) constituent le **Data Pack**, protégé par [DATA-LICENSE.md](DATA-LICENSE.md).

---

# APIExpose for RetroBat

**APIExpose** adds automation to RetroBat and EmulationStation: media, localized gamelists, ROM packs, scraping, themes, collections, arcade data and a real-time local API - the engine that feeds the [MarqueeManager](https://github.com/Nelfe80/RetroBat-Marquee-Manager) and [LedManager](https://github.com/Nelfe80/RetroBat-Led-Manager) plugins.

## 📖 Documentation

**➡ [English wiki](https://nelfe80.github.io/RetroBat-APIExpose/en/)** · **[Wiki en français](https://nelfe80.github.io/RetroBat-APIExpose/)**

## ⬇ Quick install

1. Download and run **[`APIExpose-Cabinet-Setup.exe`](https://github.com/Nelfe80/RetroBat-APIExpose/releases/latest/download/APIExpose-Cabinet-Setup.exe)**: it installs the plugin into `RetroBat\plugins\APIExpose\` and registers the EmulationStation start hook.
2. Start RetroBat: APIExpose starts automatically.

Check: `http://127.0.0.1:12345/api/v1/health` should answer `healthy`.

## 🔄 Update

Run **`RetroBat.Api.Update.exe`** from `RetroBat\plugins\APIExpose\`: it downloads the latest published release, verifies its SHA-256, backs up what it replaces, restarts the API and restores the backup if the API stops answering. Your `appsettings.json`, `state\` and `resources\` are never touched. `--check` only reports whether an update exists, `--yes` applies without asking.

> ⚠️ APIExpose can modify gamelists, media and EmulationStation settings. **Back up your RetroBat folder** before first use.

## 📄 Licensing

Free for personal, non-commercial use. Any commercial use requires a written license - see [LICENSE.md](LICENSE.md), [PERSONAL-LICENSE.md](PERSONAL-LICENSE.md), [COMMERCIAL-LICENSE.md](COMMERCIAL-LICENSE.md). The data (`resources\`) forms the **Data Pack**, protected by [DATA-LICENSE.md](DATA-LICENSE.md).
