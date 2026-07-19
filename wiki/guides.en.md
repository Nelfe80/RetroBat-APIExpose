# Role guides

Who are you, and what do you actually need to do? This page walks each journey end to end: player, cabinet builder, venue manager, tournament organizer.

!!! info "Where are licenses purchased?"
    All licenses (venues, builders) are purchased on [nelfetech.com](https://nelfetech.com/salles.html) — key delivered instantly by email. Personal use of APIExpose stays free and license-free.

## 🕹️ Player in a venue

You are in a venue equipped with the Fleet Hub. Your identity is **anonymous**: an identifier, a nickname and a recovery code — no personal data.

**Create your badge (once)**: at the desk or on the venue's players page, pick a nickname. You get a QR badge (printable or on your phone) and a **recovery code** to keep.

**Check in on a cabinet:**

=== "Cabinet with a camera"
    Show your QR badge to the cabinet's camera. A "Welcome!" message appears on screen: your scores now count for you.

=== "Cabinet without a camera (everything from your phone)"
    1. Find the **cabinet number**: shown small at the bottom-right of the RetroBat screen (with a QR), on a sticker, or provided by the venue.
    2. Scan the QR with your phone **or** open the venue's check-in page and type the cabinet number.
    3. Enter your player code. That's it.

**Leaving**: re-scan your badge (the same gesture checks you out), or tap "I'm leaving — sign out" on your phone, or use the sign-out button on your profile page. A "See you soon!" message confirms — your scores are saved.

**Finding your records**: the venue's profile page (recovery code or badge) lists your best scores per game, visit after visit.

## 🏠 Player online / at home

Your venue badge carries a **claim code**: it will let you attach your venue scores to an online account when the player platform opens (verified arcade and home rankings, side by side). Keep your recovery code — it is the key to that continuity.

## 🔧 Cabinet builder / reseller

You sell RetroBat cabinets to individuals and want to ship them with APIExpose and its Data Pack preinstalled:

1. **Buy a Builder key** (€29 per cabinet sold) on [nelfetech.com/builders](https://nelfetech.com/builders.html).
2. **Preinstall** APIExpose + Data Pack on the cabinet before delivery.
3. **Keep the key** with the sales invoice: it is your proof. Nothing to activate on the customer side — their personal use stays free.

Volume: dedicated discount codes from 10 cabinets (contact us). Details: [licensing](licences.md) and the BUILDER-LICENSE contract in the repository.

## 🏢 Venue manager

You operate cabinets commercially (venue, bar, events): the Fleet Hub supervises the fleet and licensing is counted **per equipped cabinet**.

1. **Buy the license** that fits on [nelfetech.com/salles](https://nelfetech.com/salles.html) (Starter 3 cabinets, Venue 10 cabinets, additional cabinet, 30-day Event pass).
2. **Install the hub**: a single executable on one machine of the venue's local network.
3. **Activate the key** in the hub's admin console (first entry = activation; then works 14 days without internet).
4. **Enroll your cabinets**: each RetroBat+APIExpose cabinet is added by its local address, from the admin console.
5. **Show the cabinet badge** *(camera-less cabinets)*: enable `CabinetBadgeOverlay` in the cabinet's APIExpose configuration — the check-in QR and cabinet number appear at the bottom-right of the screen, and disappear while a player is checked in.
6. **Plug in your screens**: on each physical display, open `screen.html?name=bar-screen` (one name per display) — you then control **from the console** what every screen shows (leaderboard, tournament, scan…), and each one keeps its content until you change it.

**Offline-first doctrine**: the hub listens to cabinets, it never commands them. Without network or hub, every cabinet keeps working — the license never turns a cabinet off.

## 🏆 Tournament organizer

From the hub console (host or admin role), two formats:

- **Sprint**: an armed round — each cabinet plays one game, the round starts by itself once every reserved cabinet is on the game.
- **Open session**: a 1–4 h window on the reserved cabinets — players **take turns** (each one badges in, plays, hands over), and the **per-player ranking** feeds live on the tournament screen. The screen keeps the podium up until you change it.

A sprint round goes like this:

1. **Create a round**: pick the game and the duration.
2. Players **launch the game on their cabinet** — the round arms itself when all cabinets are ready; a cabinet on the wrong game is flagged, never forced.
3. The **timer** runs, scores climb live on the tournament screen.
4. At expiry: **automatic ranking** (identified players appear with their nickname), podium displayed, round archived in history.

For a one-off event, the **Event pass** (30 days, all cabinets) avoids licensing the venue year-round. On stream, the Studio edition of [Retro Creator](https://nelfetech.com/retrocreator.html) adds Live Contest, scoreboard and hosting podium.

## ✅ Test BEFORE the event (essential)

A game is only usable in a tournament or contest if its `.MEM` definition exposes the **indispensable signals**: a `scoring` family (live score) or `hiscore` one (records) — without it, no score will ever come up. Don't find out in front of your audience:

1. **Check the game's signals**: "Check signals" button in the hub's admin console (or `GET /api/v1/tournaments/eligibility`). The verdict lists live score / records / timer and the definition's fingerprint.
2. **Run a test round**: tick "Test round" at creation. It is the full workflow — automatic arming, timer, live scores, podium — but **nothing is recorded or published**: neither in the venue history nor to the platform.
3. **Replay the players' gestures**: check-in (badge or phone), game launch, checkout — the exact day-of journey.

This advice applies to **all three roles**: venue manager and tournament organizer (the hub's test round), and streamer (the Retro Creator Studio Live Contest test round does the same on the viewer side: every participant validates their signals before opening).
