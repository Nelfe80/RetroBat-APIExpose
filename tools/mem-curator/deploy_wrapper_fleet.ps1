# deploy_wrapper_fleet.ps1
# Deploie le build de reference du wrapper sur tous les cores RetroArch wrappes.
#
# - La source est plugins\APIExpose\wrapper\wrapper.dll (build signe de reference).
# - Chaque DLL de emulators\retroarch\cores\ est remplacee par ce build
#   (le nom de fichier cible est conserve : c'est ainsi que le wrapper
#   retrouve son vrai core dans cores_real\).
# - Un backup date des wrappers actuels est fait avant remplacement.
# - Seules les DLL portant la signature RETROBAT_ARCADE_WRAPPER sont
#   remplacees ; un core non wrappe n'est jamais ecrase.
#
# Usage (depuis une console PowerShell) :
#   .\deploy_wrapper_fleet.ps1 [-WhatIf]

param(
    [string]$RetroBatRoot = "",
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $RetroBatRoot) {
    $RetroBatRoot = (Resolve-Path (Join-Path $scriptDir "..\..\..\..")).Path
}

$reference = Join-Path $RetroBatRoot "plugins\APIExpose\wrapper\wrapper.dll"
$coresDir = Join-Path $RetroBatRoot "emulators\retroarch\cores"
$coresRealDir = Join-Path $RetroBatRoot "emulators\retroarch\cores_real"
$signature = "RETROBAT_ARCADE_WRAPPER_V1_DO_NOT_DELETE"
$backupDir = Join-Path $RetroBatRoot ("plugins\APIExpose\.archive\wrapper-fleet-backup-" + (Get-Date -Format "yyyyMMdd-HHmmss"))

if (-not (Test-Path $reference)) { Write-Error "build de reference introuvable: $reference" }
$refInfo = Get-Item $reference
$refHash = (Get-FileHash $reference -Algorithm MD5).Hash
Write-Host ("Reference : {0} v{1} ({2} octets)" -f $reference, $refInfo.VersionInfo.FileVersion, $refInfo.Length)

function Test-IsWrapper([System.IO.FileInfo]$file) {
    if ($file.Length -gt 2MB) { return $false }
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    return [System.Text.Encoding]::ASCII.GetString($bytes).Contains($signature)
}

$targets = @()
foreach ($dll in Get-ChildItem $coresDir -Filter *.dll) {
    if (-not (Test-IsWrapper $dll)) {
        Write-Warning "non wrappe, ignore : $($dll.Name)"
        continue
    }
    if (-not (Test-Path (Join-Path $coresRealDir $dll.Name))) {
        Write-Warning "core reel absent, ignore : $($dll.Name)"
        continue
    }
    $targets += $dll
}

$upToDate = @($targets | Where-Object { (Get-FileHash $_.FullName -Algorithm MD5).Hash -eq $refHash })
$toUpdate = @($targets | Where-Object { (Get-FileHash $_.FullName -Algorithm MD5).Hash -ne $refHash })
Write-Host "wrappes: $($targets.Count) | deja a jour: $($upToDate.Count) | a mettre a jour: $($toUpdate.Count)"

if ($toUpdate.Count -eq 0) { Write-Host "Rien a faire."; exit 0 }
if ($WhatIf) {
    $toUpdate | ForEach-Object { Write-Host "  [whatif] $($_.Name)" }
    exit 0
}

New-Item -ItemType Directory -Force $backupDir | Out-Null
foreach ($dll in $toUpdate) {
    Copy-Item $dll.FullName (Join-Path $backupDir $dll.Name)
    Copy-Item $reference $dll.FullName -Force
}
Write-Host "Deploye sur $($toUpdate.Count) cores. Backup : $backupDir"
Write-Host "Verifier ensuite avec .\wrapper_fleet_inventory.ps1 puis tester un jeu console."
