param([string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld')
$ErrorActionPreference = 'Stop'
if (Get-Process -Name RimWorldWin64 -ErrorAction SilentlyContinue) { throw 'Close RimWorld first.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Install.ps1') -GameDir $GameDir
dotnet msbuild (Join-Path $projectRoot 'Tests\Harness\SmokeTests.csproj') -p:Configuration=Release "-p:RimWorldDir=$GameDir" -verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw 'Test harness build failed.' }
$testTarget = Join-Path $GameDir 'Mods\RimMushroomsTestHarness'
if (Test-Path -LiteralPath $testTarget) {
    $metadata = [xml](Get-Content -LiteralPath (Join-Path $testTarget 'About\About.xml') -Raw)
    if ($metadata.ModMetaData.packageId -ne 'izzypizzy.rimmushrooms.tests') { throw 'Target is a different mod.' }
} else { New-Item -ItemType Directory -Path $testTarget | Out-Null }
foreach ($folder in @('About','Assemblies')) {
    New-Item -ItemType Directory -Path (Join-Path $testTarget $folder) -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot ('Tests\Harness\' + $folder)) -File) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $testTarget $folder) -Force
    }
}
Write-Output "Prepared test harness: $testTarget"
