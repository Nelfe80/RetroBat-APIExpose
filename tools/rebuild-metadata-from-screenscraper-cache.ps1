param(
    [string]$CacheRoot = "",
    [string]$MediaRoot = "",
    [string]$SystemId = "",
    [string]$TargetSystemId = "",
    [switch]$Apply,
    [switch]$ReplaceExisting,
    [switch]$AllPayloads
)

$ErrorActionPreference = 'Stop'

function Normalize-Language {
    param([string]$Raw)

    $value = (($Raw + '').Trim() -replace '_', '-').ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($value)) {
        return ''
    }

    return ($value -split '-', 2)[0]
}

function Normalize-SystemId {
    param([string]$Raw)

    $value = (($Raw + '').Trim() -replace ' ', '_').ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($value)) {
        return ''
    }

    switch ($value) {
        { $_ -in @('mame', 'fbneo', 'fba', 'hbmame') } { return 'arcade' }
        'atarijaguar' { return 'jaguar' }
        'atarijaguarcd' { return 'jaguarcd' }
        'atarilynx' { return 'lynx' }
        default { return $value }
    }
}

function Get-ObjectProperty {
    param(
        [object]$Object,
        [string[]]$Names
    )

    if ($null -eq $Object) {
        return $null
    }

    foreach ($name in $Names) {
        $property = $Object.PSObject.Properties |
            Where-Object { $_.Name -ieq $name } |
            Select-Object -First 1
        if ($null -ne $property) {
            return $property.Value
        }
    }

    return $null
}

function Read-String {
    param(
        [object]$Object,
        [string[]]$Names
    )

    $value = Get-ObjectProperty -Object $Object -Names $Names
    if ($null -eq $value) {
        return ''
    }

    if ($value -is [string]) {
        return Normalize-Text $value
    }

    if ($value -is [System.ValueType]) {
        return Normalize-Text ([string]$value)
    }

    return ''
}

function Normalize-Text {
    param([string]$Value)

    $decoded = [System.Net.WebUtility]::HtmlDecode(($Value + '').Trim())
    return $decoded -replace "`r`n", "`n"
}

function Add-TextField {
    param(
        [System.Collections.IDictionary]$Fields,
        [string]$Name,
        [string]$Value
    )

    $normalized = Normalize-Text $Value
    if ([string]::IsNullOrWhiteSpace($Name) -or [string]::IsNullOrWhiteSpace($normalized)) {
        return
    }

    if (-not $Fields.Contains($Name) -or [string]::IsNullOrWhiteSpace([string]$Fields[$Name])) {
        $Fields[$Name] = $normalized
        return
    }

    if (([string]$Fields[$Name]).Trim().Length -lt $normalized.Trim().Length) {
        $Fields[$Name] = $normalized
    }
}

function Add-LocalizedValue {
    param(
        [System.Collections.IDictionary]$Map,
        [string]$Language,
        [string]$Value
    )

    $languageCode = Normalize-Language $Language
    $text = Normalize-Text $Value
    if ([string]::IsNullOrWhiteSpace($languageCode) -or [string]::IsNullOrWhiteSpace($text)) {
        return
    }

    if (-not $Map.Contains($languageCode)) {
        $Map[$languageCode] = New-Object System.Collections.Generic.List[string]
    }

    $list = [System.Collections.Generic.List[string]]$Map[$languageCode]
    if (-not ($list | Where-Object { $_ -ieq $text })) {
        $list.Add($text)
    }
}

function Read-LocalizedTexts {
    param(
        [object]$Node,
        [string[]]$ValueKeys
    )

    $map = [ordered]@{}
    if ($null -eq $Node) {
        return $map
    }

    if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) {
        foreach ($entry in $Node) {
            $language = Read-String $entry @('langue', 'lang', 'language')
            $value = Read-String $entry $ValueKeys
            Add-LocalizedValue -Map $map -Language $language -Value $value
        }
        return $map
    }

    if ($Node -is [string]) {
        Add-LocalizedValue -Map $map -Language 'en' -Value $Node
        return $map
    }

    foreach ($property in $Node.PSObject.Properties) {
        if ($property.Value -is [string]) {
            Add-LocalizedValue -Map $map -Language $property.Name -Value $property.Value
            continue
        }

        $language = Read-String $property.Value @('langue', 'lang', 'language')
        if ([string]::IsNullOrWhiteSpace($language)) {
            $language = $property.Name
        }

        $value = Read-String $property.Value $ValueKeys
        Add-LocalizedValue -Map $map -Language $language -Value $value
    }

    return $map
}

function Read-LocalizedCollection {
    param(
        [object]$Node,
        [string]$SingularName
    )

    $map = [ordered]@{}
    if ($null -eq $Node) {
        return $map
    }

    $items = @()
    if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) {
        $items = @($Node)
    } else {
        $items = @($Node)
    }

    foreach ($item in $items) {
        $names = Get-ObjectProperty -Object $item -Names @('noms', 'names', $SingularName, 'nom', 'name')
        if ($null -eq $names) {
            continue
        }

        if ($names -is [System.Collections.IEnumerable] -and $names -isnot [string]) {
            foreach ($entry in $names) {
                Add-LocalizedValue `
                    -Map $map `
                    -Language (Read-String $entry @('langue', 'lang', 'language')) `
                    -Value (Read-String $entry @('text', 'value', 'nom', 'name', 'libelle', 'label'))
            }
            continue
        }

        foreach ($property in $names.PSObject.Properties) {
            if ($property.Value -is [string]) {
                Add-LocalizedValue -Map $map -Language $property.Name -Value $property.Value
            }
        }
    }

    return $map
}

function Join-LocalizedValues {
    param(
        [System.Collections.IDictionary]$Map,
        [string]$Language
    )

    if (-not $Map.Contains($Language)) {
        return ''
    }

    $values = [System.Collections.Generic.List[string]]$Map[$Language]
    return ($values | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) -join ', '
}

function Select-GameName {
    param([object]$Game)

    $names = Get-ObjectProperty -Object $Game -Names @('noms', 'names')
    if ($names -is [System.Collections.IEnumerable] -and $names -isnot [string]) {
        foreach ($region in @('ss', 'wor', 'us', 'eu', 'jp')) {
            foreach ($entry in $names) {
                $entryRegion = Read-String $entry @('region', 'regionshortname')
                if ($entryRegion -ieq $region) {
                    $value = Read-String $entry @('text', 'value', 'nom', 'name')
                    if (-not [string]::IsNullOrWhiteSpace($value)) {
                        return $value
                    }
                }
            }
        }

        foreach ($entry in $names) {
            $value = Read-String $entry @('text', 'value', 'nom', 'name')
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                return $value
            }
        }
    }

    return Read-String $Game @('nom', 'name')
}

function Normalize-ReleaseDate {
    param([string]$Value)

    $raw = (($Value + '').Trim())
    if ($raw -match '^\d{8}T\d{6}$') {
        return $raw
    }

    if ($raw -match '^(\d{4})-(\d{2})-(\d{2})') {
        return "$($Matches[1])$($Matches[2])$($Matches[3])T000000"
    }

    if ($raw -match '^(\d{4})(\d{2})(\d{2})$') {
        return "$raw`T000000"
    }

    if ($raw -match '^(\d{4})$') {
        return "$($Matches[1])0101T000000"
    }

    return ''
}

function Read-ReleaseDate {
    param([object]$Game)

    $dates = Get-ObjectProperty -Object $Game -Names @('dates', 'date')
    if ($dates -is [System.Collections.IEnumerable] -and $dates -isnot [string]) {
        foreach ($entry in $dates) {
            $value = Read-String $entry @('text', 'value', 'date')
            $normalized = Normalize-ReleaseDate $value
            if (-not [string]::IsNullOrWhiteSpace($normalized)) {
                return $normalized
            }
        }
    }

    return Normalize-ReleaseDate (Read-String $Game @('date', 'releasedate'))
}

function Normalize-Rating {
    param([string]$Value)

    $raw = (($Value + '').Trim() -replace ',', '.')
    $number = 0.0
    if (-not [double]::TryParse(
        $raw,
        [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [ref]$number)) {
        return ''
    }

    if ($number -gt 1) {
        $number = $number / 20.0
    }

    $number = [Math]::Min(1.0, [Math]::Max(0.0, $number))
    return $number.ToString('0.###', [System.Globalization.CultureInfo]::InvariantCulture)
}

function Read-Region {
    param([object]$Game)

    $rom = Get-ObjectProperty -Object $Game -Names @('rom')
    $region = Read-String $rom @('romregions', 'region', 'regionshortname')
    if (-not [string]::IsNullOrWhiteSpace($region)) {
        return $region
    }

    return Read-String $Game @('region', 'regionshortname')
}

function Resolve-GameNode {
    param([object]$Payload)

    $response = Get-ObjectProperty -Object $Payload -Names @('response')
    $game = Get-ObjectProperty -Object $response -Names @('jeu', 'game')
    if ($game -is [System.Collections.IEnumerable] -and $game -isnot [string]) {
        return @($game | Select-Object -First 1)[0]
    }

    return $game
}

function Convert-PayloadToBundles {
    param(
        [string]$Path,
        [string]$System,
        [string]$Slug
    )

    $payload = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    $game = Resolve-GameNode $payload
    if ($null -eq $game) {
        return @()
    }

    $synopsis = Read-LocalizedTexts -Node (Get-ObjectProperty -Object $game -Names @('synopsis')) -ValueKeys @('text', 'value', 'synopsis')
    $genres = Read-LocalizedCollection -Node (Get-ObjectProperty -Object $game -Names @('genres')) -SingularName 'genre'
    $families = Read-LocalizedCollection -Node (Get-ObjectProperty -Object $game -Names @('familles', 'families')) -SingularName 'famille'

    $languages = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($map in @($synopsis, $genres, $families)) {
        foreach ($language in $map.Keys) {
            [void]$languages.Add((Normalize-Language $language))
        }
    }

    $rom = Get-ObjectProperty -Object $game -Names @('rom')
    $common = [ordered]@{}
    Add-TextField -Fields $common -Name 'name' -Value (Select-GameName $game)
    Add-TextField -Fields $common -Name 'releasedate' -Value (Read-ReleaseDate $game)
    Add-TextField -Fields $common -Name 'developer' -Value (Read-String (Get-ObjectProperty -Object $game -Names @('developpeur', 'developer')) @('text', 'nom', 'name'))
    Add-TextField -Fields $common -Name 'publisher' -Value (Read-String (Get-ObjectProperty -Object $game -Names @('editeur', 'publisher')) @('text', 'nom', 'name'))
    Add-TextField -Fields $common -Name 'players' -Value (Read-String (Get-ObjectProperty -Object $game -Names @('joueurs', 'players')) @('text', 'nombre', 'players'))
    Add-TextField -Fields $common -Name 'region' -Value (Read-Region $game)
    Add-TextField -Fields $common -Name 'rating' -Value (Normalize-Rating (Read-String $game @('score', 'note', 'rating')))
    Add-TextField -Fields $common -Name 'md5' -Value (Read-String $rom @('rommd5', 'md5'))
    Add-TextField -Fields $common -Name 'crc32' -Value (Read-String $rom @('romcrc', 'crc', 'crc32'))
    Add-TextField -Fields $common -Name 'gameid' -Value (Read-String $game @('id', 'idjeu', 'ss_id'))
    Add-TextField -Fields $common -Name 'system' -Value $System
    Add-TextField -Fields $common -Name 'source' -Value 'screenscraper'

    $bundles = @()
    foreach ($language in ($languages | Sort-Object)) {
        if ([string]::IsNullOrWhiteSpace($language)) {
            continue
        }

        $fields = [ordered]@{}
        foreach ($entry in $common.GetEnumerator()) {
            Add-TextField -Fields $fields -Name $entry.Key -Value $entry.Value
        }

        Add-TextField -Fields $fields -Name 'desc' -Value (Join-LocalizedValues -Map $synopsis -Language $language)
        Add-TextField -Fields $fields -Name 'genre' -Value (Join-LocalizedValues -Map $genres -Language $language)
        Add-TextField -Fields $fields -Name 'family' -Value (Join-LocalizedValues -Map $families -Language $language)
        Add-TextField -Fields $fields -Name 'lang' -Value $language

        $hasLocalizedText = $fields.Contains('desc') -or $fields.Contains('genre') -or $fields.Contains('family')
        if (-not $hasLocalizedText) {
            continue
        }

        $bundles += [pscustomobject]@{
            System = $System
            Slug = $Slug
            Language = $language
            Fields = $fields
            Source = $Path
        }
    }

    return $bundles
}

function Write-Bundle {
    param(
        [object]$Bundle,
        [string]$TargetPath
    )

    $document = [ordered]@{
        Language = $Bundle.Language
        Fields = $Bundle.Fields
        UpdatedAtUtc = [DateTime]::UtcNow.ToString('o')
    }

    $directory = Split-Path -Parent $TargetPath
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $tempPath = "$TargetPath.$PID.tmp"
    try {
        $document | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $tempPath -Encoding UTF8
        Move-Item -LiteralPath $tempPath -Destination $TargetPath -Force
    } finally {
        if (Test-Path -LiteralPath $tempPath) {
            Remove-Item -LiteralPath $tempPath -Force
        }
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($CacheRoot)) {
    $CacheRoot = Join-Path $repoRoot 'media\scrap-cache\screenscraper\games'
}

if ([string]::IsNullOrWhiteSpace($MediaRoot)) {
    $MediaRoot = Join-Path $repoRoot 'media\systems'
}

$cacheRootPath = [System.IO.Path]::GetFullPath($CacheRoot)
$mediaRootPath = [System.IO.Path]::GetFullPath($MediaRoot)

if (-not (Test-Path -LiteralPath $cacheRootPath)) {
    throw "ScreenScraper cache root introuvable: $cacheRootPath"
}

function Resolve-PayloadCacheSystem {
    param([System.IO.FileInfo]$Payload)

    $parent = Split-Path $Payload.DirectoryName -Leaf
    $grandParent = Split-Path (Split-Path $Payload.DirectoryName -Parent) -Leaf
    if ($grandParent -ieq 'games') {
        return $parent
    }

    return $grandParent
}

function Resolve-PayloadSlug {
    param([System.IO.FileInfo]$Payload)

    $grandParent = Split-Path (Split-Path $Payload.DirectoryName -Parent) -Leaf
    if ($grandParent -ieq 'games') {
        return [System.IO.Path]::GetFileNameWithoutExtension($Payload.Name)
    }

    return Split-Path $Payload.DirectoryName -Leaf
}

$payloads = Get-ChildItem -LiteralPath $cacheRootPath -Recurse -File -Filter '*.json' |
    Where-Object {
        if ([string]::IsNullOrWhiteSpace($SystemId)) {
            return $true
        }

        $cacheSystem = Resolve-PayloadCacheSystem $_
        $targetSystem = if ([string]::IsNullOrWhiteSpace($TargetSystemId)) {
            Normalize-SystemId $cacheSystem
        } else {
            Normalize-SystemId $TargetSystemId
        }

        return $cacheSystem -ieq $SystemId -or
            $targetSystem -ieq (Normalize-SystemId $SystemId)
    }

if (-not $AllPayloads) {
    $payloads = $payloads |
        Group-Object { "$(Resolve-PayloadCacheSystem $_)|$(Resolve-PayloadSlug $_)" } |
        ForEach-Object { $_.Group | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 }
}

$payloads = @($payloads | Sort-Object FullName)
$payloadCount = 0
$bundleCount = 0
$written = 0
$skippedExisting = 0
$failed = 0

foreach ($payload in $payloads) {
    $payloadCount++
    $slug = Resolve-PayloadSlug $payload
    $cacheSystem = Resolve-PayloadCacheSystem $payload
    $system = if ([string]::IsNullOrWhiteSpace($TargetSystemId)) {
        Normalize-SystemId $cacheSystem
    } else {
        Normalize-SystemId $TargetSystemId
    }
    try {
        $bundles = Convert-PayloadToBundles -Path $payload.FullName -System $system -Slug $slug
        foreach ($bundle in $bundles) {
            $bundleCount++
            $targetPath = Join-Path $mediaRootPath (Join-Path $bundle.System (Join-Path 'games' (Join-Path $bundle.Slug (Join-Path 'texts' "metadata-$($bundle.Language).json"))))
            if ((Test-Path -LiteralPath $targetPath) -and -not $ReplaceExisting) {
                $skippedExisting++
                continue
            }

            if ($Apply) {
                Write-Bundle -Bundle $bundle -TargetPath $targetPath
                $written++
            }
        }
    } catch {
        $failed++
        Write-Warning "FAILED $($payload.FullName): $($_.Exception.Message)"
    }
}

$mode = if ($Apply) { 'apply' } else { 'dry-run' }
Write-Output "mode=$mode"
Write-Output "cache_root=$cacheRootPath"
Write-Output "media_root=$mediaRootPath"
Write-Output "payloads=$payloadCount"
Write-Output "bundles=$bundleCount"
Write-Output "written=$written"
Write-Output "skipped_existing=$skippedExisting"
Write-Output "failed=$failed"

if ($failed -gt 0) {
    exit 1
}
