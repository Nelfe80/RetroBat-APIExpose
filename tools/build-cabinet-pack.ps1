param(
    [string]$Output = ""
)

# Pack BORNE distribuable (assembleurs / salles) : un zip pret a deposer dans
# <RetroBat>\plugins\APIExpose - exe unique, hook de demarrage ES, modele de
# configuration et licences. Le Data Pack (definitions .MEM + medias) se
# distribue separement (volumineux).
# NOTE : script en ASCII pur (PowerShell 5.1 lit les .ps1 sans BOM en ANSI).

$ErrorActionPreference = "Stop"
$PluginRoot = Split-Path -Parent $PSScriptRoot

$Exe = Join-Path $PluginRoot "RetroBat.Api.exe"
if (-not (Test-Path -LiteralPath $Exe)) {
    throw "RetroBat.Api.exe introuvable a la racine : lancez build.bat d'abord."
}

$Version = (Get-Item -LiteralPath $Exe).LastWriteTime.ToString("yyyyMMdd")
if ($Output.Length -eq 0) {
    $DistDir = Join-Path $PluginRoot "dist"
    New-Item -ItemType Directory -Force $DistDir | Out-Null
    $Output = Join-Path $DistDir "APIExpose-CabinetPack-$Version.zip"
}

$Stage = Join-Path ([System.IO.Path]::GetTempPath()) "apiexpose-cabinet-pack"
if (Test-Path -LiteralPath $Stage) {
    cmd.exe /c "rmdir /s /q `"$Stage`"" | Out-Null
}
New-Item -ItemType Directory -Path $Stage | Out-Null

Copy-Item -LiteralPath $Exe -Destination $Stage
Copy-Item -LiteralPath (Join-Path $PluginRoot "install-es-start-hook.bat") -Destination $Stage

# Modele de configuration : la cle API et les overlays se reglent salle par
# salle - jamais de secrets dans le pack.
$Template = [ordered]@{
    Urls = "http://0.0.0.0:12345"
    Security = [ordered]@{ ApiKey = "CHANGEZ-MOI-cle-partagee-avec-le-hub" }
    ApiExpose = [ordered]@{
        CabinetBadgeOverlay = [ordered]@{ Enabled = $true; Title = "Scannez pour jouer" }
    }
}
$Template | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $Stage "appsettings.sample.json") -Encoding utf8

foreach ($doc in @("LICENSE.md", "COMMERCIAL-LICENSE.md", "BUILDER-LICENSE.md", "NOTICE.md")) {
    $srcDoc = Join-Path $PluginRoot $doc
    if (Test-Path -LiteralPath $srcDoc) {
        Copy-Item -LiteralPath $srcDoc -Destination $Stage
    }
}

$readme = @(
    "APIExpose - pack borne",
    "======================",
    "",
    "1. Copiez ce dossier dans  <RetroBat>\plugins\APIExpose  de la borne.",
    "2. Renommez  appsettings.sample.json  en  appsettings.json  et personnalisez :",
    "   - Urls : http://0.0.0.0:12345 pour une salle (le hub joint la borne par",
    "     le LAN) ; supprimez la ligne pour une borne solo/domicile (loopback).",
    "   - Security:ApiKey : la MEME cle que Hub:CabinetApiKey du Fleet Hub.",
    "   - CabinetBadgeOverlay : le QR d'identification en bas d'ecran (salle).",
    "3. Lancez  install-es-start-hook.bat  une fois : APIExpose demarre avec",
    "   RetroBat.",
    "4. Data Pack (definitions .MEM + medias) : deposez les dossiers resources\",
    "   et media\ fournis separement a cote de l'exe.",
    "",
    "Guides : https://nelfe80.github.io/NelfeTech-Guides/",
    "Licences salle/assembleur : https://www.nelfetech.com/salles.html"
)
$readme -join "`r`n" | Set-Content -Path (Join-Path $Stage "LISEZMOI.txt") -Encoding utf8

if (Test-Path -LiteralPath $Output) {
    Remove-Item -LiteralPath $Output -Force
}
Compress-Archive -Path (Join-Path $Stage "*") -DestinationPath $Output

[pscustomobject]@{
    Pack = $Output
    SizeMB = [math]::Round((Get-Item -LiteralPath $Output).Length / 1MB, 2)
} | Format-List
