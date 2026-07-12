# wrapper_fleet_inventory.ps1
# Inventaire de la flotte de wrappers RetroArch (rapport seul, aucune modification).
#
# Le wrapper (plugins/Wrapper/wrapper.cpp) remplace chaque core dans
# emulators/retroarch/cores/ et charge le vrai core depuis cores_real/.
# Ce script compare chaque core deploye au build de reference et signale :
#  - les wrappers d'une version differente du build de reference
#  - les cores non wrappes
#  - les wrappers sans vrai core dans cores_real/ (jeu incapable de booter)
#
# Usage (depuis une console PowerShell) :
#   .\wrapper_fleet_inventory.ps1 [-Json rapport.json]

param(
    [string]$RetroBatRoot = "",
    [string]$ReferenceWrapper = "",
    [string]$Json = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $RetroBatRoot) {
    # tools/mem-curator -> APIExpose -> plugins -> RetroBat
    $RetroBatRoot = (Resolve-Path (Join-Path $scriptDir "..\..\..\..")).Path
}
if (-not $ReferenceWrapper) {
    $ReferenceWrapper = Join-Path $RetroBatRoot "plugins\APIExpose\wrapper\wrapper.dll"
}

$coresDir = Join-Path $RetroBatRoot "emulators\retroarch\cores"
$coresRealDir = Join-Path $RetroBatRoot "emulators\retroarch\cores_real"
$signature = "RETROBAT_ARCADE_WRAPPER_V1_DO_NOT_DELETE"

if (-not (Test-Path $coresDir)) { Write-Error "cores introuvable: $coresDir" }

$refHash = ""
if (Test-Path $ReferenceWrapper) {
    $refHash = (Get-FileHash $ReferenceWrapper -Algorithm MD5).Hash
    $refInfo = Get-Item $ReferenceWrapper
    Write-Host ("Reference : {0} ({1} octets, {2:yyyy-MM-dd})" -f $ReferenceWrapper, $refInfo.Length, $refInfo.LastWriteTime)
    Write-Host "MD5       : $refHash"
} else {
    Write-Warning "Build de reference introuvable: $ReferenceWrapper"
}

function Test-IsWrapper([string]$path, [long]$length) {
    if ($length -gt 2MB) { return $false }
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $text = [System.Text.Encoding]::ASCII.GetString($bytes)
    return $text.Contains($signature)
}

$rows = @()
foreach ($dll in Get-ChildItem $coresDir -Filter *.dll) {
    $isWrapper = Test-IsWrapper $dll.FullName $dll.Length
    $hash = (Get-FileHash $dll.FullName -Algorithm MD5).Hash
    $hasReal = Test-Path (Join-Path $coresRealDir $dll.Name)
    $status = "OK"
    if (-not $isWrapper) { $status = "NON-WRAPPE" }
    elseif (-not $hasReal) { $status = "SANS-CORE-REEL" }
    elseif ($refHash -and $hash -ne $refHash) { $status = "VERSION-OBSOLETE" }
    $rows += [pscustomobject]@{
        Core       = $dll.Name
        Wrapper    = $isWrapper
        Taille     = $dll.Length
        Date       = $dll.LastWriteTime.ToString("yyyy-MM-dd")
        MD5        = $hash
        CoreReel   = $hasReal
        Statut     = $status
    }
}

$total = $rows.Count
$wrapped = @($rows | Where-Object Wrapper).Count
$current = @($rows | Where-Object { $_.Statut -eq "OK" }).Count
$stale = @($rows | Where-Object { $_.Statut -eq "VERSION-OBSOLETE" })
$broken = @($rows | Where-Object { $_.Statut -eq "SANS-CORE-REEL" })
$unwrapped = @($rows | Where-Object { $_.Statut -eq "NON-WRAPPE" })

Write-Host ""
Write-Host "=== Flotte wrapper : $coresDir ==="
Write-Host "cores: $total | wrappes: $wrapped | a jour: $current | obsoletes: $($stale.Count) | sans core reel: $($broken.Count) | non wrappes: $($unwrapped.Count)"

Write-Host ""
Write-Host "--- builds deployes ---"
$rows | Where-Object Wrapper | Group-Object MD5 | Sort-Object Count -Descending | ForEach-Object {
    $sample = $_.Group[0]
    $tag = if ($refHash -and $_.Name -eq $refHash) { " (= reference)" } else { "" }
    Write-Host ("{0} cores : {1} octets, {2}{3}" -f $_.Count, $sample.Taille, $sample.Date, $tag)
}

if ($broken.Count -gt 0) {
    Write-Host ""
    Write-Warning "Wrappers sans core reel (jeu ne bootera pas) :"
    $broken | ForEach-Object { Write-Host "  $($_.Core)" }
}
if ($unwrapped.Count -gt 0) {
    Write-Host ""
    Write-Host "--- cores non wrappes ---"
    $unwrapped | ForEach-Object { Write-Host "  $($_.Core) ($($_.Taille) octets)" }
}

if ($Json) {
    $report = [pscustomobject]@{
        coresDir = $coresDir
        reference = $ReferenceWrapper
        referenceMd5 = $refHash
        total = $total
        wrapped = $wrapped
        current = $current
        stale = $stale.Count
        cores = $rows
    }
    $report | ConvertTo-Json -Depth 4 | Out-File -FilePath $Json -Encoding utf8
    Write-Host ""
    Write-Host "[+] rapport: $Json"
}
