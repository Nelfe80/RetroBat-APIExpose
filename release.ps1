# release.ps1 - Construit et publie une release APIExpose sur GitHub.
# Usage :
#   .\release.ps1                # construit les archives + release DRAFT
#   .\release.ps1 -Publish      # publie directement (sans draft)
#   .\release.ps1 -PackageOnly  # construit seulement les archives
#   .\release.ps1 -SansInstalleur  # publie sans l'installeur de borne (voir plus bas)
#   .\release.ps1 -Rapide -Publish  # programme seul (update.7z) : les bornes a jour en deux minutes
#
# QUAND PRENDRE LE MODE RAPIDE (regle user 2026-09-27) : des qu'on n'a modifie que le programme
# (src, wrapper, .installer). Les bornes ne lisent que -update.7z et SHA256SUMS.txt de la derniere
# release ; le full.7z (Data Pack complet, 250 Mo) et l'installeur ne servent qu'aux premieres
# installations, et coutaient a eux seuls 15 a 40 minutes par release. Le site sert l'installeur
# de la derniere release COMPLETE, qui se met a jour tout seul au premier lancement. Release
# complete quand l'installeur change (installer\) ou quand son Data Pack a trop vieilli.
param(
    [switch]$Publish,
    [switch]$PackageOnly,
    [switch]$SansInstalleur,
    [switch]$Rapide
)
$ErrorActionPreference = 'Stop'
$sz = @('C:\Program Files\7-Zip\7z.exe','C:\Program Files (x86)\7-Zip\7z.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $sz) { throw '7-Zip introuvable.' }

$root = Split-Path $PSScriptRoot -Parent   # ...\plugins
$name = Split-Path $PSScriptRoot -Leaf     # APIExpose
$exe  = Join-Path $PSScriptRoot 'RetroBat.Api.exe'
$verFull = (Get-Item $exe).VersionInfo.ProductVersion
$ver = ($verFull -split '\+')[0]
Write-Host "Version detectee : $verFull (tag v$ver)"

$out = Join-Path $PSScriptRoot "artifacts\release\v$ver"
New-Item -ItemType Directory -Force $out | Out-Null

# Exclusions communes : jamais de secrets, ROMs, sources, docs internes ni runtime.
$ex = @(
    "-x!$name\.git", "-x!$name\.gitignore", "-x!$name\.github",
    "-x!$name\.env", "-x!$name\events.ini",
    "-x!$name\.log", "-x!$name\.temp", "-x!$name\.cache",
    "-x!$name\.archive", "-x!$name\.versioning",
    "-x!$name\media", "-x!$name\package-installer", "-x!$name\projects-source",
    # Sources de curation (curator) : jamais dans le pack public, le runtime ne les lit pas.
    "-x!$name\resources\outputs", "-x!$name\resources\panels",
    # Cache des gamelists traduites : GENERE par l'API sur chaque borne (LocalizedGamelistCache,
    # PrebuildOnStartup), avec ses sauvegardes. Le livrer, c'est installer le cache d'une
    # borne chez toutes les autres, qui le reconstruisent de toute facon (239 fichiers,
    # 44 Mo, partis dans les paquets jusqu'a la 1.8.20 inclus).
    "-x!$name\resources\gamelist\localized",
    # Le proprietaire de chaque media (MediaSidecarStore) : ECRIT par l'API sur chaque borne,
    # comme le cache ci-dessus. Livre, il installait chez tout le monde les medias « geres »
    # de la borne qui fabrique la release (1.9.9, quatre systemes, vu le 2026-09-29).
    "-x!$name\resources\gamelist\media-sidecar",
    # Les sauvegardes faites a la main avant une operation (mame-banks.json.avant-nuit).
    "-xr!*.avant-*",
    # Etat de la borne ecrit par l'API avant chaque lancement (les reglages certifies forces
    # pour le jeu en cours) : gitignore, et pas plus a livrer que wrapper\.env.
    "-x!$name\wrapper\certified.txt",
    # Remaps RetroArch du core MAME : doctrine = cfg MAME uniquement (risque de
    # double remap "en resonance" si un rmp coexiste avec le cfg partage).
    "-x!$name\resources\controls\retroarch\mame",
    # Curator : savoir-faire prive, jamais distribue (wildcard : les copies de
    # travail comme panel_curator_ultimate_.py doivent aussi rester hors pack).
    "-x!$name\panel_curator*", "-x!$name\profiles_db*",
    # mem-curator : les sorties de generation, rapports et chemins locaux
    # restent hors pack (les outils eux-memes sont publics via le repo).
    # mem-curator EN ENTIER : outillage prive, retire du depot public et son
    # historique purge. On excluait fichier par fichier (MEM_*, baseline_*,
    # .source-base.local...), si bien que README.md et le plugin Lua partaient
    # dans full.7z sans que rien ne le signale. Un dossier prive s exclut par le
    # dossier, pas par enumeration de ce qu on y a vu un jour.
    "-x!$name\tools\mem-curator",
    # libretro-probe : outil de developpement (charge un core, lit sa table
    # d'entrees, enrichit les dynpanels). Il tourne ICI une fois, jamais sur une
    # borne : ce sont ses RESULTATS qui voyagent, dans le Data Pack.
    "-x!$name\tools\libretro-probe",
    "-x!$name\state",
    "-x!$name\docs", "-x!$name\src", "-x!$name\tests", "-x!$name\artifacts",
    # dist = installeur Inno compile (des centaines de Mo) ; installer = sources .iss.
    # Ni l'un ni l'autre ne va dans le pack runtime (sinon l'update.7z explose).
    "-x!$name\dist", "-x!$name\installer",
    # publish-tmp = sortie de publication temporaire (DLL + .pdb) laissee a la racine ;
    # jamais dans le pack runtime. + tout .pdb (symboles de debug, inutiles au runtime).
    "-x!$name\publish-tmp",
    "-x!$name\wiki", "-x!$name\mkdocs.yml", "-x!$name\site",
    "-x!$name\build.bat", "-x!$name\release.ps1",
    '-xr!__pycache__', '-xr!*.log', '-xr!.vs', '-xr!*.pdb',
    # .env : drapeaux locaux (le mode decouverte du wrapper, par exemple). Gitignores,
    # donc invisibles a la revue, mais bien presents sur le disque : ils partaient dans
    # l'archive et seul le controle en aval les voyait. Exclusion recursive, a la source.
    '-xr!.env',
    # Sauvegardes de travail : sans interet pour une installation, et un .bak d'exe pese
    # le poids d'un exe.
    '-xr!*.bak',
    # OUTILLAGE INTERNE : les scripts de tools/ sont du savoir-faire de curation et
    # d'exploitation (deploiement de flotte, migrations de medias, sondes de latence).
    # Ils ne sont ni dans le depot, ni distribues, et le runtime n'en lit aucun.
    '-xr!*.ps1', '-xr!*.py',
    # Binaires tiers : lourds, et fournis par l'INSTALLER, pas par les archives. Un pack
    # runtime n'a pas a redistribuer ffmpeg, ImageMagick ni translateLocally.
    "-x!$name\tools\ffmpeg", "-x!$name\tools\imagemagick", "-x!$name\tools\translateLocally",
    # resources\ra : donnees RetroAchievements personnelles de la borne (leaderboards).
    "-x!$name\resources\ra",
    # resources\ram\.user : definitions .MEM PERSONNELLES (couche perso, pre-integration
    # communautaire), gitignorees comme media\user. Le reste de resources\ram (les defs
    # publiques) part bien dans le Data Pack ; seul .user reste local.
    "-x!$name\resources\ram\.user",
    # Page de diagnostic ScreenScraper : outil local, jamais distribue. Recursive : elle
    # vit sous resources\scraping, pas a la racine.
    '-xr!ScreenScraper.html',
    # La configuration de CETTE borne ne part jamais : c'est celle du depot qui est livree
    # (voir plus bas). Elle portait l'auto-scrap active, la langue et les reglages d'essai de la
    # machine qui publie, et quiconque installait depuis le full.7z les recevait (2026-09-27).
    "-x!$name\appsettings.json"
)

# LA CONFIGURATION LIVREE EST CELLE DU DEPOT, comme pour l'installeur : relue et commitee,
# jamais celle de la borne qui publie. build-default-settings la tire de HEAD.
& (Join-Path $PSScriptRoot 'tools\build-default-settings.ps1')
$configDepot = Join-Path $PSScriptRoot 'installer\appsettings.default.json'
if (-not (Test-Path -LiteralPath $configDepot)) { throw "Configuration du depot absente : $configDepot" }

Set-Location $root
$full   = Join-Path $out "$name-$ver-full.7z"
$update = Join-Path $out "$name-$ver-update.7z"
# 7z "a" met a jour une archive existante sans retirer les entrees exclues :
# on repart toujours d'archives vierges.
Remove-Item $full, $update -Force -Confirm:$false -ErrorAction SilentlyContinue
if ($Rapide) {
    Write-Host 'Mode rapide : pas de full.7z, le programme seul.'
} else {
    Write-Host 'Construction full.7z (avec resources + tools, plusieurs minutes)...'
    & $sz a -t7z $full "$name\" @ex -mx=5 -bsp1 -bso0
}
Write-Host 'Construction update.7z...'
# LES OUTILS HORS ARCHIVE. L'outil de diagnostic (autonome, ~66 Mo) est parti avec chaque mise a
# jour de la 1.9.2 a la 1.9.15 (decision user 2026-09-25 : un joueur a jour par le self-updater ne
# l'aurait jamais recu). Il en faisait 62 Mo sur 62, et chaque borne le retelechargeait a chaque
# version, change ou pas. Depuis le 2026-10-01 (decision user) il voyage A PART : joint a la
# release seulement quand il a change, et outils.json (plus bas) dit a l'API de la borne ou le
# prendre ; elle ne telecharge que ce qui differe du sien (SelfUpdateService). Il reste dans le
# full.7z et l'installeur. Les ressources (.MEM, gamelists, plugin Lua) n'y sont pas non plus :
# elles arrivent par le Data Pack.
$outilsHorsArchive = @('RetroBat.Api.Diagnostic.exe')
$exclusOutils = @($outilsHorsArchive | ForEach-Object { "-x!$name\$_" })
& $sz a -t7z $update "$name\" @ex "-x!$name\resources" "-x!$name\tools" @exclusOutils -mx=5 -bsp0 -bso0

# La configuration du depot, sous le nom que le programme lit. L'updater ne l'ecrase jamais sur
# une borne qui a deja la sienne ; elle ne sert qu'a une premiere installation.
$scene = Join-Path $out 'scene-config'
Remove-Item $scene -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $scene $name) | Out-Null
Copy-Item -LiteralPath $configDepot -Destination (Join-Path $scene "$name\appsettings.json")
Push-Location $scene
try {
    foreach ($archive in @($full, $update)) {
        if (Test-Path -LiteralPath $archive) { & $sz a -t7z $archive "$name\appsettings.json" -bsp0 -bso0 }
    }
} finally { Pop-Location }
Remove-Item $scene -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue

# Controle anti-fuite : l'archive ne doit contenir ni .env, ni media, ni sources. En mode rapide,
# les memes controles portent sur l'update.7z, la seule archive publiee.
$controlee = if ($Rapide) { $update } else { $full }
$listing = & $sz l $controlee
# Le controle ne cherchait que les familles connues a l'epoque : tout tools\*.ps1 et
# tools\*.py, resources\ra et ScreenScraper.html sont passes au travers. Il couvre
# desormais ce que les exclusions ci-dessus retirent, pour que les deux listes se
# contredisent bruyamment si l'une d'elles derive.
$leaks = $listing | Select-String '\.env|\.bak|\.ps1$|\.py$|\\media\\|\\src\\|\\tests\\|\\docs\\|\\dist\\|package-installer|projects-source|\.git|panel_curator|profiles_db|\\resources\\ra\\|\\resources\\ram\\\.user\\|\\resources\\gamelist\\localized\\|\\resources\\gamelist\\media-sidecar\\|\.avant-|\\wrapper\\certified\.txt|ScreenScraper\.html|\\tools\\(ffmpeg|imagemagick|translateLocally|mem-curator|libretro-probe)\\'
if ($leaks) { throw "FUITE DETECTEE dans l'archive : $($leaks[0])" }
# La cle API de la borne ne doit JAMAIS etre committee/distribuee : le defaut reste vide
# (chaque borne genere la sienne au 1er run). On bloque si une valeur traine.
$appsettingsPath = $configDepot
if (Test-Path $appsettingsPath) {
    $apiKeyLeak = Select-String -Path $appsettingsPath -Pattern '"ApiKey"\s*:\s*"[^"]+"'
    if ($apiKeyLeak) { throw "FUITE : une cle API est presente dans appsettings.json (doit rester vide)." }
}
# Controle anti-fuite EN LISTE BLANCHE. Le controle ci-dessus enumere ce qu'on
# INTERDIT : il ne voit donc que ce qu'on a pense a interdire, et c'est ainsi que
# tools\mem-curator est parti dans la 1.8.1 avec un « OK » affiche. On retourne la
# question : tout fichier livre doit etre soit VERSIONNE, soit sous un chemin
# explicitement autorise. Le reste fait echouer la construction, bruyamment.
#
# Mesure du 2026-09-06 : 29 745 fichiers livres, 418 versionnes. Les 29 327 autres
# tiennent dans les treize sous-arbres ci-dessous (Data Pack, themes, panels) plus
# une poignee de fichiers de runtime. Un nouveau dossier non prevu arrete la
# release : c'est le comportement voulu, on decide alors en connaissance de cause.
$autorises = @(
    'resources/ram/', 'resources/theme/', 'resources/dynpanels/', 'resources/controls/',
    'resources/gamelist/', 'resources/startup-overlay/', 'resources/config-ESmenus/',
    'resources/scraping/', 'resources/iccards/', 'resources/colors/', 'resources/history/',
    'resources/command/', 'resources/locales/', 'tools/mem-explorer/',
    # Le verificateur de score (APIExposeOCR), construit depuis son propre depot et
    # depose ici par APIExposeOCR\tools\release.ps1, comme mem-explorer.
    'tools/score-verifier/',
    'RetroBat.Api.exe', 'RetroBat.Api.deps.json', 'RetroBat.Api.runtimeconfig.json',
    'RetroBat.Api.xml', 'web.config', 'tools/listen_api_ws.README.md',
    # Le self-updater, a la racine comme l'API (voir src/RetroBat.Api.Update).
    'RetroBat.Api.Update.exe',
    # L'outil de diagnostic du demarrage, construit par le depot prive APIExposeDiagnostic.
    'RetroBat.Api.Diagnostic.exe'
)
$suivis = @{}
Push-Location $PSScriptRoot
& git ls-files | ForEach-Object { $suivis[$_] = $true }
Pop-Location
if ($suivis.Count -eq 0) { throw "Controle liste blanche impossible : aucun fichier versionne lu." }

# On lit Path PUIS Attributes : 7z decrit chaque entree sur plusieurs lignes, et
# les DOSSIERS y figurent aussi. Un dossier n'est pas une fuite - se fier a la
# presence d'un point dans le chemin ne marche pas, « .installer/scripts » en est
# un contre-exemple immediat.
$inconnus = @()
$candidat = $null
foreach ($ligne in (& $sz l -slt $controlee)) {
    if ($ligne -like 'Path = *') {
        $chemin = $ligne.Substring(7)
        $candidat = $null
        if ($chemin.StartsWith("$name\")) {
            $rel = $chemin.Substring($name.Length + 1).Replace('\', '/')
            if ($rel -ne '' -and -not $suivis.ContainsKey($rel)) {
                if (-not ($autorises | Where-Object { $rel -eq $_ -or $rel.StartsWith($_) })) {
                    $candidat = $rel
                }
            }
        }
        continue
    }
    if ($candidat -and $ligne -like 'Attributes = *') {
        if ($ligne -notmatch 'D') { $inconnus += $candidat }
        $candidat = $null
    }
}
if ($inconnus) {
    throw "FUITE POSSIBLE : $($inconnus.Count) fichier(s) ni versionne(s) ni autorise(s), dont $($inconnus[0])"
}
Write-Host "Controle liste blanche : OK ($($suivis.Count) versionnes + chemins autorises)"

# Tout executable applicatif livre a la racine doit etre DECLARE dans executables.manifest.json,
# avec son contrat d'auto-test : l'outil de diagnostic ne teste que ce que le manifeste nomme.
$manifestePath = Join-Path $PSScriptRoot 'executables.manifest.json'
if (-not (Test-Path -LiteralPath $manifestePath)) { throw "executables.manifest.json absent : il doit etre livre avec les executables." }
$declares = @((Get-Content -LiteralPath $manifestePath -Raw -Encoding UTF8 | ConvertFrom-Json) | ForEach-Object { $_.file })
$exesLivres = @()
foreach ($ligne in (& $sz l -slt $controlee)) {
    if ($ligne -like 'Path = *') {
        $chemin = $ligne.Substring(7)
        if ($chemin -match ('^' + [regex]::Escape("$name\") + '[^\\]+\.exe$')) { $exesLivres += (Split-Path $chemin -Leaf) }
    }
}
if ($exesLivres.Count -eq 0) { throw "Controle du manifeste impossible : aucun executable lu dans l'archive." }
$nonDeclares = @($exesLivres | Where-Object { $declares -notcontains $_ })
if ($nonDeclares) { throw "Executable(s) livre(s) mais absent(s) de executables.manifest.json : $($nonDeclares -join ', ')" }
# En mode rapide on controle l'update.7z : les outils hors archive y manquent par construction.
$horsArchive = if ($Rapide) { $outilsHorsArchive } else { @() }
$absents = @($declares | Where-Object { $exesLivres -notcontains $_ -and $horsArchive -notcontains $_ })
if ($absents) { throw "Executable(s) declare(s) mais absent(s) de l'archive : $($absents -join ', ')" }
Write-Host "Controle du manifeste des executables : OK ($($exesLivres -join ', '))"

Write-Host 'Controle anti-fuite : OK'

# Controle de contrat : le swagger doit se generer (une regression type schemaId
# = 500 silencieux, voir 1.3.1) et il est publie comme artefact versionne.
try {
    $swagger = Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:12345/swagger/v1/swagger.json' -TimeoutSec 30
} catch {
    throw "swagger.json inaccessible ($($_.Exception.Message)) - l'API de la version a packager doit tourner."
}
if ($swagger.StatusCode -ne 200) { throw "swagger.json a retourne $($swagger.StatusCode)" }
$swaggerFile = Join-Path $out 'swagger.json'
[IO.File]::WriteAllText($swaggerFile, $swagger.Content, (New-Object System.Text.UTF8Encoding($false)))
Write-Host 'Controle swagger : OK (200, artefact ecrit)'

$asyncapiSource = Join-Path $PSScriptRoot 'wiki\asyncapi.yaml'
$asyncapiFile = $null
if (Test-Path $asyncapiSource) {
    $asyncapiFile = Join-Path $out 'asyncapi.yaml'
    Copy-Item $asyncapiSource $asyncapiFile -Force

    # Controle de derive : asyncapi.yaml est SAISIE A LA MAIN alors que
    # /api/v1/ws/streams sort du code. Rien ne garantissait qu'elles disent la
    # meme chose, et c'est elle qui part avec la release. Avertissement pour
    # l'instant : on regarde d'abord ce qu'elle crie sur l'existant.
    try {
        $streams = (Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:12345/api/v1/ws/streams' -TimeoutSec 30).Content |
            ConvertFrom-Json
        $live = @($streams.Streams | ForEach-Object { $_.Name }) | Sort-Object
        $declared = @(Select-String -Path $asyncapiSource -Pattern '^\s{4}address:\s*/ws/(.+)$' |
            ForEach-Object { $_.Matches[0].Groups[1].Value.Trim() }) | Sort-Object
        $onlyLive = @($live | Where-Object { $declared -notcontains $_ })
        $onlyDoc = @($declared | Where-Object { $live -notcontains $_ })
        if ($onlyLive.Count -or $onlyDoc.Count) {
            Write-Warning "Contrat WS desynchronise entre le code et asyncapi.yaml :"
            if ($onlyLive.Count) { Write-Warning "  servis mais non documentes : $($onlyLive -join ', ')" }
            if ($onlyDoc.Count) { Write-Warning "  documentes mais non servis : $($onlyDoc -join ', ')" }
        } else {
            Write-Host "Controle contrat WS : OK ($($live.Count) canaux, code == asyncapi)"
        }
    } catch {
        Write-Warning "Controle contrat WS impossible : $($_.Exception.Message)"
    }
}

# outils.json : pour chaque outil hors archive, sa version, son empreinte et l'adresse ou le
# prendre. L'exe n'est joint a CETTE release que s'il a change : sinon l'adresse est celle de la
# release qui le porte deja (GitHub publie l'empreinte de chaque actif, champ `digest`). Une borne
# qui saute des versions le trouve donc toujours, puisqu'elle ne lit que la derniere release.
$releasesPubliees = @((& gh api 'repos/Nelfe80/RetroBat-APIExpose/releases?per_page=50') | Out-String | ConvertFrom-Json |
    ForEach-Object { $_ } | Where-Object { -not $_.draft })
if ($releasesPubliees.Count -eq 0) { throw "Releases illisibles (gh api) : outils.json ne peut pas etre construit." }
# Ce que la derniere release publiait : un outil CHANGE doit y monter de version, sinon les bornes
# qui ont deja ce numero ne le prendraient pas (l'API ne redescend jamais et ne compare que les
# versions, pour qu'une borne de developpement garde l'outil qu'elle vient de construire).
$outilsPrecedents = @()
$actifPrecedent = @($releasesPubliees[0].assets | Where-Object { $_.name -eq 'outils.json' }) | Select-Object -First 1
if ($actifPrecedent) {
    $outilsPrecedents = @((Invoke-RestMethod -UseBasicParsing $actifPrecedent.browser_download_url -TimeoutSec 60).outils)
}
$outils = New-Object System.Collections.ArrayList
$outilsJoints = @()
foreach ($nomOutil in $outilsHorsArchive) {
    $cheminOutil = Join-Path $PSScriptRoot $nomOutil
    if (-not (Test-Path -LiteralPath $cheminOutil)) {
        throw "$nomOutil absent de la racine : il ne serait ni dans l'archive ni dans outils.json."
    }
    $fichierOutil = Get-Item -LiteralPath $cheminOutil
    $shaOutil = (Get-FileHash -LiteralPath $cheminOutil -Algorithm SHA256).Hash.ToLowerInvariant()
    $deja = $releasesPubliees | ForEach-Object { $_.assets } |
        Where-Object { $_.name -eq $nomOutil -and $_.digest -eq "sha256:$shaOutil" } | Select-Object -First 1
    if ($deja) {
        $urlOutil = $deja.browser_download_url
        Write-Host "Outil $nomOutil inchange : deja publie ($urlOutil)."
    } else {
        $versionOutil = [version](("$($fichierOutil.VersionInfo.ProductVersion)" -split '\+')[0])
        $precedent = $outilsPrecedents | Where-Object { $_.fichier -eq $nomOutil } | Select-Object -First 1
        if ($precedent -and $versionOutil -le [version](($precedent.version -split '\+')[0])) {
            throw "$nomOutil a change mais reste en $versionOutil (publie : $($precedent.version)) : monter sa version, sinon les bornes ne le prendront pas."
        }
        $urlOutil = "https://github.com/Nelfe80/RetroBat-APIExpose/releases/download/v$ver/$nomOutil"
        $outilsJoints += $cheminOutil
        Write-Host ("Outil $nomOutil nouveau : joint a cette release ({0:N0} Mo)." -f ($fichierOutil.Length / 1MB))
    }
    [void]$outils.Add([pscustomobject]@{
        fichier = $nomOutil
        version = "$($fichierOutil.VersionInfo.ProductVersion)".Trim()
        sha256  = $shaOutil
        taille  = $fichierOutil.Length
        url     = $urlOutil
    })
}
$outilsFile = Join-Path $out 'outils.json'
[IO.File]::WriteAllText($outilsFile, ([pscustomobject]@{ outils = @($outils) } | ConvertTo-Json -Depth 4),
    (New-Object System.Text.UTF8Encoding($false)))

$hashes = @(Get-FileHash "$out\*.7z" -Algorithm SHA256 | ForEach-Object { '{0}  {1}' -f $_.Hash, (Split-Path $_.Path -Leaf) })
$hashes += @($outilsJoints | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash, (Split-Path $_ -Leaf) })
# Joint a la release : c'est ce que RetroBat.Api.Update.exe lit pour verifier l'archive avant
# de l'appliquer (les notes en repli). Sans empreinte publiee, l'updater n'applique rien.
$sumsFile = Join-Path $out 'SHA256SUMS.txt'
$hashes | Set-Content $sumsFile -Encoding ascii
Write-Host ($hashes -join "`n")

if ($PackageOnly) {
    Write-Host 'PackageOnly : archives pretes, pas de release. A joindre aussi : outils.json'
    foreach ($joint in $outilsJoints) { Write-Host "  et $joint (nouvelle version)" }
    exit 0
}

# ── L'INSTALLEUR DE BORNE DOIT PARTIR AVEC LA RELEASE ───────────────────────────────────────
# Il est compile a part (Inno Setup), pas par ce script, et c'est par lui que passe CHAQUE
# nouveau joueur : la page /setup de nelfeplay.com telecharge la derniere release qui en porte
# un. Publier sans lui casse cette page en silence, ce qui est arrive de 1.7.5 a 1.7.8 puis
# avec la 1.8.15 (l'installeur avait ete compile quinze minutes APRES la publication).
#
# Deux verifications, parce qu'un installeur present peut etre un installeur perime :
#   - sa version doit etre celle qu'on publie (le .iss porte la sienne, mise a jour a la main) ;
#   - il doit etre plus recent que RetroBat.Api.exe, sinon il embarque le programme d'avant.
$setup = Join-Path $PSScriptRoot 'dist\APIExpose-Cabinet-Setup.exe'
$setupFile = $null
if ($Rapide) {
    # La derniere release COMPLETE : c'est son installeur que /setup sert, et c'est vers elle que
    # les notes renvoient une premiere installation.
    $releases = (& gh api 'repos/Nelfe80/RetroBat-APIExpose/releases?per_page=30') | Out-String | ConvertFrom-Json
    $complete = $releases | Where-Object {
        -not $_.draft -and ($_.assets | Where-Object { $_.name -eq 'APIExpose-Cabinet-Setup.exe' })
    } | Select-Object -First 1
    if (-not $complete) { throw "Aucune release complete (avec installeur) trouvee : faire une release complete." }
    $tagComplete = $complete.tag_name
    Write-Warning "Mode rapide : ni full.7z ni installeur. /setup sert l'installeur de $tagComplete, qui se met a jour seul."
    # L'installeur a-t-il change depuis ? Le numero de version du .iss ne compte pas. Le tag est
    # cree par GitHub a la publication : on le rapatrie avant de comparer.
    & git -C $PSScriptRoot fetch --quiet --tags origin
    & git -C $PSScriptRoot rev-parse --verify --quiet "$tagComplete^{commit}" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Tag $tagComplete introuvable localement : l'installeur n'a pas pu etre compare."
    } else {
        $diffInstalleur = @(& git -C $PSScriptRoot diff -U0 $tagComplete HEAD -- installer) | Where-Object {
            $_ -match '^[+-]' -and $_ -notmatch '^(\+\+\+|---)' -and $_ -notmatch '#define AppVersion'
        }
        if ($diffInstalleur) {
            Write-Warning "L'installeur a change depuis $tagComplete ($($diffInstalleur.Count) ligne(s)) : seule une release complete le livrera."
        }
    }
} elseif ($SansInstalleur) {
    Write-Warning "Publication SANS installeur de borne : /setup servira la version precedente."
} else {
    $commande = '& "C:\Program Files\Inno Setup 7\ISCC.exe" installer\CabinetSetup.iss'
    if (-not (Test-Path $setup)) {
        throw "Installeur de borne absent ($setup).`nLe compiler puis relancer :`n    $commande`nSinon : .\release.ps1 -SansInstalleur"
    }
    $setupFile = Get-Item $setup
    # Interpolation plutot que ?? : ce script doit rester lisible par Windows PowerShell 5.1.
    $setupVer = "$($setupFile.VersionInfo.ProductVersion)".Trim()
    if ($setupVer -ne $ver) {
        throw "Installeur en version '$setupVer' alors qu'on publie '$ver'.`nMettre a jour #define AppVersion dans installer\CabinetSetup.iss, recompiler :`n    $commande"
    }
    if ($setupFile.LastWriteTime -lt (Get-Item $exe).LastWriteTime) {
        throw "Installeur compile le $($setupFile.LastWriteTime) alors que RetroBat.Api.exe date du $((Get-Item $exe).LastWriteTime) : il embarque le programme d'avant.`nRecompiler :`n    $commande"
    }
    Write-Host ("Installeur de borne : OK ({0:N0} Mo, compile le {1})" -f ($setupFile.Length / 1MB), $setupFile.LastWriteTime)
}

$notes = @"
Voir le wiki pour l'installation : https://nelfe80.github.io/RetroBat-APIExpose/

| Archive | Contenu |
|---|---|
$(if (-not $Rapide) { "| ``$name-$ver-full.7z`` | Programme + tools + Data Pack complet (premiere installation) |
" })| ``$name-$ver-update.7z`` | Programme seul (mise a jour) |
| ``SHA256SUMS.txt`` | Empreintes, lues par ``RetroBat.Api.Update.exe`` |
| ``outils.json`` | Outils hors archive (outil de diagnostic) : version, empreinte, adresse. L'API de la borne ne telecharge que ce qui a change |$(foreach ($joint in $outilsJoints) { "
| ``$(Split-Path $joint -Leaf)`` | Nouvelle version de l'outil |" })$(if ($setupFile) { "
| ``APIExpose-Cabinet-Setup.exe`` | Installeur de borne (c'est ce que sert https://nelfeplay.com/download/apiexpose) |" })

Mise a jour depuis la borne : lancer ``RetroBat.Api.Update.exe`` a la racine d'APIExpose.
$(if ($Rapide) { "
Release rapide (programme seul). Premiere installation : l'installeur ou le full.7z de [$tagComplete](https://github.com/Nelfe80/RetroBat-APIExpose/releases/tag/$tagComplete), qui se met a jour seul au premier lancement.
" })

### SHA-256
``````
$($hashes -join "`n")
``````
"@
$notesFile = Join-Path $out 'notes.md'
$notes | Set-Content $notesFile -Encoding utf8
# Invocation via tableau splatte : evite les soucis de parsing des flags par PS 5.1.
$ghArgs = @('release', 'create', "v$ver",
    '--repo', 'Nelfe80/RetroBat-APIExpose', '--target', 'main',
    '--title', "APIExpose $ver", '--notes-file', $notesFile)
if (-not $Publish) { $ghArgs += '--draft' }
if (-not $Rapide) { $ghArgs += $full }
$ghArgs += @($update, $swaggerFile, $sumsFile, $outilsFile)
$ghArgs += $outilsJoints
if ($asyncapiFile) { $ghArgs += $asyncapiFile }
if ($setupFile) { $ghArgs += $setupFile.FullName }
& gh @ghArgs
if ($LASTEXITCODE -ne 0) { throw "gh release create a echoue (exit $LASTEXITCODE)." }
Write-Host "Release v$ver creee$(if (-not $Publish) { ' (draft, a publier sur GitHub)' })."
