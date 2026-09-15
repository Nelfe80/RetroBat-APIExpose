# Dépannage

## APIExpose ne semble pas démarrer

1. Vérifiez la santé du service : `http://127.0.0.1:12345/api/v1/health`. Pas de réponse ? Le processus ne tourne pas.
2. **.NET 8** est-il installé ? L'installateur s'en charge ; sinon, installez **ASP.NET Core Runtime 8** et **.NET Desktop Runtime 8** en x64 depuis la [page .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0), ou relancez simplement l'installateur.
3. Le hook est-il en place ? Relancez `install-es-start-hook.bat` (RetroBat fermé), puis redémarrez RetroBat. Le journal `.log\es-start-hook.log` dit ce qui s'est passé au dernier démarrage : API lancée, prête, .NET 8 introuvable, délai dépassé.
4. Le démarrage peut prendre du temps sur une grosse installation (traitements initiaux) : `http://127.0.0.1:12345/api/v1/startup/ready` indique où il en est.

## RetroBat reste longtemps sur l'écran de démarrage

Le hook fait patienter EmulationStation le temps qu'APIExpose soit prêt, deux minutes au plus, et n'attend pas du tout si APIExpose ne peut pas démarrer. Si votre installation est plus ancienne, son hook pouvait attendre jusqu'à dix minutes : relancez l'installateur, ou `install-es-start-hook.bat` (RetroBat fermé), pour le mettre à jour. La mise à jour automatique d'APIExpose ne remplace pas ce fichier côté EmulationStation.

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

## Toujours bloqué ?

Ouvrez un ticket sur le [suivi des problèmes d'APIExpose](https://github.com/Nelfe80/RetroBat-APIExpose/issues) en joignant les logs du dossier `.log\`.
