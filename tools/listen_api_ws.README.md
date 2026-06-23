# listen_api_ws.py

`listen_api_ws.py` est un petit client WebSocket de diagnostic pour APIExpose.
Il n'utilise aucune dependance Python externe : il ouvre la connexion WebSocket
directement avec la bibliotheque standard.

## Usage Rapide

Ecouter le flux global :

```powershell
py .\tools\listen_api_ws.py
```

Ecouter un flux specialise :

```powershell
py .\tools\listen_api_ws.py --stream marquee
py .\tools\listen_api_ws.py --stream topper
py .\tools\listen_api_ws.py --stream instruction-card
```

Afficher les flux connus :

```powershell
py .\tools\listen_api_ws.py --list-streams
```

## Flux Disponibles

Le flux global `/ws` recoit tous les evenements.

Les flux specialises connus sont :

| Stream | URL cible |
| --- | --- |
| `frontend` | `ws://127.0.0.1:12345/ws/frontend` |
| `marquee` | `ws://127.0.0.1:12345/ws/marquee` |
| `topper` | `ws://127.0.0.1:12345/ws/topper` |
| `instruction-card` | `ws://127.0.0.1:12345/ws/instruction-card` |
| `panel` | `ws://127.0.0.1:12345/ws/panel` |
| `ingame` | `ws://127.0.0.1:12345/ws/ingame` |
| `arcade` | `ws://127.0.0.1:12345/ws/arcade` |
| `hiscore` | `ws://127.0.0.1:12345/ws/hiscore` |
| `media` | `ws://127.0.0.1:12345/ws/media` |
| `roms` | `ws://127.0.0.1:12345/ws/roms` |
| `system` | `ws://127.0.0.1:12345/ws/system` |
| `control` | `ws://127.0.0.1:12345/ws/control` |

Aliases pratiques :

- `score` et `scores` pointent vers `hiscore`.
- `instructioncard`, `instruction_card` et `instructions` pointent vers
  `instruction-card`.
- `global`, `debug`, `ws` ou une valeur vide pointent vers le flux global.

## Exemples Utiles

Lire un seul evenement puis quitter :

```powershell
py .\tools\listen_api_ws.py --stream marquee --once
```

Afficher le JSON brut :

```powershell
py .\tools\listen_api_ws.py --stream system --raw
```

Logger dans un fichier :

```powershell
py .\tools\listen_api_ws.py --stream instruction-card --log-file .\logs\ws-instruction-card.log
```

Ecouter une URL personnalisee :

```powershell
py .\tools\listen_api_ws.py --url ws://127.0.0.1:12345/ws --stream topper
```

Desactiver la reconnexion automatique :

```powershell
py .\tools\listen_api_ws.py --stream marquee --no-retry
```

## Lecture De Sortie

Par defaut, le script affiche le type d'evenement puis le `payload` formate :

```text
[2026-06-07 15:20:00] Event received.
marquee.snapshot
{
  "Stream": "marquee",
  "Selection": {
    "System": "arcade",
    "Game": "1943"
  },
  "Media": {
    "Marquee": "...",
    "ScreenMarquee": "...",
    "Dmd": {
      "Still": "...",
      "Animations": [...]
    }
  }
}
```

Avec `--raw`, le message `EventEnvelope` complet est imprime tel que recu.

## Notes

- L'API doit etre lancee et le WebSocket active dans les options APIExpose.
- Si `--stream marquee` retourne `HTTP/1.1 404 Not Found`, relancer l'API avec
  le build Debug a jour. Un ancien process APIExpose ne connait que `/ws` et pas
  encore les routes `/ws/<stream>`. Un WebSocket desactive dans les options
  renvoie aussi 404.
- Le script reconnecte automatiquement apres une erreur, sauf avec `--no-retry`
  ou `--once`.
- Les flux physiques `marquee`, `topper` et `instruction-card` emettent leurs
  snapshots sur selection de jeu quand les fichiers existent dans `media/`.
