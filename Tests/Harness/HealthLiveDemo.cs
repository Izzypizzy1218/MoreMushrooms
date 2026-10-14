using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RimMushrooms;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMushroomsTests
{
    // Dedicated isolated live demo: every elapsed tick is an ordinary game tick.
    // Never included in the production mod, and no clinical time/health shortcuts.
    public sealed class HealthLiveDemo : GameComponent
    {
        private static readonly string[] Species = { "FlyAgaric", "DestroyingAngel", "SulfurTuft", "Tsukiyotake",
            "PoisonFireCoral", "DeathCap", "YellowDapperling", "JackOLantern", "DeadlyWebcap", "PantherCap", "GhostFungus" };
        private static readonly string[] Names = { "광대", "독우산", "노란다발", "화경", "붉은사슴뿔", "알광대",
            "노란각시", "잭오랜턴", "녹슨끈적", "마귀광대", "발광" };
        private const string SaveName = "MoreMushrooms-LivePoison-11";
        private List<Pawn> subjects = new List<Pawn>();
        private readonly List<Thing> doses = new List<Thing>();
        private Map map;
        private bool initialized, ready, failed, profileAccepted, screenshotRequested, screenshotDone;
        private int startTick, nextSampleTick, captureFrames;
        private float captureAt, captionUntil;
        private readonly StringBuilder report = new StringBuilder();
        private bool Enabled => GenCommandLine.CommandLineArgPassed("mushroomHealthLive");
        private string Output => Path.Combine(GenFilePaths.SaveDataFolderPath, "HealthLiveDemo");
        private string CsvPath => Path.Combine(Output, "live-status.csv");
        private int Now => Find.TickManager.TicksGame;

        public HealthLiveDemo(Game game) { }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref initialized, "mushroomHealthLiveInitialized", false);
            Scribe_Values.Look(ref ready, "mushroomHealthLiveReady", false);
            Scribe_Values.Look(ref startTick, "mushroomHealthLiveStartTick", 0);
            Scribe_Values.Look(ref nextSampleTick, "mushroomHealthLiveNextSampleTick", 0);
            Scribe_Collections.Look(ref subjects, "mushroomHealthLiveSubjects", LookMode.Reference);
            Scribe_References.Look(ref map, "mushroomHealthLiveMap");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (subjects == null) subjects = new List<Pawn>();
                // A loaded ready save is observation only; never regenerate,
                // feed again, change speed, or steal the user's selection.
                screenshotDone = ready;
            }
        }

        public override void GameComponentUpdate()
        {
            if (!Enabled || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting) return;
            if (File.Exists(Path.Combine(Output, "close.flag"))) { Application.Quit(0); return; }
            if (failed) return;
            try
            {
                if (!initialized) { Setup(); return; }
                if (!ready) { PollIngestion(); return; }
                // A single native frame after release; no repeated selection,
                // window closing, pause, or speed control after ready.
                if (!screenshotDone && ++captureFrames >= 40 && Time.realtimeSinceStartup >= captureAt)
                {
                    string path = Path.Combine(Output, "live-ready.png");
                    if (!screenshotRequested)
                    {
                        ScreenCapture.CaptureScreenshot(path);
                        screenshotRequested = true;
                        captureAt = Time.realtimeSinceStartup + 2.5f;
                    }
                    else if (File.Exists(path) && new FileInfo(path).Length > 1024)
                    {
                        screenshotDone = true;
                        File.AppendAllText(Path.Combine(Output, "live-demo-report.txt"),
                            "NATIVE_FRAME live-ready.png;screen=" + UI.screenWidth + "x" + UI.screenHeight + "\n");
                    }
                }
            }
            catch (Exception error)
            {
                failed = true;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                if (profileAccepted)
                {
                    Directory.CreateDirectory(Output);
                    File.WriteAllText(Path.Combine(Output, "failed.flag"), error.ToString());
                    File.AppendAllText(Path.Combine(Output, "live-demo-report.txt"), "RESULT FAIL\n" + error + "\n");
                }
                Log.Error("[More Mushrooms live poison demonstration] " + error);
            }
        }

        private void Setup()
        {
            string profile = Path.GetFullPath(GenFilePaths.SaveDataFolderPath);
            Require(profile.IndexOf("HealthLiveDemo", StringComparison.OrdinalIgnoreCase) >= 0,
                "An explicitly isolated HealthLiveDemo save-data profile is required.");
            profileAccepted = true;
            Directory.CreateDirectory(Output);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            DebugSettings.enableStoryteller = false;
            Find.Storyteller.incidentQueue.Clear();
            foreach (var window in Find.WindowStack.Windows.ToList()) window.Close(false);
            map = Find.CurrentMap;
            Require(map != null, "No current map.");
            map.Biome.constantOutdoorTemperature = 21f;
            map.weatherManager.TransitionTo(WeatherDefOf.Clear);
            map.weatherManager.curWeatherAge = 100000;
            foreach (var pawn in map.listerThings.AllThings.OfType<Pawn>().ToList())
                if (pawn.Spawned) pawn.DeSpawn();
            IntVec3 center = map.Center;
            CellRect clear = CellRect.CenteredOn(center, 29, 29).ClipInsideMap(map);
            foreach (IntVec3 cell in clear)
            {
                foreach (var thing in cell.GetThingList(map).ToList())
                    if (thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);
                    else if (thing.Spawned) thing.DeSpawn();
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.PavedTile);
                map.roofGrid.SetRoof(cell, null);
                map.snowGrid.SetDepth(cell, 0f);
                map.fogGrid.Unfog(cell);
            }
            foreach (var pawn in map.listerThings.AllThings.OfType<Pawn>().ToList())
                if (pawn.Spawned) pawn.DeSpawn();
            Require(clear.Cells.All(c => c.GetThingList(map).Count == 0), "Clinic clearing failed.");
            // CenteredOn takes width/height, not radii. Interior is 23x23.
            CellRect clinic = CellRect.CenteredOn(center, 25, 25);
            foreach (IntVec3 cell in clinic)
            {
                bool edge = cell.x == clinic.minX || cell.x == clinic.maxX || cell.z == clinic.minZ || cell.z == clinic.maxZ;
                if (edge) SpawnFurniture(ThingDefOf.Wall, cell, ThingDefOf.BlocksGranite);
                else map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
            }
            // A closed safe clinic confines wandering naturally; no AI or
            // mental-state suppression. Native columns support every roof cell.
            foreach (int x in new[] { -6, 0, 6 })
                foreach (int z in new[] { -6, 0, 6 })
                    SpawnFurniture(ThingDefOf.Column, center + new IntVec3(x, 0, z), ThingDefOf.BlocksGranite);
            Require(clinic.Cells.Where(c => map.roofGrid.Roofed(c)).All(c => RoofCollapseUtility.WithinRangeOfRoofHolder(c, map)),
                "A clinic roof cell lacks native structural support.");
            var beds = new List<Building_Bed>();
            for (int i = 0; i < Species.Length; i++)
            {
                IntVec3 pos = center + new IntVec3(-8 + (i % 4) * 5, 0, 7 - (i / 4) * 5);
                var bed = (Building_Bed)SpawnFurniture(ThingDefOf.Bed, pos, ThingDefOf.WoodLog);
                beds.Add(bed);
            }
            SpawnFurniture(ThingDefOf.Table2x2c, center + new IntVec3(-8, 0, -8), ThingDefOf.WoodLog);
            foreach (var offset in new[] { new IntVec3(-9, 0, -8), new IntVec3(-6, 0, -8),
                new IntVec3(-8, 0, -9), new IntVec3(-8, 0, -6) })
                SpawnFurniture(ThingDefOf.DiningChair, center + offset, ThingDefOf.WoodLog);
            var horseshoes = DefDatabase<ThingDef>.GetNamedSilentFail("HorseshoesPin");
            if (horseshoes != null) SpawnFurniture(horseshoes, center + new IntVec3(8, 0, -5), ThingDefOf.Steel);
            foreach (var offset in new[] { new IntVec3(-10, 0, 0), new IntVec3(10, 0, 0), new IntVec3(0, 0, 10) })
            {
                var lamp = SpawnFurniture(ThingDef.Named("TorchLamp"), center + offset, null);
                var fuel = lamp.TryGetComp<CompRefuelable>();
                if (fuel != null) fuel.Refuel(fuel.Props.fuelCapacity);
            }
            for (int i = 0; i < 32; i++)
            {
                var meals = ThingMaker.MakeThing(ThingDefOf.MealSurvivalPack);
                meals.stackCount = 10;
                GenSpawn.Spawn(meals, center + new IntVec3(2 + i % 8, 0, -10 + i / 8), map);
            }
            for (int i = 0; i < 3; i++)
            {
                var medicine = ThingMaker.MakeThing(ThingDefOf.MedicineIndustrial);
                medicine.stackCount = 25;
                GenSpawn.Spawn(medicine, center + new IntVec3(-10 + i, 0, -5), map);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            foreach (var room in map.regionGrid.AllRooms) room.Temperature = 21f;
            Area_Allowed allowed;
            Require(map.areaManager.TryMakeNewAllowed(out allowed), "Could not create clinic allowed area.");
            allowed.SetLabel("독버섯 실시간 관찰 병실");
            foreach (var cell in clinic.ContractedBy(1)) allowed[cell] = true;
            for (int i = 0; i < Species.Length; i++)
            {
                Pawn pawn = HealthyPawn();
                pawn.Name = new NameTriple("실험", Names[i], "");
                GenSpawn.Spawn(pawn, beds[i].Position + IntVec3.South, map);
                pawn.playerSettings.AreaRestrictionInPawnCurrentMap = allowed;
                pawn.playerSettings.selfTend = false;
                pawn.playerSettings.medCare = MedicalCareCategory.Best;
                Require(pawn.ownership.ClaimBedIfNonMedical(beds[i]) && pawn.ownership.OwnedBed == beds[i],
                    "Native personal bed ownership failed for " + Species[i]);
                pawn.foodRestriction.CurrentFoodPolicy.filter.SetAllow(ThingDefOf.MealSurvivalPack, true);
                var source = ThingMaker.MakeThing(ThingDef.Named("RMush_Raw" + Species[i]));
                source.stackCount = i == 0 || i == 9 ? 10 : 1;
                GenSpawn.Spawn(source, pawn.Position + IntVec3.East, map);
                pawn.foodRestriction.CurrentFoodPolicy.filter.SetAllow(source.def, true);
                subjects.Add(pawn); doses.Add(source);
            }
            Require(map.listerThings.AllThings.OfType<Pawn>().Count() == 11, "Expected exactly eleven spawned subjects and no extra doctor.");
            report.AppendLine("More Mushrooms 11-species LIVE native-game clinical observation.");
            report.AppendLine("METHOD: actual native Ingest jobs; one ordinary mushroom per subject, ten FlyAgaric/PantherCap mushrooms = one standard exposure including their unchanged hallucination.");
            report.AppendLine("All subsequent time, health, needs, mental states, vomiting, recovery and death use ordinary game ticks. No clock jumps, manual health ticks, severity assignment, healing, cure or post-ingestion pawn normalization.");
            report.AppendLine("CONTROL: isolated 21C clinic, initial clear weather, storyteller disabled, queue cleared; no ambient pawns. Eleven prepared healthy clothed adult human subjects, eleven owned ordinary beds (native tending is possible in bed), table/chairs, 320 survival meals and industrial medicine.");
            report.AppendLine("Automatic treatment OFF: Doctor priority=0 and selfTend=false for all eleven. The user can enable doctor work or order treatment. Untreated lethal poisoning can cause death.");
            report.AppendLine("After readiness: initial speed is the third speed button (TimeSpeed.Superfast; no claim of fixed TPS multiplier); user selection, speed and windows remain entirely under user control. Short header hides automatically. Save=" + SaveName);
            report.AppendLine("Profile=" + profile + ";version=" + VersionControl.CurrentVersionStringWithRev);
            startTick = Now;
            for (int i = 0; i < subjects.Count; i++)
            {
                var job = JobMaker.MakeJob(JobDefOf.Ingest, doses[i]);
                job.count = doses[i].stackCount; job.ingestTotalCount = true; job.playerForced = true;
                int jobId = job.loadID;
                subjects[i].jobs.TryTakeOrderedJob(job, JobTag.Misc);
                Require(subjects[i].CurJob != null && subjects[i].CurJob.loadID == jobId,
                    "Actual Ingest job not accepted for " + Species[i]);
                report.AppendLine("INGEST_STARTED species=" + Species[i] + ";pawn=" + subjects[i].ThingID
                    + ";nickname=" + Names[i] + ";items=" + doses[i].stackCount + ";jobID=" + jobId
                    + ";cell=" + subjects[i].Position);
            }
            File.WriteAllText(Path.Combine(Output, "live-demo-report.txt"), report.ToString(), new UTF8Encoding(false));
            File.WriteAllText(CsvPath, "tick,hoursSincePreparation,species,nickname,pawnId,dead,spawned,poisonActive,severity,phase,onsetTick,exposureUnits,tendQuality,consciousness,moving,pain,currentJob,allHediffs\n", new UTF8Encoding(false));
            initialized = true;
            map.mapDrawer.RegenerateEverythingNow();
            Find.CameraDriver.JumpToCurrentMapLoc(center + new IntVec3(-7, 0, 0));
            Find.CameraDriver.SetRootSize(19f);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
        }

        private Thing SpawnFurniture(ThingDef def, IntVec3 position, ThingDef stuff)
        {
            var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? stuff : null);
            thing.SetFaction(Faction.OfPlayer);
            thing.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Colony);
            return GenSpawn.Spawn(thing, position, map, Rot4.North);
        }

        private Pawn HealthyPawn()
        {
            var request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                forceGenerateNewPawn: true, allowDead: false, allowDowned: false,
                canGeneratePawnRelations: false, allowAddictions: false, allowFood: false,
                fixedBiologicalAge: 28f, fixedChronologicalAge: 28f, forceNoIdeo: true,
                forceNoBackstory: true, forbidAnyTitle: true, forceBaselinerChance: 1f,
                maximumAgeTraits: 0, forceNoGear: true);
            Pawn pawn = PawnGenerator.GeneratePawn(request);
            foreach (var condition in pawn.health.hediffSet.hediffs.ToList()) pawn.health.RemoveHediff(condition);
            foreach (var trait in pawn.story.traits.allTraits.ToList()) pawn.story.traits.RemoveTrait(trait);
            foreach (var need in pawn.needs.AllNeeds) need.CurLevelPercentage = 1f;
            pawn.needs.food.CurLevelPercentage = .50f;
            pawn.inventory.innerContainer.ClearAndDestroyContents();
            foreach (string apparelId in new[] { "Apparel_BasicShirt", "Apparel_Pants" })
            {
                var apparel = (Apparel)ThingMaker.MakeThing(ThingDef.Named(apparelId), ThingDefOf.Cloth);
                apparel.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Colony);
                pawn.apparel.Wear(apparel, false);
            }
            pawn.workSettings.EnableAndInitializeIfNotAlreadyInitialized();
            foreach (var work in DefDatabase<WorkTypeDef>.AllDefs) pawn.workSettings.SetPriority(work, 0);
            foreach (var skill in pawn.skills.skills) skill.Level = 8;
            for (int hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
            Require(!pawn.Dead && !pawn.Downed && pawn.health.hediffSet.hediffs.Count == 0, "Initial subject not healthy.");
            return pawn;
        }

        private void PollIngestion()
        {
            Require(Now - startTick < 15000, "Native eleven-subject ingestion timed out.");
            for (int i = 0; i < subjects.Count; i++)
            {
                var poison = subjects[i].health.hediffSet.GetFirstHediffOfDef(HediffDef.Named("RMush_Poison" + Species[i])) as Hediff_MushroomPoisoning;
                if (poison == null || !doses[i].Destroyed) return;
                Require(Math.Abs(poison.ExposureUnits - 1f) < .0001f, "Exposure must equal one standard unit for " + Species[i]);
                Require(!subjects[i].Dead, "Subject died before ingestion readiness.");
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            foreach (int i in Enumerable.Range(0, subjects.Count))
            {
                var poison = subjects[i].health.hediffSet.GetFirstHediffOfDef(HediffDef.Named("RMush_Poison" + Species[i])) as Hediff_MushroomPoisoning;
                report.AppendLine("INGEST_CONFIRMED species=" + Species[i] + ";pawn=" + subjects[i].ThingID
                    + ";sourceConsumed=True;exposure=" + F(poison.ExposureUnits) + ";onsetTick=" + poison.OnsetTick
                    + ";currentMentalState=" + subjects[i].MentalStateDef?.defName);
            }
            Find.Selector.ClearSelection();
            Find.Selector.Select(subjects[1], playSound: false, forceDesignatorDeselect: false);
            Require(InspectPaneUtility.OpenTab(typeof(ITab_Pawn_Health)) != null, "Native patient Health tab unavailable.");
            ready = true;
            nextSampleTick = Now + 2500;
            Sample();
            // Native save is synchronous here, and stores ready+subject refs so a
            // reload continues observation rather than feeding a second cohort.
            GameDataSaveLoader.SaveGame(SaveName);
            Require(File.Exists(GenFilePaths.FilePathForSavedGame(SaveName))
                && new FileInfo(GenFilePaths.FilePathForSavedGame(SaveName)).Length > 1024, "Native isolated save was not created.");
            report.AppendLine("READY nativeTick=" + Now + ";spawnedSubjects=11;nativeIngestConfirmed=11;save=" + SaveName
                + ";automaticTreatment=False;normalGameTicksOnly=True");
            File.WriteAllText(Path.Combine(Output, "live-demo-report.txt"), report.ToString(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Output, "ready.flag"), "11 real Ingest jobs confirmed; observation released to user; native save=" + SaveName + "\n");
            captureAt = Time.realtimeSinceStartup + 2.5f;
            captionUntil = Time.realtimeSinceStartup + 22f;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
        }

        public override void GameComponentTick()
        {
            if (!Enabled || !ready || failed || Now < nextSampleTick) return;
            try { Sample(); nextSampleTick = Now + 2500; }
            catch (Exception error)
            {
                failed = true;
                File.WriteAllText(Path.Combine(Output, "failed.flag"), error.ToString());
                Log.Error("[More Mushrooms live status logging] " + error);
            }
        }

        private void Sample()
        {
            var csv = new StringBuilder();
            for (int i = 0; i < subjects.Count; i++)
            {
                Pawn pawn = subjects[i];
                if (pawn == null) continue;
                var condition = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDef.Named("RMush_Poison" + Species[i])) as Hediff_MushroomPoisoning;
                var tend = condition?.TryGetComp<HediffComp_TendDuration>();
                var fields = new[] { Now.ToString(), F((Now - startTick) / 2500f), Species[i], Names[i], pawn.ThingID,
                    pawn.Dead.ToString(), pawn.Spawned.ToString(), (condition != null).ToString(), F(condition?.Severity ?? 0f),
                    condition?.Phase.ToString() ?? "Resolved", (condition?.OnsetTick ?? -1).ToString(), F(condition?.ExposureUnits ?? 0f),
                    F(tend?.tendQuality ?? 0f), F(pawn.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness)),
                    F(pawn.health.capacities.GetLevel(PawnCapacityDefOf.Moving)), F(pawn.health.hediffSet.PainTotal),
                    pawn.CurJobDef?.defName ?? "None", string.Join("|", pawn.health.hediffSet.hediffs.Select(h => h.def.defName)) };
                csv.AppendLine(string.Join(",", fields.Select(Csv)));
            }
            Directory.CreateDirectory(Output);
            File.AppendAllText(CsvPath, csv.ToString(), new UTF8Encoding(false));
        }

        public override void GameComponentOnGUI()
        {
            if (!Enabled || !ready || Time.realtimeSinceStartup > captionUntil) return;
            var rect = new Rect(260f, 110f, Math.Min(1060f, UI.screenWidth - 280f), 48f);
            Widgets.DrawBoxSolid(rect, new Color(.06f, .07f, .08f, .92f));
            Widgets.Label(rect.ContractedBy(10f), "독버섯 11종 실시간 관찰 · 자동 진료 OFF · 각 정착민을 선택해 건강 확인 / 속도 직접 조절");
        }

        private static string F(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        private static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
