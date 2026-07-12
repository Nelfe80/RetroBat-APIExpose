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
    # Curator : savoir-faire prive, jamais distribue.
    "-x!$name\panel_curator_ultimate.py", "-x!$name\panel_curator.ini", "-x!$name\profiles_db.py",
    # mem-curator : les sorties de generation, rapports et chemins locaux
    # restent hors pack (les outils eux-memes sont publics via le repo).
    "-x!$name\tools\mem-curator\MEM_*", "-x!$name\tools\mem-curator\_test_MEM*",
    "-x!$name\tools\mem-curator\.source-base.local",
    "-x!$name\tools\mem-curator\baseline_*.json", "-x!$name\tools\mem-curator\staging*",
    "-x!$name\state",
    "-x!$name\docs", "-x!$name\src", "-x!$name\artifacts",
    "-x!$name\wiki", "-x!$name\mkdocs.yml", "-x!$name\site",
    "-x!$name\build.bat", "-x!$name\release.ps1",
    '-xr!__pycache__', '-xr!*.log', '-xr!.vs'
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
$leaks = $listing | Select-String '\.env|\\media\\|\\src\\|\\docs\\|package-installer|projects-source|\.git|panel_curator|profiles_db'
if ($leaks) { throw "FUITE DETECTEE dans l'archive : $($leaks[0])" }
Write-Host 'Controle anti-fuite : OK'

$hashes = Get-FileHash "$out\*.7z" -Algorithm SHA256 | ForEach-Object { '{0}  {1}' -f $_.Hash, (Split-Path $_.Path -Leaf) }
$hashes | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii
Write-Host ($hashes -join "`n")

if ($PackageOnly) { Write-Host 'PackageOnly : archives pretes, pas de release.'; exit 0 }

$notes = @"
Voir le wiki pour l'installation : https://nelfe80.github.io/RetroBat-APIExpose/

| Archive | Contenu |
|---|---|
| ``$name-$ver-full.7z`` | Programme + tools + Data Pack complet (premiere installation) |
| ``$name-$ver-update.7z`` | Programme seul (mise a jour) |

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
$ghArgs += @($full, $update)
& gh @ghArgs
if ($LASTEXITCODE -ne 0) { throw "gh release create a echoue (exit $LASTEXITCODE)." }
Write-Host "Release v$ver creee$(if (-not $Publish) { ' (draft, a publier sur GitHub)' })."
