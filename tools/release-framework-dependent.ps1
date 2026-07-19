param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$NoRootCopy
)

$ErrorActionPreference = "Stop"

$PluginRoot = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $PluginRoot "src\RetroBat.Api\RetroBat.Api.csproj"
$PropsPath = Join-Path $PluginRoot "Directory.Build.props"
$ArtifactsRoot = Join-Path $PluginRoot "artifacts\release"
$TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "APIExpose-publish"

if (-not (Test-Path -LiteralPath $ProjectPath)) {
    throw "Project not found: $ProjectPath"
}

$Version = (Get-Date).ToString("yyyyMMdd.HHmmss")
if (Test-Path -LiteralPath $PropsPath) {
    $Props = Get-Content -LiteralPath $PropsPath -Raw -Encoding UTF8
    if ($Props -match "<InformationalVersion>([^<]+)</InformationalVersion>") {
        $Version = ($Matches[1] -replace "[^a-zA-Z0-9._+-]", "-")
    }
}

$ReleaseName = "api-$Version-$Runtime-framework-dependent-singlefile"
$TempPublish = Join-Path $TempRoot $ReleaseName
$ReleaseDir = Join-Path $ArtifactsRoot $ReleaseName

foreach ($Path in @($TempPublish, $ReleaseDir)) {
    if (Test-Path -LiteralPath $Path) {
        cmd.exe /c "rmdir /s /q `"$Path`"" | Out-Null
    }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

dotnet publish $ProjectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    /p:PublishSingleFile=true `
    /p:PublishTrimmed=false `
    /p:DebugType=none `
    /p:DebugSymbols=false `
    /p:GenerateDocumentationFile=false `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    -o $TempPublish

$PublishedWebConfig = Join-Path $TempPublish "web.config"
if (Test-Path -LiteralPath $PublishedWebConfig) {
    $WebConfig = Get-Content -LiteralPath $PublishedWebConfig -Raw -Encoding UTF8
    $WebConfig = $WebConfig.Replace('.\logs\stdout', '.\.log\stdout')
    Set-Content -LiteralPath $PublishedWebConfig -Value $WebConfig -Encoding UTF8
}

Get-ChildItem -LiteralPath $TempPublish -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $ReleaseDir -Recurse -Force
}

if (-not $NoRootCopy) {
    $RuntimeExtensions = @(".exe", ".config")
    Get-ChildItem -LiteralPath $TempPublish -File | Where-Object {
        $RuntimeExtensions -contains $_.Extension.ToLowerInvariant()
    } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $PluginRoot $_.Name) -Force
    }
}

$ExePath = Join-Path $ReleaseDir "RetroBat.Api.exe"
$TotalBytes = (Get-ChildItem -LiteralPath $ReleaseDir -File -Recurse | Measure-Object -Property Length -Sum).Sum
$Hash = if (Test-Path -LiteralPath $ExePath) {
    (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
} else {
    ""
}
if ($Hash) {
    Set-Content -LiteralPath (Join-Path $ReleaseDir "SHA256.txt") -Value "$Hash  RetroBat.Api.exe" -Encoding ASCII
}

[pscustomobject]@{
    Version = $Version
    ReleaseDir = $ReleaseDir
    RootCopied = -not $NoRootCopy
    ExeMB = if (Test-Path -LiteralPath $ExePath) { [math]::Round((Get-Item -LiteralPath $ExePath).Length / 1MB, 2) } else { 0 }
    TotalMB = [math]::Round($TotalBytes / 1MB, 2)
    Sha256 = $Hash
} | Format-List
