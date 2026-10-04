param([string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld')
$ErrorActionPreference = 'Stop'
if (Get-Process -Name RimWorldWin64 -ErrorAction SilentlyContinue) { throw 'Close RimWorld first.' }
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$modsRoot = [IO.Path]::GetFullPath((Join-Path $GameDir 'Mods'))
$testTarget = [IO.Path]::GetFullPath((Join-Path $modsRoot 'RimMushroomsTestHarness'))
if (!(Test-Path -LiteralPath $testTarget)) { Write-Output 'No test harness installed.'; return }
if ((Split-Path $testTarget -Parent) -ne $modsRoot) { throw 'Invalid source path.' }
$metadata = [xml](Get-Content -LiteralPath (Join-Path $testTarget 'About\About.xml') -Raw)
if ($metadata.ModMetaData.packageId -ne 'izzypizzy.rimmushrooms.tests') { throw 'Destination is a different mod.' }
$archiveRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'Tests\Runtime'))
New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
$archive = [IO.Path]::GetFullPath((Join-Path $archiveRoot ('RetiredHarness-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))))
if (!$archive.StartsWith($archiveRoot + [IO.Path]::DirectorySeparatorChar)) { throw 'Invalid archive path.' }
Move-Item -LiteralPath $testTarget -Destination $archive
Write-Output "Test harness preserved outside Mods: $archive"
