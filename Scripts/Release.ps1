param([switch]$Install)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    dotnet msbuild .\MoreMushrooms.csproj -p:Configuration=Release -verbosity:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    python .\Tests\validate_assets.py
    if ($LASTEXITCODE -ne 0) { throw 'Static validation failed.' }
    python .\Scripts\package_release.py
    if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
    if ($Install) { & .\Scripts\Install.ps1 }
} finally { Pop-Location }
