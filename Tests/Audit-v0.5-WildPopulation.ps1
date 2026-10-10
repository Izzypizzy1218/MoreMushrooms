param(
    [string]$SavePath = 'C:/Users/집/Desktop/codex/MoreMushrooms/Tests/Runtime/20261010-014454-Core/Saves/RimMushrooms-SmokeFixture.rws',
    [string]$OutputPath = 'C:/Users/집/Desktop/codex/MoreMushrooms/Tests/Runtime/EcologyInspection/v0.5-WildPopulation.json'
)
$taskSave = [xml](Get-Content -LiteralPath $SavePath -Raw)
$taskPlants = @($taskSave.SelectNodes('//thing[starts-with(def,"RMush_Plant")]'))
$taskInitial = @($taskPlants | Where-Object spawnedTick -eq '0')
$taskNonInitial = @($taskPlants | Where-Object spawnedTick -ne '0')
function Get-OutsideCentral($nodes) {
    @($nodes | Where-Object {
        $taskPosition = [regex]::Match($_.pos, '\((\d+),\s*\d+,\s*(\d+)\)')
        [Math]::Abs([int]$taskPosition.Groups[1].Value - 125) -gt 50 -or [Math]::Abs([int]$taskPosition.Groups[2].Value - 125) -gt 40
    })
}
$taskOutside = @(Get-OutsideCentral $taskPlants)
$taskInitialOutside = @(Get-OutsideCentral $taskInitial)
$taskNonInitialOutside = @(Get-OutsideCentral $taskNonInitial)
$taskReport = [ordered]@{
    auditedVersion = '0.5.0'
    savePath = $SavePath
    saveSha256 = (Get-FileHash -LiteralPath $SavePath -Algorithm SHA256).Hash.ToLowerInvariant()
    mapSize = [string]$taskSave.savegame.game.maps.li.mapInfo.size
    ticksGame = [int]$taskSave.savegame.game.tickManager.ticksGame
    totalMushroomPlants = $taskPlants.Count
    initialSpawnTickZeroPlants = $taskInitial.Count
    nonInitialPlants = $taskNonInitial.Count
    conservativeExcludedRectangle = [ordered]@{ minX = 75; maxX = 175; minZ = 85; maxZ = 165; width = 101; height = 81 }
    plantsOutsideConservativeFixtureRectangle = $taskOutside.Count
    initialPlantsOutsideConservativeFixtureRectangle = $taskInitialOutside.Count
    nonInitialPlantsOutsideConservativeFixtureRectangle = $taskNonInitialOutside.Count
    bySpawnTick = @($taskPlants | Group-Object spawnedTick | Sort-Object Count -Descending | ForEach-Object { [ordered]@{ tick = [int]$_.Name; count = $_.Count } })
    initialPlantByDef = @($taskInitial | Group-Object def | Sort-Object Name | ForEach-Object { [ordered]@{ defName = $_.Name; count = $_.Count } })
    oldCapAt250 = 112
    adoptedCapAt250 = 500
    adoptedAreaRatePer10000Cells = 80
    adoptedMinWildCap = 80
    adoptedMaxWildCap = 1500
    interpretation = 'The save retains 357 native initial plants. All 134 noninitial plants are inside the excluded central rectangle; 132 match the 39 legacy and 93 expansion artwork plants, with 2 crop-job plants. Even excluding the entire 101x81 central rectangle leaves 316 initial wild plants. Counts are lower bounds after the smoke harness cleared test patches, not an exact untouched new-map census.'
    initialGenerationCallChain = @('RimWorld.GenStep_Plants.Generate', 'RimWorld.WildPlantSpawner.CheckSpawnWildPlantAt', 'RimWorld.WildPlantSpawner.PlantChoiceWeight', 'RimWorld.WildPlantSpawner.SpawnPlant')
    nativeCacheFinding = 'GetCommonalityOfPlant calls CachePlantCommonalitiesIfShould itself before returning cached commonality; missing support in a random fixture biome is not a cold-cache defect.'
}
$taskReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
[pscustomobject]$taskReport | Select-Object totalMushroomPlants,initialSpawnTickZeroPlants,nonInitialPlants,plantsOutsideConservativeFixtureRectangle,initialPlantsOutsideConservativeFixtureRectangle,nonInitialPlantsOutsideConservativeFixtureRectangle,oldCapAt250,adoptedCapAt250 | ConvertTo-Json
