# ES Features Locale Sources

APIExpose menu translations live here:

```text
resources/config-ESmenus/locales/<locale>/es-features.po
```

At startup, APIExpose merges each file into RetroBat:

```text
RetroBat/emulationstation/es_features.locale/<locale>/es-features.po
```

The injected block is bounded by:

```text
# APIEXPOSE:BEGIN
# APIEXPOSE:END
```

Keep technical media names such as `SCREENSHOT`, `SCREENTITLE`, `THUMBNAIL` and
`MARQUEE` stable so they stay aligned with ES and file naming conventions.

Current APIExpose locale set:

```text
cs_CZ, en_GB, es, fr, it, ja_JP, nl, pl, pt_BR, ru_RU, tr
```

All locale files must expose the same `msgid` list as `en_GB`; a missing entry
can leave a new APIExpose choice visible in English or untranslated in ES.
When adding a visible APIExpose option, update the `msgstr` in every locale in
the current set. Keep `msgstr == msgid` only for deliberate technical constants
such as stable media slot names, option values, region codes and `global.*`
setting keys.

French: [README.fr.md](README.fr.md)
