using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using RimMushrooms;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMushroomsTests
{
    // These fixtures run only in the isolated game harness. Nutrition selection,
    // ingestion, tending, health updates and save/load use native game entry points.
    internal static class PoisonTests
    {
        private static readonly string[] Ids = { "FlyAgaric", "DestroyingAngel", "SulfurTuft", "Tsukiyotake", "PoisonFireCoral" };
        private static readonly string[] ExpansionIds = { "DeathCap", "YellowDapperling", "JackOLantern", "DeadlyWebcap", "PantherCap", "GhostFungus" };
        private static readonly string[] FatalIds = { "DestroyingAngel", "PoisonFireCoral", "DeathCap", "DeadlyWebcap" };
        private const int Hour = 2500, Day = 60000;
        private static readonly Dictionary<string, Snapshot> Saved = new Dictionary<string, Snapshot>();
        private static readonly HashSet<Pawn> TemporaryPawns = new HashSet<Pawn>();

        private sealed class Snapshot
        {
            public string PawnId, DefName, Source, Phase;
            public float Severity, Quality;
            public int TendLeft;
            public Dictionary<string, string> Fields;
        }

        private static ThingDef Raw(string id) => ThingDef.Named("RMush_Raw" + id);
        private static ThingDef Crop(string id) => ThingDef.Named("RMush_Plant" + id);
        private static HediffDef Condition(string id) => HediffDef.Named("RMush_Poison" + id);
        private static void Check(Action<bool, string> check, bool valid, string message) => check(valid, "poison: " + message);
        private static bool Active(Pawn pawn, Hediff_MushroomPoisoning condition) => pawn.health.hediffSet.hediffs.Contains(condition);

        private static Pawn Healthy()
        {
            var pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            foreach (var hediff in pawn.health.hediffSet.hediffs.ToList()) pawn.health.RemoveHediff(hediff);
            pawn.needs.food.CurLevelPercentage = 1f;
            pawn.needs.rest.CurLevelPercentage = 1f;
            pawn.inventory.innerContainer.ClearAndDestroyContents();
            TemporaryPawns.Add(pawn);
            return pawn;
        }

        private static Pawn CapableDoctor(Action<bool, string> check)
        {
            var medicalWork = DefDatabase<WorkTypeDef>.GetNamed("Doctor");
            for (int attempt = 0; attempt < 32; attempt++)
            {
                var doctor = Healthy();
                var skill = doctor.skills.GetSkill(SkillDefOf.Medicine);
                skill.Level = 20;
                skill.Notify_SkillDisablesChanged();
                skill.DirtyAptitudes();
                StatDefOf.MedicalTendQuality.Worker.ClearCacheForThing(doctor);
                float quality = doctor.GetStatValue(StatDefOf.MedicalTendQuality, cacheStaleAfterTicks: 0);
                if (!doctor.WorkTypeIsDisabled(medicalWork) && !skill.TotallyDisabled && skill.Level == 20 && quality >= 1f
                    && doctor.health.capacities.GetLevel(PawnCapacityDefOf.Sight) >= 0.99f
                    && doctor.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation) >= 0.99f)
                {
                    Check(check, quality > 0f, "controlled healthy doctor has enabled medical work, effective skill 20 and positive native tend quality=" + quality);
                    return doctor;
                }
                doctor.Destroy(DestroyMode.Vanish);
                TemporaryPawns.Remove(doctor);
            }
            throw new InvalidOperationException("Could not generate a healthy medically capable test physician.");
        }

        private static Hediff_MushroomPoisoning Eat(Pawn pawn, string id, Action<bool, string> check)
        {
            var mushroom = ThingMaker.MakeThing(Raw(id));
            bool psychedelic = id == "FlyAgaric" || id == "PantherCap";
            mushroom.stackCount = psychedelic ? 10 : 1;
            mushroom.Ingested(pawn, psychedelic ? 0.5f : 0.05f);
            var condition = pawn.health.hediffSet.GetFirstHediffOfDef(Condition(id)) as Hediff_MushroomPoisoning;
            Check(check, mushroom.Destroyed && condition != null, "actual native ingestion consumes one standard exposure of " + id + " and creates poisoning");
            Check(check, pawn.health.hediffSet.hediffs.Count(h => h.def == Condition(id)) == 1, "single condition after ingestion " + id);
            return condition;
        }

        private static void Advance(Pawn pawn, int targetTick)
        {
            while (Find.TickManager.TicksGame < targetTick && !pawn.Dead)
            {
                int delta = Math.Min(250, targetTick - Find.TickManager.TicksGame);
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + delta);
                pawn.health.HealthTickInterval(delta);
            }
        }

        private static void WithClock(Action action)
        {
            int original = Find.TickManager.TicksGame;
            try { action(); }
            finally { Find.TickManager.DebugSetTicksGame(original); }
        }

        private static float Tend(Pawn doctor, Pawn patient, Hediff_MushroomPoisoning condition, ThingDef medicineDef, int seed, Action<bool, string> check)
        {
            Check(check, condition.TendableNow() && patient.health.HasHediffsNeedingTend(), "native medical scanner requests care " + condition.def.defName);
            var medicine = (Medicine)ThingMaker.MakeThing(medicineDef);
            float before = condition.TreatmentReserve;
            Rand.PushState(seed);
            try { TendUtility.DoTend(doctor, patient, medicine); }
            finally { Rand.PopState(); }
            var duration = condition.TryGetComp<HediffComp_TendDuration>();
            Check(check, medicine.Destroyed && duration != null && duration.IsTended, "native DoTend consumes medicine and starts ordinary treatment " + condition.def.defName);
            Check(check, duration.tendQuality >= 0f && duration.tendQuality <= medicineDef.GetStatValueAbstract(StatDefOf.MedicalQualityMax), "actual randomized native tend quality respects medicine cap");
            Check(check, condition.TreatmentReserve > before && condition.TreatmentReserve <= condition.Settings.stabilizationTreatment * 2f,
                "ordinary tending increases bounded treatment reserve quality=" + duration.tendQuality.ToString("F3"));
            return duration.tendQuality;
        }

        public static void Run(Pawn farmer, Map map, Action<bool, string> check)
        {
            int originalTick = Find.TickManager.TicksGame;
            var originalLetters = new HashSet<Letter>(Find.LetterStack.LettersListForReading);
            var doctor = CapableDoctor(check);
            try
            {
                ValidateDefinitions(map, check);
                ValidateFoodSelection(map, check);
                ValidateExplicitIngestEligibility(map, check);
                foreach (var id in Ids) WithClock(() => ValidateCourse(id, doctor, check));
                foreach (var id in ExpansionIds) WithClock(() => ValidateCourse(id, doctor, check));
                foreach (var id in FatalIds)
                {
                    WithClock(() => ValidateGraceAndDeath(id, check));
                    WithClock(() => ValidateTreatment(id, doctor, check));
                }
                foreach (var id in new[] { "FlyAgaric", "SulfurTuft" })
                    WithClock(() => ValidateTreatment(id, doctor, check, 3f));
                WithClock(() => ValidateMedicineQuality(doctor, check));
                WithClock(() => ValidateRepeatedExposure(check));
                WithClock(() => ValidateConditionalFatality(check));
                WithClock(() => ValidateFractionalDose(check));
                WithClock(() => ValidateMixedCapacityGrace(check));
                WithClock(() => ValidatePoorCare(check));
            }
            finally
            {
                Find.TickManager.DebugSetTicksGame(originalTick);
                DestroyTemporaryPawns();
                foreach (var letter in Find.LetterStack.LettersListForReading.Where(l => !originalLetters.Contains(l)).ToList())
                    Find.LetterStack.RemoveLetter(letter);
            }
            PrepareSaved(farmer, map, check);
            Check(check, Find.TickManager.TicksGame == originalTick, "all simulation advances restore original global clock");
        }

        private static void ValidateDefinitions(Map map, Action<bool, string> check)
        {
            var selection = ThingDef.Named("RMush_PlantAssorted").GetModExtension<AssortedMushroomSettings>();
            foreach (var id in Ids)
            {
                var raw = Raw(id);
                var crop = Crop(id);
                var condition = Condition(id);
                Check(check, !raw.ConfigErrors().Any() && !crop.ConfigErrors().Any() && !condition.ConfigErrors().Any(), "resolved raw/plant/condition defs " + id);
                Check(check, condition.stages[0].vomitMtbDays <= 0f && (id == "FlyAgaric"
                    ? condition.stages.All(s => s.vomitMtbDays <= 0f) : condition.stages.Skip(1).Any(s => s.vomitMtbDays > 0f)),
                    "native vomiting stages are absent in latency and species-appropriate after symptoms " + id);
                Check(check, !raw.IsDrug && (id == "FlyAgaric" ? raw.IsNutritionGivingIngestible
                    : raw.ingestible.preferability == FoodPreferability.NeverForNutrition && !raw.IsNutritionGivingIngestible),
                    "fly agaric is normal food; other legacy toxins remain explicit-order resources " + id);
                Check(check, !crop.plant.Sowable && !crop.plant.humanFoodPlant && !crop.IsNutritionGivingIngestible && !selection.varieties.Contains(crop),
                    "wild-only poison excluded from sowing, assorted crop and direct plant food " + id);
                Check(check, crop.ingestible == null && !crop.IsIngestible,
                    "poisonous wild plant itself has no native ingestible object " + id);
                foreach (var grower in map.zoneManager.AllZones.OfType<Zone_Growing>().Cast<IPlantToGrowSettable>()
                    .Concat(map.listerThings.AllThings.OfType<Building_PlantGrower>().Cast<IPlantToGrowSettable>()))
                    Check(check, !PlantUtility.ValidPlantTypesForGrowers(new List<IPlantToGrowSettable> { grower }).Contains(crop),
                        "native crop selection excludes poisonous species " + id + " " + grower.GetType().Name);
                Check(check, crop.plant.harvestedThingDef == raw && crop.thingClass == typeof(Plant_Mushroom), "native harvesting points to correct poisonous material " + id);
                Check(check, DefDatabase<BiomeDef>.AllDefs.Any(b => b.AllWildPlants.Contains(crop)), "wild biome registration " + id);
                foreach (var recipeName in new[] { "CookMealSimple", "CookMealFine", "CookMealLavish", "MakePemmican", "MakeKibble" })
                {
                    var recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(recipeName);
                    if (recipe != null) Check(check, recipe.ingredients.Any(i => i.filter.Allows(raw)) == (id == "FlyAgaric"),
                        "ordinary recipe includes psychedelic fly agaric and excludes other toxins " + recipeName + " " + id);
                }
                var plant = (Plant)ThingMaker.MakeThing(crop);
                plant.Growth = 1f;
                float expectedYield = crop.plant.harvestYield * (crop.plant.harvestYieldAffectedByDifficulty ? Find.Storyteller.difficulty.cropYieldFactor : 1f);
                int actualYield = plant.YieldNow();
                Check(check, actualYield >= Mathf.FloorToInt(expectedYield) && actualYield <= Mathf.CeilToInt(expectedYield) && plant.Graphic != BaseContent.BadGraphic,
                    "mature native harvest yield and plant texture " + id);
                var randomGraphic = plant.Graphic as Graphic_Random;
                Check(check, randomGraphic != null && randomGraphic.SubGraphicsCount == 2,
                    "native random plant graphic loads both approved A/B variants " + id);
                Check(check, new[] { randomGraphic.SubGraphicAtIndex(0).MatSingle.mainTexture.name,
                    randomGraphic.SubGraphicAtIndex(1).MatSingle.mainTexture.name }.OrderBy(n => n)
                    .SequenceEqual(new[] { id + "A", id + "B" }), "native A/B plant texture identities " + id);
                var item = ThingMaker.MakeThing(raw);
                foreach (int count in new[] { 1, 25, 26, 50, 51, 75 })
                {
                    item.stackCount = count;
                    var expected = count <= 25 ? "01Low" : count <= 50 ? "02Medium" : "03Full";
                    Check(check, item.Graphic.MatSingleFor(item).mainTexture.name == expected, "poison material stack graphic " + id + " " + count);
                }
                item.Destroy();
                plant.Destroy();
            }
        }

        private static void ValidateFoodSelection(Map map, Action<bool, string> check)
        {
            var eater = Healthy();
            GenSpawn.Spawn(eater, map.Center + new IntVec3(-21, 0, -12), map);
            eater.needs.food.CurLevelPercentage = 0.01f;
            var fixtures = new List<Thing>();
            var restrictions = map.listerThings.AllThings.Where(t => t.def.IsNutritionGivingIngestible).ToDictionary(t => t, t => t.IsForbidden(eater));
            try
            {
                foreach (var entry in restrictions) entry.Key.SetForbidden(true, false);
                var excluded = Ids.Where(id => id != "FlyAgaric").ToArray();
                for (int i = 0; i < excluded.Length; i++)
                    fixtures.Add(GenSpawn.Spawn(Raw(excluded[i]), eater.Position + new IntVec3(i % 3, 0, 1 + i / 3), map));
                ThingDef selectedDef;
                foreach (bool desperate in new[] { false, true })
                {
                    var found = FoodUtility.BestFoodSourceOnMap(eater, eater, desperate, out selectedDef,
                        allowPlant: false, allowCorpse: false, allowDispenserFull: false, allowDispenserEmpty: false, forceScanWholeMap: true);
                    Check(check, found == null, "native map food search rejects nonpsychedelic poisons even with no safe food desperate=" + desperate);
                }
                var safe = GenSpawn.Spawn(ThingDefOf.MealSimple, eater.Position + new IntVec3(4, 0, 0), map);
                fixtures.Add(safe);
                var safeFound = FoodUtility.BestFoodSourceOnMap(eater, eater, true, out selectedDef,
                    allowPlant: false, allowCorpse: false, allowDispenserFull: false, allowDispenserEmpty: false, forceScanWholeMap: true);
                Check(check, safeFound == safe, "native search finds a safe ordinary meal while nearer poisons are ignored");
                foreach (var id in excluded) eater.inventory.innerContainer.TryAdd(ThingMaker.MakeThing(Raw(id)));
                Check(check, FoodUtility.BestFoodInInventory(eater) == null, "native inventory food search excludes poison nutrition");
            }
            finally
            {
                foreach (var thing in fixtures) if (!thing.Destroyed) thing.Destroy();
                foreach (var entry in restrictions) if (!entry.Key.Destroyed) entry.Key.SetForbidden(entry.Value, false);
                eater.Destroy();
            }
        }

        // Exercise the native eligibility/count/job checks used by the explicit
        // ingest menu. Rendering an actual UI option remains a visual check.
        private static void ValidateExplicitIngestEligibility(Map map, Action<bool, string> check)
        {
            var eater = Healthy();
            GenSpawn.Spawn(eater, map.Center + new IntVec3(-21, 0, -12), map);
            eater.needs.food.CurLevelPercentage = 0.01f;
            var fixtures = new List<Thing>();
            try
            {
                for (int i = 0; i < Ids.Length; i++)
                {
                    if (Ids[i] == "FlyAgaric") continue; // Psychedelic normal-food path is tested separately.
                    var poisonous = GenSpawn.Spawn(Raw(Ids[i]), eater.Position + new IntVec3(i % 3, 0, 1 + i / 3), map);
                    poisonous.stackCount = 75;
                    fixtures.Add(poisonous);
                    Check(check, poisonous.def.IsIngestible && poisonous.def.ingestible.showIngestFloatOption && poisonous.IngestibleNow
                        && eater.RaceProps.CanEverEat(poisonous.def) && eater.FoodIsSuitable(poisonous.def)
                        && eater.CanReach(poisonous, PathEndMode.OnCell, Danger.Deadly),
                        "native explicit-ingest eligibility checks pass " + poisonous.def.defName);
                    int amount = FoodUtility.GetMaxAmountToPickup(poisonous, eater,
                        FoodUtility.WillIngestStackCountOf(eater, poisonous.def, FoodUtility.NutritionForEater(eater, poisonous)));
                    Check(check, amount == 1, "native explicit-ingest count resolves to one despite zero nutrition " + poisonous.def.defName);
                    var ingest = JobMaker.MakeJob(JobDefOf.Ingest, poisonous);
                    ingest.count = amount;
                    eater.jobs.TryTakeOrderedJob(ingest, JobTag.Misc);
                    Check(check, eater.CurJobDef == JobDefOf.Ingest && eater.CurJob.targetA.Thing == poisonous && eater.CurJob.count == 1,
                        "native ordered ingest job accepts one poisonous mushroom from a full stack " + poisonous.def.defName);
                    eater.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                    eater.pather.StopDead();
                }
            }
            finally
            {
                foreach (var thing in fixtures) if (!thing.Destroyed) thing.Destroy();
                if (!eater.Destroyed) eater.Destroy();
                TemporaryPawns.Remove(eater);
            }
        }

        private static void ValidateCourse(string id, Pawn doctor, Action<bool, string> check)
        {
            var patient = Healthy();
            int start = Find.TickManager.TicksGame;
            var condition = Eat(patient, id, check);
            Check(check, condition.ExposureCount == 1 && condition.IsLatent && condition.Phase == MushroomPoisonPhase.Latent, "single dose starts in latent phase " + id);
            int latency = condition.OnsetTick - start;
            Check(check, latency >= condition.Settings.latencyHoursMin * Hour && latency <= condition.Settings.latencyHoursMax * Hour,
                "species-specific latency " + id + " ticks=" + latency);
            Advance(patient, condition.OnsetTick - 1);
            Check(check, condition.IsLatent && condition.FirstSymptomWarningTick == -1 && condition.Severity < 0.1f, "no clinical symptoms before latency boundary " + id);
            Advance(patient, condition.OnsetTick);
            Check(check, !condition.IsLatent && condition.FirstSymptomWarningTick == condition.OnsetTick && condition.Severity >= 0.1f,
                "native health update issues first symptom warning exactly at onset " + id);
            if (condition.Settings.fatal)
            {
                Check(check, condition.RecoveryEndTick < 0 && !condition.IsStabilized, "dangerous poison has no automatic recovery deadline before stabilization " + id);
                return;
            }
            int onset = condition.OnsetTick;
            int plannedDuration = condition.RecoveryEndTick - onset;
            Check(check, plannedDuration >= condition.Settings.recoveryDaysMin * Day && plannedDuration <= condition.Settings.recoveryDaysMax * Day + 1,
                "normal recovery target follows species range " + id + " ticks=" + plannedDuration);
            Advance(patient, onset + plannedDuration / 4);
            Check(check, Active(patient, condition) && condition.Severity >= 0.3f && !patient.Dead, "temporary clinical symptoms develop during actual course " + id);
            int deadline = onset + Mathf.CeilToInt(condition.Settings.recoveryDaysMax * Day) + 500;
            while (Active(patient, condition) && !patient.Dead && Find.TickManager.TicksGame < deadline) Advance(patient, Find.TickManager.TicksGame + 250);
            int elapsed = Find.TickManager.TicksGame - onset;
            Check(check, !Active(patient, condition) && !patient.Dead && elapsed >= condition.Settings.recoveryDaysMin * Day && elapsed <= condition.Settings.recoveryDaysMax * Day + 500,
                "native health tracker removes naturally recovered poison within target range " + id + " ticks=" + elapsed);
        }

        private static void ValidateGraceAndDeath(string id, Action<bool, string> check)
        {
            var patient = Healthy();
            var condition = Eat(patient, id, check);
            Advance(patient, condition.OnsetTick);
            int warning = condition.FirstSymptomWarningTick;
            // A deliberate severe fixture makes the grace test meaningful even
            // when the ordinary one-dose deterioration takes several days.
            condition.Severity = condition.Settings.maximumSeverity;
            Advance(patient, warning + 24 * Hour - 1);
            Check(check, !patient.Dead && !condition.CauseDeathNow(), "even severe one-dose poisoning cannot kill before 24 hours after warning " + id);
            Advance(patient, warning + 24 * Hour + 250);
            Check(check, patient.Dead, "native death occurs after grace when critical poison is untreated " + id);

            patient = Healthy();
            condition = Eat(patient, id, check);
            Advance(patient, condition.OnsetTick);
            warning = condition.FirstSymptomWarningTick;
            Advance(patient, warning + 8 * Day);
            Check(check, patient.Dead && Find.TickManager.TicksGame - warning >= 24 * Hour, "unmodified single ingestion becomes fatal when left untreated " + id);
        }

        private static void ValidateTreatment(string id, Pawn doctor, Action<bool, string> check, float doses = 1f)
        {
            var patient = Healthy();
            var condition = doses == 1f ? Eat(patient, id, check)
                : MushroomPoisoning.ApplyExposure(patient, Raw(id), Condition(id), doses);
            Advance(patient, condition.OnsetTick);
            int treatments = 0;
            while (!condition.IsStabilized && treatments < 10 && !patient.Dead)
            {
                Tend(doctor, patient, condition, ThingDefOf.MedicineIndustrial, 914 + treatments, check);
                treatments++;
                if (treatments == 1)
                {
                    Check(check, !condition.IsStabilized, "one ordinary treatment does not instantly cure or stabilize dangerous poison " + id);
                    float reserve = condition.TreatmentReserve;
                    var extra = (Medicine)ThingMaker.MakeThing(ThingDefOf.MedicineIndustrial);
                    TendUtility.DoTend(doctor, patient, extra);
                    Check(check, !extra.Destroyed && condition.TreatmentReserve == reserve, "native treatment cooldown blocks same-tick treatment spam " + id);
                    extra.Destroy();
                }
                if (!condition.IsStabilized)
                {
                    int expiry = Find.TickManager.TicksGame + 6 * Hour;
                    Advance(patient, expiry - 1);
                    Check(check, !condition.TendableNow(), "native repeat care is unavailable one tick before six-hour expiry " + id);
                    Advance(patient, expiry);
                    Check(check, condition.TendableNow(), "native repeat care becomes available at exact six-hour expiry " + id);
                }
            }
            Check(check, !patient.Dead && condition.IsStabilized && treatments >= 2, "early repeated ordinary medicine stabilizes dangerous poison " + id + " treatments=" + treatments);
            int stabilized = Find.TickManager.TicksGame;
            int recovery = condition.RecoveryEndTick - stabilized;
            Check(check, recovery >= condition.Settings.recoveryDaysMin * Day && recovery <= condition.Settings.recoveryDaysMax * Day + 1,
                "stabilized normal-severity recovery follows species range " + id);
            float severity = condition.Severity;
            Advance(patient, stabilized + Mathf.RoundToInt(condition.Settings.recoveryDaysMin * Day / 2));
            Check(check, condition.Phase == MushroomPoisonPhase.Recovering && condition.Severity < severity && !condition.CauseDeathNow(), "stabilized patient improves and leaves lethal progression " + id);
            int deadline = stabilized + Mathf.CeilToInt(condition.Settings.recoveryDaysMax * Day) + 500;
            while (Active(patient, condition) && !patient.Dead && Find.TickManager.TicksGame < deadline) Advance(patient, Find.TickManager.TicksGame + 250);
            Check(check, !patient.Dead && !Active(patient, condition), "native health tracker removes treated poison after recovery " + id);
        }

        private static void ValidateMedicineQuality(Pawn doctor, Action<bool, string> check)
        {
            var industrialPatient = Healthy();
            var herbalPatient = Healthy();
            var industrial = Eat(industrialPatient, "DestroyingAngel", check);
            var herbal = Eat(herbalPatient, "DestroyingAngel", check);
            Advance(industrialPatient, industrial.OnsetTick);
            herbalPatient.health.HealthTickInterval(250);
            float doctorStat = doctor.GetStatValue(StatDefOf.MedicalTendQuality, cacheStaleAfterTicks: 0);
            float standardBase = TendUtility.CalculateBaseTendQuality(doctor, industrialPatient, ThingDefOf.MedicineIndustrial);
            float herbalBase = TendUtility.CalculateBaseTendQuality(doctor, herbalPatient, ThingDefOf.MedicineHerbal);
            Check(check, doctorStat > 0f && standardBase > herbalBase && herbalBase > 0f,
                "same capable doctor gives positive native medicine-specific base quality industrial=" + standardBase + " herbal=" + herbalBase);
            float standardQuality = Tend(doctor, industrialPatient, industrial, ThingDefOf.MedicineIndustrial, 3371, check);
            float herbalQuality = Tend(doctor, herbalPatient, herbal, ThingDefOf.MedicineHerbal, 3371, check);
            Check(check, standardQuality > herbalQuality && industrial.TreatmentReserve > herbal.TreatmentReserve,
                "same doctor and native variance draw give ordinary medicine stronger support than herbal medicine");
            Check(check, !industrial.IsStabilized && !herbal.IsStabilized, "neither medicine is a single-use antidote");
            var medicineFreePatient = Healthy();
            var medicineFree = Eat(medicineFreePatient, "DestroyingAngel", check);
            Advance(medicineFreePatient, medicineFree.OnsetTick);
            float untreatedReserve = medicineFree.TreatmentReserve;
            Rand.PushState(3371);
            try { TendUtility.DoTend(doctor, medicineFreePatient, null); }
            finally { Rand.PopState(); }
            var duration = medicineFree.TryGetComp<HediffComp_TendDuration>();
            Check(check, duration.IsTended && medicineFree.TreatmentReserve > untreatedReserve && !medicineFree.IsStabilized,
                "native doctor care without medicine still helps through ordinary tending");
        }

        private static void ValidateRepeatedExposure(Action<bool, string> check)
        {
            var patient = Healthy();
            var condition = Eat(patient, "SulfurTuft", check);
            for (int i = 0; i < 12; i++) Eat(patient, "SulfurTuft", check);
            Check(check, condition.ExposureCount == 4 && condition.PeakSeverity <= condition.Settings.maximumSeverity, "repeated doses are capped at four exposures and bounded severity");
            Advance(patient, condition.OnsetTick);
            int extra = (int)typeof(Hediff_MushroomPoisoning).GetField("extraRecoveryTicks", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(condition);
            Check(check, condition.IsLifeThreatening && condition.RecoveryEndTick < 0 && extra <= Day,
                "repeated high exposure requires stabilization and recovery extension remains bounded by one day");
        }

        private static void ValidateConditionalFatality(Action<bool, string> check)
        {
            foreach (var id in new[] { "FlyAgaric", "SulfurTuft" })
            {
                var patient = Healthy();
                var condition = Eat(patient, id, check);
                Check(check, !condition.IsLifeThreatening && condition.ExposureUnits == 1f && condition.RecoveryEndTick > 0,
                    "ordinary exposure remains naturally recoverable " + id);
                int end = condition.RecoveryEndTick + 500;
                Advance(patient, end);
                Check(check, !patient.Dead && !Active(patient, condition), "ordinary untreated dose recovers without direct fatality " + id);

                patient = Healthy();
                condition = MushroomPoisoning.ApplyExposure(patient, Raw(id), Condition(id), 3f);
                Check(check, condition.IsLifeThreatening && condition.ExposureUnits == 3f && condition.RecoveryEndTick < 0,
                    "high exposure enters conditionally fatal course " + id);
                Advance(patient, condition.OnsetTick);
                int warning = condition.FirstSymptomWarningTick;
                condition.Severity = 1f;
                Advance(patient, warning + 24 * Hour - 1);
                Check(check, !patient.Dead && !condition.CauseDeathNow(), "high-dose conditional poison respects warning grace " + id);
                Advance(patient, warning + 24 * Hour + 250);
                Check(check, patient.Dead, "untreated critical conditional poison can be fatal after grace " + id);

                patient = Healthy();
                condition = MushroomPoisoning.ApplyExposure(patient, Raw(id), Condition(id), 3f);
                Advance(patient, condition.OnsetTick);
                warning = condition.FirstSymptomWarningTick;
                Advance(patient, warning + 6 * Day);
                Check(check, patient.Dead && Find.TickManager.TicksGame - warning >= 24 * Hour,
                    "unmodified high exposure becomes fatal when neglected " + id);
            }
            var pantherPatient = Healthy();
            var panther = MushroomPoisoning.ApplyExposure(pantherPatient, Raw("PantherCap"), Condition("PantherCap"), 4f);
            Check(check, !panther.IsLifeThreatening && panther.PeakSeverity >= 0.85f,
                "panther cap has severe neural symptoms but no unsupported direct fatal course");
            Advance(pantherPatient, panther.OnsetTick + (panther.RecoveryEndTick - panther.OnsetTick) / 4);
            float consciousness = pantherPatient.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness);
            Check(check, !pantherPatient.Dead && pantherPatient.Downed && consciousness > 0f && consciousness <= 0.3f,
                "severe panther poisoning causes native temporary unconsciousness with a positive capacity floor");
            Advance(pantherPatient, panther.RecoveryEndTick + 500);
            Check(check, !pantherPatient.Dead && !Active(pantherPatient, panther), "untreated nonfatal panther course recovers");
        }

        private static void ValidateFractionalDose(Action<bool, string> check)
        {
            var patient = Healthy();
            var condition = MushroomPoisoning.ApplyExposure(patient, Raw("DeathCap"), Condition("DeathCap"), 0.125f);
            Check(check, condition.ExposureUnits == 0.125f && !condition.IsLifeThreatening && condition.PeakSeverity < 0.1f,
                "fractional ingredient dose is preserved instead of rounded to a full fatal exposure");
            condition = MushroomPoisoning.ApplyExposure(patient, Raw("DeathCap"), Condition("DeathCap"), 0.125f);
            Check(check, condition.ExposureUnits == 0.25f && condition.IsLifeThreatening
                && patient.health.hediffSet.hediffs.Count(h => h.def == Condition("DeathCap")) == 1,
                "fractional repeated doses accumulate into one condition and can reach effective fatal exposure");
            var before = condition.ExposureUnits;
            Check(check, MushroomPoisoning.ApplyExposure(patient, Raw("DeathCap"), Condition("DeathCap"), float.NaN) == null
                && MushroomPoisoning.ApplyExposure(patient, Raw("DeathCap"), Condition("DeathCap"), -1f) == null
                && condition.ExposureUnits == before, "invalid exposure values are rejected without changing the course");
            var legacy = Healthy();
            var old = MushroomPoisoning.ApplyExposure(legacy, Raw("DestroyingAngel"), Condition("DestroyingAngel"), 1f);
            typeof(Hediff_MushroomPoisoning).GetField("exposureUnits", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(old, -1f);
            Check(check, old.ExposureUnits == 1f && old.IsLifeThreatening,
                "v0.4 integer-only exposure fallback retains the original dangerous dose");
        }

        private static void ValidateMixedCapacityGrace(Action<bool, string> check)
        {
            var patient = Healthy();
            int now = Find.TickManager.TicksGame;
            foreach (var id in FatalIds)
            {
                var condition = MushroomPoisoning.ApplyExposure(patient, Raw(id), Condition(id), 1f);
                Check(check, condition != null && !patient.Dead,
                    "mixed fixture remains alive while adding each poisoning " + id);
                typeof(Hediff_MushroomPoisoning).GetField("onsetTick", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(condition, now);
                condition.Severity = 0.9f;
                Check(check, !patient.Dead && patient.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness) > 0f,
                    "critical stage assignment cannot bypass warning grace through native capacity death " + id);
            }
            Advance(patient, now + 250);
            Check(check, !patient.Dead && patient.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness) >= 0.19f,
                "mixed mushroom conditions cannot stack consciousness offsets to lethal zero before grace");
            MushroomPsychoactive.Apply(patient, Raw("FlyAgaric"), 1f);
            Check(check, !patient.Dead && patient.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness) >= 0.1f,
                "mixed critical poisoning plus psychedelic consciousness loss retains a positive native capacity");
            Advance(patient, now + 24 * Hour);
            Check(check, !patient.Dead, "multiple critical mushroom poisons preserve the first-warning response window");
        }

        private static void ValidatePoorCare(Action<bool, string> check)
        {
            var patient = Healthy();
            var condition = Eat(patient, "DeathCap", check);
            Advance(patient, condition.OnsetTick);
            condition.Tended(0.05f, 1f);
            Check(check, !condition.IsStabilized && condition.TreatmentReserve < 0.3f,
                "one poor treatment cannot stabilize dangerous poison");
            Advance(patient, condition.FirstSymptomWarningTick + 6 * Day);
            Check(check, patient.Dead, "insufficient low-quality care followed by neglect does not prevent fatal worsening");
        }

        private static Dictionary<string, string> CustomFields(Hediff_MushroomPoisoning condition)
        {
            return typeof(Hediff_MushroomPoisoning).GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(f => f.FieldType == typeof(int) || f.FieldType == typeof(float) || f.FieldType == typeof(bool))
                .ToDictionary(f => f.Name, f => Convert.ToString(f.GetValue(condition), CultureInfo.InvariantCulture));
        }

        public static void PrepareSaved(Pawn farmer, Map map, Action<bool, string> check)
        {
            Saved.Clear();
            int original = Find.TickManager.TicksGame;
            var doctor = CapableDoctor(check);
            try
            {
                // Start an isolated fixture in the past and finish by the actual
                // save clock. No future-dated progress fields are left behind.
                Find.TickManager.DebugSetTicksGame(Math.Max(0, original - Day));
                var coralPatient = Healthy();
                var coral = Eat(coralPatient, "PoisonFireCoral", check);
                for (int count = 0; count < 6 && !coral.IsStabilized && !coralPatient.Dead; count++)
                {
                    Tend(doctor, coralPatient, coral, ThingDefOf.MedicineIndustrial, 619 + count, check);
                    int next = Find.TickManager.TicksGame + 6 * Hour + 250;
                    if (next > original) break;
                    Advance(coralPatient, next);
                }
                Advance(coralPatient, original);
                Check(check, !coralPatient.Dead && Active(coralPatient, coral), "live treated dangerous poison prepared for round-trip save");
                GenSpawn.Spawn(coralPatient, map.Center + new IntVec3(-22, 0, -14), map);
                Capture(coralPatient, coral);

                Find.TickManager.DebugSetTicksGame(Math.Max(0, original - Hour));
                var symptomaticPatient = Healthy();
                var symptomatic = Eat(symptomaticPatient, "Tsukiyotake", check);
                Advance(symptomaticPatient, original);
                Check(check, symptomatic.FirstSymptomWarningTick >= 0 && Active(symptomaticPatient, symptomatic), "symptom warning and recovery deadline prepared for save");
                GenSpawn.Spawn(symptomaticPatient, map.Center + new IntVec3(-21, 0, -14), map);
                Capture(symptomaticPatient, symptomatic);

                Find.TickManager.DebugSetTicksGame(original);
                foreach (var id in new[] { "FlyAgaric", "DestroyingAngel", "SulfurTuft" })
                {
                    var patient = Healthy();
                    var condition = Eat(patient, id, check);
                    if (id == "DestroyingAngel") Tend(doctor, patient, condition, ThingDefOf.MedicineHerbal, 1013, check);
                    if (id == "SulfurTuft") Eat(patient, id, check);
                    int index = Saved.Count;
                    GenSpawn.Spawn(patient, map.Center + new IntVec3(-22 + index, 0, -14), map);
                    Capture(patient, condition);
                }
                var fractionalPatient = Healthy();
                var fractional = MushroomPoisoning.ApplyExposure(fractionalPatient, Raw("GhostFungus"), Condition("GhostFungus"), 0.125f);
                GenSpawn.Spawn(fractionalPatient, map.Center + new IntVec3(-16, 0, -14), map);
                Capture(fractionalPatient, fractional);
                Check(check, Saved.Count == 6 && Saved.Values.Any(s => s.Phase == MushroomPoisonPhase.Latent.ToString()),
                    "legacy and new species including fractional latent exposure prepared for save");
            }
            finally
            {
                Find.TickManager.DebugSetTicksGame(original);
                DestroyTemporaryPawns();
            }
        }

        private static void Capture(Pawn patient, Hediff_MushroomPoisoning condition)
        {
            var tend = condition.TryGetComp<HediffComp_TendDuration>();
            Saved.Add(condition.def.defName, new Snapshot {
                PawnId = patient.GetUniqueLoadID(), DefName = condition.def.defName, Source = condition.sourceDef.defName,
                Phase = condition.Phase.ToString(), Severity = condition.Severity, Quality = tend.tendQuality,
                TendLeft = tend.tendTicksLeft, Fields = CustomFields(condition)
            });
            TemporaryPawns.Remove(patient);
        }

        private static void DestroyTemporaryPawns()
        {
            foreach (var pawn in TemporaryPawns.ToList())
                if (!pawn.Destroyed) pawn.Destroy(DestroyMode.Vanish);
            TemporaryPawns.Clear();
        }

        public static void VerifyLoaded(Map map, Action<bool, string> check)
        {
            Check(check, Saved.Count == 6, "expected poison save snapshots retained through native map reload");
            foreach (var saved in Saved.Values)
            {
                var patient = map.mapPawns.AllPawnsSpawned.Single(p => p.GetUniqueLoadID() == saved.PawnId);
                var condition = patient.health.hediffSet.GetFirstHediffOfDef(HediffDef.Named(saved.DefName)) as Hediff_MushroomPoisoning;
                Check(check, condition != null && condition.sourceDef.defName == saved.Source && condition.Severity == saved.Severity && condition.Phase.ToString() == saved.Phase,
                    "native save restores poison class/source/severity/phase " + saved.DefName);
                var actual = CustomFields(condition);
                foreach (var field in saved.Fields) Check(check, actual.ContainsKey(field.Key) && actual[field.Key] == field.Value, "saved poison progression field " + saved.DefName + " " + field.Key);
                var tend = condition.TryGetComp<HediffComp_TendDuration>();
                Check(check, tend.tendQuality == saved.Quality && tend.tendTicksLeft == saved.TendLeft, "native tending comp survives poison save " + saved.DefName);
                WithClock(() => {
                    if (condition.IsLatent) Advance(patient, condition.OnsetTick);
                    else Advance(patient, Find.TickManager.TicksGame + 500);
                    Check(check, !patient.Dead && condition.FirstSymptomWarningTick >= 0, "loaded poison continues through native health ticks " + saved.DefName);
                });
            }
        }

        public static void VerifyLegacy(Map map, Action<bool, string> check)
        {
            string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "Saves", "MoreMushrooms-LegacyFixture.rws");
            Check(check, File.Exists(path), "actual legacy save XML is available for poison compatibility comparison");
            var document = new XmlDocument { XmlResolver = null };
            document.Load(path);
            var primitiveFields = typeof(Hediff_MushroomPoisoning)
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(field => field.FieldType == typeof(int) || field.FieldType == typeof(float) || field.FieldType == typeof(bool))
                .ToList();
            int verified = 0, upgradedHighDose = 0;
            foreach (XmlNode savedPawn in document.SelectNodes("//thing[healthTracker/hediffSet/hediffs/li[starts-with(def, 'RMush_Poison')]]"))
            {
                string pawnId = savedPawn.SelectSingleNode("id")?.InnerText;
                var patient = map.mapPawns.AllPawnsSpawned.SingleOrDefault(pawn => pawn.ThingID == pawnId);
                Check(check, patient != null && !patient.Dead, "actual legacy poisoned pawn ID survives load " + pawnId);
                foreach (XmlNode saved in savedPawn.SelectNodes("healthTracker/hediffSet/hediffs/li[starts-with(def, 'RMush_Poison')]"))
                {
                    string defName = saved.SelectSingleNode("def").InnerText;
                    string hediffId = "Hediff_" + saved.SelectSingleNode("loadID").InnerText;
                    var condition = patient.health.hediffSet.hediffs.SingleOrDefault(hediff => hediff.GetUniqueLoadID() == hediffId)
                        as Hediff_MushroomPoisoning;
                    Check(check, condition != null && condition.def.defName == defName
                        && condition.GetType().FullName == saved.Attributes["Class"]?.Value,
                        "actual legacy poison ID, Def and class are preserved " + pawnId + " " + defName);
                    Check(check, condition.sourceDef?.defName == saved.SelectSingleNode("source")?.InnerText
                        && condition.sourceLabel == saved.SelectSingleNode("sourceLabel")?.InnerText,
                        "actual legacy poison source identity and label are preserved " + defName);
                    Check(check, LegacyFloatEqual(condition.Severity, saved.SelectSingleNode("severity")?.InnerText ?? "0"),
                        "actual legacy poison severity is unchanged before simulation " + defName);
                    int exposures = int.Parse(saved.SelectSingleNode("mushroomExposureCount")?.InnerText ?? "0", CultureInfo.InvariantCulture);
                    bool oldDoseFormat = saved.SelectSingleNode("mushroomExposureUnits") == null;
                    float dose = oldDoseFormat ? Mathf.Clamp(exposures, 1, 4)
                        : float.Parse(saved.SelectSingleNode("mushroomExposureUnits").InnerText, CultureInfo.InvariantCulture);
                    Check(check, condition.ExposureCount == exposures && Mathf.Abs(condition.ExposureUnits - dose) < 0.00001f,
                        "actual integer-only legacy exposure migrates to equal fractional units " + defName + " dose=" + dose);
                    bool oldOnsetAnnounced = bool.Parse(saved.SelectSingleNode("mushroomOnsetAnnounced")?.InnerText ?? "false");
                    bool oldStabilized = bool.Parse(saved.SelectSingleNode("mushroomStabilized")?.InnerText ?? "false");
                    bool freshWarning = oldDoseFormat && condition.Settings.conditionalFatalThreshold > 0f
                        && condition.IsLifeThreatening && oldOnsetAnnounced && !oldStabilized;
                    foreach (var field in primitiveFields)
                    {
                        if (field.Name == "exposureUnits") continue; // The deliberate v0.4 migration was checked above.
                        if (freshWarning && (field.Name == "firstSymptomWarningTick" || field.Name == "recoveryEndTick"
                            || field.Name == "recoveryProgressTicks")) continue;
                        string tag = "mushroom" + char.ToUpperInvariant(field.Name[0]) + field.Name.Substring(1);
                        string defaultValue = field.FieldType == typeof(bool) ? "false" : field.Name.EndsWith("Tick") ? "-1" : "0";
                        string expected = saved.SelectSingleNode(tag)?.InnerText ?? defaultValue;
                        object actual = field.GetValue(condition);
                        bool matches = field.FieldType == typeof(float)
                            ? LegacyFloatEqual((float)actual, expected)
                            : field.FieldType == typeof(bool) ? (bool)actual == bool.Parse(expected)
                            : (int)actual == int.Parse(expected, CultureInfo.InvariantCulture);
                        Check(check, matches, "actual legacy poison progression field preserved " + defName + " " + field.Name);
                    }
                    var tend = condition.TryGetComp<HediffComp_TendDuration>();
                    Check(check, tend != null
                        && tend.tendTicksLeft == int.Parse(saved.SelectSingleNode("tendTicksLeft")?.InnerText ?? "-1", CultureInfo.InvariantCulture)
                        && LegacyFloatEqual(tend.tendQuality, saved.SelectSingleNode("tendQuality")?.InnerText ?? "0")
                        && LegacyFloatEqual((float)typeof(HediffComp_TendDuration).GetField("totalTendQuality",
                            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(tend), saved.SelectSingleNode("totalTendQuality")?.InnerText ?? "0")
                        && condition.IsStabilized == oldStabilized,
                        "actual legacy tending timer, quality and stabilization remain intact " + defName);
                    if (freshWarning)
                    {
                        Check(check, condition.FirstSymptomWarningTick == Find.TickManager.TicksGame
                            && condition.RecoveryEndTick < 0 && !condition.CauseDeathNow()
                            && condition.Settings.responseGraceHours >= 24f,
                            "old high-dose nonfatal poison gets a fresh 24-hour warning window instead of load-time death " + defName);
                        upgradedHighDose++;
                    }
                    verified++;
                }
            }
            Check(check, true, "actual legacy poison XML comparisons=" + verified + "; high-dose warning migrations=" + upgradedHighDose
                + (verified == 0 ? " (this older fixture contains no mushroom poison conditions)" : "")
                + (verified > 0 && upgradedHighDose == 0 ? " (original fixture contains no symptomatic high-dose conditional case)" : ""));
        }

        private static bool LegacyFloatEqual(float actual, string expected)
        {
            float parsed = float.Parse(expected, CultureInfo.InvariantCulture);
            return Mathf.Abs(actual - parsed) <= 0.000001f * Mathf.Max(1f, Mathf.Abs(parsed));
        }
    }
}
