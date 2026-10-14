param([string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld', [switch]$Live)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name RimWorldWin64 -ErrorAction SilentlyContinue) { throw 'RimWorld is already running.' }
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$modeName = if ($Live) { 'HealthLiveDemo' } else { 'HealthTimeline' }
$testRoot = Join-Path $projectRoot ('Tests\Runtime\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + $modeName + '-Core')
New-Item -ItemType Directory -Path (Join-Path $testRoot 'Config') -Force | Out-Null
function HashFiles([string]$Root) {
    $result = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File) {
        $result[$file.FullName.Substring($Root.Length).TrimStart('\')] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $result
}
$userData = Join-Path $env:USERPROFILE 'AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios'
$before = [ordered]@{
    capturedAt = (Get-Date -Format o)
    gameVersion = (Get-Content -LiteralPath (Join-Path $GameDir 'Version.txt') -Raw).Trim()
    userConfig = (HashFiles (Join-Path $userData 'Config'))
    installed = (HashFiles (Join-Path $GameDir 'Mods\MoreMushrooms'))
    releases = (HashFiles (Join-Path $projectRoot 'Releases'))
    userSaves = @(Get-ChildItem -LiteralPath (Join-Path $userData 'Saves') -File | Select-Object Name,Length,LastWriteTimeUtc)
    userSaveHashes = (HashFiles (Join-Path $userData 'Saves'))
}
$before | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $testRoot 'preservation-before.json') -Encoding utf8
$modsRoot = [IO.Path]::GetFullPath((Join-Path $GameDir 'Mods'))
$harmony = Join-Path $modsRoot 'RimMushroomsTestHarmony'
if (Test-Path -LiteralPath $harmony) {
    if (!(Test-Path -LiteralPath (Join-Path $harmony 'more-mushrooms-test-only.marker'))) { throw 'Unowned Harmony staging path.' }
} else {
    Copy-Item -LiteralPath 'C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\2009463077' -Destination $harmony -Recurse
    Set-Content -LiteralPath (Join-Path $harmony 'more-mushrooms-test-only.marker') -Value 'Temporary official Harmony copy for isolated More Mushrooms tests.'
}
$testTarget = Join-Path $modsRoot 'RimMushroomsTestHarness'
if (Test-Path -LiteralPath $testTarget) {
    $meta = [xml](Get-Content -LiteralPath (Join-Path $testTarget 'About\About.xml') -Raw)
    if ($meta.ModMetaData.packageId -ne 'izzypizzy.rimmushrooms.tests') { throw 'Unowned harness staging path.' }
}
foreach ($folder in @('About','Assemblies')) {
    New-Item -ItemType Directory -Path (Join-Path $testTarget $folder) -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot ('Tests\Harness\' + $folder)) -File) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $testTarget $folder) -Force
    }
}
$version = $before.gameVersion
$modsXml = '<ModsConfigData><version>' + $version + '</version><activeMods><li>brrainz.harmony</li><li>ludeon.rimworld</li><li>izzypizzy.rimmushrooms</li><li>izzypizzy.rimmushrooms.tests</li></activeMods><knownExpansions><li>ludeon.rimworld</li><li>ludeon.rimworld.royalty</li><li>ludeon.rimworld.ideology</li><li>ludeon.rimworld.biotech</li><li>ludeon.rimworld.anomaly</li><li>ludeon.rimworld.odyssey</li></knownExpansions></ModsConfigData>'
Set-Content -LiteralPath (Join-Path $testRoot 'Config\ModsConfig.xml') -Value $modsXml -Encoding utf8
$prefs = '<PrefsData><langFolderName>Korean (한국어)</langFolderName><devMode>True</devMode><logVerbose>False</logVerbose><volumeMaster>0</volumeMaster><screenWidth>1600</screenWidth><screenHeight>1000</screenHeight><fullscreen>False</fullscreen><runInBackground>True</runInBackground><pauseOnLoad>False</pauseOnLoad><pauseOnError>False</pauseOnError><adaptiveTrainingEnabled>False</adaptiveTrainingEnabled><autosaveIntervalDays>99</autosaveIntervalDays></PrefsData>'
Set-Content -LiteralPath (Join-Path $testRoot 'Config\Prefs.xml') -Value $prefs -Encoding utf8
$log = Join-Path $testRoot 'Player.log'
$modeFlag = if ($Live) { '-mushroomHealthLive' } else { '-mushroomHealthTimeline' }
$arguments = @('-screen-fullscreen','0','-screen-width','1600','-screen-height','1000','-quicktest',$modeFlag,('-savedatafolder="' + $testRoot + '"'),'-logFile',('"' + $log + '"'))
# User explicitly requested actual rendered game screens.
$process = Start-Process -FilePath (Join-Path $GameDir 'RimWorldWin64.exe') -WorkingDirectory $GameDir -ArgumentList $arguments -WindowStyle Normal -PassThru
$record = [ordered]@{Pid=$process.Id;TestRoot=$testRoot;Log=$log;HarnessSha256=(Get-FileHash -LiteralPath (Join-Path $testTarget 'Assemblies\RimMushrooms.SmokeTests.dll')).Hash;InstalledDllSha256=$before.installed['Assemblies\RimMushrooms.dll']}
$record | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $testRoot 'launch.json') -Encoding utf8
$record | ConvertTo-Json -Depth 3
