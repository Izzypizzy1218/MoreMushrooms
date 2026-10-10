using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using RimMushrooms;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMushroomsTests
{
    // An opt-in, accelerated single-pawn clinical fixture, never a colony benchmark.
    // Native ingestion, health, memory and mental-state entry points run against
    // real generated pawns. Map/AI/economy ticks are deliberately not simulated.
    public sealed class FieldBalanceTests : GameComponent
    {
        private const int Hour = 2500, Day = 60000, Step = 150;
        private static readonly string[] Psy = { "LibertyCap", "Cubensis", "FlyAgaric", "PantherCap" };
        private static readonly string[] Tox = { "FlyAgaric", "DestroyingAngel", "SulfurTuft", "Tsukiyotake", "PoisonFireCoral", "DeathCap", "YellowDapperling", "JackOLantern", "DeadlyWebcap", "PantherCap", "GhostFungus" };
        private static readonly string[] Fatal = { "DestroyingAngel", "PoisonFireCoral", "DeathCap", "DeadlyWebcap" };
        private static readonly int[] Seeds = { 63011, 63037, 63073 };
        private readonly List<Case> cases = new List<Case>();
        private readonly List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
        private readonly List<Dictionary<string, object>> events = new List<Dictionary<string, object>>();
        private readonly List<string> failures = new List<string>();
        private readonly List<string> findings = new List<string>();
        private readonly List<Pawn> ownedPawns = new List<Pawn>();
        private Pawn doctor;
        private Map map;
        private IntVec3 site;
        private int index, baseline;
        private bool prepared, finished;
        private Dictionary<string, object> current;
        private Dictionary<string, object> doctorMetadata;
        private FieldInfo jobCountField, jobCountTextField;
        private readonly List<string> nativeErrors = new List<string>();
        private bool collectingNativeErrors;
        private string Folder => GenFilePaths.SaveDataFolderPath;
        private string Report => Path.Combine(Folder, "field-balance-report.json");

        private sealed class Case
        {
            public string Group, Species, Protocol, Medicine, Intervention;
            public int Seed, InitialDoses = 1, RepeatHour = -1, CareDelayHours;
            public string Id => Group + "/" + Species + "/" + Protocol + "/" + Seed;
        }

        public FieldBalanceTests(Game game) { }

        public override void GameComponentUpdate()
        {
            if (finished || !GenCommandLine.CommandLineArgPassed("mushroomFieldBalance")
                || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting) return;
            if (Find.CurrentMap == null) return;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            try
            {
                if (!prepared) Prepare(Find.CurrentMap);
                if (index >= cases.Count) { Finish(); return; }
                var test = cases[index++];
                var oldLetters = Find.LetterStack.LettersListForReading.ToArray();
                int clock = Find.TickManager.TicksGame;
                try
                {
                    nativeErrors.Clear();
                    collectingNativeErrors = true;
                    Rand.PushState(test.Seed);
                    try
                    {
                        RunCase(test);
                        Require(nativeErrors.Count == 0, "native error log during case: " + string.Join("\n", nativeErrors));
                    }
                    finally { collectingNativeErrors = false; Rand.PopState(); }
                }
                catch (Exception error)
                {
                    failures.Add(test.Id + ": " + error);
                    if (current == null) current = Row(test);
                    current["error"] = error.ToString();
                    current["nativeErrorLog"] = nativeErrors.ToArray();
                    if (!results.Contains(current)) results.Add(current);
                    Log.Error("[Mushroom Field Balance] " + test.Id + " FAILED: " + error);
                }
                finally
                {
                    foreach (var pawn in ownedPawns.Where(p => p != doctor).ToArray()) Cleanup(pawn);
                    Find.TickManager.DebugSetTicksGame(clock);
                    foreach (var letter in Find.LetterStack.LettersListForReading.Where(l => !oldLetters.Contains(l)).ToList())
                        Find.LetterStack.RemoveLetter(letter);
                    WriteReports(false);
                }
            }
            catch (Exception error)
            {
                failures.Add("fixture: " + error);
                Log.Error("[Mushroom Field Balance] fixture failed: " + error);
                Finish();
            }
        }

        private void Prepare(Map target)
        {
            prepared = true;
            Application.logMessageReceived += CaptureNativeError;
            map = target;
            baseline = Find.TickManager.TicksGame;
            DebugSettings.enableStoryteller = false;
            map.weatherManager.TransitionTo(WeatherDefOf.Clear);
            site = map.Center;
            jobCountField = typeof(Pawn_JobTracker).GetField("jobsGivenThisTick", BindingFlags.Instance | BindingFlags.NonPublic);
            jobCountTextField = typeof(Pawn_JobTracker).GetField("jobsGivenThisTickTextual", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(jobCountField?.FieldType == typeof(int) && jobCountTextField?.FieldType == typeof(string),
                "native per-tick job guard bookkeeping fields match this game version");
            var rect = new CellRect(site.x - 4, site.z - 4, 9, 9);
            Require(rect.Cells.All(c => c.InBounds(map)), "fixture fits map");
            foreach (var cell in rect.Cells)
            {
                foreach (var thing in cell.GetThingList(map).ToList())
                    if (!(thing is Pawn)) thing.Destroy(DestroyMode.Vanish);
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
                map.roofGrid.SetRoof(cell, null);
                map.snowGrid.SetDepth(cell, 0f);
            }
            Rand.PushState(63999);
            try { doctor = GenerateHealthy(true); }
            finally { Rand.PopState(); }
            doctorMetadata = new Dictionary<string, object> {
                { "seed", 63999 }, { "pawnId", doctor.GetUniqueLoadID() },
                { "medicalSkill", doctor.skills.GetSkill(SkillDefOf.Medicine).Level },
                { "nativeTendQualityStat", doctor.GetStatValue(StatDefOf.MedicalTendQuality, cacheStaleAfterTicks: 0) },
                { "traits", string.Join(";", doctor.story.traits.allTraits.Select(t => t.def.defName + ":" + t.Degree)) },
                { "nativeBaseQualityNoMedicine", TendUtility.CalculateBaseTendQuality(doctor, null, null) },
                { "nativeBaseQualityHerbal", TendUtility.CalculateBaseTendQuality(doctor, null, ThingDef.Named("MedicineHerbal")) },
                { "nativeBaseQualityIndustrial", TendUtility.CalculateBaseTendQuality(doctor, null, ThingDef.Named("MedicineIndustrial")) },
                { "nativeBaseQualityUltratech", TendUtility.CalculateBaseTendQuality(doctor, null, ThingDef.Named("MedicineUltratech")) }
            };
            foreach (string species in Psy)
            {
                foreach (int seed in Seeds)
                {
                    cases.Add(new Case { Group = "psychoactive", Species = species, Protocol = "first-standard-awake", Seed = seed });
                    cases.Add(new Case { Group = "psychoactive", Species = species, Protocol = "three-standard-immediate", InitialDoses = 3, Seed = seed });
                    cases.Add(new Case { Group = "psychoactive", Species = species, Protocol = "second-standard-after-six-hours", RepeatHour = 6, Seed = seed });
                }
                cases.Add(new Case { Group = "psychoactive", Species = species, Protocol = "controlled-sleep-at-two-hours", Intervention = "sleep", Seed = Seeds[0] });
                cases.Add(new Case { Group = "psychoactive", Species = species, Protocol = "anesthetic-downing-at-two-hours", Intervention = "anesthetic", Seed = Seeds[0] });
            }
            foreach (string species in Tox)
                foreach (int seed in Seeds.Take(2))
                    foreach (string medicine in new[] { "untreated", "MedicineIndustrial" })
                        cases.Add(new Case { Group = "poison", Species = species, Protocol = "one-standard-" + medicine, Medicine = medicine, Seed = seed });
            foreach (string species in Fatal)
            {
                foreach (int seed in Seeds.Take(2))
                    foreach (string medicine in new[] { "none", "MedicineHerbal", "MedicineUltratech" })
                        cases.Add(new Case { Group = "poison", Species = species, Protocol = "one-standard-" + medicine, Medicine = medicine, Seed = seed });
                foreach (int delay in new[] { 24, 72 })
                    cases.Add(new Case { Group = "poison", Species = species, Protocol = "industrial-start-warning-plus-" + delay + "h", Medicine = "MedicineIndustrial", CareDelayHours = delay, Seed = Seeds[0] });
            }
            foreach (string species in new[] { "FlyAgaric", "SulfurTuft" })
                foreach (int seed in Seeds.Take(2))
                    foreach (string medicine in new[] { "untreated", "MedicineIndustrial" })
                        cases.Add(new Case { Group = "poison", Species = species, Protocol = "three-standard-" + medicine, InitialDoses = 3, Medicine = medicine, Seed = seed });
            foreach (string medicine in new[] { "untreated", "MedicineIndustrial" })
                cases.Add(new Case { Group = "poison", Species = "PantherCap", Protocol = "four-standard-" + medicine, InitialDoses = 4, Medicine = medicine, Seed = Seeds[0] });
            Log.Message("[Mushroom Field Balance] prepared " + cases.Count + " accelerated native single-pawn cases; output=" + Report);
            WriteReports(false);
        }

        private Pawn GenerateHealthy(bool physician = false)
        {
            for (int attempt = 0; attempt < 32; attempt++)
            {
                var request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                    forceGenerateNewPawn: true, canGeneratePawnRelations: false, allowAddictions: false,
                    allowPregnant: false, fixedBiologicalAge: 30f, forceNoIdeo: true,
                    forceBaselinerChance: 1f, forceNoGear: true);
                var pawn = PawnGenerator.GeneratePawn(request);
                ownedPawns.Add(pawn);
                foreach (var effect in pawn.health.hediffSet.hediffs.ToList()) pawn.health.RemoveHediff(effect);
                pawn.inventory?.innerContainer.ClearAndDestroyContents();
                pawn.needs.food.CurLevelPercentage = 1f;
                pawn.needs.rest.CurLevelPercentage = 1f;
                GenSpawn.Spawn(pawn, physician ? site + new IntVec3(3, 0, 0) : site, map);
                if (pawn.Dead || pawn.Downed || !pawn.Awake()) { Cleanup(pawn); continue; }
                if (!physician) return pawn;
                var skill = pawn.skills.GetSkill(SkillDefOf.Medicine);
                skill.Level = 10;
                skill.Notify_SkillDisablesChanged();
                skill.DirtyAptitudes();
                StatDefOf.MedicalTendQuality.Worker.ClearCacheForThing(pawn);
                if (!pawn.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Doctor")) && !skill.TotallyDisabled
                    && pawn.GetStatValue(StatDefOf.MedicalTendQuality, cacheStaleAfterTicks: 0) > 0f) return pawn;
                Cleanup(pawn);
            }
            throw new InvalidOperationException("No suitable healthy adult fixture pawn after 32 attempts.");
        }

        private void RunCase(Case test)
        {
            current = Row(test);
            var patient = GenerateHealthy();
            int start = Find.TickManager.TicksGame;
            var initialLetters = Find.LetterStack.LettersListForReading.ToArray();
            current["pawnId"] = patient.GetUniqueLoadID();
            current["traits"] = string.Join(";", patient.story.traits.allTraits.Select(t => t.def.defName + ":" + t.Degree));
            current["healthAtStart"] = Diagnostic(patient);
            var wait = JobMaker.MakeJob(JobDefOf.Wait);
            wait.expiryInterval = 9999999;
            patient.jobs.StartJob(wait, JobCondition.InterruptForced);
            string initialJob = patient.CurJobDef?.defName;
            Ingest(test, patient, test.InitialDoses, start);
            var hall = Hall(patient);
            var poison = Poison(patient, test.Species);
            current["initialOrderedJob"] = initialJob;
            current["initialOrderedJobInterrupted"] = patient.CurJob != wait;
            current["initialMoodOffset"] = Memory(patient)?.moodOffset ?? 0;
            current["initialMoodStage"] = Memory(patient)?.CurStageIndex ?? -1;
            current["scheduledInitialControlHours"] = hall == null ? (object)null : Hours(hall.EndTick - start);
            current["initialTolerance"] = Tolerance(patient);
            current["scheduledLatencyHours"] = poison == null ? (object)null : Hours(poison.OnsetTick - start);
            current["lifeThreateningAtIngestion"] = poison?.IsLifeThreatening ?? false;
            current["actualExposureUnitsAtIngestion"] = poison == null ? (object)null : poison.ExposureUnits;
            if (test.Group == "psychoactive")
            {
                Require(hall != null && patient.MentalState is MentalState_MushroomWander, "native ingestion starts hallucination wandering");
                Require(hall.EndTick - start >= 6 * Hour, "new ingestion schedules at least six hours");
                Require(hall.EndTick - hall.EpisodeStartTick <= 24 * Hour, "scheduled episode respects 24h cap");
                if (test.InitialDoses == 1)
                    Require(Memory(patient)?.moodOffset == (test.Species == "FlyAgaric" || test.Species == "PantherCap" ? 10 : 15), "standard first exposure mood");
            }
            else Require(poison != null, "native ingestion creates requested poison");

            int mentalTicks = 0, downTicks = 0, sleepTicks = 0, unavailableTicks = 0;
            int positiveMoodTicks = 0, negativeMoodTicks = 0, poisonTicks = 0;
            double moodPointTicks = 0;
            int treatmentCount = 0, consumedMedicine = 0, repeatCount = 0;
            bool intervened = false, released = false, previousMental = patient.InMentalState;
            bool previousDown = patient.Downed, previousAwake = patient.Awake(), previousPanic = hall?.Panic ?? false;
            bool panicEver = previousPanic;
            float previousMood = Memory(patient)?.moodOffset ?? 0f;
            bool warningSeen = false, reached24 = false, stabilizedSeen = false;
            int warningTick = -1, firstControlStop = -1, deathTick = -1, curedTick = -1;
            float peakSeverity = poison?.Severity ?? 0f, minimumConsciousness = 1f, minimumMovement = 1f;
            var qualities = new List<float>();
            var anesthetic = (Hediff)null;
            int horizon = start + 16 * Day;
            for (int now = start; now < horizon && !patient.Dead; )
            {
                var memory = Memory(patient);
                bool mental = patient.MentalState is MentalState_MushroomWander;
                bool down = patient.Downed, awake = patient.Awake();
                if (mental) mentalTicks += Step;
                if (down) downTicks += Step;
                if (!awake) sleepTicks += Step;
                if (mental || down || !awake) unavailableTicks += Step;
                if (memory != null)
                {
                    if (memory.moodOffset > 0) positiveMoodTicks += Step;
                    if (memory.moodOffset < 0) negativeMoodTicks += Step;
                    moodPointTicks += memory.moodOffset * Step;
                }
                if (poison != null && patient.health.hediffSet.hediffs.Contains(poison)) poisonTicks += Step;
                now += Step;
                Find.TickManager.DebugSetTicksGame(now);
                ResetNativeJobTickBookkeeping(patient);
                patient.health.HealthTickInterval(Step);
                if (patient.Dead)
                {
                    deathTick = now;
                    if (previousMental && firstControlStop < 0) firstControlStop = now;
                    if (poison != null) peakSeverity = Mathf.Max(peakSeverity, poison.Severity);
                    if (warningTick >= 0 && !reached24 && now >= warningTick + Day)
                    {
                        reached24 = true;
                        current["aliveAtWarningPlus24Hours"] = false;
                        current["severityAtWarningPlus24Hours"] = poison.Severity;
                    }
                    Event(test, "death", start, Diagnostic(patient));
                    break;
                }
                (patient.MentalState as MentalState_MushroomWander)?.MentalStateTick(Step);
                patient.needs.mood.thoughts.memories.MemoryThoughtInterval();

                if (test.RepeatHour >= 0 && repeatCount == 0 && now - start >= test.RepeatHour * Hour)
                {
                    Ingest(test, patient, 1, start);
                    repeatCount++;
                    current["moodAfterDelayedRepeat"] = Memory(patient)?.moodOffset ?? 0;
                    current["toleranceAfterDelayedRepeat"] = Tolerance(patient);
                }
                if (!intervened && test.Intervention != null && now - start >= 2 * Hour)
                {
                    intervened = true;
                    if (test.Intervention == "sleep")
                    {
                        // Controlled external sleep intervention, not a measured
                        // autonomous decision by the normal hunger/rest AI.
                        patient.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, patient.Position), JobCondition.InterruptForced);
                        Require(patient.jobs.curDriver != null, "sleep intervention has native job driver");
                        patient.jobs.posture = PawnPosture.LayingOnGroundNormal;
                        patient.jobs.curDriver.asleep = true;
                        Require(!patient.Awake(), "native Awake observes controlled sleeping driver");
                    }
                    else
                    {
                        anesthetic = HediffMaker.MakeHediff(HediffDefOf.Anesthetic, patient);
                        anesthetic.Severity = 1f;
                        patient.health.AddHediff(anesthetic);
                        Require(patient.Downed, "native anesthetic actually downs fixture pawn");
                    }
                    Event(test, "intervention-" + test.Intervention, start, Diagnostic(patient));
                    (patient.MentalState as MentalState_MushroomWander)?.MentalStateTick(1);
                    Require(!(patient.MentalState is MentalState_MushroomWander), "sleep/downing ends native mental state");
                    if (test.Intervention == "sleep") Require(!patient.Awake(), "native recovery preserves controlled sleep job");
                }
                if (intervened && !released && now - start >= 4 * Hour)
                {
                    released = true;
                    if (anesthetic != null && patient.health.hediffSet.hediffs.Contains(anesthetic)) patient.health.RemoveHediff(anesthetic);
                    if (test.Intervention == "sleep") patient.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                    Hall(patient)?.PostTickInterval(Step);
                    Require(!(patient.MentalState is MentalState_MushroomWander), "episode does not restart after controlled rescue/wake");
                    Event(test, "intervention-released", start, Diagnostic(patient));
                }

                poison = Poison(patient, test.Species) ?? poison;
                if (poison != null)
                {
                    peakSeverity = Mathf.Max(peakSeverity, poison.Severity);
                    if (!warningSeen && poison.FirstSymptomWarningTick >= 0)
                    {
                        warningSeen = true;
                        warningTick = poison.FirstSymptomWarningTick;
                        current["warningAtHours"] = Hours(warningTick - start);
                        current["newLettersAtWarning"] = Find.LetterStack.LettersListForReading
                            .Where(letter => !initialLetters.Contains(letter)).Select(letter => (object)letter.Label.ToString()).ToList();
                        Event(test, "first-symptom-warning", start, "severity=" + F(poison.Severity) + "; lifeThreatening=" + poison.IsLifeThreatening);
                    }
                    if (warningSeen && !reached24 && now >= warningTick + Day)
                    {
                        reached24 = true;
                        current["aliveAtWarningPlus24Hours"] = !patient.Dead;
                        current["severityAtWarningPlus24Hours"] = poison.Severity;
                        Event(test, "warning-plus-24h", start, Diagnostic(patient));
                    }
                    if (test.Medicine != null && test.Medicine != "untreated" && warningSeen
                        && now >= warningTick + test.CareDelayHours * Hour
                        && patient.health.hediffSet.hediffs.Contains(poison) && poison.TendableNow())
                    {
                        var medicine = test.Medicine == "none" ? null : (Medicine)ThingMaker.MakeThing(ThingDef.Named(test.Medicine));
                        float reserve = poison.TreatmentReserve;
                        float recordedTends = patient.records.GetValue(RecordDefOf.TimesTendedTo);
                        // Pairing seed + treatment ordinal supplies the same tend
                        // variance across medicine tiers, independently of course RNG.
                        Rand.PushState(test.Seed + 1000 + treatmentCount);
                        try { TendUtility.DoTend(doctor, patient, medicine); }
                        finally { Rand.PopState(); }
                        var duration = poison.TryGetComp<HediffComp_TendDuration>();
                        Require(duration != null && duration.IsTended && poison.TreatmentReserve >= reserve
                            && patient.records.GetValue(RecordDefOf.TimesTendedTo) == recordedTends + 1f,
                            "actual native care records tending and maintains/increases reserve, including reserve cap");
                        treatmentCount++;
                        qualities.Add(duration.tendQuality);
                        if (medicine != null)
                        {
                            Require(medicine.Destroyed, "native treatment consumes one medicine item");
                            consumedMedicine++;
                        }
                        Event(test, "native-tend", start, "quality=" + F(duration.tendQuality) + "; reserve=" + F(poison.TreatmentReserve));
                    }
                    if (poison.IsStabilized && !stabilizedSeen)
                    {
                        stabilizedSeen = true;
                        current["stabilizedAtHours"] = Hours(now - start);
                        current["treatmentsToStabilize"] = treatmentCount;
                        Event(test, "stabilized", start, "severity=" + F(poison.Severity));
                    }
                    if (curedTick < 0 && !patient.health.hediffSet.hediffs.Contains(poison))
                    {
                        curedTick = now;
                        Event(test, "poison-removed-by-native-health", start, Diagnostic(patient));
                    }
                }
                minimumConsciousness = Mathf.Min(minimumConsciousness, patient.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness));
                minimumMovement = Mathf.Min(minimumMovement, patient.health.capacities.GetLevel(PawnCapacityDefOf.Moving));
                hall = Hall(patient);
                if (hall != null) Require(hall.EndTick - hall.EpisodeStartTick <= Day, "actual repeated episode stays capped at 24h");
                bool currentMental = patient.MentalState is MentalState_MushroomWander;
                if (previousMental && !currentMental)
                {
                    if (firstControlStop < 0) firstControlStop = now;
                    Event(test, "mental-state-stopped", start, Diagnostic(patient));
                }
                if (currentMental != previousMental) Event(test, "mental-state=" + currentMental, start, Diagnostic(patient));
                if (patient.Downed != previousDown) Event(test, "downed=" + patient.Downed, start, Diagnostic(patient));
                if (patient.Awake() != previousAwake) Event(test, "awake=" + patient.Awake(), start, Diagnostic(patient));
                bool currentPanic = hall?.Panic ?? false;
                panicEver |= currentPanic;
                if (currentPanic != previousPanic) Event(test, "panic=" + currentPanic, start, "mood=" + (Memory(patient)?.moodOffset ?? 0));
                float currentMood = Memory(patient)?.moodOffset ?? 0f;
                if (currentMood != previousMood) Event(test, "mushroom-mood=" + F(currentMood), start, "stage=" + (Memory(patient)?.CurStageIndex ?? -1));
                previousMental = currentMental; previousDown = patient.Downed; previousAwake = patient.Awake(); previousPanic = currentPanic;
                previousMood = currentMood;
                if (now - start >= Day && (test.RepeatHour < 0 || repeatCount > 0)
                    && (hall == null || hall.ShouldRemove) && Memory(patient) == null
                    && (poison == null || !patient.health.hediffSet.hediffs.Contains(poison))) break;
            }
            int elapsed = Find.TickManager.TicksGame - start;
            current["elapsedHours"] = Hours(elapsed);
            current["died"] = patient.Dead;
            current["deathAtHours"] = deathTick < 0 ? (object)null : Hours(deathTick - start);
            current["deathHoursAfterWarning"] = deathTick < 0 || warningTick < 0 ? (object)null : Hours(deathTick - warningTick);
            current["curedAtHours"] = curedTick < 0 ? (object)null : Hours(curedTick - start);
            current["poisonStillActiveAtEnd"] = poison != null && patient.health.hediffSet.hediffs.Contains(poison);
            current["censoredAtHorizon"] = !patient.Dead && elapsed >= 16 * Day
                && ((poison != null && patient.health.hediffSet.hediffs.Contains(poison)) || Hall(patient) != null || Memory(patient) != null);
            current["mentalStateHours"] = Hours(mentalTicks);
            current["firstMentalStateStopHours"] = firstControlStop < 0 ? (object)null : Hours(firstControlStop - start);
            current["downedHours"] = Hours(downTicks);
            current["notAwakeHoursIncludingDowned"] = Hours(sleepTicks);
            current["normalCommandUnavailableProxyHours"] = Hours(unavailableTicks);
            current["trueColonyJobLossHours"] = null;
            current["positiveMushroomMoodHours"] = Hours(positiveMoodTicks);
            current["negativeMushroomMoodHours"] = Hours(negativeMoodTicks);
            current["mushroomMoodPointHours"] = moodPointTicks / Hour;
            current["poisonActiveHours"] = Hours(poisonTicks);
            current["peakObservedPoisonSeverity"] = peakSeverity;
            current["minimumConsciousness"] = minimumConsciousness;
            current["minimumMovement"] = minimumMovement;
            current["nativeTreatments"] = treatmentCount;
            current["medicineItemsConsumed"] = consumedMedicine;
            current["tendQualities"] = qualities.Cast<object>().ToList();
            current["finalTolerance"] = Tolerance(patient);
            current["panicEverObserved"] = panicEver;
            current["finalHealth"] = Diagnostic(patient);
            if (patient.Dead && poison != null && poison.IsLifeThreatening)
                Require(warningTick >= 0 && deathTick - warningTick >= Day, "actual lethal course preserves 24h after warning");
            if (test.Group == "psychoactive" && test.Protocol == "first-standard-awake"
                && (test.Species == "LibertyCap" || test.Species == "Cubensis"))
                Require(mentalTicks >= 6 * Hour && mentalTicks <= 8 * Hour + Step,
                    "healthy awake first psilocybin exposure realizes the 6-8h scheduled mental state");
            if (test.Medicine != null && test.Medicine != "untreated" && patient.Dead)
                findings.Add(test.Id + " died despite care: " + Json(current));
            if ((bool)current["censoredAtHorizon"]) findings.Add(test.Id + " remains unresolved at 16-day observation horizon");
            current["error"] = null;
            results.Add(current);
            Log.Message("[Mushroom Field Balance] " + index + "/" + cases.Count + " " + test.Id + " dead=" + patient.Dead
                + " mentalHours=" + F(Hours(mentalTicks)) + " moodPointHours=" + F(moodPointTicks / Hour) + " nativeTends=" + treatmentCount);
        }

        private void Ingest(Case test, Pawn pawn, int doses, int start)
        {
            var raw = ThingDef.Named("RMush_Raw" + test.Species);
            int unitCount = (test.Species == "FlyAgaric" || test.Species == "PantherCap" || test.Species == "LibertyCap" || test.Species == "Cubensis") ? 10 : 1;
            for (int dose = 0; dose < doses; dose++)
            {
                var food = ThingMaker.MakeThing(raw);
                food.stackCount = unitCount;
                food.Ingested(pawn, unitCount * .05f);
                Require(food.Destroyed, "native ingestion consumes complete standard exposure");
                Event(test, "ingested-standard-dose", start, "rawCount=" + unitCount + "; mood=" + (Memory(pawn)?.moodOffset ?? 0)
                    + "; tolerance=" + F(Tolerance(pawn)) + "; panic=" + (Hall(pawn)?.Panic ?? false));
            }
        }

        private static Hediff_MushroomHallucination Hall(Pawn pawn) => pawn.health.hediffSet.hediffs.OfType<Hediff_MushroomHallucination>().FirstOrDefault();
        private static Hediff_MushroomPoisoning Poison(Pawn pawn, string species) => pawn.health.hediffSet.hediffs.OfType<Hediff_MushroomPoisoning>().FirstOrDefault(p => p.def.defName == "RMush_Poison" + species);
        private static Thought_MushroomHallucination Memory(Pawn pawn) => pawn.needs?.mood?.thoughts.memories.Memories.OfType<Thought_MushroomHallucination>().FirstOrDefault(m => !m.ShouldDiscard);
        private static float Tolerance(Pawn pawn) => pawn.health.hediffSet.hediffs.OfType<Hediff_MushroomTolerance>().FirstOrDefault()?.Severity ?? 0f;
        private static double Hours(int ticks) => ticks / (double)Hour;
        private static string F(double number) => number.ToString("0.######", CultureInfo.InvariantCulture);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private void ResetNativeJobTickBookkeeping(Pawn pawn)
        {
            // JobTrackerTick normally clears these after DriverTick each native
            // tick. Many simulated clock steps share one render frame here; only
            // clear its transient loop guard, without running normal job AI.
            jobCountField.SetValue(pawn.jobs, 0);
            jobCountTextField.SetValue(pawn.jobs, "");
        }

        private void CaptureNativeError(string message, string stackTrace, LogType type)
        {
            if (collectingNativeErrors && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert))
                nativeErrors.Add(type + ": " + message + "\n" + stackTrace);
        }

        private static string Diagnostic(Pawn pawn)
        {
            return pawn.GetUniqueLoadID() + "; dead=" + pawn.Dead + "; downed=" + pawn.Downed + "; awake=" + pawn.Awake()
                + "; mental=" + (pawn.MentalStateDef?.defName ?? "none") + "; job=" + (pawn.CurJobDef?.defName ?? "none")
                + "; hediffs=" + string.Join("|", pawn.health.hediffSet.hediffs.Select(h => h.def.defName + ":" + F(h.Severity)))
                + "; bodyHealth=" + F(pawn.health.summaryHealth.SummaryHealthPercent);
        }

        private static Dictionary<string, object> Row(Case test) => new Dictionary<string, object> {
            { "caseId", test.Id }, { "group", test.Group }, { "species", test.Species }, { "protocol", test.Protocol },
            { "seed", test.Seed }, { "initialStandardDoses", test.InitialDoses }, { "delayedRepeatHour", test.RepeatHour },
            { "medicine", test.Medicine }, { "careDelayHoursAfterWarning", test.CareDelayHours }, { "intervention", test.Intervention }
        };

        private void Event(Case test, string kind, int start, string detail)
        {
            events.Add(new Dictionary<string, object> { { "caseId", test.Id }, { "event", kind },
                { "atHours", Hours(Find.TickManager.TicksGame - start) }, { "detail", detail } });
        }

        private void Cleanup(Pawn pawn)
        {
            pawn.relations?.ClearAllRelations();
            var corpse = pawn.Corpse;
            if (!pawn.Destroyed) pawn.Destroy(DestroyMode.Vanish);
            if (corpse != null && !corpse.Destroyed) corpse.Destroy(DestroyMode.Vanish);
            ownedPawns.Remove(pawn);
        }

        private void Finish()
        {
            if (finished) return;
            finished = true;
            collectingNativeErrors = false;
            Application.logMessageReceived -= CaptureNativeError;
            foreach (var pawn in ownedPawns.ToArray()) Cleanup(pawn);
            Find.TickManager.DebugSetTicksGame(baseline);
            WriteReports(true);
            Log.Message("[Mushroom Field Balance] RESULT " + (failures.Count == 0 ? "PASS" : "FAIL") + " cases=" + results.Count + " failures=" + failures.Count);
            Application.Quit(failures.Count == 0 ? 0 : 1);
        }

        private void WriteReports(bool complete)
        {
            Directory.CreateDirectory(Folder);
            var report = new Dictionary<string, object> {
                { "schemaVersion", 1 }, { "complete", complete }, { "result", complete ? failures.Count == 0 ? "PASS" : "FAIL" : "RUNNING" },
                { "gameVersion", VersionControl.CurrentVersionStringWithRev }, { "commandLineOptIn", "-mushroomFieldBalance" },
                { "plannedCases", cases.Count }, { "completedCases", results.Count }, { "resolutionGameHours", Hours(Step) },
                { "doctor", doctorMetadata },
                { "method", "Paused world; accelerated 150-tick clock advances for one real pawn. Native Thing.Ingested, Pawn_HealthTracker.HealthTickInterval, MentalStateTick, MemoryThoughtInterval and TendUtility.DoTend. No production values/severity/timers patched." },
                { "limitations", new[] {
                    "This is not full colony play. Work/pathfinding/normal job AI, hunger/rest decisions, weather/raids and logistics are not advanced; no real colony job-loss number is measured.",
                    "Mental-state hours and normal-command-unavailable union hours are controlled work-availability proxies, not measured crafting, hauling or harvest losses.",
                    "Awake protocols hold initial nutrition/rest by not ticking needs. Sleep intervention explicitly starts a native LayDown driver and sets its public ground posture/asleep flag at approximately 2h, releasing at 4h. It does not estimate spontaneous sleep frequency.",
                    "Downing intervention adds native anesthesia at approximately 2h and removes it at 4h. Native poisoning may independently down a pawn earlier or later.",
                    "Only mushroom memory mood offsets are integrated; overall mood and unrelated memories/ideology effects are not a net mood benefit estimate.",
                    "Care uses the real tend scanner and medicine consumption, but applies DoTend immediately when eligible; it excludes doctor travel, tending job duration, bed rest bonus and medicine supply constraints.",
                    "Patients are newly generated healthy 30-year-old baseliners with no generated relations or ideology; initial health conditions are removed once before ingestion. Traits remain recorded. Samples are small fixed-seed QA, not population probabilities.",
                    "Sixteen-day observation is censored when unresolved. Time sampling uncertainty is at most one 150-tick step per transition; poison internal progression may update at 300 ticks with this step.",
                    "Native HealthTickInterval may start vomiting jobs. Transient jobsGivenThisTick and jobsGivenThisTickTextual loop-guard fields alone are cleared each simulated step, matching JobTrackerTick bookkeeping while deliberately not running job drivers. Vomiting jobs can interrupt jobs but their full duration/work loss is not measured.",
                    "Single-species raw standard exposures only. Actual cooked mixtures, variable meal fractions, combined poison species, animals, old saves, addiction policies and colony economics are outside this run." } },
                { "failures", failures }, { "findings", findings }, { "summaries", Summaries() }, { "cases", results }, { "events", events }
            };
            File.WriteAllText(Report, Json(report), new UTF8Encoding(false));
            WriteCsv(Path.Combine(Folder, "field-balance-cases.csv"), results);
            WriteCsv(Path.Combine(Folder, "field-balance-events.csv"), events);
            File.WriteAllText(Path.Combine(Folder, "field-balance-report.txt"), "Mushroom field balance: " + report["result"]
                + Environment.NewLine + "Cases " + results.Count + "/" + cases.Count + "; failures " + failures.Count
                + Environment.NewLine + "Accelerated native single-pawn health/mental-state fixture; not measured colony job loss."
                + Environment.NewLine + string.Join(Environment.NewLine, failures.Concat(findings)), new UTF8Encoding(false));
        }

        private static void WriteCsv(string path, List<Dictionary<string, object>> rows)
        {
            var keys = rows.SelectMany(row => row.Keys).Distinct().ToList();
            var csv = new StringBuilder();
            csv.AppendLine(string.Join(",", keys.Select(Csv)));
            foreach (var row in rows)
                csv.AppendLine(string.Join(",", keys.Select(key => Csv(row.ContainsKey(key) && row[key] != null
                    ? row[key] is string ? (string)row[key] : Json(row[key]) : ""))));
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
        }

        private List<Dictionary<string, object>> Summaries()
        {
            var output = new List<Dictionary<string, object>>();
            foreach (var group in results.Where(row => row.ContainsKey("error") && row["error"] == null)
                .GroupBy(row => (string)row["group"] + "/" + row["species"] + "/" + row["protocol"]))
            {
                var rows = group.ToList();
                var summary = new Dictionary<string, object> {
                    { "groupSpeciesProtocol", group.Key }, { "samples", rows.Count },
                    { "deaths", rows.Count(row => (bool)row["died"]) },
                    { "panicCases", rows.Count(row => (bool)row["panicEverObserved"]) },
                    { "unresolvedAtHorizon", rows.Count(row => (bool)row["censoredAtHorizon"]) }
                };
                foreach (string key in new[] { "mentalStateHours", "positiveMushroomMoodHours", "negativeMushroomMoodHours",
                    "mushroomMoodPointHours", "downedHours", "normalCommandUnavailableProxyHours", "poisonActiveHours",
                    "nativeTreatments", "medicineItemsConsumed", "warningAtHours", "deathHoursAfterWarning", "curedAtHours" })
                {
                    var values = rows.Where(row => row.ContainsKey(key) && row[key] != null)
                        .Select(row => Convert.ToDouble(row[key], CultureInfo.InvariantCulture)).ToList();
                    summary[key] = values.Count == 0 ? null : new Dictionary<string, object> {
                        { "n", values.Count }, { "mean", values.Average() }, { "min", values.Min() }, { "max", values.Max() }
                    };
                }
                output.Add(summary);
            }
            return output;
        }

        private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        private static string Json(object value)
        {
            if (value == null) return "null";
            if (value is string)
            {
                var output = new StringBuilder("\"");
                foreach (char character in (string)value)
                {
                    if (character == '"' || character == '\\') output.Append('\\').Append(character);
                    else if (character == '\n') output.Append("\\n");
                    else if (character == '\r') output.Append("\\r");
                    else if (character == '\t') output.Append("\\t");
                    else if (character < 32) output.Append("\\u").Append(((int)character).ToString("x4"));
                    else output.Append(character);
                }
                return output.Append('"').ToString();
            }
            if (value is bool) return (bool)value ? "true" : "false";
            var dictionary = value as Dictionary<string, object>;
            if (dictionary != null) return "{" + string.Join(",", dictionary.Select(pair => Json(pair.Key) + ":" + Json(pair.Value))) + "}";
            var enumerable = value as IEnumerable;
            if (enumerable != null) return "[" + string.Join(",", enumerable.Cast<object>().Select(Json)) + "]";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
