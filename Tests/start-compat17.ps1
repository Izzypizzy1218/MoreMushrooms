param(
    [ValidateSet('Smoke','Habitat','Visual')][string]$Mode = 'Smoke',
    [switch]$AllDlc,
    [ValidateSet('English','Korean')][string]$Language = 'English',
    [string]$LegacySave,
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld'
)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name RimWorldWin64,RimWorld -ErrorAction SilentlyContinue) { throw 'Close RimWorld before starting isolated compatibility tests.' }
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$modsRoot = [IO.Path]::GetFullPath((Join-Path $GameDir 'Mods'))
$harnessRoot = [IO.Path]::GetFullPath((Join-Path $modsRoot 'RimMushroomsTestHarness'))
$harmonyRoot = [IO.Path]::GetFullPath((Join-Path $modsRoot 'RimMushroomsTestHarmony'))
foreach ($target in @($harnessRoot,$harmonyRoot)) {
    if ((Split-Path $target -Parent) -ne $modsRoot) { throw 'Unexpected staging target.' }
}
$officialHarmony = 'C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\2009463077'
if (Test-Path -LiteralPath $harmonyRoot) {
    if (!(Test-Path -LiteralPath (Join-Path $harmonyRoot 'more-mushrooms-test-only.marker'))) { throw 'Harmony staging is not owned by this harness.' }
} else {
    Copy-Item -LiteralPath $officialHarmony -Destination $harmonyRoot -Recurse
    Set-Content -LiteralPath (Join-Path $harmonyRoot 'more-mushrooms-test-only.marker') -Value 'Temporary official Harmony for isolated More Mushrooms tests.'
}
if (Test-Path -LiteralPath $harnessRoot) {
    $about = [xml](Get-Content -LiteralPath (Join-Path $harnessRoot 'About\About.xml') -Raw)
    if ($about.ModMetaData.packageId -ne 'izzypizzy.rimmushrooms.tests') { throw 'Harness staging belongs to another mod.' }
}
New-Item -ItemType Directory -Path (Join-Path $harnessRoot 'About'),(Join-Path $harnessRoot 'Assemblies') -Force | Out-Null
$harnessAbout = [IO.File]::ReadAllText((Join-Path $projectRoot 'Tests\Harness\About\About.xml')).Replace('<li>1.6</li>','<li>1.6</li><li>1.7</li>')
[IO.File]::WriteAllText((Join-Path $harnessRoot 'About\About.xml'),$harnessAbout,[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $projectRoot 'Tests\Runtime\Port17Harness\Assemblies\RimMushrooms.SmokeTests.dll') -Destination (Join-Path $harnessRoot 'Assemblies\RimMushrooms.SmokeTests.dll')
$testId = (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-Compat17-' + $Mode + '-' + $(if ($AllDlc) {'DLC'} else {'Core'}) + '-' + $Language
$testRoot = Join-Path $projectRoot ('Tests\Runtime\' + $testId)
$configRoot = Join-Path $testRoot 'Config'
New-Item -ItemType Directory -Path $configRoot -Force | Out-Null
$mods = @('brrainz.harmony','ludeon.rimworld')
if ($AllDlc) { $mods += @('ludeon.rimworld.royalty','ludeon.rimworld.ideology','ludeon.rimworld.biotech','ludeon.rimworld.anomaly','ludeon.rimworld.odyssey') }
$mods += @('izzypizzy.rimmushrooms','izzypizzy.rimmushrooms.tests')
$version = (Get-Content -LiteralPath (Join-Path $GameDir 'Version.txt') -Raw).Trim()
if (!$version.StartsWith('1.7.')) { throw 'Expected a RimWorld 1.7 installation.' }
$list = ($mods | ForEach-Object {'<li>' + $_ + '</li>'}) -join ''
$known = '<li>ludeon.rimworld</li><li>ludeon.rimworld.royalty</li><li>ludeon.rimworld.ideology</li><li>ludeon.rimworld.biotech</li><li>ludeon.rimworld.anomaly</li><li>ludeon.rimworld.odyssey</li>'
$config = '<ModsConfigData><version>'+$version+'</version><activeMods>'+$list+'</activeMods><knownExpansions>'+$known+'</knownExpansions></ModsConfigData>'
Set-Content -LiteralPath (Join-Path $configRoot 'ModsConfig.xml') -Value $config -Encoding utf8
$lang = if ($Language -eq 'Korean') {'Korean (한국어)'} else {'English'}
$prefs = '<PrefsData><langFolderName>'+$lang+'</langFolderName><devMode>True</devMode><logVerbose>False</logVerbose><volumeMaster>0</volumeMaster><screenWidth>1600</screenWidth><screenHeight>1000</screenHeight><fullscreen>False</fullscreen><runInBackground>True</runInBackground><pauseOnLoad>False</pauseOnLoad><pauseOnError>False</pauseOnError><adaptiveTrainingEnabled>False</adaptiveTrainingEnabled><autosaveIntervalDays>99</autosaveIntervalDays></PrefsData>'
Set-Content -LiteralPath (Join-Path $configRoot 'Prefs.xml') -Value $prefs -Encoding utf8
$flag = @{Smoke='mushroomSmoke';Habitat='mushroomHabitatCluster';Visual='mushroomVisual'}[$Mode]
$arguments = @('-quicktest',('-'+$flag),('-mushroomLanguage='+$Language),('-savedatafolder="'+$testRoot+'"'),'-screen-fullscreen','0','-screen-width','1600','-screen-height','1000')
if ($Mode -ne 'Visual') { $arguments += '-batchmode' }
if ($LegacySave) {
    if (!(Test-Path -LiteralPath $LegacySave -PathType Leaf)) { throw 'Legacy fixture does not exist.' }
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'Saves') -Force | Out-Null
    Copy-Item -LiteralPath $LegacySave -Destination (Join-Path $testRoot 'Saves\MoreMushrooms-LegacyFixture.rws')
    (Get-FileHash -LiteralPath $LegacySave -Algorithm SHA256).Hash | Set-Content -LiteralPath (Join-Path $testRoot 'legacy-input.sha256')
    $arguments += '-mushroomLegacySave'
}
$process = Start-Process -FilePath (Join-Path $GameDir 'RimWorld.exe') -WorkingDirectory $GameDir -ArgumentList $arguments -WindowStyle Hidden -PassThru
$record = [ordered]@{Pid=$process.Id;TestRoot=$testRoot;Mode=$Mode;AllDlc=[bool]$AllDlc;Language=$Language;GameVersion=$version;ExpectedMods=$mods;Executable=(Join-Path $GameDir 'RimWorld.exe');StartedUtc=$process.StartTime.ToUniversalTime().ToString('o')}
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'launch.json') -Encoding utf8
$record | ConvertTo-Json -Depth 5
