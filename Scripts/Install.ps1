param([string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$modsRoot = [IO.Path]::GetFullPath((Join-Path $GameDir 'Mods'))
$installTarget = [IO.Path]::GetFullPath((Join-Path $modsRoot 'MoreMushrooms'))
$legacyTarget = [IO.Path]::GetFullPath((Join-Path $modsRoot 'RimMushrooms'))
if (!(Test-Path -LiteralPath (Join-Path $GameDir 'RimWorldWin64.exe'))) { throw 'RimWorld executable not found.' }
foreach ($target in @($installTarget, $legacyTarget)) {
    if ((Split-Path $target -Parent) -ne $modsRoot) { throw 'Invalid installation target.' }
}
if (Get-Process -Name RimWorldWin64 -ErrorAction SilentlyContinue) { throw 'Close RimWorld before installing.' }
foreach ($required in @('About\About.xml','Assemblies\RimMushrooms.dll','Defs','Languages','Textures','Credits','Patches')) {
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot $required))) { throw "Missing source: $required. Build first." }
}
# Preflight both names before moving either, so a rename cannot leave duplicates.
$oldInstalls = @($installTarget, $legacyTarget) | Where-Object { Test-Path -LiteralPath $_ }
$publishedIds = @()
foreach ($aboutRoot in @((Join-Path $projectRoot 'About')) + @($oldInstalls | ForEach-Object { Join-Path $_ 'About' })) {
    $idPath = Join-Path $aboutRoot 'PublishedFileId.txt'
    if (Test-Path -LiteralPath $idPath) {
        $idValue = ([IO.File]::ReadAllText($idPath)).Trim()
        if ($idValue -notmatch '^[1-9][0-9]*$') { throw 'Invalid Workshop published file ID.' }
        $publishedIds += $idValue
    }
}
$distinctPublishedIds = @($publishedIds | Select-Object -Unique)
if ($distinctPublishedIds.Count -gt 1) { throw 'Conflicting Workshop published file IDs.' }
foreach ($oldInstall in $oldInstalls) {
    $existing = [xml](Get-Content -LiteralPath (Join-Path $oldInstall 'About\About.xml') -Raw)
    if ($existing.ModMetaData.packageId -ne 'izzypizzy.rimmushrooms') { throw 'Destination belongs to a different mod.' }
}
if ($distinctPublishedIds.Count -eq 1) {
    [IO.File]::WriteAllText((Join-Path $projectRoot 'About\PublishedFileId.txt'), $distinctPublishedIds[0], [Text.UTF8Encoding]::new($false))
}
foreach ($oldInstall in $oldInstalls) {
    $backupRoot = Join-Path $projectRoot 'Backups'
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $backupTarget = [IO.Path]::GetFullPath((Join-Path $backupRoot ((Split-Path $oldInstall -Leaf) + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))))
    if (!( $backupTarget.StartsWith($backupRoot + [IO.Path]::DirectorySeparatorChar))) { throw 'Invalid backup path.' }
    Move-Item -LiteralPath $oldInstall -Destination $backupTarget
    Write-Output "Previous install backed up to $backupTarget"
}
New-Item -ItemType Directory -Path $installTarget | Out-Null
foreach ($name in @('About','Assemblies','Defs','Languages','Textures','Credits','Patches')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $installTarget -Recurse
}
foreach ($name in @('README.md','CHANGELOG.md')) {
    if (Test-Path -LiteralPath (Join-Path $projectRoot $name)) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $installTarget }
}
if (Test-Path -LiteralPath (Join-Path $projectRoot 'Docs\VALIDATION.md')) {
    New-Item -ItemType Directory -Path (Join-Path $installTarget 'Docs') | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Docs\VALIDATION.md') -Destination (Join-Path $installTarget 'Docs')
}
foreach ($file in Get-ChildItem -LiteralPath $installTarget -File -Recurse) {
    $relative = $file.FullName.Substring($installTarget.Length + 1)
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $projectRoot $relative)).Hash) { throw "Install hash mismatch: $relative" }
}
Write-Output "Installed and verified: $installTarget (active mod list unchanged)"
