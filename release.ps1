# release.ps1 - Construit et publie une release APIExpose sur GitHub.
# Usage :
#   .\release.ps1                # construit les archives + release DRAFT
#   .\release.ps1 -Publish      # publie directement (sans draft)
#   .\release.ps1 -PackageOnly  # construit seulement les archives
param(
    [switch]$Publish,
    [switch]$PackageOnly
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
    '-xr!ScreenScraper.html'
)

Set-Location $root
$full   = Join-Path $out "$name-$ver-full.7z"
$update = Join-Path $out "$name-$ver-update.7z"
# 7z "a" met a jour une archive existante sans retirer les entrees exclues :
# on repart toujours d'archives vierges.
Remove-Item $full, $update -Force -Confirm:$false -ErrorAction SilentlyContinue
Write-Host 'Construction full.7z (avec resources + tools, plusieurs minutes)...'
& $sz a -t7z $full "$name\" @ex -mx=5 -bsp1 -bso0
Write-Host 'Construction update.7z...'
& $sz a -t7z $update "$name\" @ex "-x!$name\resources" "-x!$name\tools" -mx=5 -bsp0 -bso0

# Controle anti-fuite : l'archive ne doit contenir ni .env, ni media, ni sources.
$listing = & $sz l $full
# Le controle ne cherchait que les familles connues a l'epoque : tout tools\*.ps1 et
# tools\*.py, resources\ra et ScreenScraper.html sont passes au travers. Il couvre
# desormais ce que les exclusions ci-dessus retirent, pour que les deux listes se
# contredisent bruyamment si l'une d'elles derive.
$leaks = $listing | Select-String '\.env|\.bak|\.ps1$|\.py$|\\media\\|\\src\\|\\tests\\|\\docs\\|\\dist\\|package-installer|projects-source|\.git|panel_curator|profiles_db|\\resources\\ra\\|\\resources\\ram\\\.user\\|ScreenScraper\.html|\\tools\\(ffmpeg|imagemagick|translateLocally|mem-curator|libretro-probe)\\'
if ($leaks) { throw "FUITE DETECTEE dans l'archive : $($leaks[0])" }
# La cle API de la borne ne doit JAMAIS etre committee/distribuee : le defaut reste vide
# (chaque borne genere la sienne au 1er run). On bloque si une valeur traine.
$appsettingsPath = Join-Path $PSScriptRoot 'appsettings.json'
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
    'RetroBat.Api.Update.exe'
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
foreach ($ligne in (& $sz l -slt $full)) {
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

$hashes = Get-FileHash "$out\*.7z" -Algorithm SHA256 | ForEach-Object { '{0}  {1}' -f $_.Hash, (Split-Path $_.Path -Leaf) }
# Joint a la release : c'est ce que RetroBat.Api.Update.exe lit pour verifier l'archive avant
# de l'appliquer (les notes en repli). Sans empreinte publiee, l'updater n'applique rien.
$sumsFile = Join-Path $out 'SHA256SUMS.txt'
$hashes | Set-Content $sumsFile -Encoding ascii
Write-Host ($hashes -join "`n")

if ($PackageOnly) { Write-Host 'PackageOnly : archives pretes, pas de release.'; exit 0 }

$notes = @"
Voir le wiki pour l'installation : https://nelfe80.github.io/RetroBat-APIExpose/

| Archive | Contenu |
|---|---|
| ``$name-$ver-full.7z`` | Programme + tools + Data Pack complet (premiere installation) |
| ``$name-$ver-update.7z`` | Programme seul (mise a jour) |
| ``SHA256SUMS.txt`` | Empreintes, lues par ``RetroBat.Api.Update.exe`` |

Mise a jour depuis la borne : lancer ``RetroBat.Api.Update.exe`` a la racine d'APIExpose.

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
$ghArgs += @($full, $update, $swaggerFile, $sumsFile)
if ($asyncapiFile) { $ghArgs += $asyncapiFile }
& gh @ghArgs
if ($LASTEXITCODE -ne 0) { throw "gh release create a echoue (exit $LASTEXITCODE)." }
Write-Host "Release v$ver creee$(if (-not $Publish) { ' (draft, a publier sur GitHub)' })."
