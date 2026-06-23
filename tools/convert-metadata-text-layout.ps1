param(
    [string[]]$Root = @(),
    [switch]$Apply,
    [switch]$RemoveLegacy,
    [switch]$Backup,
    [switch]$MergeExisting
)

$ErrorActionPreference = 'Stop'

function ConvertTo-PlainObject {
    param([object]$Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $result = [ordered]@{}
        foreach ($key in $Value.Keys) {
            $result[$key] = ConvertTo-PlainObject $Value[$key]
        }
        return $result
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $items = @()
        foreach ($item in $Value) {
            $items += ConvertTo-PlainObject $item
        }
        return $items
    }

    if ($Value -is [pscustomobject]) {
        $result = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties) {
            $result[$property.Name] = ConvertTo-PlainObject $property.Value
        }
        return $result
    }

    return $Value
}

function Normalize-Language {
    param([string]$Raw)

    $value = (($Raw + '').Trim() -replace '_', '-').ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($value)) {
        return 'en'
    }

    return ($value -split '-', 2)[0]
}

function Read-JsonBundle {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return [ordered]@{}
    }

    $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return [ordered]@{}
    }

    return ConvertTo-PlainObject ($raw | ConvertFrom-Json)
}

function Get-BundleFields {
    param([System.Collections.IDictionary]$Bundle)

    if ($Bundle.Contains('Fields') -and $Bundle['Fields'] -is [System.Collections.IDictionary]) {
        return $Bundle['Fields']
    }

    if ($Bundle.Contains('fields') -and $Bundle['fields'] -is [System.Collections.IDictionary]) {
        $Bundle['Fields'] = $Bundle['fields']
        $Bundle.Remove('fields')
        return $Bundle['Fields']
    }

    $Bundle['Fields'] = [ordered]@{}
    return $Bundle['Fields']
}

function Merge-Bundle {
    param(
        [System.Collections.IDictionary]$Destination,
        [System.Collections.IDictionary]$Source,
        [string]$Language
    )

    $changed = $false
    if (-not $Destination.Contains('Language') -or $Destination['Language'] -ne $Language) {
        $Destination['Language'] = $Language
        $changed = $true
    }

    $destinationFields = Get-BundleFields $Destination
    $sourceFields = Get-BundleFields $Source
    foreach ($key in $sourceFields.Keys) {
        $value = $sourceFields[$key]
        if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
            continue
        }

        $existing = if ($destinationFields.Contains($key)) { $destinationFields[$key] } else { $null }
        if ($null -eq $existing -or
            [string]::IsNullOrWhiteSpace([string]$existing) -or
            ([string]$existing).Trim().Length -lt ([string]$value).Trim().Length) {
            $destinationFields[$key] = $value
            $changed = $true
        }
    }

    if ($changed) {
        $Destination['UpdatedAtUtc'] = [DateTime]::UtcNow.ToString('o')
    }

    return $changed
}

function Test-BundleHasFields {
    param([System.Collections.IDictionary]$Bundle)

    $fields = Get-BundleFields $Bundle
    foreach ($key in $fields.Keys) {
        $value = $fields[$key]
        if ($null -ne $value -and -not [string]::IsNullOrWhiteSpace([string]$value)) {
            return $true
        }
    }

    return $false
}

function Write-JsonBundle {
    param(
        [string]$Path,
        [System.Collections.IDictionary]$Bundle
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $tempPath = "$Path.$PID.tmp"
    try {
        $Bundle | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $tempPath -Encoding UTF8
        if (Test-Path -LiteralPath $Path) {
            Move-Item -LiteralPath $tempPath -Destination $Path -Force
        } else {
            Move-Item -LiteralPath $tempPath -Destination $Path
        }
    } finally {
        if (Test-Path -LiteralPath $tempPath) {
            Remove-Item -LiteralPath $tempPath -Force
        }
    }
}

function Test-LegacyMetadataPath {
    param([System.IO.FileInfo]$File)

    return $File.Name -ieq 'metadata.json' -and
        $null -ne $File.Directory -and
        $null -ne $File.Directory.Parent -and
        $File.Directory.Parent.Name -ieq 'texts'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$scanRoots = @()
if ($Root.Count -gt 0) {
    foreach ($entry in $Root) {
        $scanRoots += [System.IO.Path]::GetFullPath($entry)
    }
} else {
    $scanRoots += Join-Path $repoRoot 'media\systems'
    $scanRoots += Join-Path $repoRoot 'media\user\systems'
}

$existingRoots = @($scanRoots | Where-Object { Test-Path -LiteralPath $_ })
$scanned = 0
$convertible = 0
$written = 0
$removed = 0
$failed = 0

foreach ($scanRoot in $existingRoots) {
    Get-ChildItem -LiteralPath $scanRoot -Recurse -File -Filter 'metadata.json' -ErrorAction SilentlyContinue |
        Where-Object { Test-LegacyMetadataPath $_ } |
        ForEach-Object {
            $scanned++
            $legacyPath = $_.FullName
            try {
                $language = Normalize-Language $_.Directory.Name
                $textsRoot = $_.Directory.Parent.FullName
                $targetPath = Join-Path $textsRoot "metadata-$language.json"
                $targetExists = Test-Path -LiteralPath $targetPath
                $sourceBundle = Read-JsonBundle $legacyPath
                $targetBundle = Read-JsonBundle $targetPath
                $sourceHasFields = Test-BundleHasFields $sourceBundle
                $changed = $false
                $wouldWrite = $false

                if (-not $targetExists) {
                    if ($sourceHasFields) {
                        $changed = Merge-Bundle -Destination $targetBundle -Source $sourceBundle -Language $language
                        $wouldWrite = $changed -or -not (Test-Path -LiteralPath $targetPath)
                    }
                } elseif ($MergeExisting) {
                    $changed = Merge-Bundle -Destination $targetBundle -Source $sourceBundle -Language $language
                    $wouldWrite = $changed
                }

                if ($wouldWrite) {
                    $convertible++
                }

                if ($Apply) {
                    if ($wouldWrite) {
                        Write-JsonBundle -Path $targetPath -Bundle $targetBundle
                        $written++
                    }

                    if ($RemoveLegacy) {
                        if ($Backup) {
                            Copy-Item -LiteralPath $legacyPath -Destination "$legacyPath.bak" -Force
                        }

                        Remove-Item -LiteralPath $legacyPath -Force
                        $removed++

                        try {
                            Remove-Item -LiteralPath $_.Directory.FullName -Force -ErrorAction Stop
                        } catch {
                            # Keep non-empty language folders.
                        }
                    }
                }
            } catch {
                $failed++
                Write-Warning "FAILED $legacyPath : $($_.Exception.Message)"
            }
        }
}

$mode = if ($Apply) { 'apply' } else { 'dry-run' }
Write-Output "mode=$mode"
Write-Output "roots=$($existingRoots.Count)"
Write-Output "legacy_scanned=$scanned"
Write-Output "convertible=$convertible"
Write-Output "written=$written"
Write-Output "legacy_removed=$removed"
Write-Output "failed=$failed"

if ($failed -gt 0) {
    exit 1
}
