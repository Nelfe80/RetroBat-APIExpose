param(
    [bool]$Apply = $true
)

$ErrorActionPreference = "Stop"

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Data
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $json = $Data | ConvertTo-Json -Depth 100
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $json, $utf8NoBom)
}

function Get-ScrapeManagedPaths {
    param(
        [Parameter(Mandatory = $true)][string]$GameDirectory
    )

    $managedPaths = New-Object System.Collections.ArrayList
    foreach ($child in @('artwork', 'documents', 'scraping', 'texts', 'ui')) {
        $path = Join-Path $GameDirectory $child
        if (Test-Path -LiteralPath $path) {
            [void]$managedPaths.Add($path)
        }
    }

    foreach ($pattern in @('image.*', 'video.*', 'manual.*', 'description.*')) {
        foreach ($file in Get-ChildItem -LiteralPath $GameDirectory -Filter $pattern -File -ErrorAction SilentlyContinue) {
            [void]$managedPaths.Add($file.FullName)
        }
    }

    return $managedPaths
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$mediaSystemsRoot = Join-Path $repoRoot 'media\systems'
$mediaAliasesGamesRoot = Join-Path $repoRoot 'media\aliases\games'
$mediaAliasesSharedRoot = Join-Path $repoRoot 'media\aliases\shared'
$romsRoot = 'E:\RetroBat\roms'
$artifactRoot = Join-Path $repoRoot 'artifacts\media-reset'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportPath = Join-Path $artifactRoot "scraped-media-reset-$timestamp.json"

$report = [ordered]@{
    generated_at = (Get-Date).ToString('o')
    apply = [bool]$Apply
    screenscraper_game_dirs = New-Object System.Collections.ArrayList
    deleted_canonical_paths = New-Object System.Collections.ArrayList
    deleted_projection_files = New-Object System.Collections.ArrayList
    cleaned_gamelists = New-Object System.Collections.ArrayList
    deleted_alias_files = New-Object System.Collections.ArrayList
    deleted_shared_files = New-Object System.Collections.ArrayList
}

$metadataFiles = Get-ChildItem -Recurse -LiteralPath $mediaSystemsRoot -Filter metadata.json -File -ErrorAction SilentlyContinue
$gameDirs = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($file in $metadataFiles) {
    try {
        $json = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        $source = [string]$json.Fields.source
        if ([string]::IsNullOrWhiteSpace($source)) {
            continue
        }

        if ($source.Trim().ToLowerInvariant() -eq 'screenscraper') {
            $gameDir = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $file.FullName))
            if ($gameDirs.Add($gameDir)) {
                [void]$report.screenscraper_game_dirs.Add($gameDir)
            }
        }
    }
    catch {
    }
}

foreach ($gameDir in $report.screenscraper_game_dirs) {
    foreach ($path in Get-ScrapeManagedPaths -GameDirectory $gameDir) {
        [void]$report.deleted_canonical_paths.Add($path)
        if ($Apply -and (Test-Path -LiteralPath $path)) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }

    if ($Apply -and (Test-Path -LiteralPath $gameDir)) {
        $remaining = Get-ChildItem -LiteralPath $gameDir -Force -ErrorAction SilentlyContinue
        if ($null -eq $remaining -or $remaining.Count -eq 0) {
            Remove-Item -LiteralPath $gameDir -Force
            [void]$report.deleted_canonical_paths.Add($gameDir)
        }
    }
}

$projectionFolders = @('images', 'manuals', 'videos')
$projectionPatterns = @(
    '*-image.*',
    '*-thumb.*',
    '*-logo.*',
    '*-wheel.*',
    '*-wheelcarbon.*',
    '*-wheelsteel.*',
    '*-marquee.*',
    '*-screenmarquee.*',
    '*-box2d.*',
    '*-box3d.*',
    '*-boxback.*',
    '*-fanart.*',
    '*-bezel.*',
    '*-map.*',
    '*-manual.*',
    '*-video.*',
    '*_default.*',
    'scraping_in_progress.png',
    'no_media_found.png'
)

if (Test-Path -LiteralPath $romsRoot) {
    foreach ($systemDir in Get-ChildItem -LiteralPath $romsRoot -Directory) {
        foreach ($folder in $projectionFolders) {
            $folderPath = Join-Path $systemDir.FullName $folder
            if (-not (Test-Path -LiteralPath $folderPath)) {
                continue
            }

            foreach ($pattern in $projectionPatterns) {
                foreach ($file in Get-ChildItem -LiteralPath $folderPath -Filter $pattern -File -ErrorAction SilentlyContinue) {
                    [void]$report.deleted_projection_files.Add($file.FullName)
                    if ($Apply) {
                        Remove-Item -LiteralPath $file.FullName -Force
                    }
                }
            }
        }

        $gamelistPath = Join-Path $systemDir.FullName 'gamelist.xml'
        if (-not (Test-Path -LiteralPath $gamelistPath)) {
            continue
        }

        try {
            [xml]$document = Get-Content -LiteralPath $gamelistPath -Raw
            $removed = 0
            foreach ($gameNode in @($document.gameList.game)) {
                if ($null -eq $gameNode) {
                    continue
                }

                foreach ($tagName in @('image', 'manual', 'video', 'marquee', 'thumbnail', 'fanart', 'bezel', 'boxback', 'map', 'desc')) {
                    $child = $gameNode.SelectSingleNode($tagName)
                    if ($null -ne $child) {
                        [void]$gameNode.RemoveChild($child)
                        $removed++
                    }
                }
            }

            if ($removed -gt 0) {
                [void]$report.cleaned_gamelists.Add([ordered]@{
                    path = $gamelistPath
                    removed_tags = $removed
                })

                if ($Apply) {
                    $document.Save($gamelistPath)
                }
            }
        }
        catch {
        }
    }
}

foreach ($aliasFile in Get-ChildItem -LiteralPath $mediaAliasesGamesRoot -Filter '*.json' -File -ErrorAction SilentlyContinue) {
    [void]$report.deleted_alias_files.Add($aliasFile.FullName)
    if ($Apply) {
        Remove-Item -LiteralPath $aliasFile.FullName -Force
    }
}

foreach ($sharedFileName in @('scrape-availability.json', 'pending-scrapes.json', 'media-hashes.json', 'bootstrap-placeholder-state.json')) {
    $sharedPath = Join-Path $mediaAliasesSharedRoot $sharedFileName
    if (Test-Path -LiteralPath $sharedPath) {
        [void]$report.deleted_shared_files.Add($sharedPath)
        if ($Apply) {
            Remove-Item -LiteralPath $sharedPath -Force
        }
    }
}

Write-JsonFile -Path $reportPath -Data $report

Write-Output "Rapport: $reportPath"
Write-Output "Jeux ScreenScraper identifies: $($report.screenscraper_game_dirs.Count)"
Write-Output "Chemins canoniques supprimes: $($report.deleted_canonical_paths.Count)"
Write-Output "Fichiers projetes supprimes: $($report.deleted_projection_files.Count)"
Write-Output "Gamelists nettoyees: $($report.cleaned_gamelists.Count)"
Write-Output "Fichiers d'alias supprimes: $($report.deleted_alias_files.Count)"
Write-Output "Caches partages supprimes: $($report.deleted_shared_files.Count)"
