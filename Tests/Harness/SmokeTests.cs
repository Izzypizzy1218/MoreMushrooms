using System;
using System.IO;
using System.Linq;
using RimWorld;
using RimMushrooms;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMushroomsTests
{
    public sealed class SmokeTests : GameComponent
    {
        private static readonly string[] LegacyIds = { "Button", "Shiitake", "Oyster", "KingOyster", "Enoki", "WoodEar", "Beech", "Maitake", "LionsMane", "Matsutake" };
        internal static bool LegacyRaw(ThingDef def) => LegacyIds.Any(id => def.defName == "RMush_Raw" + id);
        private static bool NewCultivated(ThingDef def) => def.defName == "RMush_RawCauliflower" || def.defName == "RMush_RawPurpleBlewit";
        private int phase, cropIndex, frames, assortedCycles;
        private bool finished;
        private Pawn farmer;
        private Plant currentPlant;
        private ThingDef[] plants;
        private IntVec3 workCell;
        private float deadline;
        private int baseline;
        private bool originalHarvestFailable;
        private Zone_Growing growingZone;
        private HydroponicsTests hydroTests;
        private string Report => Path.Combine(GenFilePaths.SaveDataFolderPath, "smoke-report.txt");
        public SmokeTests(Game game) { }
        public override void LoadedGame()
        {
            // Inspect saved values before the simulation advances on reload.
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            phase = 100;
        }

        private void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            File.AppendAllText(Report, "PASS " + message + Environment.NewLine);
        }
        private string Texture(Thing thing) => thing.Graphic.MatSingleFor(thing).mainTexture.name;
        private int Count(Map map, ThingDef def) => map.listerThings.ThingsOfDef(def).Where(t => t.Position.DistanceTo(workCell) < 5f).Sum(t => t.stackCount);


        public override void GameComponentUpdate()
        {
            if (finished || !GenCommandLine.CommandLineArgPassed("mushroomSmoke") || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting) return;
            var map = Find.CurrentMap;
            if (map == null) return;
            try
            {
                if (phase < 100)
                {
                    // This is an isolated crop/health fixture. Random incidents
                    // and lightning fires must not preempt ordered test jobs.
                    DebugSettings.enableStoryteller = false;
                    if (map.weatherManager.curWeather != WeatherDefOf.Clear)
                        map.weatherManager.TransitionTo(WeatherDefOf.Clear);
                    map.weatherManager.curWeatherAge = 0;
                }
                if ((phase >= 1 && phase <= 3) || (phase >= 10 && phase <= 13) || phase == 20)
                {
                    foreach (var window in Find.WindowStack.Windows.ToList()) window.Close(false);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
                    if (farmer != null)
                    {
                        // Stop with the worker's actual condition instead of
                        // dereferencing needs that native death can remove.
                        if (farmer.Dead || farmer.Destroyed || !farmer.Spawned || farmer.Downed
                            || farmer.needs?.food == null || farmer.needs?.rest == null)
                            throw new InvalidOperationException("Crop worker became unavailable: " + WorkerDiagnostic(map));
                        farmer.needs.food.CurLevelPercentage = 1f;
                        farmer.needs.rest.CurLevelPercentage = 1f;
                        if (farmer.needs.mood != null) farmer.needs.mood.CurLevelPercentage = 1f;
                    }
                }
                if (phase == 100)
                {
                    bool legacyLoaded = File.Exists(Path.Combine(GenFilePaths.SaveDataFolderPath, "new-save-verified.flag"));
                    LightTests.VerifyLoaded(map, legacyLoaded, Check);
                    MoodTests.VerifyLoaded(map, Check, legacyLoaded);
                    AssortedTests.VerifyLoaded(map, Check);
                    HydroponicsTests.VerifyLoaded(map, Check);
                    if (!legacyLoaded)
                    {
                        EcologyTests.VerifyLoaded(map, false, Check);
                        PoisonTests.VerifyLoaded(map, Check);
                        PsychoactiveTests.VerifyLoaded(map, Check);
                        ExpansionTests.VerifyLoaded(map, Check);
                        MealExposureTests.VerifyLoaded(map, Check);
                    }
                    else
                    {
                        PoisonTests.VerifyLegacy(map, Check);
                        PsychoactiveTests.VerifyLegacy(map, Check);
                        EcologyTests.VerifyLoaded(map, true, Check);
                    }
                    Check(map.listerThings.AllThings.Count(t => t.def.defName.StartsWith("RMush_Plant")) >= 33, "plant growth fixtures survive save/load");
                    foreach (var t in map.listerThings.AllThings.Where(t => t.def.defName.StartsWith("RMush_Raw")))
                        Check(Texture(t) == (t.stackCount <= 25 ? "01Low" : t.stackCount <= 50 ? "02Medium" : "03Full"), "saved stack graphic " + t.def.defName + ":" + t.stackCount);
                    if (GenCommandLine.CommandLineArgPassed("mushroomLegacySave") && !legacyLoaded)
                    {
                        File.WriteAllText(Path.Combine(GenFilePaths.SaveDataFolderPath, "new-save-verified.flag"), "PASS");
                        phase = 101;
                        return;
                    }
                    File.AppendAllText(Report, "RESULT PASS" + Environment.NewLine);
                    Log.Message("[Rim Mushrooms Tests] RESULT PASS");
                    finished = true;
                    Application.Quit(0);
                    return;
                }
                if (phase == 101)
                {
                    phase = 102;
                    GameDataSaveLoader.LoadGame("MoreMushrooms-LegacyFixture");
                    return;
                }
                if (phase == 0) { Setup(map); phase = 1; if (GenCommandLine.CommandLineArgPassed("mushroomMoodOnly")) cropIndex = plants.Length; }
                if (phase == 10)
                {
                    farmer.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                    var members = AssortedTests.Selection.GetModExtension<AssortedMushroomSettings>().varieties;
                    var expected = assortedCycles < members.Count ? members[assortedCycles] : ThingDef.Named("RMush_PlantPurpleBlewit");
                    var job = AssortedTests.NativeSowJobForSpecies(farmer, workCell, expected);
                    if (job != null && job.def == JobDefOf.HaulToCell)
                    {
                        job.playerForced = true;
                        farmer.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                        deadline = Time.realtimeSinceStartup + 40f;
                        phase = 13;
                        return;
                    }
                    Check(job != null && job.def == JobDefOf.Sow && job.plantDefToSow != AssortedTests.Selection, "assorted actual sow job selects species cycle=" + assortedCycles
                        + " job=" + job + " selection=" + AssortedMushrooms.SelectionAt(workCell, map) + " allowSow=" + growingZone.allowSow
                        + " skill=" + farmer.skills.GetSkill(SkillDefOf.Plants).Level + " temperature=" + workCell.GetTemperature(map)
                        + " things=" + string.Join(",", workCell.GetThingList(map).Select(t => t.def.defName)));
                    Check(job.plantDefToSow == expected, "native assorted ground selection exercises " + expected.defName);
                    job.playerForced = true;
                    farmer.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    deadline = Time.realtimeSinceStartup + 40f;
                    phase = 11;
                    return;
                }
                if (phase == 11)
                {
                    if (Time.realtimeSinceStartup > deadline) throw new Exception("Assorted sow timeout: " + farmer.CurJob);
                    currentPlant = workCell.GetPlant(map);
                    if (currentPlant == null || currentPlant.Growth <= 0f || farmer.CurJobDef == JobDefOf.Sow) return;
                    Check(AssortedTests.Selection.GetModExtension<AssortedMushroomSettings>().varieties.Contains(currentPlant.def) && currentPlant.sown, "pawn actually planted assorted member " + currentPlant.def.defName);
                    Check(AssortedTests.Sower.JobOnCell(farmer, workCell) == null, "actual assorted planting is kept until harvest");
                    if (++assortedCycles > AssortedTests.Selection.GetModExtension<AssortedMushroomSettings>().varieties.Count)
                    {
                        currentPlant.Growth = 0.37f;
                        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                        farmer.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                        hydroTests = new HydroponicsTests(map, farmer, Check);
                        phase = 20;
                        return;
                    }
                    currentPlant.Growth = 1f;
                    Check(AssortedTests.Harvester.HasJobOnCell(farmer, workCell), "automatic worker recognizes mature assorted crop");
                    baseline = Count(map, currentPlant.def.plant.harvestedThingDef);
                    originalHarvestFailable = currentPlant.def.plant.harvestFailable;
                    currentPlant.def.plant.harvestFailable = false;
                    var harvest = AssortedTests.Harvester.JobOnCell(farmer, workCell);
                    harvest.playerForced = true;
                    farmer.jobs.TryTakeOrderedJob(harvest, JobTag.Misc);
                    deadline = Time.realtimeSinceStartup + 40f;
                    phase = 12;
                    return;
                }
                if (phase == 12)
                {
                    if (Time.realtimeSinceStartup > deadline) throw new Exception("Assorted harvest timeout: " + farmer.CurJob);
                    if (!currentPlant.Destroyed) return;
                    currentPlant.def.plant.harvestFailable = originalHarvestFailable;
                    Check(Count(map, currentPlant.def.plant.harvestedThingDef) > baseline, "actual assorted harvest yields chosen species " + currentPlant.def.plant.harvestedThingDef.defName);
                    phase = 10;
                    return;
                }
                if (phase == 13)
                {
                    if (Time.realtimeSinceStartup > deadline) throw new Exception("Assorted haul-aside timeout: " + farmer.CurJob);
                    if (farmer.CurJobDef == JobDefOf.HaulToCell) return;
                    Check(true, "actual automatic hauling clears harvested pile for assorted replanting");
                    phase = 10;
                    return;
                }
                if (phase == 20)
                {
                    if (!hydroTests.Update()) return;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    farmer.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                    // The ground fixture grew while the hydroponics jobs ran.
                    // Restore its documented value before save/load assertions.
                    workCell.GetPlant(map).Growth = 0.37f;
                    PoisonTests.Run(farmer, map, Check);
                    PsychoactiveTests.Run(farmer, map, Check);
                    ExpansionTests.Run(farmer, map, Check);
                    MealExposureTests.Run(farmer, map, Check);
                    NutrientPasteTests.Run(farmer, map, Check);
                    MoodTests.Run(farmer, Check);
                    phase = 4; frames = 0;
                    return;
                }
                if (phase == 1) { StartCrop(map); return; }
                if (phase == 2)
                {
                    if (Time.realtimeSinceStartup > deadline) throw new Exception("Sow timeout: " + plants[cropIndex].defName + " job=" + farmer.CurJob + " ticks=" + Find.TickManager.TicksGame + " paused=" + Find.TickManager.Paused + " growth=" + workCell.GetPlant(map)?.Growth);
                    currentPlant = workCell.GetPlant(map);
                    if (currentPlant == null || currentPlant.Growth <= 0 || farmer.CurJobDef == JobDefOf.Sow) return;
                    Check(currentPlant.def == plants[cropIndex] && currentPlant.sown, "actual pawn sow job " + currentPlant.def.defName);
                    float oldGrowth = currentPlant.Growth;
                    // Set local time to midday; natural growth is checked through the real plant ticker.
                    if (cropIndex == 0)
                    {
                        int deltaToNoon = (int)((0.5f - GenLocalDate.DayPercent(currentPlant) + 1f) % 1f * 60000f);
                        Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + deltaToNoon);
                    }
                    currentPlant.TickLong();
                    Check(currentPlant.Growth > oldGrowth, "actual growth tick " + currentPlant.def.defName);
                    StartHarvest(map);
                    return;
                }
                if (phase == 3)
                {
                    if (Time.realtimeSinceStartup > deadline) throw new Exception("Harvest timeout: " + plants[cropIndex].defName + " job=" + farmer.CurJob + " downed=" + farmer.Downed + " mental=" + farmer.InMentalState + " paused=" + Find.TickManager.Paused);
                    if (!currentPlant.Destroyed && farmer.CurJobDef != JobDefOf.Harvest && !farmer.InMentalState)
                    {
                        var retryJob = JobMaker.MakeJob(JobDefOf.Harvest, currentPlant);
                        retryJob.playerForced = true;
                        farmer.jobs.TryTakeOrderedJob(retryJob, JobTag.Misc);
                    }
                    if (!currentPlant.Destroyed) return;
                    plants[cropIndex].plant.harvestFailable = originalHarvestFailable;
                    var raw = plants[cropIndex].plant.harvestedThingDef;
                    Check(Count(map, raw) > baseline, "actual pawn harvest job produces " + raw.defName + " before=" + baseline + " after=" + Count(map, raw) + " harvestStat=" + farmer.GetStatValue(StatDefOf.PlantHarvestYield));
                    cropIndex++;
                    phase = 1;
                    return;
                }
                if (phase == 4 && ++frames > 30)
                {
                    phase = 5; frames = 0;
                }
                else if (phase == 5 && ++frames > 30)
                {
                    EcologyTests.PrepareSave(map, Check);
                    GameDataSaveLoader.SaveGame("RimMushrooms-SmokeFixture");
                    phase = 6;
                }
                else if (phase == 6)
                {
                    phase = 7;
                    GameDataSaveLoader.LoadGame("RimMushrooms-SmokeFixture");
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private void Fail(Exception e)
        {
            string diagnostic = WorkerDiagnostic(Find.CurrentMap);
            File.AppendAllText(Report, "DIAGNOSTIC failure " + diagnostic + Environment.NewLine);
            Log.Message("[Rim Mushrooms Tests] Failure diagnostic: " + diagnostic);
            File.AppendAllText(Report, "RESULT FAIL " + e + Environment.NewLine);
            Log.Error("[Rim Mushrooms Tests] " + e);
            finished = true;
            Application.Quit(1);
        }

        private string WorkerDiagnostic(Map map)
        {
            try
            {
                if (farmer == null) return "phase=" + phase + " worker=null";
                string conditions = farmer.health?.hediffSet?.hediffs == null ? "unavailable"
                    : string.Join(";", farmer.health.hediffSet.hediffs.Select(h => h.def.defName + ":" + h.Severity
                        + ":part=" + (h.Part?.def?.defName ?? "whole") + ":source=" + (h.sourceDef?.defName ?? "none")
                        + ":sourceLabel=" + (h.sourceLabel ?? "none")));
                string nearby = map == null ? "map=null" : string.Join(";", map.mapPawns.AllPawnsSpawned
                    .Where(p => p != farmer).OrderBy(p => p.Position.DistanceTo(farmer.PositionHeld)).Take(30)
                    .Select(p => p.ThingID + ":" + p.def.defName + ":pos=" + p.Position + ":job=" + p.CurJob));
                return "phase=" + phase + " crop=" + cropIndex + " assorted=" + assortedCycles
                    + " tick=" + Find.TickManager.TicksGame + " worker=" + farmer.ThingID
                    + " dead=" + farmer.Dead + " downed=" + farmer.Downed + " destroyed=" + farmer.Destroyed
                    + " spawned=" + farmer.Spawned + " position=" + farmer.PositionHeld + " corpse=" + farmer.Corpse
                    + " needs=" + (farmer.needs != null) + " food=" + (farmer.needs?.food != null)
                    + " rest=" + (farmer.needs?.rest != null) + " body=" + farmer.RaceProps.body?.defName
                    + " job=" + farmer.CurJob + " conditions=[" + conditions + "] nearby=[" + nearby + "]";
            }
            catch (Exception diagnosticError)
            {
                return "phase=" + phase + " diagnostic unavailable: " + diagnosticError.GetType().Name + ": " + diagnosticError.Message;
            }
        }

        private void Setup(Map map)
        {
            File.WriteAllText(Report, "Rim Mushrooms runtime test\n" + VersionControl.CurrentVersionStringWithRev + "\n");
            string expectedLanguage;
            if (GenCommandLine.TryGetCommandLineArg("mushroomLanguage", out expectedLanguage))
            {
                Check(LanguageDatabase.activeLanguage.folderName.StartsWith(expectedLanguage), "active language " + LanguageDatabase.activeLanguage.folderName);
                Check(DefDatabase<ThingDef>.GetNamed("RMush_RawButton").label == (expectedLanguage == "Korean" ? "양송이버섯" : "button mushroom"), "translated button label");
            }
            foreach (var window in Find.WindowStack.Windows.ToList()) window.Close(false);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            // Quicktest chooses a random world; these are crop fixtures at 21C.
            map.Biome.constantOutdoorTemperature = 21f;
            plants = DefDatabase<ThingDef>.AllDefs.Where(d => d.defName.StartsWith("RMush_Plant") && d.plant?.harvestedThingDef != null
                && (LegacyRaw(d.plant.harvestedThingDef) || NewCultivated(d.plant.harvestedThingDef))).OrderBy(d => d.defName).ToArray();
            var items = DefDatabase<ThingDef>.AllDefs.Where(LegacyRaw).OrderBy(d => d.defName).ToArray();
            Check(plants.Length == 13 && items.Length == 10, "legacy eleven plus two new cultivable plants and ten legacy ingredients loaded");
            Check(plants.Count(d => d.plant.Sowable) == 11, "exactly eleven cultivable varieties");
            Check(!DefDatabase<ThingDef>.GetNamed("RMush_PlantMatsutake").plant.Sowable, "matsutake wild only");
            foreach (var d in plants.Concat(items)) Check(!d.ConfigErrors().Any(), "resolved def config " + d.defName);
            farmer = map.mapPawns.FreeColonistsSpawned.First(p => !p.Downed && p.DevelopmentalStage == DevelopmentalStage.Adult
                && !p.WorkTypeIsDisabled(WorkTypeDefOf.Growing));
            File.AppendAllText(Report, "DIAGNOSTIC initial worker " + WorkerDiagnostic(map) + Environment.NewLine);
            // Prepare a healthy adult before any test cases, as the medical suites
            // do for their patients. Never heal or revive this worker during jobs.
            foreach (var condition in farmer.health.hediffSet.hediffs.ToList()) farmer.health.RemoveHediff(condition);
            var ambientWildlife = map.mapPawns.AllPawnsSpawned
                .Where(p => p.Faction != Faction.OfPlayer && (p.RaceProps.Animal || p.HostileTo(Faction.OfPlayer))).ToList();
            File.AppendAllText(Report, "DIAGNOSTIC initial ambient wildlife isolated: "
                + string.Join(";", ambientWildlife.Select(p => p.ThingID + ":" + p.def.defName + ":job=" + p.CurJob)) + Environment.NewLine);
            // Native predators keep hunting with the storyteller disabled. These
            // crop/health fixtures do not exercise combat or ambient predation.
            foreach (var pawn in ambientWildlife) pawn.Destroy(DestroyMode.Vanish);
            Check(!farmer.Dead && !farmer.Downed && farmer.health.hediffSet.hediffs.Count == 0
                && farmer.needs?.food != null && farmer.needs?.rest != null,
                "healthy adult crop worker prepared before tests; initial ambient wildlife isolated=" + ambientWildlife.Count);
            // Keep ordered fixture jobs from competing with automatic farming.
            // Automatic sow/harvest scans are checked separately in the suites.
            farmer.workSettings.SetPriority(WorkTypeDefOf.Growing, 0);
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                pawn.needs.food.CurLevelPercentage = 1f;
                pawn.needs.rest.CurLevelPercentage = 1f;
                if (pawn != farmer) pawn.drafter.Drafted = true;
            }
            farmer.skills.GetSkill(SkillDefOf.Plants).Level = 20;
            farmer.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            workCell = map.Center + new IntVec3(18, 0, 0);
            // GenSpawn permits a plant to overlap a natural rock. Clear through
            // the lowest actual artwork row, so native compressed rocks cannot
            // correctly wipe an invalid fixture overlap when the save is loaded.
            int lowestArtworkRow = Math.Min(-17, 14 - (plants.Length - 1) * 3) - 1;
            var fixtureRect = new CellRect(map.Center.x - 23, map.Center.z + lowestArtworkRow, 47, 18 - lowestArtworkRow);
            Check(fixtureRect.Cells.All(c => c.InBounds(map)), "crop/art fixture clearing covers every initial growth row");
            foreach (var cell in fixtureRect)
            {
                foreach (var thing in cell.GetThingList(map).ToList())
                    if (!(thing is Pawn)) thing.Destroy(DestroyMode.Vanish);
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
                map.roofGrid.SetRoof(cell, null);
                map.snowGrid.SetDepth(cell, 0f);
            }
            // Fresh quicktest maps can still cache freezing room temperatures
            // after the biome target is set. Establish the declared warm fixture
            // in the actual native rooms intersecting only this cleared patch.
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            float previousWorkTemperature = workCell.GetTemperature(map);
            var fixtureRooms = fixtureRect.Cells.Select(c => c.GetRoom(map)).Where(r => r != null).Distinct().ToArray();
            foreach (var room in fixtureRooms) room.Temperature = 21f;
            Check(fixtureRooms.Length > 0 && fixtureRect.Cells.All(c => Math.Abs(c.GetTemperature(map) - 21f) < 0.001f),
                "crop/art fixture native room temperatures are 21C; rooms=" + fixtureRooms.Length
                + " previousWorkCell=" + previousWorkTemperature + " actualWorkCell=" + workCell.GetTemperature(map));
            map.fogGrid.ClearAllFog();
            farmer.Position = workCell + IntVec3.West;
            farmer.Notify_Teleported();
            growingZone = new Zone_Growing(map.zoneManager);
            map.zoneManager.RegisterZone(growingZone);
            growingZone.AddCell(workCell);
            int row = 0;
            foreach (var d in plants)
            {
                Check(d.thingClass == typeof(Plant_Mushroom) && !d.plant.diesToLight && !d.plant.dieIfNoSunlight, "shade-aware plant without light damage " + d.defName);
                Check(d.plant.maxMeshCount == 9 && d.plant.visualSizeRange.min == 0.3f && d.plant.visualSizeRange.max == 0.65f, "native growth rendering " + d.defName);
                if (d.defName != "RMush_PlantEnoki")
                    Check(DefDatabase<BiomeDef>.AllDefs.Any(b => b.AllWildPlants.Contains(d)), "wild biome registration " + d.defName);
                else Check(!DefDatabase<BiomeDef>.AllDefs.Any(b => b.AllWildPlants.Contains(d)), "white enoki excluded from wild spawn");
                for (int g = 0; g < 3; g++)
                {
                    var p = (Plant)GenSpawn.Spawn(d, map.Center + new IntVec3(-12 + g * 3, 0, 14 - row * 3), map);
                    p.Growth = new[] {0.1f,0.5f,1f}[g];
                    Check(p.Graphic != BaseContent.BadGraphic, "plant graphic " + d.defName + " " + g);
                    if (g == 2) Check(p.YieldNow() > 0, "mature yield " + d.defName);
                }
                row++;
            }
            row = 0;
            foreach (var d in items)
            {
                Check(d.ingestible.foodType == FoodTypeFlags.Fungus && d.thingCategories.Contains(ThingCategoryDefOf.PlantFoodRaw), "cooking/fungus category " + d.defName);
                Check(DefDatabase<RecipeDef>.GetNamed("CookMealSimple").ingredients.Any(i => i.filter.Allows(d)), "simple meal ingredient accepted " + d.defName);
                var t = ThingMaker.MakeThing(d);
                Check(t.TryGetComp<CompRottable>() != null, "rottable ingredient " + d.defName);
                foreach (int n in new[] {1,25,26,50,51,75})
                {
                    t.stackCount = n;
                    Check(Texture(t) == (n <= 25 ? "01Low" : n <= 50 ? "02Medium" : "03Full"), "stack boundary " + d.defName + ":" + n);
                }
                GenSpawn.Spawn(t, map.Center + new IntVec3(2, 0, 14 - row * 3), map);
                var split = t.SplitOff(25);
                Check(t.stackCount == 50 && Texture(t) == "02Medium" && Texture(split) == "01Low", "split 75 into 50+25 " + d.defName);
                Check(t.TryAbsorbStack(split, true) && Texture(t) == "03Full", "merge 50+25 " + d.defName);
                Check(farmer.carryTracker.TryStartCarry(t, 25, reserve: false) == 25, "carry split " + d.defName);
                Check(Texture(farmer.carryTracker.CarriedThing) == "01Low", "carried material " + d.defName);
                Thing dropped;
                Check(farmer.carryTracker.TryDropCarriedThing(t.Position, ThingPlaceMode.Direct, out dropped), "drop and merge " + d.defName);
                Check(t.stackCount == 75 && Texture(t) == "03Full", "dropped full material " + d.defName);
                t.SetForbidden(true, false);
                for (int g = 0; g < 2; g++)
                {
                    var sample = ThingMaker.MakeThing(d); sample.stackCount = g == 0 ? 25 : 50;
                    GenSpawn.Spawn(sample, map.Center + new IntVec3(5 + g * 3, 0, 14 - row * 3), map);
                    sample.SetForbidden(true, false);
                }
                row++;
            }
            Find.CameraDriver.JumpToCurrentMapLoc(map.Center);
            Find.CameraDriver.SetRootSize(19f);
            LightTests.Run(map, workCell, Check);
            AssortedTests.Run(map, farmer, growingZone, workCell, Check);
            EcologyTests.Verify(map, farmer, Check);
            Check(true, "fixture created; live sow/harvest jobs starting");
        }

        private void StartCrop(Map map)
        {
            if (cropIndex >= plants.Length)
            {
                growingZone.SetPlantDefToGrow(AssortedTests.Selection);
                growingZone.allowCut = false;
                farmer.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                foreach (var t in workCell.GetThingList(map).ToList()) if (!(t is Pawn)) t.Destroy(DestroyMode.Vanish);
                phase = 10;
                return;
            }
            var d = plants[cropIndex];
            foreach (var t in workCell.GetThingList(map).ToList()) if (!(t is Pawn)) t.Destroy(DestroyMode.Vanish);
            farmer.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            farmer.pather.StopDead();
            farmer.Position = workCell + IntVec3.West;
            farmer.Notify_Teleported();
            baseline = Count(map, d.plant.harvestedThingDef);
            deadline = Time.realtimeSinceStartup + 40f;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
            if (d.plant.Sowable)
            {
                growingZone.SetPlantDefToGrow(d);
                Check(d.CanNowPlantAt(workCell, map), "sow cell accepted " + d.defName + " terrain=" + workCell.GetTerrain(map).defName + " fertility=" + map.fertilityGrid.FertilityAt(workCell));
                var job = JobMaker.MakeJob(JobDefOf.Sow, workCell);
                job.plantDefToSow = d;
                job.playerForced = true;
                farmer.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                phase = 2;
            }
            else
            {
                currentPlant = (Plant)GenSpawn.Spawn(d, workCell, map);
                StartHarvest(map);
            }
        }

        private void StartHarvest(Map map)
        {
            currentPlant.Growth = 1f;
            // Exercise the successful vanilla harvest path deterministically. Production
            // definitions retain normal harvest failure; restore before moving on/saving.
            originalHarvestFailable = currentPlant.def.plant.harvestFailable;
            currentPlant.def.plant.harvestFailable = false;
            var job = JobMaker.MakeJob(JobDefOf.Harvest, currentPlant);
            job.playerForced = true;
            farmer.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            deadline = Time.realtimeSinceStartup + 40f;
            phase = 3;
        }

    }
}
