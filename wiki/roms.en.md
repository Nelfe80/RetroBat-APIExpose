# ROMs and collections

## Importing a ROM pack

Drop your archives into the plugin folder, then restart RetroBat:

```text
package-installer\Megadrive 32x (36 games).7z
package-installer\GX4000 (26 games).7z
```

APIExpose imports the ROMs, the media and the gamelist provided in the pack. Accepted formats: `.zip`, `.7z`, `.rar`. Fine-grained control lives in the ES menu `ROMS PACK MANAGER`.

!!! warning "Your ROMs remain your ROMs"
    APIExpose neither ships nor downloads any ROM: the `package-installer\` folder is yours, fed by your own game backups.

## ROMs on demand (on-the-fly)

For large packs, the on-the-fly mode shows games in EmulationStation **without extracting everything upfront**: the game appears in the list, and its ROM is extracted only when you launch it. Ideal for exploring a full pack without filling the disk.

## Collections

APIExpose can create or enrich collections: dynamic collections by game family, collection packs with their media and themes. Collection packs go here:

```text
package-installer\collections\<theme-name>\
```

Then manage them from the ES menu `COLLECTIONS PACK MANAGER`.

## The Roms Manager: clean lists

The Roms Manager cleans your game lists with profiles adapted to your use:

- hide certain clones;
- manage variants of the same game;
- favor a region or a language;
- **protect your favorites** and RetroAchievements games;
- apply simple profiles (from permissive to strict).

Everything is driven from the ES menus, no file editing required.

## Themes

APIExpose also feeds RetroBat/EmulationStation themes: theme media, enriched entry data, collection assets, and the HyperBat theme when available. See the `THEMES MANAGER` menu.
