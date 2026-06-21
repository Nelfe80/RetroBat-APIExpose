# APIExpose Personal Machine License

## Personal use

A personal APIExpose license allows one individual to use APIExpose on one personal machine.

```text
1 personal machine = 1 personal APIExpose license
```

A user may own several personal machines, but each machine requires its own personal license.

## Activation de la licence (Dépôt du fichier)

Par respect pour nos utilisateurs et pour simplifier le déploiement, aucun DRM matériel intrusif n'est appliqué. L'enregistrement se fait simplement par la création d'un fichier de configuration de licence à la racine du dossier d'installation du plugin.

### Fichier `license.ini`

Pour activer votre licence personnelle sur votre machine, créez un fichier nommé `license.ini` à la racine du dossier `APIExpose` (ou du plugin associé) avec la structure suivante :

```ini
[License]
LicenseId=APX-PRS-2026-000381
Licensee=Votre Nom
```

## Utilisation par machine (Per-Machine)

Bien que la validation soit basée sur l'honnêteté, chaque licence personnelle (`APX-PRS-...`) reste acquise pour une seule machine physique. Si vous utilisez le plugin sur plusieurs machines personnelles distinctes, chacune d'elles doit disposer de son propre fichier `license.ini` contenant son propre numéro de licence unique.

## No commercial redistribution

A personal license does not allow:

- resale;
- redistribution;
- preinstallation for a third party;
- bundling in a paid product;
- use in a commercial RetroBat distribution;
- use in a paid installation or support offer.

Any such use requires a commercial reseller license.
