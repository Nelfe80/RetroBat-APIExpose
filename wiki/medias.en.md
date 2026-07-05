# Media and scraping

## Your media always come first

APIExpose finds, organizes and projects game media: screenshots, logos and wheels, boxarts, fanarts, videos, manuals, magazines, maps and theme media. But **your files take priority** — place them here and APIExpose will use them before any downloaded media:

```text
media\user\systems\<system>\games\<game>\
```

## Media for marquee, topper and instruction card

Display plugins (MarqueeManager…) read their media from the local store. To customize a game, the useful locations are:

```text
artwork\marquee\marquee.png
artwork\marquee\screenmarquee.png
artwork\marquee\dmd.png          (or dmd.gif, dmd2.gif)
artwork\marquee\topper.jpg
artwork\ic\ic.png                (instruction card)
artwork\fanart.png
ui\wheels\wheel.png
```

To override an entire **system**'s media:

```text
media\user\systems\<system>\
```

If no local system media exists, APIExpose looks in the current EmulationStation theme, then in `es-theme-carbon` — it does not walk through every installed theme.

## Automatic scraping

APIExpose scrapes **locally first**, then queries ScreenScraper only when needed. Everything is driven from the ES menu `AUTO SCRAPING MANAGER`.

The current game's entry can update **without reloading the whole list**, but only on a real visible change: image, logo or thumbnail added/replaced, localized text in the right language, freshly scraped video. Raw metadata or wrong-language text does not trigger a live refresh.

## Texts and languages

APIExpose manages localized entry texts: description, genre, date, developer, publisher, players, language, region, family.

!!! tip "Change EmulationStation's language without fear"
    When the ES language changes, APIExpose realigns the gamelists in the new language, and invalidates in-flight remote scrapes to avoid reusing results from the old language.
