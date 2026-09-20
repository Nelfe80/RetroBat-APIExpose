# Ecrit installer\appsettings.default.json : la configuration que l'installeur livre.
#
# POURQUOI. Le .iss embarquait ..\appsettings.json, c'est-a-dire le fichier de la machine qui
# compile. On livrait donc a tous ceux qui installent le plugin la configuration d'une borne
# particuliere : sa langue, ses collections, ses reglages d'essai, et les reecritures du service
# de synchro. Ici la source est le DEPOT, donc ce qui a ete relu et commite.
$ErrorActionPreference = 'Stop'
$racine = Split-Path $PSScriptRoot -Parent
$sortie = Join-Path $racine 'installer\appsettings.default.json'

Push-Location $racine
try {
    $contenu = & git show HEAD:appsettings.json
    if ($LASTEXITCODE -ne 0 -or -not $contenu) { throw "appsettings.json introuvable dans HEAD." }
} finally { Pop-Location }

# Le fichier doit rester lisible par .NET : UTF-8 avec BOM, comme celui du depot.
[IO.File]::WriteAllText($sortie, ($contenu -join "`r`n"), (New-Object System.Text.UTF8Encoding($true)))

$ctrl = Get-Content $sortie -Raw | ConvertFrom-Json
"configuration livree : auto-scrap={0}, marquee={1}, generation={2}" -f `
    $ctrl.ApiExpose.Scraping.AutoScrapingEnabled, `
    $ctrl.ApiExpose.MarqueeManager.Enabled, `
    $ctrl.ApiExpose.MarqueeManager.AutogenProfile
