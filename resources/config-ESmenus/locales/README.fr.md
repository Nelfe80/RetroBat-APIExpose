# Sources Des Locales ES Features

Les traductions des menus APIExpose sont ici :

```text
resources/config-ESmenus/locales/<locale>/es-features.po
```

Au demarrage, APIExpose merge chaque fichier dans RetroBat :

```text
RetroBat/emulationstation/es_features.locale/<locale>/es-features.po
```

Le bloc injecte est encadre par :

```text
# APIEXPOSE:BEGIN
# APIEXPOSE:END
```

Gardez les termes techniques `SCREENSHOT`, `SCREENTITLE`, `THUMBNAIL` et
`MARQUEE` stables pour rester alignes avec ES et les conventions de nommage.

Locales APIExpose actuelles :

```text
cs_CZ, en_GB, es, fr, it, ja_JP, nl, pl, pt_BR, ru_RU, tr
```

Tous les fichiers de locale doivent exposer la meme liste de `msgid` que
`en_GB`; une entree manquante peut laisser un nouveau choix APIExpose visible
en anglais ou non traduit dans ES.
Quand une option APIExpose visible est ajoutee, le `msgstr` doit etre renseigne
dans toutes les locales de la liste courante. Ne gardez `msgstr == msgid` que
pour les constantes techniques voulues : noms de slots media stables, valeurs
d'option, codes region et cles de settings `global.*`.

English: [README.md](README.md)
