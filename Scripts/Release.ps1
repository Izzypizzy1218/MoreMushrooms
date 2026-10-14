param([switch]$Install, [switch]$ReuseVerifiedBinary)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    if ($ReuseVerifiedBinary) {
        # Compatibility metadata releases can retain the original net472 DLL
        # for both 1.6 and Waylith's legacy-mod compatibility layer in 1.7.
        python .\Scripts\verify_reused_binary.py --previous-version 0.9.1
        if ($LASTEXITCODE -ne 0) { throw 'Archived binary/source verification failed.' }
    } else {
        dotnet msbuild .\MoreMushrooms.csproj -p:Configuration=Release -verbosity:minimal
        if ($LASTEXITCODE -ne 0) { throw 'Build failed. Build against a RimWorld 1.6 installation, or use -ReuseVerifiedBinary for an unchanged compatibility-only release.' }
    }
    python .\Scripts\generate_defs.py
    if ($LASTEXITCODE -ne 0) { throw 'Mushroom and ecology definition generation failed.' }
    python .\Tests\validate_assets.py
    if ($LASTEXITCODE -ne 0) { throw 'Static validation failed.' }
    python .\Scripts\package_release.py
    if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
    if ($Install) { & .\Scripts\Install.ps1 }
} finally { Pop-Location }
