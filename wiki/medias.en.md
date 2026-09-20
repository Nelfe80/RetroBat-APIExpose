# Media and scraping

## Your media always come first

APIExpose finds, organizes and projects game media: screenshots, logos and wheels, boxarts, fanarts, videos, manuals, magazines, maps and theme media. But **your files take priority** - place them here and APIExpose will use them before any downloaded media:

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

If no local system media exists, APIExpose looks in the current EmulationStation theme, then in `es-theme-carbon` - it does not walk through every installed theme.

## Automatic scraping

APIExpose scrapes **locally first**, then queries ScreenScraper only when needed. Everything is driven from the ES menu `AUTO SCRAPING MANAGER`.

The current game's entry can update **without reloading the whole list**, but only on a real visible change: image, logo or thumbnail added/replaced, localized text in the right language, freshly scraped video. Raw metadata or wrong-language text does not trigger a live refresh.

## An arcade game is scraped only once

The same arcade game is often installed under several folders: `mame`, `fbneo`, `neogeo`, `cps2` and so on. Its media are the same, so APIExpose shares them: when a card looks for an image, it checks its own system's store first, then the stores of the other arcade systems. If it finds the file, it uses it where it already is, without downloading or copying it again.

Texts are shared the same way: description, genre, publisher, number of players. If another arcade folder already knows the game, the card shows up complete straight away instead of displaying "Unknown" while data is fetched from afar. APIExpose still asks for this system's own version in the background, and it takes over on your next visit.

After a scrape, the reverse happens too: if the same game exists under another arcade folder, its card is prepared for it. It will show up the next time that system refreshes its display, with nothing reloaded and no flicker.

Your own media still come first, and media scraped specifically for one system always win over another system's.

## Texts and languages

APIExpose manages localized entry texts: description, genre, date, developer, publisher, players, language, region, family.

!!! tip "Change EmulationStation's language without fear"
    When the ES language changes, APIExpose realigns the gamelists in the new language, and invalidates in-flight remote scrapes to avoid reusing results from the old language.
