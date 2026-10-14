using System;
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
    // Isolated native visual demonstration, never shipped in the production mod.
    // Ingest and TendPatient run as real jobs. Between those jobs only native
    // health updates and the global clock advance; this is not a colony simulation.
    public sealed class HealthTimelineTests : GameComponent
    {
        private const int Hour = 2500;
        private Map map;
        private Pawn patient, doctor;
        private Building_Bed bed;
        private Thing food, medicine;
        private Hediff_MushroomPoisoning poison;
        private int state, shot, ingestTick, jobStartTick, tendCount, lastTendTick;
        private int jobId, medicineBefore, frames;
        private float reserveBefore, lastQuality, readyAt, deadline;
        private bool initialized, finished, capturePending, captureRequested, bedReturnAssigned;
        private string pending, stageTitle;
        private readonly StringBuilder report = new StringBuilder();
        private readonly StringBuilder csv = new StringBuilder();
        private string Output => Path.Combine(GenFilePaths.SaveDataFolderPath, "HealthTimeline");
        private bool Enabled => GenCommandLine.CommandLineArgPassed("mushroomHealthTimeline");
        private bool Hold => GenCommandLine.CommandLineArgPassed("healthDemoHold");
        private int Now => Find.TickManager.TicksGame;
        private bool Active => poison != null && patient.health.hediffSet.hediffs.Contains(poison);

        public HealthTimelineTests(Game game) { }

        public override void GameComponentUpdate()
        {
            if (!Enabled || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting) return;
            if (finished)
            {
                if (File.Exists(Path.Combine(Output, "close.flag"))) Application.Quit(0);
                return;
            }
            try
            {
                if (!initialized) { Setup(); return; }
                Require(!patient.Dead, "Patient died unexpectedly during the clinical demonstration.");
                // Only close intrusive windows while preparing these native frames.
                foreach (var window in Find.WindowStack.Windows.ToList()) window.Close(false);
                if (capturePending) { CaptureUpdate(); return; }
                if (state == 1) PollIngestion();
                else if (state == 2) PollBed();
                else if (state == 3) AdvanceNextStage();
                else if (state == 4) PollTend();
                else if (state == 5) PollReturnToBed();
            }
            catch (Exception error)
            {
                Directory.CreateDirectory(Output);
                report.AppendLine("RESULT FAIL\n" + error);
                WriteReports();
                Log.Error("[Mushroom health timeline] " + error);
                finished = true;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                Application.Quit(2);
            }
        }

        private void Setup()
        {
            string profile = Path.GetFullPath(GenFilePaths.SaveDataFolderPath);
            Require(profile.IndexOf("HealthTimeline", StringComparison.OrdinalIgnoreCase) >= 0,
                "Refusing to modify a profile whose path does not identify HealthTimeline: " + profile);
            Directory.CreateDirectory(Output);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            DebugSettings.enableStoryteller = false;
            map = Find.CurrentMap;
            Require(map != null, "No current map.");
            map.Biome.constantOutdoorTemperature = 21f;
            map.weatherManager.TransitionTo(WeatherDefOf.Clear);
            map.weatherManager.curWeatherAge = 100000;
            Find.Storyteller.incidentQueue.Clear();
            // listerThings includes dormant ancient pawns absent from mapPawns.
            foreach (var pawn in map.listerThings.AllThings.OfType<Pawn>().ToList())
                if (pawn.Spawned) pawn.DeSpawn();
            var center = map.Center;
            var area = CellRect.CenteredOn(center, 9, 8).ClipInsideMap(map);
            foreach (var cell in area)
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
            Require(area.Cells.All(c => c.GetThingList(map).Count == 0), "Clinic patch not empty after native clearing.");
            var room = CellRect.CenteredOn(center, 6, 5);
            foreach (var cell in room)
            {
                bool edge = cell.x == room.minX || cell.x == room.maxX || cell.z == room.minZ || cell.z == room.maxZ;
                if (edge)
                {
                    var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite);
                    wall.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(wall, cell, map);
                }
                else map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
            }
            bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
            bed.SetFaction(Faction.OfPlayer);
            bed.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Colony);
            GenSpawn.Spawn(bed, center + new IntVec3(-1, 0, 1), map, Rot4.North);
            bed.Medical = true;
            var lamp = ThingMaker.MakeThing(ThingDef.Named("TorchLamp"));
            lamp.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(lamp, center + new IntVec3(3, 0, 3), map);
            var fuel = lamp.TryGetComp<CompRefuelable>();
            if (fuel != null) fuel.Refuel(fuel.Props.fuelCapacity);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            // Outdoor room caches can retain the quick-test tile's old freezing
            // temperature after setting a constant biome temperature. Establish
            // the isolated fixture climate before either pawn is exposed.
            foreach (var nativeRoom in map.regionGrid.AllRooms) nativeRoom.Temperature = 21f;
            patient = MakeHealthy(center + new IntVec3(0, 0, -1), false);
            doctor = MakeHealthy(center + new IntVec3(3, 0, -1), true);
            patient.Name = new NameTriple("시연", "환자", "");
            doctor.Name = new NameTriple("시연", "의사", "");
            patient.playerSettings.medCare = MedicalCareCategory.Best;
            patient.playerSettings.selfTend = false;
            food = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDef.Named("RMush_RawDestroyingAngel")),
                patient.Position + IntVec3.South, map);
            food.stackCount = 1;
            patient.foodRestriction.CurrentFoodPolicy.filter.SetAllow(food.def, true);
            medicine = ThingMaker.MakeThing(ThingDefOf.MedicineIndustrial);
            medicine.stackCount = 12;
            GenSpawn.Spawn(medicine, center + new IntVec3(2, 0, -1), map);
            report.AppendLine("Native RimWorld clinical visual demonstration; species=DestroyingAngel/Amanita virosa/독우산광대버섯.");
            report.AppendLine("METHOD: real forced Ingest job of one raw mushroom and real TendPatient jobs using industrial medicine.");
            report.AppendLine("METHOD: between jobs, DebugSetTicksGame plus patient.health.HealthTickInterval(250); other colony, needs, weather and work are not simulated across compressed time.");
            report.AppendLine("No post-ingestion severity assignment, added poison, cure, immunity, damage suppression or removal of patient hediffs.");
            report.AppendLine("The 35% stage reduces gastrointestinal symptoms while poisoning/liver injury progresses; severity does not improve at that stage.");
            report.AppendLine("Native Health tab and native ScreenCapture frames; no image editing. Prepared healthy adult humans; doctor Medicine=20; medical bed; industrial medicine.");
            report.AppendLine("Controlled isolated climate=21C, clear weather, no queued incidents; native room caches initialized before ingestion. Native vomiting jobs are allowed to finish and the patient returns to bed before further tending.");
            report.AppendLine("Profile=" + profile + ";gameVersion=" + VersionControl.CurrentVersionStringWithRev);
            csv.AppendLine("frame,tick,hoursAfterIngest,active,phase,clinicalStage,severity,tendCount,lastTendQuality,treatmentReserve,consciousness,manipulation,moving,bloodFiltration,breathing,pain,onsetTick,recoveryEndTick");
            map.mapDrawer.RegenerateEverythingNow();
            Find.CameraDriver.JumpToCurrentMapLoc(center);
            Find.CameraDriver.SetRootSize(13f);
            var job = JobMaker.MakeJob(JobDefOf.Ingest, food);
            job.count = 1; job.ingestTotalCount = true; job.playerForced = true;
            jobId = job.loadID; jobStartTick = Now;
            initialized = true; state = 1;
            patient.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            Require(patient.CurJob != null && patient.CurJob.loadID == jobId, "Real Ingest job was not accepted.");
            report.AppendLine("INGEST_JOB started tick=" + Now + ";loadID=" + jobId + ";food=" + food.ThingID);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            WriteReports();
        }

        private Pawn MakeHealthy(IntVec3 position, bool physician)
        {
            for (int attempt = 0; attempt < 32; attempt++)
            {
                var request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                    forceGenerateNewPawn: true, allowDead: false, allowDowned: false,
                    canGeneratePawnRelations: false, allowAddictions: false, allowFood: false,
                    fixedBiologicalAge: 28f, fixedChronologicalAge: 28f, forceNoIdeo: true,
                    forceNoBackstory: true, forbidAnyTitle: true, forceBaselinerChance: 1f,
                    maximumAgeTraits: 0, forceNoGear: true);
                var pawn = PawnGenerator.GeneratePawn(request);
                foreach (var hediff in pawn.health.hediffSet.hediffs.ToList()) pawn.health.RemoveHediff(hediff);
                foreach (var trait in pawn.story.traits.allTraits.ToList()) pawn.story.traits.RemoveTrait(trait);
                foreach (var need in pawn.needs.AllNeeds) need.CurLevelPercentage = 1f;
                pawn.needs.food.CurLevelPercentage = 0.55f;
                pawn.inventory.innerContainer.ClearAndDestroyContents();
                pawn.workSettings.EnableAndInitializeIfNotAlreadyInitialized();
                foreach (var work in DefDatabase<WorkTypeDef>.AllDefs) pawn.workSettings.SetPriority(work, 0);
                var skill = pawn.skills.GetSkill(SkillDefOf.Medicine);
                skill.Level = physician ? 20 : 5;
                skill.Notify_SkillDisablesChanged(); skill.DirtyAptitudes();
                StatDefOf.MedicalTendQuality.Worker.ClearCacheForThing(pawn);
                if (physician && (pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor) || skill.TotallyDisabled
                    || pawn.GetStatValue(StatDefOf.MedicalTendQuality, cacheStaleAfterTicks: 0) < 1f))
                { pawn.Destroy(DestroyMode.Vanish); continue; }
                GenSpawn.Spawn(pawn, position, map);
                for (int hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
                Require(!pawn.Dead && !pawn.Downed && pawn.health.hediffSet.hediffs.Count == 0,
                    "Prepared clinical pawn is not healthy.");
                return pawn;
            }
            throw new Exception("Could not generate a medically capable healthy physician.");
        }

        private void PollIngestion()
        {
            Require(Now - jobStartTick < 10000, "Actual Ingest job timed out.");
            poison = patient.health.hediffSet.GetFirstHediffOfDef(HediffDef.Named("RMush_PoisonDestroyingAngel")) as Hediff_MushroomPoisoning;
            if (poison == null) return;
            Require(food.Destroyed && Math.Abs(poison.ExposureUnits - 1f) < 0.0001f,
                "Native Ingest did not consume exactly one mushroom/exposure.");
            Require(patient.health.hediffSet.hediffs.All(h => ReferenceEquals(h, poison)),
                "Unrelated condition appeared during initial native ingestion: " + string.Join("|", patient.health.hediffSet.hediffs.Select(h => h.def.defName)));
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            ingestTick = Now;
            report.AppendLine("INGEST_JOB completed tick=" + Now + ";exposure=" + F(poison.ExposureUnits)
                + ";onsetTick=" + poison.OnsetTick + ";hediffs=" + string.Join("|", patient.health.hediffSet.hediffs.Select(h => h.def.defName)));
            var job = JobMaker.MakeJob(JobDefOf.LayDown, bed);
            job.playerForced = true; job.restUntilHealed = true;
            patient.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            state = 2; jobStartTick = Now;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
        }

        private void PollBed()
        {
            Require(Now - jobStartTick < 5000, "Patient did not enter the medical bed.");
            if (!patient.InBed() || patient.CurrentBed() != bed) return;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            state = 3;
            PrepareShot("01-latent", "잠복기 · 아직 드러나지 않은 중독");
        }

        private void AdvanceNextStage()
        {
            if (shot == 1)
            {
                AdvanceHealth(poison.OnsetTick + 250);
                Require(Active && !poison.IsLatent && poison.Severity >= .15f, "Onset did not occur natively.");
                PrepareShot("02-onset", "발현 · 위장 증상 시작");
            }
            else if (shot == 2)
            {
                AdvanceHealth(ingestTick + 31 * Hour);
                Require(Active && poison.Severity >= .35f && poison.Severity < .6f, "Expected natural 35% clinical stage.");
                PrepareShot("03-gastrointestinal-remission", "위장 증상 완화 · 간 손상과 중독은 진행");
            }
            else if (shot == 3)
            {
                AdvanceHealth(ingestTick + 60 * Hour);
                Require(Active && poison.Severity >= .6f && !poison.IsStabilized, "Expected untreated severe clinical stage.");
                PrepareShot("04-severe", "악화 · 심각한 간 손상");
            }
            else if (shot == 4) StartTend();
            else if (shot == 5)
            {
                AdvanceUntilTendable();
                StartTend();
            }
            else if (shot == 6)
            {
                int limit = Now + 8 * 60000;
                while (Active && poison.Severity > .30f && Now < limit) AdvanceHealth(Now + 250);
                Require(Active && poison.IsStabilized && poison.Severity > .28f && poison.Severity <= .30f,
                    "Native recovery did not reach approximately 30%.");
                PrepareShot("07-recovering", "회복 중 · 중독 강도가 내려가는 중");
            }
            else if (shot == 7)
            {
                int limit = Now + 8 * 60000;
                while (Active && Now < limit) AdvanceHealth(Now + 250);
                Require(!Active && !patient.Dead, "Native recovery did not remove the resolved poisoning.");
                PrepareShot("08-recovered", "회복 완료 · 중독 상태 소멸");
            }
            else throw new Exception("Unexpected timeline stage " + shot);
        }

        private void AdvanceHealth(int target)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            while (Now < target)
            {
                int delta = Math.Min(250, target - Now);
                Find.TickManager.DebugSetTicksGame(Now + delta);
                patient.health.HealthTickInterval(delta);
                Require(!patient.Dead, "Patient died during native accelerated health interval.");
            }
        }

        private void AdvanceUntilTendable()
        {
            Require(Active && tendCount < 6, "Clinical stabilization failed after six ordinary treatments.");
            int target = Math.Max(Now, lastTendTick + 6 * Hour + 250);
            AdvanceHealth(target);
            int limit = Now + 2 * Hour;
            while (!poison.TendableNow() && Now < limit) AdvanceHealth(Now + 250);
            Require(poison.TendableNow(), "Native six-hour tending timer did not expire.");
        }

        private void StartTend()
        {
            Require(Active && poison.TendableNow() && patient.health.HasHediffsNeedingTend(), "Patient is not natively tendable.");
            if (!patient.InBed() || patient.CurrentBed() != bed)
            {
                report.AppendLine("BED_RETURN waiting tick=" + Now + ";nativeJob=" + patient.CurJobDef + ";posture=" + patient.GetPosture());
                state = 5; jobStartTick = Now; bedReturnAssigned = false;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
                return;
            }
            Require(!medicine.Destroyed && medicine.stackCount > 0, "Industrial medicine exhausted.");
            reserveBefore = poison.TreatmentReserve;
            medicineBefore = medicine.stackCount;
            var job = JobMaker.MakeJob(JobDefOf.TendPatient, patient, medicine);
            job.playerForced = true;
            jobId = job.loadID; jobStartTick = Now;
            doctor.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            Require(doctor.CurJob != null && doctor.CurJob.loadID == jobId && doctor.CurJob.def == JobDefOf.TendPatient,
                "Actual native TendPatient job not accepted.");
            report.AppendLine("TEND_JOB started tick=" + Now + ";loadID=" + jobId + ";medicine=" + medicine.def.defName
                + ";priorReserve=" + F(reserveBefore));
            state = 4;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
        }

        private void PollReturnToBed()
        {
            Require(Now - jobStartTick < 12000, "Native symptom job / medical-bed return timed out;current=" + patient.CurJobDef);
            // Do not cancel or erase actual poison-triggered vomiting. Let the
            // real job finish in ordinary game ticks before asking for bed rest.
            if (patient.CurJobDef == JobDefOf.Vomit) return;
            if (patient.InBed() && patient.CurrentBed() == bed)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                report.AppendLine("BED_RETURN completed tick=" + Now + ";nativeJob=" + patient.CurJobDef);
                StartTend();
                return;
            }
            if (!bedReturnAssigned)
            {
                var job = JobMaker.MakeJob(JobDefOf.LayDown, bed);
                job.playerForced = true; job.restUntilHealed = true;
                patient.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                bedReturnAssigned = true;
            }
        }

        private void PollTend()
        {
            Require(Now - jobStartTick < 10000, "Actual TendPatient job timed out;current=" + doctor.CurJobDef);
            var tend = poison.TryGetComp<HediffComp_TendDuration>();
            if (poison.TreatmentReserve <= reserveBefore || tend == null || !tend.IsTended) return;
            Require(medicine.Destroyed || medicine.stackCount < medicineBefore, "Native tending did not consume medicine.");
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            tendCount++; lastTendTick = Now; lastQuality = tend.tendQuality;
            report.AppendLine("TEND_JOB completed tick=" + Now + ";loadID=" + jobId + ";tendCount=" + tendCount
                + ";quality=" + F(lastQuality) + ";reserve=" + F(poison.TreatmentReserve)
                + ";nativeTendTicksLeft=" + tend.tendTicksLeft + ";stabilized=" + poison.IsStabilized);
            state = 3;
            if (tendCount == 1)
            {
                Require(!poison.IsStabilized, "First ordinary treatment unexpectedly stabilized fatal poisoning.");
                PrepareShot("05-first-treatment", "첫 치료 · 악화를 억제하지만 아직 불안정");
            }
            else if (poison.IsStabilized)
            {
                Require(tendCount >= 2, "Stabilization happened before repeated native medical care.");
                PrepareShot("06-stabilized", "반복 치료 후 안정화 · 이제 회복 시작");
            }
            else { AdvanceUntilTendable(); StartTend(); }
        }

        private void PrepareShot(string name, string title)
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Find.Selector.ClearSelection();
            Find.Selector.Select(patient, playSound: false, forceDesignatorDeselect: false);
            Require(InspectPaneUtility.OpenTab(typeof(ITab_Pawn_Health)) != null, "Native Health inspect tab unavailable.");
            Require(Find.MainTabsRoot.OpenTab == MainButtonDefOf.Inspect
                && ((MainTabWindow_Inspect)MainButtonDefOf.Inspect.TabWindow).OpenTabType == typeof(ITab_Pawn_Health),
                "Native Health tab is not open for the patient.");
            Find.CameraDriver.JumpToCurrentMapLoc(bed.Position + new IntVec3(-2, 0, 0));
            Find.CameraDriver.SetRootSize(13f);
            stageTitle = title;
            pending = Path.Combine(Output, name + ".png");
            capturePending = true; captureRequested = false;
            frames = 0; readyAt = Time.realtimeSinceStartup + 2.5f;
            Record(name);
            WriteReports();
        }

        private void CaptureUpdate()
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            if (++frames < 40 || Time.realtimeSinceStartup < readyAt) return;
            if (!captureRequested)
            {
                ScreenCapture.CaptureScreenshot(pending);
                captureRequested = true; frames = 0;
                readyAt = Time.realtimeSinceStartup + 2.5f;
                deadline = Time.realtimeSinceStartup + 20f;
                return;
            }
            if (!File.Exists(pending) || new FileInfo(pending).Length < 1024)
            {
                Require(Time.realtimeSinceStartup < deadline, "Native screenshot not written: " + pending);
                return;
            }
            report.AppendLine("FRAME " + Path.GetFileName(pending) + ";bytes=" + new FileInfo(pending).Length
                + ";screen=" + UI.screenWidth + "x" + UI.screenHeight + ";HealthTab630x430=True");
            capturePending = false; shot++;
            if (shot < 8) { WriteReports(); return; }
            report.AppendLine("RESULT PASS;frames=8;actualIngestJobs=1;actualTendJobs=" + tendCount + ";patientAlive=True;poisonResolved=True");
            WriteReports();
            File.WriteAllText(Path.Combine(Output, "ready-paused.flag"), "Native Health timeline complete; patient selected; paused.\n");
            finished = true;
            if (!Hold) Application.Quit(0);
        }

        private void Record(string frame)
        {
            Require(patient.health.hediffSet.hediffs.All(h => ReferenceEquals(h, poison)),
                "Clinical frame contains an unrelated patient condition: " + string.Join("|", patient.health.hediffSet.hediffs.Select(h => h.def.defName)));
            float severity = Active ? poison.Severity : 0f;
            var values = new[] { frame, Now.ToString(), F((Now - ingestTick) / (float)Hour), Active.ToString(),
                Active ? poison.Phase.ToString() : "Resolved", Active ? poison.CurStage?.label ?? "" : "",
                F(severity), tendCount.ToString(), F(lastQuality), F(poison.TreatmentReserve),
                F(patient.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness)),
                F(patient.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation)),
                F(patient.health.capacities.GetLevel(PawnCapacityDefOf.Moving)),
                F(patient.health.capacities.GetLevel(PawnCapacityDefOf.BloodFiltration)),
                F(patient.health.capacities.GetLevel(PawnCapacityDefOf.Breathing)),
                F(patient.health.hediffSet.PainTotal), poison.OnsetTick.ToString(), poison.RecoveryEndTick.ToString() };
            csv.AppendLine(string.Join(",", values.Select(Csv)));
            report.AppendLine("SNAPSHOT " + string.Join(";", values));
            if (Active) report.AppendLine("NATIVE_HEALTH_TOOLTIP " + poison.GetTooltip(patient, false).Replace("\n", " | "));
        }

        private void WriteReports()
        {
            File.WriteAllText(Path.Combine(Output, "health-timeline-report.txt"), report.ToString(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Output, "health-timeline.csv"), csv.ToString(), new UTF8Encoding(false));
        }

        public override void GameComponentOnGUI()
        {
            if (!Enabled || !initialized || finished || !capturePending) return;
            var rect = new Rect(240f, 24f, Math.Min(1100f, UI.screenWidth - 260f), 116f);
            Widgets.DrawBoxSolid(rect, new Color(0.08f, 0.09f, 0.10f, .92f));
            var oldFont = Text.Font;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x + 14f, rect.y + 8f, rect.width - 28f, 35f), stageTitle);
            Text.Font = GameFont.Small;
            string summary = "독우산광대버섯 1개 섭취 · 경과 " + ((Now - ingestTick) / (float)Hour).ToString("F1") + "시간"
                + " · 중독 " + (Active ? poison.Severity.ToStringPercent() : "없음") + " · 실제 치료 " + tendCount + "회";
            Widgets.Label(new Rect(rect.x + 14f, rect.y + 46f, rect.width - 28f, 28f), summary);
            Widgets.Label(new Rect(rect.x + 14f, rect.y + 77f, rect.width - 28f, 28f),
                "실제 섭취·치료 작업 / 중간 경과는 건강 틱만 시간 압축 / 기본 건강 탭 원본 화면");
            Text.Font = oldFont;
        }

        private static string F(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        private static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
