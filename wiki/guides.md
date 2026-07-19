# Guides par profil

Qui êtes-vous, et que faut-il faire concrètement ? Cette page décrit chaque parcours de bout en bout : joueur, assembleur de bornes, gestionnaire de salle, organisateur de tournois.

!!! info "Où s'achètent les licences ?"
    Toutes les licences (salles, assembleurs) s'achètent sur [nelfetech.com](https://nelfetech.com/salles.html) — clé livrée par email instantanément. L'usage personnel d'APIExpose reste gratuit et sans licence.

## 🕹️ Joueur en salle

Vous êtes dans une salle équipée du Fleet Hub. Votre identité est **anonyme** : un identifiant, un pseudo et un code de secours — aucune donnée personnelle.

**Créer son badge (une fois)** : au comptoir ou sur la page joueurs de la salle, choisissez un pseudo. Vous recevez un badge QR (imprimable ou sur téléphone) et un **code de secours** à garder.

**S'identifier sur une borne :**

=== "Borne avec caméra"
    Présentez votre badge QR devant la caméra de la borne. Un message « Bienvenue ! » s'affiche à l'écran : vos scores comptent pour vous.

=== "Borne sans caméra (tout se fait du téléphone)"
    1. Repérez le **numéro de la borne** : affiché en petit en bas à droite de l'écran RetroBat (avec un QR), sur un sticker, ou fourni par la salle.
    2. Scannez le QR avec votre téléphone **ou** ouvrez la page d'identification de la salle et tapez le numéro de la borne.
    3. Entrez votre code joueur. C'est tout.

**Partir** : re-scannez votre badge (le même geste déconnecte), ou touchez « Je pars — me déconnecter » sur votre téléphone, ou utilisez le bouton de déconnexion de votre page profil. Un message « À bientôt ! » confirme — vos scores sont enregistrés.

**Retrouver ses records** : la page profil de la salle (code de secours ou badge) liste vos meilleurs scores par jeu, d'une visite à l'autre.

## 🏠 Joueur en ligne / à domicile

Votre badge de salle contient un **code de réclamation** : il vous permettra de rattacher vos scores de salle à un compte en ligne quand la plateforme joueurs ouvrira (classements arcade vérifiés et domicile, côte à côte). Gardez votre code de secours — c'est la clé de cette continuité.

## 🔧 Assembleur / revendeur de bornes

Vous vendez des bornes RetroBat à des particuliers et voulez les livrer avec APIExpose et son Data Pack préinstallés :

1. **Achetez une clé Builder** (29 € par borne vendue) sur [nelfetech.com/builders](https://nelfetech.com/builders.html).
2. **Préinstallez** APIExpose + Data Pack sur la borne avant livraison.
3. **Conservez la clé** avec la facture de vente : elle est votre justificatif. Rien à activer côté client — son usage personnel reste gratuit.

Volume : codes de remise dédiés dès 10 bornes (nous contacter). Détails : [licences](licences.md) et le contrat BUILDER-LICENSE du dépôt.

## 🏢 Gestionnaire de salle

Vous exploitez des bornes commercialement (salle, bar, événementiel) : le Fleet Hub supervise la flotte et la licence se compte **par borne équipée**.

1. **Achetez la licence** adaptée sur [nelfetech.com/salles](https://nelfetech.com/salles.html) (Starter 3 bornes, Salle 10 bornes, borne additionnelle, Event pass 30 jours).
2. **Installez le hub** : un exécutable unique sur une machine du réseau local de la salle.
3. **Activez la clé** dans la console d'administration du hub (première saisie = activation ; fonctionne ensuite 14 jours sans internet).
4. **Enrôlez vos bornes** : chaque borne RetroBat+APIExpose est ajoutée par son adresse locale, depuis la console d'administration.
5. **Affichez le badge de borne** *(bornes sans caméra)* : activez `CabinetBadgeOverlay` dans la configuration APIExpose de la borne — le QR d'identification et le numéro s'affichent en bas à droite de l'écran, et disparaissent pendant qu'un joueur est connecté.
6. **Branchez vos écrans** : sur chaque affichage physique, ouvrez `screen.html?name=ecran-bar` (un nom par écran) — vous pilotez ensuite **depuis la console** ce que chaque écran montre (leaderboard, tournoi, scan…), et chacun garde son contenu tant que vous ne le changez pas.

**Doctrine offline-first** : le hub écoute les bornes, il ne les commande pas. Sans réseau ni hub, chaque borne continue de fonctionner — la licence n'éteint jamais une borne.

## 🏆 Organisateur de tournois

Depuis la console du hub (rôle animateur ou admin), deux formats :

- **Sprint** : une manche armée — chaque borne joue une partie, la manche démarre seule quand toutes les bornes réservées sont sur le jeu.
- **Session libre** : une fenêtre de 1 à 4 h sur les bornes réservées — les joueurs **se relaient** (chacun se badge, joue, laisse la place), et le **classement par joueur** s'alimente en direct sur l'écran tournoi. L'écran garde le podium affiché tant que vous ne le changez pas.

Déroulé d'une manche sprint :

1. **Créez une manche** : choisissez le jeu et la durée.
2. Les joueurs **lancent le jeu sur leur borne** — la manche s'arme toute seule quand toutes les bornes sont prêtes ; une borne sur le mauvais jeu est signalée, jamais forcée.
3. Le **chrono** tourne, les scores montent en direct sur l'écran tournoi.
4. À l'expiration : **classement automatique** (les joueurs identifiés apparaissent avec leur pseudo), podium affiché, manche archivée dans l'historique.

Pour un événement ponctuel, l'**Event pass** (30 jours, toutes bornes) évite de licencier la salle à l'année. En stream, l'édition Studio de [Retro Creator](https://nelfetech.com/retrocreator.html) ajoute Live Contest, scoreboard et podium d'animation.

## ✅ Tester AVANT l'événement (indispensable)

Un jeu n'est exploitable en tournoi ou en contest que si sa définition `.MEM` expose les **signaux indispensables** : une famille `scoring` (score live) ou `hiscore` (records) — sans elle, aucun score ne remontera jamais. Ne l'apprenez pas devant le public :

1. **Vérifiez les signaux du jeu** : bouton « Vérifier les signaux » dans la console d'administration du hub (ou `GET /api/v1/tournaments/eligibility`). Le verdict liste score live / records / timer et l'empreinte de la définition.
2. **Lancez une manche de test** : cochez « Manche de test » à la création. C'est le workflow complet — armement automatique, chrono, scores live, podium — mais **rien n'est enregistré ni publié** : ni dans l'historique de la salle, ni vers la plateforme.
3. **Rejouez le geste des joueurs** : identification (badge ou téléphone), lancement du jeu, sortie — le même parcours que le jour J.

Ce conseil vaut pour **les trois rôles** : gestionnaire de salle et organisateur de tournois (manche de test du hub), et streamer (la manche de test Live Contest de Retro Creator Studio fait la même chose côté viewers : chaque participant valide ses signaux avant l'ouverture).
