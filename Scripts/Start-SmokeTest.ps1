param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld',
    [switch]$AllDlc,
    [switch]$MoodOnly,
    [string]$LegacySave,
    [ValidateSet('English','Korean')][string]$Language = 'English'
)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name RimWorldWin64 -ErrorAction SilentlyContinue) { throw 'Close RimWorld before starting an isolated test.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$testId = (Get-Date -Format 'yyyyMMdd-HHmmss') + $(if ($AllDlc) { '-DLC' } else { '-Core' })
if ($MoodOnly) { $testId += '-Mood' }
$testRoot = Join-Path $projectRoot ('Tests\Runtime\' + $testId)
$configRoot = Join-Path $testRoot 'Config'
New-Item -ItemType Directory -Path $configRoot -Force | Out-Null
if ($LegacySave) {
    if (!(Test-Path -LiteralPath $LegacySave -PathType Leaf)) { throw 'Legacy test save not found.' }
    $saveRoot = Join-Path $testRoot 'Saves'
    New-Item -ItemType Directory -Path $saveRoot -Force | Out-Null
    Copy-Item -LiteralPath $LegacySave -Destination (Join-Path $saveRoot 'MoreMushrooms-LegacyFixture.rws')
}
$mods = @('ludeon.rimworld')
if ($AllDlc) { $mods += @('ludeon.rimworld.royalty','ludeon.rimworld.ideology','ludeon.rimworld.biotech','ludeon.rimworld.anomaly','ludeon.rimworld.odyssey') }
$mods += @('izzypizzy.rimmushrooms','izzypizzy.rimmushrooms.tests')
$modList = ($mods | ForEach-Object { '<li>' + $_ + '</li>' }) -join ''
$version = (Get-Content -LiteralPath (Join-Path $GameDir 'Version.txt') -Raw).Trim()
$modsXml = '<ModsConfigData><version>' + $version + '</version><activeMods>' + $modList + '</activeMods><knownExpansions><li>ludeon.rimworld</li><li>ludeon.rimworld.royalty</li><li>ludeon.rimworld.ideology</li><li>ludeon.rimworld.biotech</li><li>ludeon.rimworld.anomaly</li><li>ludeon.rimworld.odyssey</li></knownExpansions></ModsConfigData>'
Set-Content -LiteralPath (Join-Path $configRoot 'ModsConfig.xml') -Value $modsXml -Encoding utf8
$langFolder = if ($Language -eq 'Korean') { 'Korean (한국어)' } else { 'English' }
# Native pause-on-load advances one tick first. The harness pauses in LoadedGame
# so persistence checks compare the saved values before that simulation step.
$prefs = '<PrefsData><langFolderName>' + $langFolder + '</langFolderName><devMode>True</devMode><logVerbose>False</logVerbose><volumeMaster>0</volumeMaster><screenWidth>1600</screenWidth><screenHeight>1000</screenHeight><fullscreen>False</fullscreen><runInBackground>True</runInBackground><pauseOnLoad>False</pauseOnLoad><pauseOnError>False</pauseOnError><adaptiveTrainingEnabled>False</adaptiveTrainingEnabled></PrefsData>'
Set-Content -LiteralPath (Join-Path $configRoot 'Prefs.xml') -Value $prefs -Encoding utf8
$logPath = Join-Path $testRoot 'Player.log'
$arguments = @('-batchmode','-screen-fullscreen','0','-screen-width','1600','-screen-height','1000','-quicktest','-mushroomSmoke',('-mushroomLanguage=' + $Language),('-savedatafolder="' + $testRoot + '"'),'-logFile',('"' + $logPath + '"'))
if ($MoodOnly) { $arguments += '-mushroomMoodOnly' }
if ($LegacySave) { $arguments += '-mushroomLegacySave' }
$gameProcess = Start-Process -FilePath (Join-Path $GameDir 'RimWorldWin64.exe') -WorkingDirectory $GameDir -ArgumentList $arguments -WindowStyle Hidden -PassThru
[pscustomobject]@{Pid=$gameProcess.Id; TestRoot=$testRoot; Log=$logPath; Language=$Language; AllDlc=[bool]$AllDlc} | ConvertTo-Json
