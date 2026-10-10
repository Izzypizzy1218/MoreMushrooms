param(
    [ValidateSet('Ecology','Balance','WorkBalance','Compatibility','Visual')][string]$Mode,
    [switch]$UserMods,
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld'
)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name RimWorldWin64 -ErrorAction SilentlyContinue) { throw 'Close RimWorld before starting isolated QA.' }
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$modsRoot = [IO.Path]::GetFullPath((Join-Path $GameDir 'Mods'))
$harnessRoot = [IO.Path]::GetFullPath((Join-Path $modsRoot 'MoreMushrooms-QA-Harness'))
if ((Split-Path $harnessRoot -Parent) -ne $modsRoot) { throw 'Invalid harness target.' }
if (Test-Path -LiteralPath $harnessRoot) {
    $existing = [xml](Get-Content -LiteralPath (Join-Path $harnessRoot 'About\About.xml') -Raw)
    if ($existing.ModMetaData.packageId -ne 'izzypizzy.rimmushrooms.tests') { throw 'QA target belongs to a different mod.' }
} else { New-Item -ItemType Directory -Path $harnessRoot | Out-Null }
New-Item -ItemType Directory -Path (Join-Path $harnessRoot 'About'),(Join-Path $harnessRoot 'Assemblies') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'Tests\Harness\About\About.xml') -Destination (Join-Path $harnessRoot 'About\About.xml')
Copy-Item -LiteralPath (Join-Path $projectRoot 'Tests\Harness\Assemblies\RimMushrooms.SmokeTests.dll') -Destination (Join-Path $harnessRoot 'Assemblies\RimMushrooms.SmokeTests.dll')
$testId = (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-Field' + $Mode + $(if ($UserMods) { '-UserMods' } else { '-Core' })
$testRoot = Join-Path $projectRoot ('Tests\Runtime\' + $testId)
$configRoot = Join-Path $testRoot 'Config'
New-Item -ItemType Directory -Path $configRoot | Out-Null
$mods = @('brrainz.harmony','ludeon.rimworld','izzypizzy.rimmushrooms')
if ($UserMods) {
    $userModsPath = Join-Path $env:USERPROFILE 'AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\ModsConfig.xml'
    $userConfigDocument = [xml](Get-Content -LiteralPath $userModsPath -Raw)
    $mods = @($userConfigDocument.ModsConfigData.activeMods.li | ForEach-Object { [string]$_ })
}
$mods = @($mods | Where-Object { $_ -ne 'izzypizzy.rimmushrooms.tests' }) + @('izzypizzy.rimmushrooms.tests')
$version = (Get-Content -LiteralPath (Join-Path $GameDir 'Version.txt') -Raw).Trim()
$modList = ($mods | ForEach-Object { '<li>' + [Security.SecurityElement]::Escape($_) + '</li>' }) -join ''
$modsXml = '<ModsConfigData><version>' + $version + '</version><activeMods>' + $modList + '</activeMods><knownExpansions><li>ludeon.rimworld</li><li>ludeon.rimworld.royalty</li><li>ludeon.rimworld.ideology</li><li>ludeon.rimworld.biotech</li><li>ludeon.rimworld.anomaly</li><li>ludeon.rimworld.odyssey</li></knownExpansions></ModsConfigData>'
Set-Content -LiteralPath (Join-Path $configRoot 'ModsConfig.xml') -Value $modsXml -Encoding utf8
Set-Content -LiteralPath (Join-Path $testRoot 'compatibility-expected-mod-ids.txt') -Value $mods -Encoding utf8
# No personal mod settings, API keys, saves or policies are copied.
$prefs = '<PrefsData><langFolderName>English</langFolderName><devMode>True</devMode><logVerbose>False</logVerbose><volumeMaster>0</volumeMaster><screenWidth>1600</screenWidth><screenHeight>1000</screenHeight><fullscreen>False</fullscreen><runInBackground>True</runInBackground><pauseOnLoad>False</pauseOnLoad><pauseOnError>False</pauseOnError><adaptiveTrainingEnabled>False</adaptiveTrainingEnabled></PrefsData>'
if ($Mode -eq 'WorkBalance') {
    # Native synchronous Autosaving waits for its UI to draw; batchmode has no
    # OnGUI. Keep automatic saving outside this disposable one-day observation.
    $prefs = $prefs.Replace('</PrefsData>', '<autosaveIntervalDays>99</autosaveIntervalDays></PrefsData>')
}
Set-Content -LiteralPath (Join-Path $configRoot 'Prefs.xml') -Value $prefs -Encoding utf8
$flag = @{Ecology='mushroomFieldEcology'; Balance='mushroomFieldBalance'; WorkBalance='mushroomFieldWorkBalance'; Compatibility='mushroomCompatibility'; Visual='mushroomVisual'}[$Mode]
$logPath = Join-Path $testRoot 'Player.log'
$arguments = @('-screen-fullscreen','0','-screen-width','1600','-screen-height','1000','-quicktest',('-' + $flag),('-savedatafolder="' + $testRoot + '"'),'-logFile',('"' + $logPath + '"'))
if ($Mode -ne 'Visual') { $arguments = @('-batchmode') + $arguments }
if ($Mode -eq 'Ecology') {
    $testInput = Join-Path $projectRoot 'Tests\Fixtures\v0.5-wild-weights.csv'
    if (!(Test-Path -LiteralPath $testInput)) { throw 'Missing v0.5 ecology comparison input.' }
    $arguments += @('-mushroomFieldMapSize=250',('-mushroomFieldBaseline="' + $testInput + '"'))
}
# The requested actual-frame visual inspection requires a rendered window.
$windowStyle = if ($Mode -eq 'Visual') { 'Normal' } else { 'Hidden' }
$gameProcess = Start-Process -FilePath (Join-Path $GameDir 'RimWorldWin64.exe') -WorkingDirectory $GameDir -ArgumentList $arguments -WindowStyle $windowStyle -PassThru
$record = [pscustomobject]@{Pid=$gameProcess.Id; TestRoot=$testRoot; Log=$logPath; Mode=$Mode; UserMods=[bool]$UserMods; Harness=$harnessRoot; ExpectedMods=$mods; HarnessSha256=(Get-FileHash -LiteralPath (Join-Path $harnessRoot 'Assemblies\RimMushrooms.SmokeTests.dll')).Hash}
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'launch.json') -Encoding utf8
$record | ConvertTo-Json -Depth 5
