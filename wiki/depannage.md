# Dépannage

## APIExpose ne semble pas démarrer

1. Vérifiez la santé du service : `http://127.0.0.1:12345/api/v1/health`. Pas de réponse ? Le processus ne tourne pas.
2. Le **runtime .NET 8** est-il installé ?
3. Le hook est-il en place ? Relancez `install-es-start-hook.bat` (RetroBat fermé), puis redémarrez RetroBat.
4. Le démarrage peut prendre du temps sur une grosse installation (traitements initiaux) : `http://127.0.0.1:12345/api/v1/startup/ready` indique où il en est.

## Les menus APIExpose n'apparaissent pas dans EmulationStation

Les options s'installent dans `EXTENDED OPTIONS` et les managers dédiés au premier démarrage complet. Redémarrez RetroBat une fois APIExpose sain (`/health` répond).

## Un pack déposé dans package-installer ne s'importe pas

- Relancez RetroBat : l'import se fait pendant la phase de démarrage.
- Vérifiez le format (`.zip`, `.7z`, `.rar`) et que l'archive n'est pas corrompue.
- Consultez les logs (voir plus bas) : chaque pack traité y laisse une trace.

## Mes gamelists ont changé et je veux revenir en arrière

APIExpose sauvegarde avant de modifier : cherchez les dossiers de backup à côté des gamelists (par exemple `.api-expose-gamelist-backups`). C'est aussi pour cela que la règle d'or reste : **sauvegarde complète avant la première utilisation**.

## Le scraping ScreenScraper ne fonctionne pas

Les appels distants nécessitent des identifiants valides et ScreenScraper doit être accessible. Vérifiez la configuration dans le menu ES `AUTO SCRAPING MANAGER`, et gardez à l'esprit que le scraping local est toujours tenté d'abord.

## Où sont les logs ?

Dans le dossier `.log\` du plugin, organisés par fonctionnalité (imports de packs, menus ES, payloads). C'est la première chose à joindre à une demande d'aide sur GitHub.
