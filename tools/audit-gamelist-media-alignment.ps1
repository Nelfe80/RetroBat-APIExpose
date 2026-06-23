param(
    [string]$SystemId = "nes",
    [string]$OutputPath = "",
    [int]$SampleSize = 20
)

$ErrorActionPreference = "Stop"

$pluginRoot = Split-Path -Parent $PSScriptRoot
$retroBatRoot = Resolve-Path (Join-Path $pluginRoot "..\..")
$romsRoot = Join-Path $retroBatRoot "roms"
$esSettingsPath = Join-Path $retroBatRoot "emulationstation\.emulationstation\es_settings.cfg"
$appSettingsPath = Join-Path $pluginRoot "appsettings.json"

function Read-EsSetting {
    param([xml]$Document, [string]$Name)

    if ($null -eq $Document -or $null -eq $Document.config) {
        return $null
    }

    $node = $Document.config.string | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if ($null -eq $node) {
        return $null
    }

    return [string]$node.value
}

function Resolve-WheelStyleKind {
    param([string]$WheelStyle)

    $normalized = if ($null -eq $WheelStyle) { "" } else { $WheelStyle.Trim().ToLowerInvariant() }
    if ($normalized -in @("steel", "wheel-steel", "wheelsteel")) {
        return "wheelsteel"
    }

    return "wheelcarbon"
}

function Resolve-ExpectedKind {
    param(
        [string]$Source,
        [string]$Target,
        [string]$WheelStyle
    )

    $normalized = if ($null -eq $Source) { "" } else { $Source.Trim().ToLowerInvariant() }
    switch ($normalized) {
        "logo" { return "wheel" }
        "wheel" { return "wheel" }
        "wheel-hd" {
            if ($Target -eq "logo") {
                return Resolve-WheelStyleKind $WheelStyle
            }
            return "wheelcarbon"
        }
        "wheel-carbon" { return "wheelcarbon" }
        "wheelcarbon" { return "wheelcarbon" }
        "wheel-steel" { return "wheelsteel" }
        "wheelsteel" { return "wheelsteel" }
        "marquee" { return "marquee" }
        "screenmarquee" { return "screenmarquee" }
        "screen-marquee" { return "screenmarquee" }
        "screenmarqueesmall" { return "screenmarqueesmall" }
        "screen-marquee-small" { return "screenmarqueesmall" }
        "ss" { return "thumbnail" }
        "screenshot" { return "thumbnail" }
        "sstitle" { return "image" }
        "title" { return "image" }
        "box2d" { return "box2d" }
        "box-2d" { return "box2d" }
        "box3d" { return "box3d" }
        "box-3d" { return "box3d" }
        "fanart" { return "fanart" }
        "boxback" { return "boxback" }
        "box-back" { return "boxback" }
        default { return "" }
    }
}

function Get-ExpectedSuffixes {
    param([string]$Kind)

    switch ($Kind) {
        "image" { return @("-image", "-titleshot") }
        "thumbnail" { return @("-thumb") }
        "wheel" { return @("-wheel") }
        "wheelcarbon" { return @("-wheelcarbon") }
        "wheelsteel" { return @("-wheelsteel") }
        "marquee" { return @("-marquee") }
        "screenmarquee" { return @("-screenmarquee") }
        "screenmarqueesmall" { return @("-screenmarqueesmall") }
        "box2d" { return @("-box2d") }
        "box3d" { return @("-box3d") }
        "fanart" { return @("-fanart") }
        "boxback" { return @("-boxback") }
        default { return @() }
    }
}

function Get-CanonicalPatterns {
    param([string]$Kind)

    switch ($Kind) {
        "image" { return @("/games/[^/]+/image.") }
        "thumbnail" { return @("/games/[^/]+/artwork/thumbnail.", "/games/[^/]+/artwork/box/front.") }
        "wheel" { return @("/games/[^/]+/ui/wheels/wheel.") }
        "wheelcarbon" { return @("/games/[^/]+/ui/wheels/wheel-carbon.") }
        "wheelsteel" { return @("/games/[^/]+/ui/wheels/wheel-steel.") }
        "marquee" { return @("/games/[^/]+/artwork/marquee/marquee.") }
        "screenmarquee" { return @("/games/[^/]+/artwork/screenmarquee/screenmarquee.") }
        "screenmarqueesmall" { return @("/games/[^/]+/artwork/screenmarquee/screenmarquee-small.") }
        "box2d" { return @("/games/[^/]+/artwork/box/front.") }
        "box3d" { return @("/games/[^/]+/artwork/box/3d.") }
        "fanart" { return @("/games/[^/]+/artwork/fanart/fanart.") }
        "boxback" { return @("/games/[^/]+/artwork/box/back.") }
        default { return @() }
    }
}

function Resolve-MediaPath {
    param([string]$SystemRoot, [string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $normalized = $Value.Trim().Replace("/", "\")
    if ([System.IO.Path]::IsPathRooted($normalized)) {
        return $normalized
    }

    if ($normalized.StartsWith(".\")) {
        $normalized = $normalized.Substring(2)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $SystemRoot $normalized))
}

function Test-ProjectedCandidateExists {
    param(
        [string]$StorageSystemRoot,
        [string]$ProjectionBaseName,
        [string]$Kind
    )

    $suffixes = Get-ExpectedSuffixes $Kind
    if ($suffixes.Count -eq 0) {
        return $false
    }

    $folder = Join-Path $StorageSystemRoot "images"
    if (-not (Test-Path -LiteralPath $folder)) {
        return $false
    }

    foreach ($suffix in $suffixes) {
        if (Get-ChildItem -LiteralPath $folder -File -Filter "$ProjectionBaseName$suffix.*" -ErrorAction SilentlyContinue | Select-Object -First 1) {
            return $true
        }
    }

    return $false
}

function Test-KindMatch {
    param([string]$Value, [string]$Kind)

    if ([string]::IsNullOrWhiteSpace($Value) -or [string]::IsNullOrWhiteSpace($Kind)) {
        return $false
    }

    $normalized = $Value.Replace("\", "/")
    foreach ($suffix in (Get-ExpectedSuffixes $Kind)) {
        if ($normalized -match [regex]::Escape($suffix) + "\.") {
            return $true
        }
    }

    foreach ($pattern in (Get-CanonicalPatterns $Kind)) {
        if ($normalized -match $pattern) {
            return $true
        }
    }

    return $false
}

function Classify-Slot {
    param(
        [string]$SystemRoot,
        [string]$StorageSystemRoot,
        [string]$ProjectionBaseName,
        [string]$Slot,
        [string]$Value,
        [string]$Kind
    )

    $diskPath = Resolve-MediaPath $SystemRoot $Value
    $hasProjectedCandidate = Test-ProjectedCandidateExists $StorageSystemRoot $ProjectionBaseName $Kind

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return [ordered]@{
            slot = $Slot
            expectedKind = $Kind
            status = if ($hasProjectedCandidate) { "empty-but-projected-media-exists" } else { "empty-no-media" }
            value = ""
            exists = $false
            projectedCandidateExists = $hasProjectedCandidate
        }
    }

    $exists = Test-Path -LiteralPath $diskPath
    $kindMatch = Test-KindMatch $Value $Kind
    $isProjected = $Value.Replace("\", "/") -match "^\./images/"

    $status = if (-not $exists) {
        "missing-file"
    } elseif (-not $kindMatch) {
        "wrong-kind"
    } elseif (-not $isProjected) {
        "ok-non-projected"
    } else {
        "ok"
    }

    return [ordered]@{
        slot = $Slot
        expectedKind = $Kind
        status = $status
        value = $Value
        exists = $exists
        projectedCandidateExists = $hasProjectedCandidate
    }
}

if (-not (Test-Path -LiteralPath $appSettingsPath)) {
    throw "appsettings.json introuvable: $appSettingsPath"
}

$appSettings = Get-Content -LiteralPath $appSettingsPath -Raw | ConvertFrom-Json
$wheelStyle = $appSettings.ApiExpose.Scraping.WheelStyle
if ([string]::IsNullOrWhiteSpace($wheelStyle)) {
    $wheelStyle = "carbon"
}

[xml]$esSettings = if (Test-Path -LiteralPath $esSettingsPath) {
    Get-Content -LiteralPath $esSettingsPath -Raw
} else {
    "<config />"
}

$imageSource = Read-EsSetting $esSettings "ScrapperImageSrc"
$logoSource = Read-EsSetting $esSettings "ScrapperLogoSrc"
$thumbSource = Read-EsSetting $esSettings "ScrapperThumbSrc"
if ([string]::IsNullOrWhiteSpace($imageSource)) { $imageSource = "ss" }
if ([string]::IsNullOrWhiteSpace($logoSource)) { $logoSource = "logo" }
if ([string]::IsNullOrWhiteSpace($thumbSource)) { $thumbSource = "box2d" }

$imageKind = Resolve-ExpectedKind $imageSource "image" $wheelStyle
$logoKind = Resolve-ExpectedKind $logoSource "logo" $wheelStyle
$thumbKind = Resolve-ExpectedKind $thumbSource "thumbnail" $wheelStyle

$systemRoot = Join-Path $romsRoot $SystemId
$storageSystemId = if ($SystemId -in @("mame", "mame64", "fbneo", "fba", "hbmame")) { "arcade" } else { $SystemId }
$storageSystemRoot = Join-Path $romsRoot $storageSystemId
$gamelistPath = Join-Path $systemRoot "gamelist.xml"
if (-not (Test-Path -LiteralPath $gamelistPath)) {
    throw "gamelist.xml introuvable: $gamelistPath"
}

[xml]$gamelist = Get-Content -LiteralPath $gamelistPath -Raw
$slotResults = New-Object System.Collections.Generic.List[object]
$gameCount = 0

foreach ($game in $gamelist.gameList.game) {
    $gameCount++
    $gamePath = [string]$game.path
    $projectionBaseName = [System.IO.Path]::GetFileNameWithoutExtension($gamePath.Replace("/", "\"))
    if ([string]::IsNullOrWhiteSpace($projectionBaseName)) {
        $projectionBaseName = [string]$game.name
    }

    foreach ($slotAudit in @(
        (Classify-Slot $systemRoot $storageSystemRoot $projectionBaseName "image" ([string]$game.image) $imageKind),
        (Classify-Slot $systemRoot $storageSystemRoot $projectionBaseName "thumbnail" ([string]$game.thumbnail) $thumbKind),
        (Classify-Slot $systemRoot $storageSystemRoot $projectionBaseName "marquee" ([string]$game.marquee) $logoKind)
    )) {
        $slotAudit.game = [string]$game.name
        $slotAudit.path = $gamePath
        $slotResults.Add([pscustomobject]$slotAudit)
    }
}

$byStatus = $slotResults |
    Group-Object slot, status |
    ForEach-Object {
        $parts = $_.Name -split ", "
        [pscustomobject]@{
            slot = $parts[0]
            status = $parts[1]
            count = $_.Count
        }
    } |
    Sort-Object slot, status

$issues = $slotResults | Where-Object { $_.status -in @("wrong-kind", "missing-file", "empty-but-projected-media-exists") }
$samples = $issues | Select-Object -First $SampleSize game, path, slot, expectedKind, status, value

$report = [ordered]@{
    generatedAt = (Get-Date).ToString("o")
    systemId = $SystemId
    gamelistPath = $gamelistPath
    settings = [ordered]@{
        imageSource = $imageSource
        imageKind = $imageKind
        logoSource = $logoSource
        logoKind = $logoKind
        thumbSource = $thumbSource
        thumbKind = $thumbKind
        wheelStyle = $wheelStyle
    }
    games = $gameCount
    slots = $slotResults.Count
    issueSlots = $issues.Count
    byStatus = @($byStatus)
    samples = @($samples)
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $outputDir = Join-Path $pluginRoot "artifacts\gamelist-audit"
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
    $OutputPath = Join-Path $outputDir "$SystemId-media-alignment-$stamp.json"
}

$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding UTF8

Write-Output "Audit gamelist media: $SystemId"
Write-Output "Settings: image=$imageSource/$imageKind, thumbnail=$thumbSource/$thumbKind, marquee=$logoSource/$logoKind, wheelStyle=$wheelStyle"
Write-Output "Games: $gameCount; slots: $($slotResults.Count); issueSlots: $($issues.Count)"
$byStatus | Format-Table -AutoSize
if ($samples.Count -gt 0) {
    Write-Output "Samples:"
    $samples | Format-Table -AutoSize
}
Write-Output "Report: $OutputPath"
