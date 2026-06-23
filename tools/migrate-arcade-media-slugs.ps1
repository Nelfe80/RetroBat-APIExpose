param(
    [bool]$Apply = $true
)

$ErrorActionPreference = "Stop"

function ConvertTo-PlainData {
    param([Parameter(ValueFromPipeline = $true)]$InputObject)

    if ($null -eq $InputObject) {
        return $null
    }

    if ($InputObject -is [System.Collections.IDictionary]) {
        $result = [ordered]@{}
        foreach ($key in $InputObject.Keys) {
            $result[$key] = ConvertTo-PlainData $InputObject[$key]
        }

        return $result
    }

    if ($InputObject -is [System.Management.Automation.PSCustomObject]) {
        $result = [ordered]@{}
        foreach ($property in $InputObject.PSObject.Properties) {
            $result[$property.Name] = ConvertTo-PlainData $property.Value
        }

        return $result
    }

    if ($InputObject -is [System.Collections.IEnumerable] -and -not ($InputObject -is [string])) {
        $items = New-Object System.Collections.ArrayList
        foreach ($item in $InputObject) {
            [void]$items.Add((ConvertTo-PlainData $item))
        }

        return ,$items.ToArray()
    }

    return $InputObject
}

function Read-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $raw = Get-Content -LiteralPath $Path -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return $null
    }

    return ConvertTo-PlainData ($raw | ConvertFrom-Json)
}

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

function Normalize-RomSlug {
    param(
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $stem = [System.IO.Path]::GetFileNameWithoutExtension($Value)
    if ([string]::IsNullOrWhiteSpace($stem)) {
        $stem = $Value
    }

    $cleaned = [regex]::Replace($stem, '\(.*?\)|\[.*?\]', ' ')
    $cleaned = $cleaned.Replace('&', ' ')
    $cleaned = [regex]::Replace($cleaned, '[^a-zA-Z0-9]+', ' ')
    $cleaned = [regex]::Replace($cleaned, '\s+', ' ').Trim().ToLowerInvariant()
    return $cleaned.Replace(' ', '_')
}

function Resolve-FinalSlug {
    param(
        [Parameter(Mandatory = $true)][string]$Slug,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Mappings
    )

    $current = $Slug
    $visited = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    while ($Mappings.Contains($current) -and $visited.Add($current)) {
        $current = [string]$Mappings[$current]
    }

    return $current
}

function Get-AvailabilityScore {
    param($Entry)

    if ($null -eq $Entry) {
        return -1
    }

    $mediaCount = 0
    if ($Entry.Contains('MediaUrls') -and $Entry['MediaUrls'] -is [System.Collections.IDictionary]) {
        $mediaCount = $Entry['MediaUrls'].Count
    }

    $synopsisCount = 0
    if ($Entry.Contains('Synopses') -and $Entry['Synopses'] -is [System.Collections.IDictionary]) {
        $synopsisCount = $Entry['Synopses'].Count
    }

    $metadataCount = 0
    if ($Entry.Contains('MetadataFields') -and $Entry['MetadataFields'] -is [System.Collections.IDictionary]) {
        $metadataCount = $Entry['MetadataFields'].Count
    }

    $positiveCacheBonus = if ($Entry.Contains('IsNegativeCache') -and -not [bool]$Entry['IsNegativeCache']) { 1000 } else { 0 }
    $resolvedBonus = if ($Entry.Contains('MetadataResolved') -and [bool]$Entry['MetadataResolved']) { 100 } else { 0 }
    $versionBonus = if ($Entry.Contains('MetadataVersion') -and $Entry['MetadataVersion'] -is [int]) { [int]$Entry['MetadataVersion'] } else { 0 }

    return $positiveCacheBonus + $resolvedBonus + $versionBonus + ($mediaCount * 10) + ($synopsisCount * 3) + $metadataCount
}

function Merge-DictionaryValues {
    param(
        [System.Collections.IDictionary]$Target,
        [System.Collections.IDictionary]$Source
    )

    if ($null -eq $Target -or $null -eq $Source) {
        return
    }

    foreach ($key in $Source.Keys) {
        if (-not $Target.Contains($key) -or [string]::IsNullOrWhiteSpace([string]$Target[$key])) {
            $Target[$key] = $Source[$key]
        }
    }
}

function Merge-AvailabilityEntry {
    param(
        [Parameter(Mandatory = $true)]$Left,
        [Parameter(Mandatory = $true)]$Right
    )

    $leftData = ConvertTo-PlainData $Left
    $rightData = ConvertTo-PlainData $Right

    $primary = if ((Get-AvailabilityScore $leftData) -ge (Get-AvailabilityScore $rightData)) { $leftData } else { $rightData }
    $secondary = if ([object]::ReferenceEquals($primary, $leftData)) { $rightData } else { $leftData }

    foreach ($property in @('ScreenScraperGameId', 'ScreenScraperSystemId', 'MetadataLanguage')) {
        if ((-not $primary.Contains($property)) -or [string]::IsNullOrWhiteSpace([string]$primary[$property])) {
            $primary[$property] = $secondary[$property]
        }
    }

    if ((-not $primary.Contains('MetadataVersion')) -or [int]$secondary['MetadataVersion'] -gt [int]$primary['MetadataVersion']) {
        $primary['MetadataVersion'] = $secondary['MetadataVersion']
    }

    if ((-not $primary.Contains('MetadataResolved')) -or -not [bool]$primary['MetadataResolved']) {
        $primary['MetadataResolved'] = [bool]$secondary['MetadataResolved']
    }

    $primaryIsNegative = if ($primary.Contains('IsNegativeCache')) { [bool]$primary['IsNegativeCache'] } else { $false }
    $secondaryIsNegative = if ($secondary.Contains('IsNegativeCache')) { [bool]$secondary['IsNegativeCache'] } else { $false }
    $primary['IsNegativeCache'] = $primaryIsNegative -and $secondaryIsNegative

    if (-not $primary['IsNegativeCache']) {
        $primary['NegativeCacheUntilUtc'] = $null
    } elseif ([string]::IsNullOrWhiteSpace([string]$primary['NegativeCacheUntilUtc'])) {
        $primary['NegativeCacheUntilUtc'] = $secondary['NegativeCacheUntilUtc']
    }

    if ([string]::IsNullOrWhiteSpace([string]$primary['CachedAtUtc']) -or [string]$secondary['CachedAtUtc'] -gt [string]$primary['CachedAtUtc']) {
        $primary['CachedAtUtc'] = $secondary['CachedAtUtc']
    }

    if (-not ($primary['MediaUrls'] -is [System.Collections.IDictionary])) {
        $primary['MediaUrls'] = [ordered]@{}
    }

    if (-not ($primary['Synopses'] -is [System.Collections.IDictionary])) {
        $primary['Synopses'] = [ordered]@{}
    }

    if (-not ($primary['MetadataFields'] -is [System.Collections.IDictionary])) {
        $primary['MetadataFields'] = [ordered]@{}
    }

    Merge-DictionaryValues -Target $primary['MediaUrls'] -Source $secondary['MediaUrls']
    Merge-DictionaryValues -Target $primary['Synopses'] -Source $secondary['Synopses']
    Merge-DictionaryValues -Target $primary['MetadataFields'] -Source $secondary['MetadataFields']

    return $primary
}

function Move-DirectoryContent {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$TargetDirectory,
        [Parameter(Mandatory = $true)][string]$ConflictRoot,
        [Parameter(Mandatory = $true)]$MovedFiles,
        [Parameter(Mandatory = $true)]$ConflictFiles
    )

    if (-not (Test-Path -LiteralPath $SourceDirectory)) {
        return
    }

    [System.IO.Directory]::CreateDirectory($TargetDirectory) | Out-Null

    $sourceFiles = Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File | Sort-Object FullName
    foreach ($sourceFile in $sourceFiles) {
        $relativePath = $sourceFile.FullName.Substring($SourceDirectory.Length).TrimStart('\')
        $targetPath = Join-Path $TargetDirectory $relativePath
        $targetParent = Split-Path -Parent $targetPath
        [System.IO.Directory]::CreateDirectory($targetParent) | Out-Null

        if (Test-Path -LiteralPath $targetPath) {
            $sameLength = ([System.IO.FileInfo](Get-Item -LiteralPath $targetPath)).Length -eq $sourceFile.Length
            $sameHash = $false
            if ($sameLength) {
                $sourceHash = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
                $targetHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
                $sameHash = [string]::Equals($sourceHash, $targetHash, [System.StringComparison]::OrdinalIgnoreCase)
            }

            if ($sameHash) {
                Remove-Item -LiteralPath $sourceFile.FullName -Force
                [void]$ConflictFiles.Add([ordered]@{
                    type = 'identical'
                    source = $sourceFile.FullName
                    target = $targetPath
                    action = 'discarded_source_duplicate'
                })
            } else {
                $conflictPath = Join-Path $ConflictRoot $relativePath
                $conflictParent = Split-Path -Parent $conflictPath
                [System.IO.Directory]::CreateDirectory($conflictParent) | Out-Null
                Move-Item -LiteralPath $sourceFile.FullName -Destination $conflictPath -Force
                [void]$ConflictFiles.Add([ordered]@{
                    type = 'different'
                    source = $sourceFile.FullName
                    target = $targetPath
                    preserved_at = $conflictPath
                    action = 'moved_to_conflicts'
                })
            }

            continue
        }

        Move-Item -LiteralPath $sourceFile.FullName -Destination $targetPath
        [void]$MovedFiles.Add([ordered]@{
            source = $sourceFile.FullName
            target = $targetPath
        })
    }

    $directories = Get-ChildItem -LiteralPath $SourceDirectory -Recurse -Directory | Sort-Object FullName -Descending
    foreach ($directory in $directories) {
        if (-not (Get-ChildItem -LiteralPath $directory.FullName -Force)) {
            Remove-Item -LiteralPath $directory.FullName -Force
        }
    }

    if (-not (Get-ChildItem -LiteralPath $SourceDirectory -Force)) {
        Remove-Item -LiteralPath $SourceDirectory -Force
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$mediaRoot = Join-Path $repoRoot 'media'
$arcadeGamesRoot = Join-Path $mediaRoot 'systems\arcade\games'
$arcadeAliasPath = Join-Path $mediaRoot 'aliases\games\arcade.json'
$availabilityPath = Join-Path $mediaRoot 'aliases\shared\scrape-availability.json'
$mediaHashesPath = Join-Path $mediaRoot 'aliases\shared\media-hashes.json'
$pendingPath = Join-Path $mediaRoot 'aliases\shared\pending-scrapes.json'
$artifactRoot = Join-Path $repoRoot 'artifacts\media-migration'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportPath = Join-Path $artifactRoot "arcade-slug-migration-$timestamp.json"
$conflictsRoot = Join-Path $artifactRoot "conflicts-$timestamp"

$arcadeAlias = Read-JsonFile -Path $arcadeAliasPath
if ($null -eq $arcadeAlias -or -not $arcadeAlias.Contains('Entries')) {
    throw "Impossible de charger $arcadeAliasPath"
}

$mappings = [ordered]@{}
$mappingSources = [ordered]@{}
foreach ($entry in $arcadeAlias['Entries'].GetEnumerator()) {
    $key = [string]$entry.Key
    $value = [string]$entry.Value
    if ([string]::IsNullOrWhiteSpace($value)) {
        continue
    }

    $romSeed = $null
    if ($key.StartsWith('file:', [System.StringComparison]::OrdinalIgnoreCase)) {
        $romSeed = $key.Substring(5)
    } elseif ($key.StartsWith('path:', [System.StringComparison]::OrdinalIgnoreCase)) {
        $pathValue = $key.Substring(5) -replace '/', '\'
        $romSeed = [System.IO.Path]::GetFileName($pathValue)
    }

    if ([string]::IsNullOrWhiteSpace($romSeed)) {
        continue
    }

    $targetSlug = Normalize-RomSlug $romSeed
    if ([string]::IsNullOrWhiteSpace($targetSlug) -or [string]::Equals($targetSlug, $value, [System.StringComparison]::OrdinalIgnoreCase)) {
        continue
    }

    if ($mappings.Contains($value) -and -not [string]::Equals([string]$mappings[$value], $targetSlug, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Conflit de migration pour '$value' : '$($mappings[$value])' vs '$targetSlug'."
    }

    $mappings[$value] = $targetSlug
    if (-not $mappingSources.Contains($value)) {
        $mappingSources[$value] = New-Object System.Collections.ArrayList
    }

    [void]$mappingSources[$value].Add($key)
}

$resolvedMappings = [ordered]@{}
foreach ($sourceSlug in $mappings.Keys) {
    $resolvedMappings[$sourceSlug] = Resolve-FinalSlug -Slug $mappings[$sourceSlug] -Mappings $mappings
}

$report = [ordered]@{
    generated_at = (Get-Date).ToString('o')
    apply = [bool]$Apply
    mappings = New-Object System.Collections.ArrayList
    aliases_updated = 0
    alias_keys_added = New-Object System.Collections.ArrayList
    availability_keys_moved = 0
    media_hash_paths_updated = 0
    pending_jobs_updated = 0
    directories_removed = New-Object System.Collections.ArrayList
    directories_created = New-Object System.Collections.ArrayList
    moved_files = New-Object System.Collections.ArrayList
    conflicts = New-Object System.Collections.ArrayList
    pending_file_skipped = $false
}

foreach ($sourceSlug in $resolvedMappings.Keys | Sort-Object) {
    [void]$report['mappings'].Add([ordered]@{
        source = $sourceSlug
        target = $resolvedMappings[$sourceSlug]
        keys = $mappingSources[$sourceSlug]
    })
}

if ($Apply) {
    [System.IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
}

foreach ($mapping in $report['mappings']) {
    $sourceSlug = [string]$mapping['source']
    $targetSlug = [string]$mapping['target']
    $sourceDirectory = Join-Path $arcadeGamesRoot $sourceSlug
    $targetDirectory = Join-Path $arcadeGamesRoot $targetSlug

    if (-not (Test-Path -LiteralPath $sourceDirectory)) {
        continue
    }

    if ($Apply) {
        if (-not (Test-Path -LiteralPath $targetDirectory)) {
            [System.IO.Directory]::CreateDirectory($targetDirectory) | Out-Null
            [void]$report['directories_created'].Add($targetDirectory)
        }

        $conflictDirectory = Join-Path $conflictsRoot $sourceSlug
        Move-DirectoryContent `
            -SourceDirectory $sourceDirectory `
            -TargetDirectory $targetDirectory `
            -ConflictRoot $conflictDirectory `
            -MovedFiles $report['moved_files'] `
            -ConflictFiles $report['conflicts']

        if (-not (Test-Path -LiteralPath $sourceDirectory)) {
            [void]$report['directories_removed'].Add($sourceDirectory)
        }
    }
}

$aliasEntries = $arcadeAlias['Entries']
$aliasUpdateCount = 0
foreach ($key in @($aliasEntries.Keys)) {
    $currentValue = [string]$aliasEntries[$key]
    $finalValue = Resolve-FinalSlug -Slug $currentValue -Mappings $resolvedMappings
    if (-not [string]::Equals($currentValue, $finalValue, [System.StringComparison]::OrdinalIgnoreCase)) {
        $aliasEntries[$key] = $finalValue
        $aliasUpdateCount++
    }
}

foreach ($entry in $report['mappings']) {
    $targetSlug = [string]$entry['target']
    foreach ($newKey in @("slug:$targetSlug", "name:$targetSlug", "scrapname:$targetSlug")) {
        if (-not $aliasEntries.Contains($newKey)) {
            $aliasEntries[$newKey] = $targetSlug
            [void]$report['alias_keys_added'].Add($newKey)
        }
    }
}

$report['aliases_updated'] = $aliasUpdateCount

$availability = Read-JsonFile -Path $availabilityPath
if ($availability -and $availability.Contains('Entries')) {
    foreach ($key in @($availability['Entries'].Keys)) {
        if (-not $key.StartsWith('arcade|', [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $separatorIndex = $key.IndexOf('|')
        $slug = $key.Substring($separatorIndex + 1)
        $finalSlug = Resolve-FinalSlug -Slug $slug -Mappings $resolvedMappings
        if ([string]::Equals($slug, $finalSlug, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $targetKey = "arcade|$finalSlug"
        if ($availability['Entries'].Contains($targetKey)) {
            $availability['Entries'][$targetKey] = Merge-AvailabilityEntry -Left $availability['Entries'][$targetKey] -Right $availability['Entries'][$key]
        } else {
            $availability['Entries'][$targetKey] = $availability['Entries'][$key]
        }

        $availability['Entries'].Remove($key)
        $report['availability_keys_moved'] = [int]$report['availability_keys_moved'] + 1
    }
}

$mediaHashes = Read-JsonFile -Path $mediaHashesPath
if ($mediaHashes -and $mediaHashes.Contains('Entries')) {
    foreach ($key in @($mediaHashes['Entries'].Keys)) {
        $currentPath = [string]$mediaHashes['Entries'][$key]
        $updatedPath = $currentPath
        foreach ($entry in $report['mappings']) {
            $sourceSlug = [string]$entry['source']
            $targetSlug = [string]$entry['target']
            $updatedPath = $updatedPath.Replace("systems/arcade/games/$sourceSlug/", "systems/arcade/games/$targetSlug/")
        }

        if (-not [string]::Equals($currentPath, $updatedPath, [System.StringComparison]::Ordinal)) {
            $mediaHashes['Entries'][$key] = $updatedPath
            $report['media_hash_paths_updated'] = [int]$report['media_hash_paths_updated'] + 1
        }
    }
}

$pendingUpdatedCount = 0
if (Test-Path -LiteralPath $pendingPath) {
    try {
        $pendingState = Read-JsonFile -Path $pendingPath
        if ($pendingState -and $pendingState.Contains('Jobs')) {
            foreach ($job in $pendingState['Jobs']) {
                if ($null -eq $job -or -not $job.Contains('Plan') -or $null -eq $job['Plan']) {
                    continue
                }

                $plan = $job['Plan']
                if (-not $plan.Contains('SystemId') -or -not [string]::Equals([string]$plan['SystemId'], 'arcade', [System.StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }

                $currentSlug = [string]$plan['GameSlug']
                $finalSlug = Resolve-FinalSlug -Slug $currentSlug -Mappings $resolvedMappings
                if ([string]::Equals($currentSlug, $finalSlug, [System.StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }

                $plan['GameSlug'] = $finalSlug
                if ($job.Contains('Need') -and $job['Need'] -and $job['Need'].Contains('Kind')) {
                    $job['JobKey'] = "arcade|$finalSlug|$($job['Need']['Kind'])"
                }

                foreach ($need in @($plan['Needs'])) {
                    foreach ($property in @('ExistingPath', 'ImportedPath', 'ProjectedPath')) {
                        if (-not $need.Contains($property)) {
                            continue
                        }

                        $currentValue = [string]$need[$property]
                        if ([string]::IsNullOrWhiteSpace($currentValue)) {
                            continue
                        }

                        foreach ($entry in $report['mappings']) {
                            $sourceSlug = [string]$entry['source']
                            $targetSlug = [string]$entry['target']
                            $currentValue = $currentValue.Replace("/systems/arcade/games/$sourceSlug/", "/systems/arcade/games/$targetSlug/")
                            $currentValue = $currentValue.Replace("systems/arcade/games/$sourceSlug/", "systems/arcade/games/$targetSlug/")
                        }

                        $need[$property] = $currentValue
                    }
                }

                $pendingUpdatedCount++
            }
        }
    }
    catch {
        $report['pending_file_skipped'] = $true
    }
}

$report['pending_jobs_updated'] = $pendingUpdatedCount

if ($Apply) {
    Write-JsonFile -Path $arcadeAliasPath -Data $arcadeAlias

    if ($availability) {
        Write-JsonFile -Path $availabilityPath -Data $availability
    }

    if ($mediaHashes) {
        Write-JsonFile -Path $mediaHashesPath -Data $mediaHashes
    }

    if ($pendingState -and -not $report['pending_file_skipped']) {
        Write-JsonFile -Path $pendingPath -Data $pendingState
    }
}

Write-JsonFile -Path $reportPath -Data $report

Write-Output "Rapport: $reportPath"
Write-Output "Mappings: $($report['mappings'].Count)"
Write-Output "Aliases mis a jour: $($report['aliases_updated'])"
Write-Output "Cles availability migrees: $($report['availability_keys_moved'])"
Write-Output "Chemins media-hashes mis a jour: $($report['media_hash_paths_updated'])"
Write-Output "Jobs pending mis a jour: $($report['pending_jobs_updated'])"
Write-Output "Fichiers deplaces: $($report['moved_files'].Count)"
Write-Output "Conflits preserves: $($report['conflicts'].Count)"
