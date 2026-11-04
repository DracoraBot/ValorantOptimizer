#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$FrameworkDependent,
    [switch]$CreateZip,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$artifacts = Join-Path $root 'artifacts'

$version = '0.1.0'
$propsPath = Join-Path $root 'Directory.Build.props'
if (Test-Path $propsPath) {
    [xml]$props = Get-Content $propsPath -Raw
    $propertyGroup = $props.Project.PropertyGroup | Select-Object -First 1
    if ($propertyGroup -and $propertyGroup.Version) {
        $version = [string]$propertyGroup.Version
    }
}
$versionArg = "-p:Version=$version"

Write-Host "== VALORANT Optimization Toolkit build ==" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration"
Write-Host "Runtime: $(if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained single-file' })"
Write-Host "Output: $dist"

if (Test-Path $dist) {
    Remove-Item $dist -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $dist, $artifacts | Out-Null

Push-Location $root
try {
    Write-Host "`n[1/4] Building solution..." -ForegroundColor Cyan
    & dotnet build 'ValorantOptimizer.sln' -c $Configuration -v q --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE."
    }

    if (-not $SkipTests) {
        Write-Host "`n[2/4] Running tests..." -ForegroundColor Cyan
        & dotnet test 'ValorantOptimizer.sln' -c $Configuration --no-build -v q --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Tests failed with exit code $LASTEXITCODE."
        }
    }
    else {
        Write-Host "`n[2/4] Tests skipped." -ForegroundColor Yellow
    }

    Write-Host "`n[3/4] Publishing app and elevated helper..." -ForegroundColor Cyan
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    $publishArgs = @(
        'publish', 'src\ValorantOptimizer.App\ValorantOptimizer.App.csproj',
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', $selfContained,
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        $versionArg,
        '-o', $dist,
        '-v', 'minimal',
        '--nologo'
    )
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "App publish failed with exit code $LASTEXITCODE."
    }

    $helperDir = Join-Path $dist 'helper'
    $helperArgs = @(
        'publish', 'src\ValorantOptimizer.Elevated\ValorantOptimizer.Elevated.csproj',
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', $selfContained,
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        $versionArg,
        '-o', $helperDir,
        '-v', 'minimal',
        '--nologo'
    )
    & dotnet @helperArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Elevated helper publish failed with exit code $LASTEXITCODE."
    }

    Write-Host "`n[4/4] Assembling release files..." -ForegroundColor Cyan
    Copy-Item (Join-Path $root 'README.md') $dist -Force
    Copy-Item (Join-Path $root 'docs') $dist -Recurse -Force
    Copy-Item (Join-Path $root 'assets\app-icon.png') $dist -Force

    $checksumPath = Join-Path $dist 'SHA256SUMS.txt'
    $checksumLines = Get-ChildItem $dist -File -Recurse |
        Where-Object { $_.FullName -ne $checksumPath } |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring($dist.Length).TrimStart('\').Replace('\', '/')
            $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relative"
        }
    Set-Content -Path $checksumPath -Value $checksumLines -Encoding UTF8

    if ($CreateZip) {
        $zipPath = Join-Path $artifacts "ValorantOptimizer-$version-win-x64.zip"
        if (Test-Path $zipPath) {
            Remove-Item $zipPath -Force
        }

        Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zipPath
        Write-Host "Zip: $zipPath"
    }

    $appExe = Join-Path $dist 'ValorantOptimizer.exe'
    $helperExe = Join-Path $helperDir 'ValorantOptimizer.Elevated.exe'
    $sizeMb = [math]::Round((Get-ChildItem $dist -File -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)

    Write-Host "`nRelease complete." -ForegroundColor Green
    Write-Host "  App:    $appExe"
    Write-Host "  Helper: $helperExe"
    Write-Host "  Size:   $sizeMb MB"
    Write-Host "  Checksums: $checksumPath"
    Write-Host "`nRun '$appExe --smoke <path>' for a headless read-only verification."
}
finally {
    Pop-Location
}
