# Scraping Reference Data

Ces fichiers sont des referentiels internalises utilises par `APIExpose` pour son moteur de scrap.

Principes :

- `APIExpose` ne depend plus de `projects-source/scrape-master` a l'execution ;
- ces fichiers servent de bootstrap de normalisation uniquement ;
- la logique de scrap et de projection reste propre au code `APIExpose` ;
- les donnees pourront ensuite etre nettoyees, reduites ou remplacees par des formats natifs.

Sources d'origine :

- corpus de reference extrait initialement de ARRM, puis copie localement dans `resources/scraping/reference/`

Fichiers conserves pour la V1 :

- `systems_screenscraper.txt`
- `crc_no_calcul.txt`
- `arcade_systems_list.txt`
- `systems_as_folder.txt`
- `folder_search_depth_per_system.txt`
- `remove_list_arcade_bios.txt`
- `custom_games_names.txt`
