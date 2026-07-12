# mem-curator v5

Generateur, validateur et outillage de diff des fichiers `.MEM` consommes par
le runtime ingame de RetroBat (wrapper RetroArch et pont MAME Lua).

Le contrat de sortie est defini par **le code qui fait foi** :

1. `plugins/Wrapper/wrapper.cpp` (`LoadMemFile` + `ProcessWatchValue`) — le
   parseur runtime de reference pour les cores RetroArch wrappes ;
2. `src/RetroBat.Api/Infrastructure/MameLuaIngameProvider.cs` — le parseur C#
   pour MAME standalone via le plugin Lua `apiexpose_ingame`.

La nomenclature metier (actions V11, familles `flow.lifecycle`,
`scoring.points`, etc.) est celle portee par le curator v4 et reste inchangee.

## Fichiers

| Fichier | Role |
| --- | --- |
| `mem_curator_v5.py` | Generation des `.MEM` + `alias.json` depuis les sources de curation (RA, DOFLinx, MAME, datacrystal). |
| `validate_mem_repo.py` | Audit d'un arbre `resources/ram` contre le contrat wrapper.cpp. |
| `diff_mem_repos.py` | Diff avant/apres entre deux arbres ram (non-regression). |
| `wrapper_fleet_inventory.ps1` | Inventaire des wrappers deployes dans `emulators/retroarch/cores` (rapport seul). |
| `mame_apiexpose_ingame/` | Source canonique du plugin Lua MAME (copie git de `resources/ram/tools/mame_apiexpose_ingame`, a garder synchronisee). |

## Prerequis

- Python 3.x (`py`).
- Les sources de curation (dossier contenant `sources/ra`, `sources/doflinx`,
  `sources/datacrystal`, `sources/gamehacking`). Chemin declare via la
  variable d'environnement `AG_SOURCE_BASE` ou le fichier local (non
  versionne) `.source-base.local` contenant une seule ligne : le chemin.
- `requests` uniquement pour la passe LLM optionnelle (`--no-llm` s'en passe).

## Usage

Test unitaire sur un jeu :

```powershell
py mem_curator_v5.py megadrive --game sonic-the-hedgehog-1 --no-llm --force --reset-alias --output-dir _test_MEM --log-dir _test_MEM_logs
```

Generation complete deterministe (tous systemes) :

```powershell
py mem_curator_v5.py all --output-dir MEM_staging --log-dir MEM_staging_logs --reset-alias --force --no-llm
```

Validation puis diff avant deploiement :

```powershell
py validate_mem_repo.py MEM_staging --json staging_report.json
py diff_mem_repos.py ..\..\resources\ram MEM_staging --json staging_diff.json
```

Deploiement : copie manuelle validee de `MEM_staging\<system>\` vers
`resources\ram\<system>\`, **toujours precedee d'un backup** de
`resources\ram` (dossier non versionne).

## Deltas v4 -> v5

Le v5 est un portage direct du v4 (`ag_mass_curator_local_v4_all_systems.py`)
avec les corrections suivantes, toutes alignees sur le parseur wrapper.cpp :

1. **Entrees inactives conservees.** Les events `no_log=true`/`no_survey=true`
   restent emis avec leurs flags : wrapper.cpp les saute au chargement (cout
   nul) et l'utilisateur final peut les reactiver en passant le flag a false,
   sans curator. Les aggregateurs score/timer d'APIExpose les lisent aussi
   comme definitions. `--drop-inactive` produit la variante allegee
   (non re-activable) ; si un jeu n'a que des events inactifs, la sortie
   complete est conservee.
2. **Flags emis seulement a `true`.** `no_log=false`/`no_survey=false` sont
   implicites pour tous les parseurs (absence = false).
3. **Cles meta toujours emises.** `player`, `score_kind`, `score_mask`,
   `score_encoding`, `timer_kind/role/direction/unit` sont exploites par
   `LiveScoreAggregatorProvider` et `LiveTimerAggregatorProvider` (parsing
   direct des .MEM pour `/ws/score` et `/ws/timer`). Seuls `bit=` (le `mask`
   porte l'information) et `rom.file` sont omis en profil wrapper : verifies
   lus par personne dans tout `src/`.
4. **Condition `any` normalisee en `change`** : `any` n'existe pas dans
   wrapper.cpp (CondType::unknown ne declenche jamais).
5. **Types larges recentres.** Les types `u40be`..`u64be` des sources DOFLinx
   (scores BCD) sont convertis en `u32be` sur les 4 octets de poids faible
   (decalage d'adresse), car wrapper.cpp lit 1 a 4 octets ; l'ancien
   comportement retombait sur u8 et cassait les deltas de score.
6. **`desc` durcie.** Le mot `address` (delimiteur d'entrees du parseur) et le
   caractere `=` (delimiteur de cles) sont neutralises dans les descriptions.
7. **Alias sensibles a la casse.** wrapper.cpp fait un match texte exact sur
   le nom de fichier ROM : chaque alias est enregistre dans sa casse d'origine
   ET en minuscules (les parseurs C# sont insensibles a la casse).
8. **Tri deterministe** des events par famille puis adresse : diffs stables
   entre deux generations.
9. **Ligne de provenance** en tete de fichier (`-- mem-curator vX (date)
   profile=...`).
10. **Resume global** `_summary.json` dans le dossier de sortie (fichiers,
    events emis/candidats, events morts filtres, par systeme).
11. Le serveur LLM par defaut est neutre (`AG_LOCAL_SERVER_URL` a definir) et
    `requests` est optionnel.

## Contrat wrapper.cpp (rappel)

- Conditions reconnues : `change`, `eq`/`equal`, `neq`/`not_equal`,
  `increase`, `decrease`, `bit_true`, `bit_false`, `color`. Toute autre valeur
  ne declenche jamais.
- Types reconnus : `u8`, `u16be/le`, `u24be/le`, `u32be/le`. Tout autre type
  est lu comme `u8`.
- `eq`/`neq` comparent a `value=` (0 si absent). `bit_true`/`bit_false`
  utilisent `mask=`. `min=`/`max=` bornent le delta absolu ; `factor=`
  multiplie le delta.
- `no_log=true` ou `no_survey=true` : watcher ignore des le chargement.
- La categorie/sous-famille est retrouvee par indentation : garder le layout
  canonique `events > categorie > sous-famille > entrees` (4/6/8 espaces).
- Les scores (`categorie scoring` ou `is_score=true`) sont decodes BCD et ne
  sont pas soumis a l'auto-mute anti-spam (8 declenchements consecutifs a
  moins de 20 frames d'ecart = mute definitif du watcher).
- `alias.json` : une entree par ligne ; priorite au hash MD5 de la ROM, puis
  match exact (sensible a la casse) du nom de fichier sans extension.
