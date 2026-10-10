using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using RimMushrooms;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimMushroomsTests
{
    // Deliberately independent of SmokeTests. No crop patch, forced growth,
    // forced ring, cleared habitat, saved game, or player configuration is used.
    public sealed class FieldEcologyTests : GameComponent
    {
        private static readonly string[] Biomes = { "TemperateForest", "TemperateSwamp", "BorealForest", "AridShrubland", "TropicalRainforest", "TropicalSwamp", "Tundra" };
        private static readonly string[] Seeds = { "MoreMushrooms-Field-A-20261011", "MoreMushrooms-Field-B-20261011" };
        private static readonly HashSet<string> OldIds = new HashSet<string> { "Button", "Shiitake", "Oyster", "KingOyster", "Enoki", "WoodEar", "Beech", "Maitake", "LionsMane", "Matsutake", "FlyAgaric", "DestroyingAngel", "SulfurTuft", "Tsukiyotake", "PoisonFireCoral" };
        private static readonly string[] CacheNames = { "cachedPlantCommonalities", "cachedWildPlants", "cachedLowestWildPlantOrder", "cachedMaxWildPlantsClusterRadius" };
        private static readonly MethodInfo NativeTemperatureOffset = typeof(GameConditionManager).GetMethod("AggregateTemperatureOffset", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo NativeSnowMelt = typeof(SteadyEnvironmentEffects).GetMethod("MeltAmountAt", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo NativeSnowCycle = typeof(SteadyEnvironmentEffects).GetField("cycleIndex", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo NativeSnowFraction = typeof(SteadyEnvironmentEffects).GetField("MapFractionCheckPerTick", BindingFlags.Static | BindingFlags.NonPublic);
        private readonly List<string> nativeErrors = new List<string>();
        private readonly List<Census> censuses = new List<Census>();
        private readonly List<Series> series = new List<Series>();
        private readonly Queue<Sample> samples = new Queue<Sample>();
        private readonly Dictionary<string, Dictionary<string, float>> baseline = new Dictionary<string, Dictionary<string, float>>();
        private readonly Dictionary<string, object> metadata = new Dictionary<string, object>();
        private readonly StringBuilder eventRows = new StringBuilder();
        private Map originalMap, activeMap;
        private MapParent activeParent;
        private ThingDef[] species;
        private Sample activeSample;
        private Series activeSeries;
        private Pawn abstractHarvester;
        private Func<float, float> nativeMeltAmount;
        private int snowLastTick, snowCycleIndex, snowChecksPerTick, remainingSnowCells, absoluteTickOffset;
        private int baseTick, mapSize = 250, nextEventTick, nextPlantTick, nextEcologyTick, nextDayTick;
        private int seriesStart, lastReportDay, sampleNumber;
        private string originalWorldSeed, output, baselinePath;
        private bool started, finished, originalStoryteller, originalFastEcology, geographySeedPushed;
        private float originalHarvestStat;
        private const int HorizonDays = 120;
        private const int EcologyInterval = 2500;
        // Native 1.6 Plant.TickLong advances 2,000 ticks, not 250 ticks.
        private const int NativePlantInterval = 2000;

        public FieldEcologyTests(Game game)
        {
            if (!GenCommandLine.CommandLineArgPassed("mushroomFieldEcology")) return;
            Application.logMessageReceived += CaptureNativeLog;
            // Root_Play creates Game before deriving its quicktest world seed.
            // This makes that derivation repeatable for an unchanged mod set.
            // The actual resulting geography seed is also recorded explicitly.
            Rand.PushState(GenText.StableStringHash("MoreMushrooms-Field-Geography-20261011"));
            geographySeedPushed = true;
        }

        private void CaptureNativeLog(string condition, string stackTrace, LogType type)
        {
            if (finished || (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
            lock (nativeErrors) nativeErrors.Add(type + ": " + condition + "\n" + stackTrace);
        }

        public override void StartedNewGame()
        {
            // InitNewGame calls this inside the quicktest LongEvent. Restore
            // before Root.Update's EnsureStateStackEmpty, which runs before
            // the first GameComponentUpdate after that event finishes.
            if (geographySeedPushed) { Rand.PopState(); geographySeedPushed = false; }
        }

        public override void GameComponentUpdate()
        {
            if (finished || !GenCommandLine.CommandLineArgPassed("mushroomFieldEcology")
                || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting || Find.CurrentMap == null) return;
            try
            {
                if (!started) Initialize();
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                AssertNoNativeErrors();
                if (activeSeries != null) AdvanceSeries();
                else if (samples.Count > 0) StartSample(samples.Dequeue());
                else Complete();
            }
            catch (Exception error) { Fail(error); }
        }

        private void Initialize()
        {
            started = true;
            if (geographySeedPushed) { Rand.PopState(); geographySeedPushed = false; }
            string specified;
            Require(!GenCommandLine.CommandLineArgPassed("mushroomSmoke"), "Do not combine field and smoke flags.");
            Require(GenCommandLine.TryGetCommandLineArg("savedatafolder", out specified), "An explicit isolated -savedatafolder is required.");
            output = Path.GetFullPath(GenFilePaths.SaveDataFolderPath).TrimEnd(Path.DirectorySeparatorChar);
            Require(string.Equals(output, Path.GetFullPath(specified).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase), "Save-data argument must resolve to the active profile.");
            Require(output.Replace('/', '\\').IndexOf("\\Tests\\Runtime\\", StringComparison.OrdinalIgnoreCase) >= 0
                && Path.GetFileName(output).IndexOf("FieldEcology", StringComparison.OrdinalIgnoreCase) >= 0,
                "Field QA requires a Tests/Runtime/*FieldEcology* profile; user profiles are forbidden.");
            Directory.CreateDirectory(output);
            Require(GenCommandLine.TryGetCommandLineArg("mushroomFieldBaseline", out baselinePath) && File.Exists(baselinePath), "Pass the generated baseline-v0.5-weights.csv with -mushroomFieldBaseline.");
            if (GenCommandLine.TryGetCommandLineArg("mushroomFieldMapSize", out specified))
                Require(int.TryParse(specified, out mapSize) && (mapSize == 150 || mapSize == 250), "Map size must be 150 or 250.");
            // Enoki has distinct cultivated and wild plant art/definitions;
            // count its wild form once, and exclude the assorted crop selector.
            species = DefDatabase<ThingDef>.AllDefs.Where(d => WildMushroomEcology.IsMushroom(d)
                && d.plant?.harvestedThingDef != null && d.defName != "RMush_PlantEnoki").OrderBy(d => d.defName, StringComparer.Ordinal).ToArray();
            Require(species.Length == 46, "Expected exactly 46 mushroom plant definitions, found " + species.Length);
            Require(species.Count(d => OldIds.Contains(Id(d))) == 15, "Old/new species partition must be 15/31.");
            ReadBaseline();
            originalMap = Find.CurrentMap;
            baseTick = Find.TickManager.TicksGame;
            originalWorldSeed = Find.World.info.seedString;
            originalStoryteller = DebugSettings.enableStoryteller;
            originalFastEcology = DebugSettings.fastEcology;
            DebugSettings.enableStoryteller = false;
            DebugSettings.fastEcology = false;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            metadata["status"] = "running";
            metadata["gameVersion"] = VersionControl.CurrentVersionString;
            metadata["mapSize"] = mapSize;
            metadata["geographySeed"] = originalWorldSeed;
            metadata["geographySeedHash"] = Find.World.info.Seed;
            metadata["mapSeeds"] = Seeds;
            metadata["speciesMapping"] = "46 harvest-producing wild plant definitions. Cultivated RMush_PlantEnoki and the assorted selector are excluded. RMush_PlantEnokiWild maps to baseline species Enoki and belongs to old15.";
            metadata["tileSelection"] = "For each biome: natural land tiles without an existing MapParent; prefer no mutators, then flat hilliness, then smallest tileId. No generated plant outcome is inspected during selection. Same selected tile is used for both map seeds and all weight/scenario copies.";
            metadata["baseGameTick"] = baseTick;
            metadata["baseAbsoluteTick"] = Find.TickManager.TicksAbs;
            metadata["runningMods"] = LoadedModManager.RunningMods.Select(m => m.PackageIdPlayerFacing).ToArray();
            metadata["baselineCsvSha256"] = Sha256(baselinePath);
            metadata["baselineMeaning"] = "Native map generation with v0.5 wildBiomes weights only. Current species, seasonal Harmony rules, habitat rules and caps remain active. This is a weight-only proxy, not the v0.5 DLL or a realized supply forecast.";
            metadata["longitudinalMeaning"] = "Accelerated component model: ecology every 2500 game-clock ticks; mushroom Plant.TickLong every native 2000 ticks; native celestial light, seasonal temperature and outdoor room equalization. Other plants, native background wild reseeding, animals, weather transitions, incidents and pawn jobs are not ticked. Initial weather is held. No terrain/roof/plant clearing, forced maturity, forced spawn or bypassed regrowth roll.";
            metadata["temperatureValidation"] = "At each plant/ecology update, TileTemperaturesComp.WorldComponentTick refreshes native clock-dependent temperature caches. Cached OutdoorTemp must match native OutdoorTemperatureAt(tile,TicksAbs) plus active map condition offsets within 0.001C. Outdoor room trackers are then equalized natively. No temperature, tile, season or weather is forced.";
            metadata["snowModel"] = "Snow-only reconstruction of native SteadyEnvironmentEffects: native MeltAmountAt delegate, native ceil(area*MapFractionCheckPerTick) visits per tick, native cellsInRandomOrder/cycle index, native SnowGrid.AddDepth. Temperature is evaluated at the native 60-tick cache cadence. Frozen/no-snow intervals advance the same visit index without unnecessary cell work. Initial weather is held and zero SnowRate is required; new snow, sand, filth/deterioration/fire/gas and other steady effects are not modeled. Initial snow is never forcibly cleared.";
            metadata["pairedRngMeaning"] = "Only after the untouched initial census, longitudinal ecology randomState is initialized from the explicit map seed and tile. Passive/harvest copies therefore begin with the same deterministic ecology seed. Native conditional random draws can diverge after harvest. This is random seeding, not a forced success or altered probability.";
            metadata["harvestMeaning"] = "Both seeds have passive and independently selected 25%-of-mature-plants-per-day harvest conditions on identically regenerated starting maps. The abstract unspawned Plants-10 colonist uses the native success/yield formula and PlantCollected hook; no travel, work, reachability, food safety choice or storage is modeled. Units are collected-potential, not colony stock or realized gameplay supply. Failed harvests destroy/reset plants natively and cannot queue regrowth. Each mature plant gets a new independent 0.25 selection roll per daily sweep.";
            metadata["settings"] = Settings();
            File.WriteAllText(Path.Combine(output, "initial-census.csv"), "sample,mode,biome,mapSeed,tile,mapId,totalCells,fertileCells,snowBlockedFertileCells,plants,mushrooms,old15,new31,season,plantDef,count,rawCommonality,nativeCommonality,seasonFactor\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(output, "daily-census.csv"), "sample,scenario,day,season,temperature,population,old15,new31,mature,matureEvents,matureDailyObservations,ringEvents,ringAdditions,regrowthAdditions,pending,extraBudgetUsed,cap,harvestAttempts,harvestFailures,harvestUnits,snowRemainingCells,snowCellsCleared,snowDepthRemoved,plantDef,count\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(output, "field-events.csv"), "sample,scenario,gameTick,day,event,plantDef,thingId,cell,growth,health,generation,amount,selectionProbability,nativeYieldStat,regrowthQueued\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(output, "field-ecology-report.txt"), "More Mushrooms native field ecology QA\n" + metadata["baselineMeaning"] + "\n" + metadata["longitudinalMeaning"] + "\n" + metadata["harvestMeaning"] + "\n\n", Encoding.UTF8);
            foreach (string biome in Biomes)
            {
                PlanetTile tile = ChooseTile(biome);
                for (int seed = 0; seed < Seeds.Length; seed++)
                {
                    samples.Enqueue(new Sample { biome = biome, tile = tile, seed = seed, baseline = false });
                    samples.Enqueue(new Sample { biome = biome, tile = tile, seed = seed, baseline = true });
                    if (LongitudinalBiome(biome)) samples.Enqueue(new Sample { biome = biome, tile = tile, seed = seed, baseline = false, harvest = true });
                }
            }
            Append("START " + samples.Count + " native maps; 12 longitudinal series x 120 days; geographySeed=" + originalWorldSeed);
            WriteResults();
        }

        private PlanetTile ChooseTile(string biomeName)
        {
            // Selection never considers a generated mushroom count. Prefer
            // ordinary flat natural tiles without mixed-biome mutators.
            var candidates = Enumerable.Range(0, Find.WorldGrid.TilesCount).Select(i => new PlanetTile(i))
                .Where(t => Find.WorldGrid[t].PrimaryBiome.defName == biomeName && !Find.WorldGrid[t].WaterCovered
                    && !Find.WorldObjects.AnyMapParentAt(t) && (biomeName != "Tundra" || Find.WorldGrid[t].Mutators.Count == 0)).ToArray();
            Require(candidates.Length > 0, "No natural " + biomeName + " tile exists in geography seed " + originalWorldSeed);
            return candidates.OrderBy(t => Find.WorldGrid[t].Mutators.Count != 0 ? 1 : 0)
                .ThenBy(t => Find.WorldGrid[t].hilliness == Hilliness.Flat ? 0 : 1).ThenBy(t => t.tileId).First();
        }

        private void StartSample(Sample sample)
        {
            activeSample = sample;
            sampleNumber++;
            Find.TickManager.DebugSetTicksGame(baseTick);
            Find.World.tileTemperatures.ClearCaches();
            activeParent = (MapParent)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
            activeParent.Tile = sample.tile;
            activeParent.SetFaction(Faction.OfPlayer);
            Find.WorldObjects.Add(activeParent);
            string priorSeed = Find.World.info.seedString;
            WeightScope scope = null;
            try
            {
                if (sample.baseline) scope = new WeightScope(species, baseline);
                Find.World.info.seedString = Seeds[sample.seed];
                activeMap = MapGenerator.GenerateMap(new IntVec3(mapSize, 1, mapSize), activeParent, MapGeneratorDefOf.Base_Player);
                Require(activeMap.Biome.defName == sample.biome, "Generated biome differs from selected natural tile.");
                // This census is the first operation after native map generation.
                // No fixture patch, field helper or simulated clock preceded it.
                Census census = CountInitial(activeMap, sample);
                if (sample.harvest)
                {
                    var passive = censuses.Single(c => c.biome == census.biome && c.mapSeed == census.mapSeed && c.mode == "current");
                    Require(passive.plants == census.plants && passive.fertileCells == census.fertileCells
                        && passive.plantStateSha256 == census.plantStateSha256 && passive.terrainRoofSha256 == census.terrainRoofSha256
                        && passive.snowStateSha256 == census.snowStateSha256
                        && passive.byDef.Zip(census.byDef, (a, b) => a.plantDef == b.plantDef && a.count == b.count).All(v => v),
                        "Harvest and passive initial native census must match for " + census.biome + " " + census.mapSeed);
                }
                censuses.Add(census);
                WriteInitialCsv(census);
                AssertNoNativeErrors();
                Append("INITIAL " + census.sample + " mode=" + census.mode + " biome=" + census.biome + " tile=" + census.tile
                    + " mushrooms=" + census.mushrooms + " old15=" + census.old15 + " new31=" + census.new31 + " allPlants=" + census.plants + " fertileCells=" + census.fertileCells);
            }
            finally
            {
                Find.World.info.seedString = priorSeed;
                scope?.Dispose();
            }
            if (!sample.baseline && LongitudinalBiome(sample.biome))
                BeginSeries();
            else CleanupMap();
            WriteResults();
        }

        private Census CountInitial(Map map, Sample sample)
        {
            Plant[] plants = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant>().ToArray();
            Plant_Mushroom[] mushrooms = plants.OfType<Plant_Mushroom>().Where(p => species.Contains(p.def)).ToArray();
            var tile = map.TileInfo;
            Vector2 longLat = Find.WorldGrid.LongLatOf(map.Tile);
            var result = new Census {
                sample = sample.biome + "-" + (sample.seed == 0 ? "A" : "B") + "-" + (sample.baseline ? "v05-weight-proxy" : sample.harvest ? "current-harvest25-copy" : "current"),
                mode = sample.baseline ? "v0.5-weight-only-proxy" : sample.harvest ? "current-harvest25-copy" : "current", biome = map.Biome.defName,
                mapSeed = Seeds[sample.seed], mapSeedHash = GenText.StableStringHash(Seeds[sample.seed]),
                nativeGenerationSeed = Gen.HashCombineInt(GenText.StableStringHash(Seeds[sample.seed]), map.Tile.GetHashCode()),
                tile = map.Tile.ToString(), mapId = map.uniqueID, totalCells = map.cellIndices.NumGridCells,
                fertileCells = map.AllCells.Count(c => map.fertilityGrid.FertilityAt(c) > 0f),
                fertileUnroofedCells = map.AllCells.Count(c => !c.Roofed(map) && map.fertilityGrid.FertilityAt(c) > 0f),
                snowBlockedFertileCells = map.AllCells.Count(c => !c.Roofed(map) && map.fertilityGrid.FertilityAt(c) > 0f && c.GetSnowDepth(map) >= 0.2f),
                plants = plants.Length, mushrooms = mushrooms.Length,
                old15 = mushrooms.Count(p => OldIds.Contains(Id(p.def))), new31 = mushrooms.Count(p => !OldIds.Contains(Id(p.def))),
                season = GenLocalDate.Season(map).ToString(), gameTick = Find.TickManager.TicksGame, absoluteTick = Find.TickManager.TicksAbs,
                longitude = longLat.x, latitude = longLat.y, tileAnnualTemperature = tile.temperature, actualOutdoorTemperature = map.mapTemperature.OutdoorTemp,
                rainfall = tile.rainfall, swampiness = tile.swampiness, hilliness = tile.hilliness.ToString(),
                mutators = tile.Mutators.Select(m => m.defName).ToArray(), allMapBiomes = map.Biomes.Select(b => b.defName).ToArray(),
                wildCap = map.GetComponent<MapComponent_WildMushrooms>().WildCap,
                ecologyRandomState = map.GetComponent<MapComponent_WildMushrooms>().RandomState,
                nextRingTick = map.GetComponent<MapComponent_WildMushrooms>().NextRingTick,
                weather = map.weatherManager.curWeather.defName
            };
            result.plantStateSha256 = HashText(string.Join("\n", plants.OrderBy(p => map.cellIndices.CellToIndex(p.Position)).ThenBy(p => p.def.defName, StringComparer.Ordinal)
                .Select(p => Csv(p.def.defName, p.Position, p.Growth, p.HitPoints, p.sown))));
            result.terrainRoofSha256 = HashText(string.Join("\n", map.AllCells.Select(c => Csv(c.GetTerrain(map).defName, c.GetRoof(map)?.defName))));
            result.snowStateSha256 = HashText(string.Join("\n", map.AllCells.Select(c => c.GetSnowDepth(map).ToString("R", CultureInfo.InvariantCulture))));
            foreach (ThingDef def in species)
                result.byDef.Add(new SpeciesCount { plantDef = def.defName, count = mushrooms.Count(p => p.def == def),
                    rawCommonality = RawWeight(def, map.Biome), nativeCommonality = WildMushroomEcology.NativeCommonality(def, map),
                    seasonFactor = WildMushroomEcology.SeasonMultiplierAt(def, map) });
            Require(result.old15 + result.new31 == result.mushrooms && result.byDef.Sum(s => s.count) == result.mushrooms, "Census totals must reconcile.");
            if (sample.biome == "Tundra") Require(mushrooms.Length == 0, "Unsupported Tundra control must contain zero custom mushrooms.");
            return result;
        }

        private void BeginSeries()
        {
            seriesStart = baseTick;
            nextPlantTick = baseTick + NativePlantInterval;
            nextEcologyTick = baseTick + EcologyInterval;
            nextDayTick = baseTick + GenDate.TicksPerDay;
            lastReportDay = 0;
            activeSeries = new Series { sample = activeSample.biome + "-" + (activeSample.seed == 0 ? "A" : "B"),
                scenario = activeSample.harvest ? "daily-25percent-abstract-harvest" : "passive", startTick = baseTick, horizonDays = HorizonDays,
                initialRandomState = activeMap.GetComponent<MapComponent_WildMushrooms>().RandomState };
            var randomField = typeof(MapComponent_WildMushrooms).GetField("randomState", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(randomField != null, "Native ecology random-state field must exist for paired deterministic seeds.");
            int pairedSeed = Gen.HashCombineInt(GenText.StableStringHash(Seeds[activeSample.seed]), activeMap.Tile.GetHashCode());
            if (pairedSeed == 0) pairedSeed = 1;
            randomField.SetValue(activeMap.GetComponent<MapComponent_WildMushrooms>(), pairedSeed);
            activeSeries.pairedEcologySeed = pairedSeed;
            InitializeSnowModel();
            foreach (ThingDef def in species) activeSeries.harvestUnitsByDef[def.defName] = 0;
            if (activeSample.harvest)
            {
                Rand.PushState(Gen.HashCombineInt(GenText.StableStringHash(Seeds[activeSample.seed]), activeMap.Tile.GetHashCode()));
                try
                {
                    abstractHarvester = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                    abstractHarvester.skills.GetSkill(SkillDefOf.Plants).Level = 10;
                    originalHarvestStat = abstractHarvester.GetStatValue(StatDefOf.PlantHarvestYield);
                    activeSeries.harvestStat = originalHarvestStat;
                    Require(!abstractHarvester.Spawned && originalHarvestStat > 0f, "Abstract harvest actor must remain unspawned with a valid native yield stat.");
                }
                finally { Rand.PopState(); }
            }
            series.Add(activeSeries);
            ObserveMature();
            activeSeries.initialMature = activeSeries.matureEvents;
            RecordDay(0);
            Append("LONGITUDINAL START " + activeSeries.sample + " " + activeSeries.scenario + " harvestStat=" + activeSeries.harvestStat.ToString("R", CultureInfo.InvariantCulture));
        }

        private void AdvanceSeries()
        {
            // Four days per UI update; game remains paused and no native full
            // tick manager pass, pawn job or storyteller event is performed.
            int stop = Math.Min(baseTick + HorizonDays * GenDate.TicksPerDay, nextDayTick + 3 * GenDate.TicksPerDay);
            while ((nextEventTick = Math.Min(nextPlantTick, Math.Min(nextEcologyTick, nextDayTick))) <= stop)
            {
                Find.TickManager.DebugSetTicksGame(nextEventTick);
                Rand.PushState(Gen.HashCombineInt(GenText.StableStringHash(activeSeries.sample), nextEventTick - baseTick));
                try
                {
                    if (nextEventTick == nextPlantTick)
                    {
                        UpdateNativeEnvironment();
                        var plants = WildPlants();
                        foreach (var plant in plants)
                        {
                            plant.TickLong();
                            activeSeries.nativePlantTickCalls++;
                            if (!plant.Spawned || plant.Destroyed) activeSeries.naturalLosses++;
                        }
                        ObserveMature();
                        nextPlantTick += NativePlantInterval;
                    }
                    if (nextEventTick == nextEcologyTick)
                    {
                        UpdateNativeEnvironment();
                        RunEcologyStep();
                        nextEcologyTick += EcologyInterval;
                    }
                    if (nextEventTick == nextDayTick)
                    {
                        int day = (nextDayTick - seriesStart) / GenDate.TicksPerDay;
                        activeSeries.matureDailyObservations += WildPlants().Count(p => p.LifeStage == PlantLifeStage.Mature && p.HarvestableNow && p.CanYieldNow());
                        if (abstractHarvester != null) HarvestSweep();
                        RecordDay(day);
                        nextDayTick += GenDate.TicksPerDay;
                    }
                }
                finally { Rand.PopState(); }
                AssertNoNativeErrors();
            }
            if (lastReportDay >= HorizonDays)
            {
                Require(activeSeries.ecologyCalls == HorizonDays * GenDate.TicksPerDay / EcologyInterval, "Ecology horizon call count must be exact.");
                Require(activeSeries.snowModeledTicks == (long)HorizonDays * GenDate.TicksPerDay, "Snow-only elapsed horizon must be exact.");
                Require(activeSeries.snowCellVisitsExecuted + activeSeries.snowCellVisitsSkipped == activeSeries.snowModeledTicks * snowChecksPerTick,
                    "Snow-only cell visits must match the native per-tick frequency.");
                Require(activeSeries.initialSnowCells == remainingSnowCells + activeSeries.snowCellsCleared, "Snow cell counts must reconcile without new snowfall.");
                Append("LONGITUDINAL COMPLETE " + activeSeries.sample + " scenario=" + activeSeries.scenario + " rings=" + activeSeries.ringEvents
                    + " ringPlants=" + activeSeries.ringAdditions + " regrowth=" + activeSeries.regrowthAdditions + " harvestUnits=" + activeSeries.harvestUnits
                    + " attempts=" + activeSeries.harvestAttempts + " failures=" + activeSeries.harvestFailures + " naturalLosses=" + activeSeries.naturalLosses);
                activeSeries = null;
                abstractHarvester = null;
                CleanupMap();
            }
            WriteResults();
        }

        private void UpdateNativeEnvironment()
        {
            // GetOutdoorTemp reads a cache; DebugSetTicksGame does not tick
            // this world component. Without this native refresh, temperature
            // remains frozen at map generation despite advancing the seasons.
            Find.World.tileTemperatures.WorldComponentTick();
            Require(NativeTemperatureOffset != null, "Native internal temperature-offset method must exist.");
            float conditionOffset = (float)NativeTemperatureOffset.Invoke(activeMap.gameConditionManager, null);
            float expected = activeMap.Biome.constantOutdoorTemperature
                ?? (Find.World.tileTemperatures.OutdoorTemperatureAt(activeMap.Tile, Find.TickManager.TicksAbs)
                    + conditionOffset);
            float actual = activeMap.mapTemperature.OutdoorTemp;
            float error = Math.Abs(actual - expected);
            Require(WildMushroomEcology.Finite(actual) && WildMushroomEcology.Finite(expected) && error < 0.001f,
                "Native temperature cache disagrees with clock calculation: tick=" + Find.TickManager.TicksGame + " cached=" + actual + " expected=" + expected);
            activeSeries.nativeTemperatureChecks++;
            activeSeries.maxTemperatureCacheError = Math.Max(activeSeries.maxTemperatureCacheError, error);
            AdvanceNativeSnow(conditionOffset);
            activeMap.skyManager.SkyManagerUpdate();
            foreach (Room room in activeMap.regionGrid.AllRooms)
                if (room.UsesOutdoorTemperature) room.TempTracker.EqualizeTemperature();
        }

        private void InitializeSnowModel()
        {
            Require(NativeSnowMelt != null && NativeSnowCycle != null && NativeSnowFraction != null,
                "Native steady-environment snow API must be recognized.");
            Require(activeMap.weatherManager.SnowRate <= 0.001f, "Snow-only melt model requires initial zero snowfall.");
            nativeMeltAmount = (Func<float, float>)Delegate.CreateDelegate(typeof(Func<float, float>), activeMap.steadyEnvironmentEffects, NativeSnowMelt);
            snowCycleIndex = (int)NativeSnowCycle.GetValue(activeMap.steadyEnvironmentEffects);
            snowChecksPerTick = Mathf.CeilToInt(activeMap.Area * (float)NativeSnowFraction.GetRawConstantValue());
            Require(snowChecksPerTick > 0 && snowCycleIndex >= 0 && snowCycleIndex < activeMap.Area, "Native snow visitation schedule must be valid.");
            snowLastTick = baseTick;
            absoluteTickOffset = Find.TickManager.TicksAbs - Find.TickManager.TicksGame;
            remainingSnowCells = activeMap.AllCells.Count(c => c.GetSnowDepth(activeMap) > 0f
                && (c.GetRoom(activeMap) == null || c.GetRoom(activeMap).UsesOutdoorTemperature));
            activeSeries.initialSnowCells = remainingSnowCells;
            activeSeries.snowChecksPerTick = snowChecksPerTick;
            activeSeries.initialSnowCycleIndex = snowCycleIndex;
        }

        private void AdvanceNativeSnow(float conditionOffset)
        {
            Require(activeMap.weatherManager.SnowRate <= 0.001f, "Held-weather snow-only model cannot silently omit snowfall.");
            int now = Find.TickManager.TicksGame;
            while (snowLastTick < now)
            {
                if (remainingSnowCells == 0)
                {
                    AdvanceSnowIndex(now - snowLastTick);
                    snowLastTick = now;
                    break;
                }
                int climateTick = baseTick + ((snowLastTick + 1 - baseTick) / 60) * 60;
                int segmentEnd = Math.Min(now, climateTick + 59);
                float temperature = activeMap.Biome.constantOutdoorTemperature
                    ?? (Find.World.tileTemperatures.OutdoorTemperatureAt(activeMap.Tile, climateTick + absoluteTickOffset) + conditionOffset);
                float melt = nativeMeltAmount(temperature);
                Require(WildMushroomEcology.Finite(melt) && melt >= 0f, "Native snow melt function must return a finite nonnegative depth.");
                if (melt <= 0f)
                {
                    AdvanceSnowIndex(segmentEnd - snowLastTick);
                    snowLastTick = segmentEnd;
                    continue;
                }
                while (snowLastTick < segmentEnd)
                {
                    for (int i = 0; i < snowChecksPerTick; i++)
                    {
                        if (snowCycleIndex >= activeMap.Area) snowCycleIndex = 0;
                        IntVec3 cell = activeMap.cellsInRandomOrder.Get(snowCycleIndex++);
                        activeSeries.snowCellVisitsExecuted++;
                        float before = cell.GetSnowDepth(activeMap);
                        if (before <= 0f) continue;
                        Room room = cell.GetRoom(activeMap);
                        if (room != null && !room.UsesOutdoorTemperature) continue;
                        activeMap.snowGrid.AddDepth(cell, -melt);
                        float after = cell.GetSnowDepth(activeMap);
                        Require(after <= before, "Native melting must not add snow.");
                        activeSeries.snowDepthRemoved += before - after;
                        activeSeries.snowMeltUpdates++;
                        if (after <= 0f) { remainingSnowCells--; activeSeries.snowCellsCleared++; }
                    }
                    snowLastTick++;
                    activeSeries.snowModeledTicks++;
                    if (remainingSnowCells == 0) break;
                }
            }
        }

        private void AdvanceSnowIndex(int ticks)
        {
            long visits = (long)ticks * snowChecksPerTick;
            snowCycleIndex = (int)((snowCycleIndex + visits) % activeMap.Area);
            activeSeries.snowCellVisitsSkipped += visits;
            activeSeries.snowModeledTicks += ticks;
        }

        private Plant_Mushroom[] WildPlants() => activeMap.listerThings.ThingsInGroup(ThingRequestGroup.Plant)
            .OfType<Plant_Mushroom>().Where(p => p.Spawned && !p.sown).ToArray();

        private void ObserveMature()
        {
            foreach (var plant in WildPlants().Where(p => p.LifeStage == PlantLifeStage.Mature))
                if (activeSeries.seenMature.Add(plant.ThingID))
                {
                    activeSeries.matureEvents++;
                    Event("first-mature-observation", plant);
                }
        }

        private void RunEcologyStep()
        {
            var component = activeMap.GetComponent<MapComponent_WildMushrooms>();
            var prior = new HashSet<string>(WildPlants().Select(p => p.ThingID));
            var pending = component.PendingRegrowth.ToArray();
            int oldRingTick = component.NextRingTick;
            int oldBudget = component.ExtraSpawnedToday;
            if (Find.TickManager.TicksGame >= oldRingTick && component.Settings.dailyExtraBudget - oldBudget >= component.Settings.ringMinCount
                && component.WildPopulationCount < component.WildCap) activeSeries.ringOpportunityChecks++;
            component.MapComponentTick();
            activeSeries.ecologyCalls++;
            var added = WildPlants().Where(p => !prior.Contains(p.ThingID)).ToArray();
            int regenerated = added.Count(p => p.WildRegrowthGeneration == 1);
            int ring = added.Length - regenerated;
            activeSeries.regrowthAdditions += regenerated;
            activeSeries.ringAdditions += ring;
            foreach (var plant in added) Event(plant.WildRegrowthGeneration == 1 ? "regrowth-addition" : "ring-addition", plant);
            if (component.NextRingTick != oldRingTick && ring > 0) activeSeries.ringEvents++;
            var retained = new HashSet<string>(component.PendingRegrowth.Select(p => p.sourceId));
            activeSeries.regrowthExpiredOrInvalid += pending.Count(p => !retained.Contains(p.sourceId)
                && !added.Any(a => a.WildRegrowthGeneration == 1 && a.def == p.plantDef && a.Position == p.cell));
            activeSeries.duePendingCheckObservations += component.PendingRegrowth.Count(p => p.dueTick <= Find.TickManager.TicksGame);
            activeSeries.maxBudgetUsed = Math.Max(activeSeries.maxBudgetUsed, component.ExtraSpawnedToday);
            activeSeries.maxPopulation = Math.Max(activeSeries.maxPopulation, component.WildPopulationCount);
            activeSeries.maxPending = Math.Max(activeSeries.maxPending, component.PendingRegrowthCount);
            int gameDay = Find.TickManager.TicksGame / GenDate.TicksPerDay;
            if (!activeSeries.dailyExtraAdditionsByGameDay.ContainsKey(gameDay)) activeSeries.dailyExtraAdditionsByGameDay[gameDay] = 0;
            activeSeries.dailyExtraAdditionsByGameDay[gameDay] += added.Length;
            if (oldBudget >= component.Settings.dailyExtraBudget) activeSeries.budgetSaturatedCheckObservations++;
            if (component.WildPopulationCount >= component.WildCap) activeSeries.capSaturatedCheckObservations++;
            Require(component.ExtraSpawnedToday <= component.Settings.dailyExtraBudget, "Native daily extra budget exceeded.");
            Require(component.PendingRegrowthCount <= component.Settings.maxPendingRegrowth, "Native pending ticket cap exceeded.");
            Require(component.WildPopulationCount <= component.WildCap, "Native mushroom population cap exceeded.");
            Require(component.ExtraSpawnedToday - oldBudget == added.Length, "Extra-spawn budget must reconcile with actual additions.");
            Require(activeSeries.dailyExtraAdditionsByGameDay[gameDay] == component.ExtraSpawnedToday, "Observed daily additions must reconcile with native daily counter.");
        }

        private void HarvestSweep()
        {
            var component = activeMap.GetComponent<MapComponent_WildMushrooms>();
            foreach (Plant_Mushroom plant in WildPlants().Where(p => p.LifeStage == PlantLifeStage.Mature && p.HarvestableNow && p.CanYieldNow()).OrderBy(p => p.ThingID, StringComparer.Ordinal).ToArray())
            {
                if (Rand.Value >= 0.25f) continue;
                activeSeries.harvestAttempts++;
                // Match native JobDriver_PlantWork's success and yield formula.
                // No growth, health, harvestFailable or chance is overwritten.
                StatDef stat = plant.def.plant.harvestedThingDef.IsDrug || plant.def.plant.drugForHarvestPurposes ? StatDefOf.DrugHarvestYield : StatDefOf.PlantHarvestYield;
                float yieldStat = abstractHarvester.GetStatValue(stat);
                bool failed = abstractHarvester.RaceProps.Humanlike && plant.def.plant.harvestFailable && !plant.Blighted && Rand.Value > yieldStat;
                int amount = 0;
                if (!failed)
                {
                    amount = plant.YieldNow();
                    if (yieldStat > 1f) amount = GenMath.RoundRandom(amount * yieldStat);
                    if (amount > 0) plant.NotifySuccessfulHarvest();
                }
                else activeSeries.harvestFailures++;
                int ticketsBefore = component.PendingRegrowthCount;
                // Save the observation before PlantCollected can destroy it.
                string eventPrefix = Csv(activeSeries.sample, activeSeries.scenario, Find.TickManager.TicksGame,
                    (Find.TickManager.TicksGame - seriesStart) / (float)GenDate.TicksPerDay,
                    failed ? "harvest-failure" : "harvest-success", plant.def.defName, plant.ThingID, plant.Position.ToString(),
                    plant.Growth, plant.HitPoints, plant.WildRegrowthGeneration, amount, 0.25f, yieldStat);
                plant.PlantCollected(abstractHarvester, PlantDestructionMode.Chop);
                int queued = component.PendingRegrowthCount - ticketsBefore;
                Require(!failed || queued == 0, "A failed harvest must not queue regrowth.");
                activeSeries.regrowthQueued += queued;
                activeSeries.harvestUnits += amount;
                activeSeries.harvestUnitsByDef[plant.def.defName] += amount;
                eventRows.AppendLine(eventPrefix + "," + queued.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void RecordDay(int day)
        {
            var component = activeMap.GetComponent<MapComponent_WildMushrooms>();
            var plants = WildPlants();
            var record = new Day { day = day, season = GenLocalDate.Season(activeMap).ToString(), temperature = activeMap.mapTemperature.OutdoorTemp,
                population = plants.Length, old15 = plants.Count(p => OldIds.Contains(Id(p.def))), new31 = plants.Count(p => !OldIds.Contains(Id(p.def))),
                mature = plants.Count(p => p.LifeStage == PlantLifeStage.Mature), matureEvents = activeSeries.matureEvents, matureDailyObservations = activeSeries.matureDailyObservations,
                ringEvents = activeSeries.ringEvents, ringAdditions = activeSeries.ringAdditions, regrowthAdditions = activeSeries.regrowthAdditions,
                pending = component.PendingRegrowthCount, extraBudgetUsed = component.ExtraSpawnedToday, cap = component.WildCap,
                harvestAttempts = activeSeries.harvestAttempts, harvestFailures = activeSeries.harvestFailures, harvestUnits = activeSeries.harvestUnits,
                snowRemainingCells = remainingSnowCells, snowCellsCleared = activeSeries.snowCellsCleared, snowDepthRemoved = activeSeries.snowDepthRemoved };
            foreach (ThingDef def in species) record.byDef[def.defName] = plants.Count(p => p.def == def);
            activeSeries.days.Add(record);
            var lines = new StringBuilder();
            foreach (var pair in record.byDef)
                lines.AppendLine(Csv(activeSeries.sample, activeSeries.scenario, day, record.season, record.temperature, record.population,
                    record.old15, record.new31, record.mature, record.matureEvents, record.matureDailyObservations, record.ringEvents, record.ringAdditions, record.regrowthAdditions, record.pending, record.extraBudgetUsed,
                    record.cap, record.harvestAttempts, record.harvestFailures, record.harvestUnits, record.snowRemainingCells, record.snowCellsCleared, record.snowDepthRemoved, pair.Key, pair.Value));
            File.AppendAllText(Path.Combine(output, "daily-census.csv"), lines.ToString(), Encoding.UTF8);
            if (eventRows.Length > 0)
            {
                File.AppendAllText(Path.Combine(output, "field-events.csv"), eventRows.ToString(), Encoding.UTF8);
                eventRows.Clear();
            }
            lastReportDay = day;
            if (day % 30 == 0) Append("DAY " + activeSeries.sample + " " + day + " season=" + record.season + " population=" + record.population + " rings=" + record.ringEvents + " regrowth=" + record.regrowthAdditions + " harvestUnits=" + record.harvestUnits);
        }

        private void CleanupMap()
        {
            if (activeMap != null)
            {
                Current.Game.CurrentMap = originalMap;
                Current.Game.DeinitAndRemoveMap(activeMap, false);
                activeMap = null;
            }
            if (activeParent != null && !activeParent.Destroyed) activeParent.Destroy();
            activeParent = null;
            Find.TickManager.DebugSetTicksGame(baseTick);
            Find.World.tileTemperatures.ClearCaches();
            AssertNoNativeErrors();
        }

        private void Complete()
        {
            Require(censuses.Count == 34 && series.Count == 12, "Expected 28 primary initial censuses, six paired harvest copies and twelve 120-day series.");
            Require(series.All(s => s.days.Count == 121), "Each series must retain day 0 through day 120.");
            Require(censuses.All(c => c.byDef.Count == 46), "Every initial census must include all 46 definitions, including zeros.");
            Append("INITIAL PAIRED SUMMARY (weight-only proxy; no v0.5 runtime/supply claim)");
            foreach (var current in censuses.Where(c => c.mode == "current"))
            {
                var proxy = censuses.Single(c => c.mode == "v0.5-weight-only-proxy" && c.biome == current.biome && c.mapSeed == current.mapSeed);
                Append(current.biome + " " + current.mapSeed + " current=" + current.mushrooms + " proxy=" + proxy.mushrooms
                    + " delta=" + (current.mushrooms - proxy.mushrooms) + " currentSpecies=" + current.byDef.Count(d => d.count > 0)
                    + " proxySpecies=" + proxy.byDef.Count(d => d.count > 0) + " old15=" + current.old15 + "/" + proxy.old15 + " new31=" + current.new31 + "/" + proxy.new31);
            }
            Append("SPECIES INITIAL OBSERVATIONS across 14 primary maps per mode (not probabilities)");
            foreach (ThingDef def in species)
            {
                var current = censuses.Where(c => c.mode == "current").Select(c => c.byDef.Single(d => d.plantDef == def.defName)).ToArray();
                var proxy = censuses.Where(c => c.mode == "v0.5-weight-only-proxy").Select(c => c.byDef.Single(d => d.plantDef == def.defName)).ToArray();
                Append(def.defName + " currentCount=" + current.Sum(d => d.count) + " proxyCount=" + proxy.Sum(d => d.count)
                    + " currentDetectedMaps=" + current.Count(d => d.count > 0) + " currentNativeEligibleMaps=" + current.Count(d => d.nativeCommonality > 0f));
            }
            metadata["status"] = "passed";
            metadata["completedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            metadata["exitCode"] = 0;
            Append("PASS: untouched initial censuses, native weight-only proxy and accelerated ecology completed. Zero detections are observations, not proof of absence; the two seeds are not a statistical prevalence estimate.");
            WriteResults();
            Finish(0);
        }

        private void Fail(Exception error)
        {
            metadata["status"] = "failed";
            metadata["failure"] = error.ToString();
            metadata["exitCode"] = 1;
            if (output != null)
            {
                try { Append("FAIL " + error); WriteResults(); } catch { }
            }
            Log.Error("More Mushrooms field ecology QA failed: " + error);
            Finish(1);
        }

        private void Finish(int exitCode)
        {
            finished = true;
            Application.logMessageReceived -= CaptureNativeLog;
            if (geographySeedPushed) { Rand.PopState(); geographySeedPushed = false; }
            if (originalMap != null)
            {
                try
                {
                    if (activeMap != null) { Current.Game.CurrentMap = originalMap; Current.Game.DeinitAndRemoveMap(activeMap, false); }
                    if (activeParent != null && !activeParent.Destroyed) activeParent.Destroy();
                    Find.TickManager.DebugSetTicksGame(baseTick);
                    Find.World.info.seedString = originalWorldSeed;
                    Find.World.tileTemperatures.ClearCaches();
                    DebugSettings.enableStoryteller = originalStoryteller;
                    DebugSettings.fastEcology = originalFastEcology;
                }
                catch (Exception cleanup)
                {
                    metadata["status"] = "failed"; metadata["cleanupFailure"] = cleanup.ToString(); metadata["exitCode"] = 1;
                    if (output != null) { File.AppendAllText(Path.Combine(output, "field-ecology-report.txt"), "Cleanup failed: " + cleanup); WriteResults(); }
                    exitCode = 1;
                }
            }
            Application.Quit(exitCode);
        }

        private void ReadBaseline()
        {
            foreach (string line in File.ReadAllLines(baselinePath).Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                string[] fields = line.Split(',');
                Require(fields.Length == 3, "Baseline CSV must have three unquoted columns.");
                if (fields[0] == "RMush_PlantEnoki") fields[0] = "RMush_PlantEnokiWild";
                float value;
                Require(float.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0f && !float.IsNaN(value) && !float.IsInfinity(value), "Invalid baseline commonality.");
                if (!baseline.ContainsKey(fields[0])) baseline[fields[0]] = new Dictionary<string, float>();
                baseline[fields[0]].Add(fields[1], value);
            }
            Require(baseline.Count == 46 && species.All(d => baseline.ContainsKey(d.defName)), "Baseline CSV must cover the same 46 species.");
            Require(baseline.Values.All(v => Biomes.Take(6).All(v.ContainsKey)), "Baseline CSV must include zero-valued records for all six supported biomes.");
        }

        // Only the in-memory native definition records and their exact cache
        // references are swapped. Disk definitions and all original references
        // are restored in finally before the next UI update or native tick.
        private sealed class WeightScope : IDisposable
        {
            private readonly Dictionary<ThingDef, List<PlantBiomeRecord>> records = new Dictionary<ThingDef, List<PlantBiomeRecord>>();
            private readonly Dictionary<BiomeDef, object[]> caches = new Dictionary<BiomeDef, object[]>();
            private readonly FieldInfo[] fields;
            public WeightScope(ThingDef[] definitions, Dictionary<string, Dictionary<string, float>> values)
            {
                fields = CacheNames.Select(n => typeof(BiomeDef).GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)).ToArray();
                Require(fields.All(f => f != null), "Native BiomeDef cache layout must be recognized before swapping weights.");
                foreach (BiomeDef biome in DefDatabase<BiomeDef>.AllDefs) caches[biome] = fields.Select(f => f.GetValue(biome)).ToArray();
                try
                {
                    foreach (ThingDef def in definitions)
                    {
                        records[def] = def.plant.wildBiomes;
                        def.plant.wildBiomes = values[def.defName].Where(p => p.Value > 0f)
                            .Select(p => new PlantBiomeRecord { biome = DefDatabase<BiomeDef>.GetNamed(p.Key), commonality = p.Value }).ToList();
                    }
                    foreach (BiomeDef biome in caches.Keys) foreach (FieldInfo field in fields) field.SetValue(biome, null);
                }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            {
                foreach (var record in records) record.Key.plant.wildBiomes = record.Value;
                foreach (var cache in caches) for (int i = 0; i < fields.Length; i++) fields[i].SetValue(cache.Key, cache.Value[i]);
                Require(records.All(r => ReferenceEquals(r.Key.plant.wildBiomes, r.Value)), "Definition weight references were not restored.");
                Require(caches.All(c => fields.Select((f, i) => ReferenceEquals(f.GetValue(c.Key), c.Value[i]) || Equals(f.GetValue(c.Key), c.Value[i])).All(v => v)), "Biome caches were not restored exactly.");
            }
        }

        private void WriteInitialCsv(Census census)
        {
            var text = new StringBuilder();
            foreach (var s in census.byDef) text.AppendLine(Csv(census.sample, census.mode, census.biome, census.mapSeed, census.tile,
                census.mapId, census.totalCells, census.fertileCells, census.snowBlockedFertileCells, census.plants, census.mushrooms, census.old15, census.new31,
                census.season, s.plantDef, s.count, s.rawCommonality, s.nativeCommonality, s.seasonFactor));
            File.AppendAllText(Path.Combine(output, "initial-census.csv"), text.ToString(), Encoding.UTF8);
        }

        private void WriteResults()
        {
            metadata["initialCensuses"] = censuses;
            metadata["longitudinalSeries"] = series;
            lock (nativeErrors) metadata["nativeErrors"] = nativeErrors.ToArray();
            File.WriteAllText(Path.Combine(output, "field-ecology-results.json"), Json(metadata), Encoding.UTF8);
        }
        private void Event(string kind, Plant_Mushroom plant)
        {
            eventRows.AppendLine(Csv(activeSeries.sample, activeSeries.scenario, Find.TickManager.TicksGame,
                (Find.TickManager.TicksGame - seriesStart) / (float)GenDate.TicksPerDay, kind, plant.def.defName, plant.ThingID,
                plant.Position.ToString(), plant.Growth, plant.HitPoints, plant.WildRegrowthGeneration, 0, 0, 0, 0));
        }
        private void Append(string text) => File.AppendAllText(Path.Combine(output, "field-ecology-report.txt"), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + text + Environment.NewLine, Encoding.UTF8);
        private void AssertNoNativeErrors() { lock (nativeErrors) Require(nativeErrors.Count == 0, "Native runtime emitted error/exception: " + string.Join("\n", nativeErrors.ToArray())); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static string Id(ThingDef def) => def.defName == "RMush_PlantEnokiWild" ? "Enoki" : def.defName.Substring("RMush_Plant".Length);
        private static bool LongitudinalBiome(string biome) => biome == "TemperateForest" || biome == "BorealForest" || biome == "TropicalRainforest";
        private static float RawWeight(ThingDef def, BiomeDef biome) => def.plant.wildBiomes?.Where(r => r.biome == biome).Sum(r => r.commonality) ?? 0f;
        private static string Sha256(string path) { using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private static string HashText(string text) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        private static object Settings()
        {
            var s = WildMushroomEcology.Settings;
            return new Dictionary<string, object> { { "checkIntervalTicks", s.checkIntervalTicks }, { "sampleAttempts", s.sampleAttempts }, { "dailyExtraBudget", s.dailyExtraBudget },
                { "wildCapPer10000Cells", s.wildCapPer10000Cells }, { "minWildCap", s.minWildCap }, { "maxWildCap", s.maxWildCap }, { "ringChancePerCheck", s.ringChancePerCheck },
                { "ringCooldownDays", s.ringCooldownDays }, { "ringMinCount", s.ringMinCount }, { "ringMaxCount", s.ringMaxCount }, { "maxPendingRegrowth", s.maxPendingRegrowth }, { "regrowthExpiryDays", s.regrowthExpiryDays } };
        }
        private static string Csv(params object[] values) => string.Join(",", values.Select(v => "\"" + Convert.ToString(v, CultureInfo.InvariantCulture).Replace("\"", "\"\"") + "\""));
        private static string Json(object value)
        {
            if (value == null) return "null";
            if (value is string) return "\"" + ((string)value).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is IDictionary)
            {
                // Mono's generic Dictionary IEnumerable enumerator yields
                // KeyValuePair, even when the value also implements IDictionary.
                // Enumerate Keys rather than casting that sequence to entries.
                var dictionary = (IDictionary)value;
                return "{" + string.Join(",", dictionary.Keys.Cast<object>().Select(key => Json(Convert.ToString(key, CultureInfo.InvariantCulture)) + ":" + Json(dictionary[key]))) + "}";
            }
            if (value is IEnumerable) return "[" + string.Join(",", ((IEnumerable)value).Cast<object>().Select(Json)) + "]";
            if (value is IConvertible) return Convert.ToString(value, CultureInfo.InvariantCulture);
            return "{" + string.Join(",", value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => Json(f.Name) + ":" + Json(f.GetValue(value)))) + "}";
        }

        private sealed class Sample { public string biome; public PlanetTile tile; public int seed; public bool baseline, harvest; }
        private sealed class SpeciesCount { public string plantDef; public int count; public float rawCommonality, nativeCommonality, seasonFactor; }
        private sealed class Census
        {
            public string sample, mode, biome, mapSeed, tile, season, hilliness, weather, plantStateSha256, terrainRoofSha256, snowStateSha256;
            public int mapSeedHash, nativeGenerationSeed, mapId, totalCells, fertileCells, fertileUnroofedCells, snowBlockedFertileCells, plants, mushrooms, old15, new31, gameTick, absoluteTick, wildCap, ecologyRandomState, nextRingTick;
            public float longitude, latitude, tileAnnualTemperature, actualOutdoorTemperature, rainfall, swampiness;
            public string[] mutators, allMapBiomes;
            public List<SpeciesCount> byDef = new List<SpeciesCount>();
        }
        private sealed class Series
        {
            public string sample, scenario;
            public int startTick, horizonDays, initialRandomState, pairedEcologySeed, ecologyCalls, nativePlantTickCalls, nativeTemperatureChecks, naturalLosses, initialMature, matureEvents, matureDailyObservations, ringOpportunityChecks,
                ringEvents, ringAdditions, regrowthAdditions, regrowthQueued, regrowthExpiredOrInvalid, duePendingCheckObservations,
                maxBudgetUsed, maxPopulation, maxPending, budgetSaturatedCheckObservations, capSaturatedCheckObservations,
                harvestAttempts, harvestFailures, harvestUnits;
            public int initialSnowCells, snowChecksPerTick, initialSnowCycleIndex, snowMeltUpdates, snowCellsCleared;
            public long snowModeledTicks, snowCellVisitsExecuted, snowCellVisitsSkipped;
            public double snowDepthRemoved;
            public float harvestStat, maxTemperatureCacheError;
            internal readonly HashSet<string> seenMature = new HashSet<string>();
            public Dictionary<string, int> harvestUnitsByDef = new Dictionary<string, int>();
            public Dictionary<int, int> dailyExtraAdditionsByGameDay = new Dictionary<int, int>();
            public List<Day> days = new List<Day>();
        }
        private sealed class Day
        {
            public int day, population, old15, new31, mature, matureEvents, matureDailyObservations, ringEvents, ringAdditions, regrowthAdditions, pending, extraBudgetUsed, cap, harvestAttempts, harvestFailures, harvestUnits;
            public int snowRemainingCells, snowCellsCleared;
            public double snowDepthRemoved;
            public string season;
            public float temperature;
            public Dictionary<string, int> byDef = new Dictionary<string, int>();
        }
    }
}
