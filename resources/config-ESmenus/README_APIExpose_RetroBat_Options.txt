README - Menus RetroBat pour APIExpose
======================================

Document mis a jour le 2026-05-25.

Objectif
--------

Ce dossier decrit comment exposer des options APIExpose dans les menus
RetroBat / EmulationStation, sans modifier le code source
d'EmulationStation.

Le menu RetroBat sert uniquement d'interface utilisateur. Il ecrit des valeurs
dans :

    E:\RetroBat\emulationstation\.emulationstation\es_settings.cfg

APIExpose, application C# ASP.NET, lit ensuite ces valeurs et les applique a
son runtime.

Nom visible dans EmulationStation
---------------------------------

Le nom "APIExpose" ne doit pas etre le nom principal affiche dans ES.

Nom de groupe canonique dans `es_features.cfg` :

    EXTENDED OPTIONS

Traduction francaise via `es_features.locale/fr/es-features.po` :

    OPTIONS ETENDUES

Options racine visibles :

    ENABLE API EXPOSE

Sous-menus fonctionnels :

    LOCAL MEDIA MANAGER
    AUTO SCRAPING MANAGER
    THEMES MANAGER
    MARQUEE MANAGER
    ROMS MANAGER
    CONTROL PANEL MANAGER
    GAME EVENTS MANAGER
    API SETTINGS

Chaque sous-menu fonctionnel commence par son interrupteur d'activation
canonique, traduit ensuite par locale :

    ENABLE LOCAL MEDIA MANAGER
    ENABLE AUTO SCRAPING MANAGER
    ENABLE THEMES MANAGER
    ENABLE MARQUEE MANAGER
    ENABLE ROMS MANAGER
    ENABLE GAME EVENTS MANAGER

Sous-menu LOCAL MEDIA MANAGER :

    ACTIVER LE LOCAL MEDIA MANAGER
    IMAGE PRINCIPALE
    LOGO / MARQUEE
    VIGNETTE
    STYLE WHEEL HD

Sous-menu AUTO SCRAPING MANAGER :

    ACTIVER L'AUTO SCRAPING MANAGER
    ACTIVER SCREENSCRAPER
    TRADUIRE LES DESCRIPTIONS
    SCRAP DISTANT APRES LOCAL
    REFRESH FICHE APRES SCRAP
    NOTIFY MEDIA SCRAPE
    SCRAPE MARQUEES
    SCRAPE SCREEN MARQUEES
    SCRAPE SMALL SCREEN MARQUEES
    SCRAPE STEAMGRID
    SCRAPE MIX
    SCRAPE MAPS
    SCRAPE MANUALS
    SCRAPE MAGAZINES
    SCRAPE VIDEOS
    SCRAPE NORMALIZED VIDEOS
    SCRAPE BEZELS

    Ce sous-menu ne porte pas les choix d'affichage des medias. Les anciennes
    options de type image/logo/thumb/wheel sont gerees par LOCAL MEDIA MANAGER
    via `global.apiexpose.media_allocation.*`.
    ACTIVER L'AUTO SCRAPING MANAGER lance la passe locale puis la passe distante si
    besoin, et met a jour la fiche du jeu en temps reel quand un vrai
    changement est applique.
    TRADUIRE LES DESCRIPTIONS complete une description localisee manquante a
    partir d'une description anglaise ScreenScraper via `translateLocally`.
    Si cette option est desactivee, aucune demande de traduction n'est mise en
    queue et les descriptions restent limitees aux textes locaux/ScreenScraper.
    Les manuels et videos restent des opt-in car ce sont des fichiers lourds.
    NOTIFY MEDIA SCRAPE affiche une notification quand un media lourd
    distant est vraiment importe : manuel, magazine, video ou video normalisee.
    BEZEL ASPECT et BEZEL ORIENTATION sont ranges dans CABINET SETTINGS.

Sous-menu CONTROL PANEL MANAGER :

    CABINET PROFILE
    CONTROL PANELS
    BUTTONS PER PLAYER
    ARCADE JOYSTICK
    ANALOG JOYSTICK
    SPINNERS
    TRACKBALLS
    WHEELS
    LIGHTGUNS
    ENABLE CONTROL PANEL DISPLAY
    PUSH CONTROL PANEL TO WS STREAM

    La source canonique des panels est `resources/dynpanels`. Les panels jeu
    dans `resources/dynpanels/games` concernent l'arcade; sinon APIExpose
    retombe sur le panel systeme dans `resources/dynpanels/systems`. Les
    donnees produites servent aux themes et aux panels LED.

Sous-menu MARQUEE MANAGER :

    ACTIVER MARQUEE MANAGER
    POUSSER LES DATAS MARQUEE DANS LE FLUX WS
    GENERATION MARQUEE
    NOTIFICATION GENERATION MARQUEE

    Ce manager regroupe la cible des flux utiles aux clients marquee :
    image marquee canonique, logo, fanart et contexte jeu pour generer une
    scene marquee a la volee.
    GENERATION MARQUEE reste a NO par defaut. Le choix d'une dimension active
    la generation d'un marquee personnalise apres scraping quand aucun vrai
    marquee n'est disponible. Cette generation vise surtout consoles et
    ordinateurs; elle utilise fanart et logo/wheel, jamais screenshot ou
    screentitle.
    Si un faux artwork/marquee/marquee.png a le meme hash que screenmarquee
    ou screenmarquee-small, il est efface avant la decision de generation.

Sous-menu ROMS MANAGER :

    ACTIVER LE ROMS MANAGER
    ROMS PROFILE
    NEVER HIDE FAVORITES
    LANGUAGE
    REGION
    RETROACHIEVEMENTS GAMES
    ROM VERSION

    CLONES
    PROTOTYPES
    DEMOS
    BETA / ALPHA VERSIONS
    USEFUL PATCHES
    HACKS AND MODS
    CHEATS AND TRAINERS
    HOMEBREWS AND AFTERMARKET
    UNLICENSED GAMES
    BOOTLEGS AND PIRATES
    ADULT GAMES
    CASINO AND GAMBLING
    MAHJONG
    QUIZ GAMES
    NON-GAMES
    UNKNOWN ROMS
    ARCADE TESTS AND DIAGNOSTICS

Sous-menu CABINET SETTINGS :

    SCREEN / COCKTAIL ORIENTATION
    BEZEL ORIENTATION
    BEZEL ASPECT
    MULTI-SCREEN GAMES
    FUNCTIONAL SECOND SCREEN
    WIDE OR SURROUND DISPLAY
    PORTABLE LINK GAMEPLAY
    CABINET CONTROL COMPATIBILITY
    PLAYER COUNT COMPATIBILITY
    BUTTON COMPATIBILITY

    Les modes PREFER ne sont plus exposes. Les choix visibles sont des modes
    de filtrage explicites : AUTO, ONLY, HIDE, ONLY COMPATIBLE ou les
    variantes d'orientation.

    `SCREEN / COCKTAIL ORIENTATION` pilote aussi les bornes cocktail.
    `BEZEL ORIENTATION` et `BEZEL ASPECT` restent des options de scraping,
    mais elles sont rangees ici parce qu'elles decrivent le rendu borne/ecran.
    `ARCADE TESTS AND DIAGNOSTICS` pilote aussi les location tests arcade.

    Les packs pour ROM INSTALLER et ON-THE-FLY ROM INSTALLER sont a
    deposer dans `package-installer` au format `.7z`, `.zip` ou `.rar`.

Sous-menu API SETTINGS :

    LANGUAGE PROFILE
    REGION PROFILE
    REPAIR GAMELISTS ON STARTUP
    SYNC GAMELISTS WITH SYSTEM LANGUAGE
    AFFICHER LE SPLASHSCREEN API
    AFFICHER LES NOTIFICATIONS TOAST
    AFFICHER LES NOTIFICATIONS API
    AFFICHER LES BARRES DE PROGRESSION TOAST
    ACTIVER SWAGGER
    ACTIVER LE FLUX WEBSOCKET

Les cles techniques restent stables et explicites :

    global.apiexpose.enabled
    global.apiexpose.local_media_manager.enabled
    global.apiexpose.local_media_manager.populate_all_requested
    global.apiexpose.media_allocation.image_source
    global.apiexpose.media_allocation.logo_source
    global.apiexpose.media_allocation.thumb_source
    global.apiexpose.media_allocation.wheel_style
    global.apiexpose.media_allocation.region_mode
    global.apiexpose.media_allocation.user_region
    global.apiexpose.api.region_profile
    global.apiexpose.api.language_profile
    global.apiexpose.api.repair_gamelists_on_startup
    global.apiexpose.api.sync_gamelists_with_system_language
    global.apiexpose.scraping.auto_enabled
    global.apiexpose.scraping.screenscraper.enabled
    global.apiexpose.scraping.description_translation.enabled
    global.apiexpose.scraping.queue.enabled
    global.apiexpose.scraping.remote_after_local_only
    global.apiexpose.scraping.exact_local_media.enabled
    global.apiexpose.scraping.refresh_current_after_success
    global.apiexpose.scraping.notify_media.enabled
    global.apiexpose.scraping.marquee.enabled
    global.apiexpose.scraping.screen_marquee.enabled
    global.apiexpose.scraping.screen_marquee_small.enabled
    global.apiexpose.scraping.steamgrid.enabled
    global.apiexpose.scraping.mix.enabled
    global.apiexpose.scraping.maps.enabled
    global.apiexpose.scraping.manuals.enabled
    global.apiexpose.scraping.magazines.enabled
    global.apiexpose.scraping.videos.enabled
    global.apiexpose.scraping.video_normalized.enabled
    global.apiexpose.scraping.bezels.enabled
    global.apiexpose.scraping.bezel_aspect
    global.apiexpose.scraping.bezel_orientation
    global.apiexpose.datas_theme_expose.enabled
    global.apiexpose.datas_theme_expose.high_score.enabled
    global.apiexpose.datas_theme_expose.cpo_control_panel.enabled
    global.apiexpose.datas_theme_expose.cpo.websocket_push.enabled
    global.apiexpose.datas_theme_expose.cpo.general_panel_buttons
    global.apiexpose.marquee_manager.enabled
    global.apiexpose.marquee_manager.websocket_assets.enabled
    global.apiexpose.marquee_manager.autogen_profile
    global.apiexpose.marquee_manager.autogen_notify.enabled
    global.apiexpose.rom_set_manager.enabled
    global.apiexpose.game_events_manager.enabled
    global.apiexpose.romset.defaults_initialized
    global.apiexpose.romset.profile
    global.apiexpose.romset.never_hide_favorites
    global.apiexpose.romset.ra_mode
    global.apiexpose.romset.language_mode
    global.apiexpose.romset.region_mode
    global.apiexpose.romset.rom_version
    global.apiexpose.romset.clones_mode
    global.apiexpose.romset.prototypes_mode
    global.apiexpose.romset.demos_mode
    global.apiexpose.romset.beta_alpha_mode
    global.apiexpose.romset.location_tests_mode (compatibilite legacy)
    global.apiexpose.romset.useful_patches_mode
    global.apiexpose.romset.hacks_mods_mode
    global.apiexpose.romset.cheats_trainers_mode
    global.apiexpose.romset.bootlegs_pirates_mode
    global.apiexpose.romset.unlicensed_mode
    global.apiexpose.romset.homebrews_aftermarket_mode
    global.apiexpose.romset.adult_mode
    global.apiexpose.romset.casino_mode
    global.apiexpose.romset.mahjong_mode
    global.apiexpose.romset.quiz_mode
    global.apiexpose.romset.non_games_mode
    global.apiexpose.romset.arcade_diagnostics_mode
    global.apiexpose.romset.screen_orientation
    global.apiexpose.romset.cocktail_games (compatibilite legacy)
    global.apiexpose.romset.multi_screen_games
    global.apiexpose.romset.functional_second_screen
    global.apiexpose.romset.wide_surround_display
    global.apiexpose.romset.portable_link_gameplay
    global.apiexpose.romset.cabinet_controls_compatibility
    global.apiexpose.romset.player_count
    global.apiexpose.romset.button_compatibility
    global.apiexpose.romset.only_retroachievements
    global.apiexpose.romset.show_clones
    global.apiexpose.romset.show_prototypes
    global.apiexpose.romset.show_bootlegs_hacks
    global.apiexpose.romset.show_adult
    global.apiexpose.romset.show_casino
    global.apiexpose.romset.show_mahjong
    global.apiexpose.romset.show_non_games
    global.apiexpose.romset.show_non_arcade
    global.apiexpose.romset.show_horizontal
    global.apiexpose.romset.show_vertical
    global.apiexpose.romset.variant_mode
    global.apiexpose.romset.translations
    global.apiexpose.romset.arcade_handling
    global.apiexpose.startup_overlay.enabled
    global.apiexpose.toast_notifications.enabled
    global.apiexpose.api_notifications.enabled
    global.apiexpose.task_progress.enabled
    global.apiexpose.swagger.enabled
    global.apiexpose.websocket.enabled

Important vocabulaire :

    Notifications toast = overlays Windows APIExpose affiches au-dessus de ES.
    Notifications API = notifications natives poussees vers l'API EmulationStation
    sur le port 1234 (/notify). C'est un canal separe qui ne doit pas etre
    confondu avec les toasts APIExpose.

Ce choix permet d'avoir un libelle utilisateur propre dans ES tout en gardant
un namespace technique clair pour APIExpose.

Principe global
---------------

La chaine attendue est :

    es_features.cfg
    -> menu visible dans RetroBat / ES
    -> es_settings.cfg
    -> lecteur C# APIExpose
    -> options runtime APIExpose

APIExpose ne doit pas dependre du menu pour fonctionner. Si aucune cle
`global.apiexpose.*` n'existe dans `es_settings.cfg`, l'API utilise ses valeurs
normales de `appsettings.json`.

Pour eviter une divergence visuelle dans RetroBat, APIExpose initialise aussi
au demarrage les cles `global.apiexpose.*` manquantes dans `es_settings.cfg`
avec les valeurs par defaut de `appsettings.json`. Ainsi, un switch absent ne
s'affiche pas faussement sur OFF alors que le runtime le considere ON.

Ordre de resolution
-------------------

`appsettings.json` reste la configuration de base du plugin.

Les valeurs lues depuis `es_settings.cfg` sont des overrides utilisateur.

Ordre logique :

    1. appsettings.json
    2. global.apiexpose.*
    3. <system>.apiexpose.* si une option systeme existe et si un systeme est courant

Autrement dit :

    appsettings.json = base
    global.apiexpose.* = surcharge globale depuis RetroBat
    <system>.apiexpose.* = surcharge systeme depuis RetroBat

Exemple actuel :

    ApiExpose.MediaAllocation.LogoSource = logo dans appsettings.json
    global.apiexpose.media_allocation.logo_source = logo

Resultat :

    la balise visible <marquee> est realignee sur la source media logo/wheel
    par defaut. Elle ne pointe vers le vrai media marquee canonique que si
    LOGO / MARQUEE est regle explicitement sur MARQUEE.

Format reel de es_settings.cfg
------------------------------

RetroBat ecrit les settings au format XML.

Exemple observe :

    <string name="global.apiexpose.enabled" value="1" />

Le lecteur APIExpose ne doit donc pas partir sur un format `key=value`.
Il doit parser le XML et lire les attributs :

    name
    value

Les types peuvent etre :

    <string name="..." value="..." />
    <bool name="..." value="true" />
    <int name="..." value="..." />

Pour les options de type switch, RetroBat peut ecrire `1`, `0`, `true`,
`false`, `on`, `off`. Le lecteur doit accepter ces variantes.

Fichiers RetroBat observes
--------------------------

Fichier courant des features :

    E:\RetroBat\emulationstation\.emulationstation\es_features.cfg

Fichier courant des settings :

    E:\RetroBat\emulationstation\.emulationstation\es_settings.cfg

Locales :

    E:\RetroBat\emulationstation\es_features.locale\fr\es-features.po
    E:\RetroBat\emulationstation\es_features.locale\en_GB\es-features.po

Le fragment source APIExpose utilise des `msgid` anglais canoniques. Les
libelles visibles en francais sont fournis par :

    resources/config-ESmenus/locales/fr/es-features.po

APIExpose merge ce fichier dans le dossier `es_features.locale` au demarrage.

Premiere implementation racine
------------------------------

La premiere implementation doit valider :

    menu ES
    -> ecriture dans es_settings.cfg
    -> lecture par APIExpose
    -> comportement observable

Options racine :

    global.apiexpose.enabled
    global.apiexpose.local_media_manager.enabled
    global.apiexpose.media_allocation.image_source
    global.apiexpose.media_allocation.logo_source
    global.apiexpose.media_allocation.thumb_source
    global.apiexpose.media_allocation.wheel_style
    global.apiexpose.scraping.auto_enabled
    global.apiexpose.datas_theme_expose.enabled
    global.apiexpose.datas_theme_expose.high_score.enabled
    global.apiexpose.datas_theme_expose.cpo_control_panel.enabled
    global.apiexpose.datas_theme_expose.cpo.websocket_push.enabled
    global.apiexpose.datas_theme_expose.cpo.general_panel_buttons
    global.apiexpose.marquee_manager.enabled
    global.apiexpose.marquee_manager.websocket_assets.enabled
    global.apiexpose.rom_set_manager.enabled
    global.apiexpose.game_events_manager.enabled

Options du sous-menu ROMS MANAGER :

    global.apiexpose.romset.never_hide_favorites
    global.apiexpose.romset.only_retroachievements
    global.apiexpose.romset.show_clones
    global.apiexpose.romset.show_prototypes
    global.apiexpose.romset.show_bootlegs_hacks
    global.apiexpose.romset.show_adult
    global.apiexpose.romset.show_casino
    global.apiexpose.romset.show_mahjong
    global.apiexpose.romset.show_non_games
    global.apiexpose.romset.show_non_arcade
    global.apiexpose.romset.show_horizontal
    global.apiexpose.romset.show_vertical

Options du sous-menu PARAMETRES API :

    global.apiexpose.startup_overlay.enabled
    global.apiexpose.toast_notifications.enabled
    global.apiexpose.api_notifications.enabled
    global.apiexpose.task_progress.enabled
    global.apiexpose.swagger.enabled
    global.apiexpose.websocket.enabled

Commande du sous-menu LOCAL MEDIA MANAGER :

    global.apiexpose.local_media_manager.populate_all_requested

    ES ne propose pas de bouton d'action natif dans es_features.cfg.
    Cette option est donc un switch surveille : chaque changement de valeur
    detecte dans `es_settings.cfg` lance la mise a jour locale des gamelists.
    APIExpose affiche une confirmation via l'API ES `/messagebox`,
    lance l'action, puis laisse la valeur telle quelle. RetroBat/ES ne relit
    cette valeur qu'au demarrage de son interface.

Fragments cibles dans <sharedFeatures> :

    <feature name="ACTIVER L'API EXPOSE"
             value="global.apiexpose.enabled"
             description="Active ou desactive les fonctions avancees du plugin local."
             preset="switch"/>

    <feature name="ACTIVER LE LOCAL MEDIA MANAGER"
             value="global.apiexpose.local_media_manager.enabled"
             preset="switch"/>

    <feature name="Mettre Ã  jour les mÃ©dias"
             value="global.apiexpose.local_media_manager.populate_all_requested"
             preset="switch"/>

    <feature name="IMAGE PRINCIPALE"
             value="global.apiexpose.media_allocation.image_source"/>

    <feature name="LOGO / MARQUEE"
             value="global.apiexpose.media_allocation.logo_source"/>

    <feature name="VIGNETTE"
             value="global.apiexpose.media_allocation.thumb_source"/>

    <feature name="STYLE WHEEL HD"
             value="global.apiexpose.media_allocation.wheel_style"/>

    <feature name="ACTIVER L'AUTO SCRAPING MANAGER"
             value="global.apiexpose.scraping.auto_enabled"
             preset="switch"/>

    <feature name="ACTIVER THEMES MANAGER"
             value="global.apiexpose.datas_theme_expose.enabled"
             preset="switch"/>

    <feature name="ACTIVER HIGH SCORE EXPOSE"
             value="global.apiexpose.datas_theme_expose.high_score.enabled"
             preset="switch"/>

    <feature name="ENABLE CONTROL PANEL DISPLAY"
             value="global.apiexpose.datas_theme_expose.cpo_control_panel.enabled"
             preset="switch"/>

    <feature name="PUSH CONTROL PANEL TO WS STREAM"
             value="global.apiexpose.datas_theme_expose.cpo.websocket_push.enabled"
             preset="switch"/>

    <feature name="ACTIVER MARQUEE MANAGER"
             value="global.apiexpose.marquee_manager.enabled"
             preset="switch"/>

    <feature name="ACTIVER LE ROMS MANAGER"
             value="global.apiexpose.rom_set_manager.enabled"
             preset="switch"/>

    <feature name="ACTIVER LE GAME EVENTS MANAGER"
             value="global.apiexpose.game_events_manager.enabled"
             preset="switch"/>

    <feature name="AFFICHER LE SPLASHSCREEN API"
             value="global.apiexpose.startup_overlay.enabled"
             preset="switch"/>

    <feature name="AFFICHER LES NOTIFICATIONS TOAST"
             value="global.apiexpose.toast_notifications.enabled"
             preset="switch"/>

    <feature name="AFFICHER LES NOTIFICATIONS API"
             value="global.apiexpose.api_notifications.enabled"
             preset="switch"/>

    <feature name="AFFICHER LES BARRES DE PROGRESSION TOAST"
             value="global.apiexpose.task_progress.enabled"
             preset="switch"/>

    <feature name="ACTIVER SWAGGER"
             value="global.apiexpose.swagger.enabled"
             preset="switch"/>

    <feature name="ACTIVER LE FLUX WEBSOCKET"
             value="global.apiexpose.websocket.enabled"
             preset="switch"/>

Fragments cibles dans <globalFeatures> :

    <sharedFeature group="OPTIONS ETENDUES"
                   submenu=""
                   value="global.apiexpose.enabled"
                   order="900"/>

    Les autres entrees de modules sont rangees dans leur propre sous-menu :

    <sharedFeature group="OPTIONS ETENDUES"
                   submenu="LOCAL MEDIA MANAGER"
                   value="global.apiexpose.local_media_manager.enabled"
                   order="910"/>

    <sharedFeature group="OPTIONS ETENDUES"
                   submenu="LOCAL MEDIA MANAGER"
                   value="global.apiexpose.media_allocation.image_source"
                   order="911"/>

    <sharedFeature group="OPTIONS ETENDUES"
                   submenu="LOCAL MEDIA MANAGER"
                   value="global.apiexpose.media_allocation.logo_source"
                   order="912"/>

    <sharedFeature group="OPTIONS ETENDUES"
                   submenu="LOCAL MEDIA MANAGER"
                   value="global.apiexpose.media_allocation.thumb_source"
                   order="913"/>

    <sharedFeature group="OPTIONS ETENDUES"
                   submenu="LOCAL MEDIA MANAGER"
                   value="global.apiexpose.media_allocation.wheel_style"
                   order="914"/>

    <sharedFeature group="OPTIONS ETENDUES"
                   submenu="LOCAL MEDIA MANAGER"
                   value="global.apiexpose.media_allocation.region_mode"
                   order="916"/>

    <sharedFeature group="OPTIONS ETENDUES"
                   submenu="LOCAL MEDIA MANAGER"
                   value="global.apiexpose.media_allocation.logo_region_mode"
                   order="917"/>

    MEDIA REGION MODE pilote les medias originaux : region de la ROM par defaut,
    profil region central ou langue de l'interface. LOGO / WHEEL LOCALIZATION
    reste separe car le logo sert au choix du jeu et favorise par defaut une
    langue lisible par l'utilisateur.

    <sharedFeature group="OPTIONS ETENDUES"
                   submenu="ROMS MANAGER"
                   value="global.apiexpose.rom_set_manager.enabled"
                   order="950"/>

    <sharedFeature group="OPTIONS ETENDUES"
                   submenu="ROMS MANAGER"
                   value="global.apiexpose.romset.never_hide_favorites"
                   order="951"/>

    <sharedFeature group="OPTIONS ETENDUES"
                   submenu="ROMS MANAGER"
                   value="global.apiexpose.romset.only_retroachievements"
                   order="952"/>

    <sharedFeature group="OPTIONS ETENDUES"
                   submenu="ROMS MANAGER"
                   value="global.apiexpose.romset.show_clones"
                   order="953"/>

    Les parametres utilisateur de l'API sont ranges dans le sous-menu :

    <sharedFeature group="OPTIONS ETENDUES"
                   submenu="PARAMETRES API"
                   value="global.apiexpose.swagger.enabled"
                   order="973"/>

Comportement attendu :

    global.apiexpose.enabled=1
        APIExpose fonctionne normalement.

    global.apiexpose.enabled=0
        APIExpose reste joignable pour les endpoints vitaux de diagnostic,
        mais les fonctions automatiques et intrusives sont coupees.

Il ne faut pas arreter brutalement le serveur HTTP quand l'option vaut 0.
Sinon l'utilisateur ne peut plus diagnostiquer ni reactiver proprement depuis
l'API. L'option doit plutot desactiver les comportements :

    - projection locale automatique sur game-selected ;
    - overlays/toasts non essentiels ;
    - controle ES automatique ;
    - futurs scrapes automatiques ;
    - operations de fond non critiques.

Les endpoints de status, health, swagger et maintenance de base peuvent rester
disponibles.

Mapping initial C#
-----------------

Cle RetroBat :

    global.apiexpose.enabled

Option runtime cible :

    ApiExpose.Enabled

Autres mappings racine :

    global.apiexpose.local_media_manager.enabled
    -> ApiExpose.LocalMediaManager.Enabled

    global.apiexpose.media_allocation.image_source
    -> ApiExpose.MediaAllocation.ImageSource puis <image> dans les gamelists

    global.apiexpose.media_allocation.logo_source
    -> ApiExpose.MediaAllocation.LogoSource puis <marquee> dans les gamelists

    global.apiexpose.media_allocation.thumb_source
    -> ApiExpose.MediaAllocation.ThumbSource puis <thumbnail> dans les gamelists

    global.apiexpose.media_allocation.wheel_style
    -> ApiExpose.MediaAllocation.WheelStyle quand logo_source vaut wheel-hd

    global.apiexpose.scraping.auto_enabled
    -> ApiExpose.Scraping.AutoScrapingEnabled

    global.apiexpose.scraping.description_translation.enabled
    -> ApiExpose.Scraping.DescriptionTranslationEnabled. Active la traduction
       locale `desc` apres ScreenScraper quand la langue courante n'a pas de
       description et qu'une source anglaise existe.

    Les cles `global.apiexpose.scraping.*` pilotent le nouveau moteur distant
    local-first. Elles ne doivent pas piloter la reallocation visible des
    gamelists. Les choix image/logo/thumb/wheel sont centralises dans
    `global.apiexpose.media_allocation.*`.

    global.apiexpose.scraping.queue.enabled
    -> active la file de scraping basse priorite. Elle complete les medias
       restants uniquement quand aucun scrap live ni navigation recente n'est
       actif.

    global.apiexpose.scraping.maps.enabled / manuals.enabled / videos.enabled / video_normalized.enabled
    -> autorisent explicitement les documents ou gros fichiers distants. Par defaut false.

    global.apiexpose.scraping.bezels.enabled / bezel_aspect / bezel_orientation
    -> activent et cadrent le scraping des bezels : format 16:9/4:3 et
       orientation horizontal/vertical/cocktail. Seule la region peut fallback.

    Quand ces choix changent, APIExpose les recopie aussi vers les anciennes
    cles ES `ScrapperImageSrc`, `ScrapperLogoSrc`, `ScrapperThumbSrc` et
    `WheelStyle`. Cela garde le scraper manuel RetroBat/ES aligne avec les
    choix du Local Media Manager.

    global.apiexpose.datas_theme_expose.enabled
    -> ApiExpose.DatasThemeExpose.Enabled

    global.apiexpose.datas_theme_expose.high_score.enabled
    -> ApiExpose.DatasThemeExpose.HighScoreExposeEnabled

    global.apiexpose.datas_theme_expose.cpo_control_panel.enabled
    -> ApiExpose.DatasThemeExpose.CpoControlPanelExposeEnabled

    global.apiexpose.datas_theme_expose.cpo.websocket_push.enabled
    -> publication WebSocket `cpo.panel.config.selected` depuis resources/dynpanels

    global.apiexpose.marquee_manager.enabled
    -> ApiExpose.MarqueeManager.Enabled

    global.apiexpose.marquee_manager.websocket_assets.enabled
    -> cible future de publication WebSocket marquee/logo/fanart

    global.apiexpose.marquee_manager.autogen_profile
    -> ApiExpose.MarqueeManager.AutogenProfile

    global.apiexpose.marquee_manager.autogen_notify.enabled
    -> ApiExpose.MarqueeManager.AutogenNotifyEnabled

    global.apiexpose.rom_set_manager.enabled
    -> ApiExpose.RomSetManager.Enabled

    global.apiexpose.game_events_manager.enabled
    -> ApiExpose.RetroArchWrapperDeployment.Enabled et ecoute runtime des evenements ingame

    global.apiexpose.romset.only_retroachievements/show_clones/prototypes/etc.
    -> Roms Manager : ecrit ou restaure les balises <hidden> dans les gamelists

    Apres initialisation, une cle show_* absente vaut false. RetroBat peut
    supprimer la cle quand un switch AFFICHER est coupe, au lieu d'ecrire 0.

    global.apiexpose.romset.variant_mode/region_profile/language_profile/translations/arcade_handling
    -> Roms Manager : calcule la meilleure variante visible par groupe de ROMs.
       display_only produit un rapport Swagger sans masquer; hide_variants masquera
       les variantes non retenues quand ce mode sera valide. Ces options sont
       exposees dans le sous-menu ROMS MANAGER.

    global.apiexpose.startup_overlay.enabled
    -> ApiExpose.StartupOverlay.Enabled

    global.apiexpose.toast_notifications.enabled
    -> ApiExpose.Toasts.Enabled

    global.apiexpose.api_notifications.enabled
    -> ApiExpose.ApiNotifications.Enabled

    global.apiexpose.task_progress.enabled
    -> ApiExpose.TaskProgress.Enabled

    global.apiexpose.swagger.enabled
    -> ApiExpose.Swagger.Enabled

    global.apiexpose.websocket.enabled
    -> ApiExpose.WebSocket.Enabled

Puis creer un service de resolution runtime, par exemple :

    EmulationStationMenuSettingsService
    ApiExposeRuntimeOptionsService

Responsabilites :

    - charger `es_settings.cfg` au demarrage ;
    - surveiller ses modifications ;
    - exposer `IsApiExposeEnabled` ;
    - appliquer les overrides sur les services qui doivent respecter l'option ;
    - journaliser les cles lues en debug.

Etat actuel :

    GET /api/v1/Config/local-options

    expose deja les valeurs `appsettings.json`, les overrides
    `es_settings.cfg` et la valeur effective pour les interrupteurs racine et
    le sous-menu PARAMETRES API.

    Swagger :

        http://127.0.0.1:12345/swagger/index.html

    Flux WebSocket :

        ws://127.0.0.1:12345/ws

    Test notification toast :

        POST /api/v1/toast-notifications

    Test notification API ES native :

        POST /api/v1/es-notifications
        POST /api/v1/es-notifications/messagebox

    Les options Swagger, WebSocket, notifications toast et notifications API
    sont appliquees au
    runtime. Si Swagger est coupe, l'URL /swagger repond 404, mais l'API locale
    continue de fonctionner normalement.

    L'ecouteur d'evenements ingame est applique au demarrage : s'il est coupe,
    l'API ne lance pas l'ecoute ingame ni le provider de hi-scores console.

    Le ROMS Manager applique ses filtres via :

        GET  /api/v1/rom-set-manager/options
        POST /api/v1/rom-set-manager/audit
        POST /api/v1/rom-set-manager/apply
        POST /api/v1/rom-set-manager/restore

    Les filtres sont tous actifs par defaut. Desactiver un filtre masque les
    ROMs concernees avec <hidden>true</hidden>. Si RetroBat retire une cle
    show_* quand elle est coupee, APIExpose l'interprete comme false grace au
    marqueur global.apiexpose.romset.defaults_initialized. APIExpose ajoute
    aussi des balises apiexpose_romset_* pour restaurer uniquement ses propres masquages.
    Par defaut, global.apiexpose.romset.never_hide_favorites est actif : un jeu
    avec <favorite>true</favorite> n'est jamais masque par les filtres Roms
    Manager, meme s'il est clone ou sans cheevosId. Si une ancienne passe
    APIExpose l'avait masque, la passe suivante restaure ses balises hidden.
    Quand une option Roms Manager change dans es_settings.cfg, APIExpose applique
    automatiquement les filtres sur les gamelists, affiche une barre de
    progression toast, puis planifie un reloadgames si une gamelist a change.
    Le filtre RetroAchievements s'appuie sur <cheevosId> : si
    global.apiexpose.romset.only_retroachievements est active, un jeu sans
    cheevosId numerique strictement positif est masque avec la raison
    no-retroachievements. Si l'option est inactive, tous les jeux restent
    visibles. <cheevosHash> seul ne suffit pas, car il peut seulement indiquer
    un hash calcule sans correspondance RetroAchievements.
    En mode display_only, les decisions de variantes sont exposees dans le
    champ VariantDecisions du retour d'audit/apply, sans provoquer de masquage
    supplementaire.
    La resolution normalise les tags courts courants des ROMs, par exemple
    (U)/(E)/(J)/(W) vers USA/Europe/Japan/World, pour rapprocher les fichiers
    installes des noms canoniques issus de system_groups.
    Quand AFFICHER LES CLONES ET VARIANTES est coupe, APIExpose complete aussi
    system_groups avec une detection locale des doublons de gamelist : tags
    region/langue/qualite retires, traductions reconnues, et prefixes numeriques
    forts comme 1943 regroupes pour garder une seule variante visible.

    Les options AUTO SCRAPING MANAGER sont appliquees par `RemoteScrapingService` pour
    filtrer ce qui peut entrer dans la future queue provider-based.
    La connexion ScreenScraper est resolue par `ScreenScraperConnectionService` :
    utilisateur/mot de passe depuis les settings scraper ES, credentials
    developpeur depuis variables d'environnement, appsettings ou bundle valide.
    Les secrets ne doivent jamais apparaitre dans Swagger ni dans les logs.

    Les options encore marquees `declared-not-enforced` seront branchees module
    par module.

Lecture a chaud
---------------

APIExpose doit relire `es_settings.cfg` :

    - au demarrage ;
    - quand le fichier change ;
    - eventuellement apres un reloadgames ou une reallocation ES.

La surveillance doit etre prudente :

    - debounce court pour eviter les lectures pendant ecriture ;
    - FileShare.ReadWrite ;
    - retry court en cas de verrou ;
    - aucun crash si le XML est temporairement invalide.

Injection de es_features.cfg
----------------------------

Deux strategies sont possibles.

1. Mode manuel pour le premier test

    On prepare un fragment dans `resources/config-ESmenus`.
    On l'integre manuellement dans une copie de test de `es_features.cfg`.
    On valide que RetroBat affiche le menu et ecrit la cle.

2. Mode automatique actuel

    APIExpose installe au demarrage un bloc marque dans `es_features.cfg`
    quand `ApiExpose:EsFeaturesMenu:InstallOnStartup=true`.

    Pour l'implementation courante, il nettoie les anciennes entrees
    `global.apiexpose.*` deja presentes dans `<sharedFeatures>` et
    `<globalFeatures>`, puis installe le groupe OPTIONS ETENDUES avec ses
    sous-menus : Local Media Manager, Auto Scraping Manager, Themes Manager,
    Control Panel Manager, Marquee Manager, Roms Manager, Game Events Manager et
    Parametres API.

    sous le groupe visible :

        OPTIONS ETENDUES

    Le bloc doit etre encadre par des marqueurs :

        <!-- APIEXPOSE:BEGIN -->
        ...
        <!-- APIEXPOSE:END -->

    Cela permet de mettre a jour proprement uniquement ce que le plugin ajoute.

    Analyse des types es_features utiles :

    - `preset="switch"` convient aux activations simples et aux actions
      surveillees par APIExpose quand le changement de valeur suffit.
    - `preset="slider"` reste possible pour des valeurs numeriques, mais n'est
      plus utilise pour declencher les actions APIExpose.
    - `preset="input"` est un champ texte libre, utile pour une URL ou un chemin,
      mais pas pour lancer une action.
    - les fichiers `es_features.locale` ne definissent que les traductions ;
      ils ne changent pas les capacites du menu.
    - aucun type bouton/action/commande natif n'a ete trouve dans les
      es_features RetroBat consultes. Le code source consulte lit `preset`,
      `preset-parameters`, `group`, `submenu`, `order`, `value` et les `choice`,
      mais ne lit pas `type` ni `command`.
    - le banc d'essai `RECHARGER APIEXPOSE` n'a rien donne de probant et a ete
      retire. Les actions APIExpose passent donc par un switch surveille, sans
      reecriture automatique de la valeur par l'API.

    Les fichiers YAML trouves dans RetroBat concernent surtout les shaders, les
    mappings input et quelques templates emulateurs. Ils ne semblent pas piloter
    directement les entrees `es_features.cfg`.

Important :

    APIExpose ne doit pas nettoyer le menu a chaque fermeture tant que RetroBat
    peut encore utiliser `es_features.cfg`. Le nettoyage automatique a la
    fermeture est risque si l'API plante ou si ES est encore ouvert.

    La desinstallation complete devra plutot etre une action explicite :

        POST /api/v1/Maintenance/es-features/uninstall

Options futures
---------------

Une fois les options racine validees, on pourra ajouter :

Globales :

    global.apiexpose.startup_overlay.enabled
    global.apiexpose.toast_notifications.enabled
    global.apiexpose.api_notifications.enabled
    global.apiexpose.task_progress.enabled
    global.apiexpose.es_controller.enabled
    global.apiexpose.es_controller.restore_selection_after_reload
    global.apiexpose.logging.es_flow_logs

Par systeme :

    <system>.apiexpose.local_media.enabled
    <system>.apiexpose.media_allocation.logo_source
    <system>.apiexpose.media_allocation.image_source
    <system>.apiexpose.media_allocation.thumb_source

Les options fines du futur scraper distant seront definies plus tard. Elles ne
doivent pas reprendre les anciennes options media de reallocation visible.

Attention : les options de scraping distant ne doivent pas reactiver l'ancien
module ScreenScraper archive. Elles serviront au futur moteur de scraping.

Locales
-------

Le dossier :

    E:\RetroBat\emulationstation\es_features.locale

contient les traductions des libelles de `es_features.cfg`.

Pour une integration propre, les libelles doivent etre ajoutes aux fichiers
`.po`, par exemple :

    msgctxt "game_options"
    msgid "EXTENDED OPTIONS"
    msgstr "OPTIONS ETENDUES"

    msgctxt "game_options"
    msgid "ENABLE API EXPOSE"
    msgstr "ACTIVER L'API EXPOSE"

Le fragment source APIExpose est deja en `msgid` anglais canonique. Les
libelles visibles sont fournis par les fichiers source
`resources/config-ESmenus/locales/<locale>/es-features.po`, puis injectes dans
les fichiers RetroBat cibles au demarrage. Chaque nouveau `msgid` visible doit
donc recevoir un `msgstr` dans toutes les locales supportees, pas seulement
dans `fr`.

Implementation APIExpose :

    Les fichiers source sont ranges dans :

        resources/config-ESmenus/locales/<locale>/es-features.po

    Au demarrage, si `ApiExpose:EsFeaturesMenu:LocaleDeploymentEnabled=true`,
    APIExpose merge chaque fichier source dans :

        E:\RetroBat\emulationstation\es_features.locale\<locale>\es-features.po

    Le bloc injecte est encadre par :

        # APIEXPOSE:BEGIN
        ...
        # APIEXPOSE:END

    Le nettoyage de fin de session retire uniquement ce bloc. Les traductions
    RetroBat existantes restent intactes.

Resume
------

La cible projet est :

    appsettings.json fournit les valeurs de base ;
    es_features.cfg expose des options dans RetroBat ;
    es_settings.cfg stocke les choix utilisateur ;
    APIExpose lit es_settings.cfg en XML ;
    les cles global.apiexpose.* surchargent la base ;
    les cles <system>.apiexpose.* surchargent le global quand pertinent.

Premier jalon :

    ajouter les grands interrupteurs de modules
    sous le groupe visible OPTIONS ETENDUES
    puis faire respecter progressivement ces options par les comportements
    runtime.





