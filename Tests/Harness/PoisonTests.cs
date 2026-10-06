using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
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
            mushroom.stackCount = 1;
            mushroom.Ingested(pawn, 0.05f);
            var condition = pawn.health.hediffSet.GetFirstHediffOfDef(Condition(id)) as Hediff_MushroomPoisoning;
            Check(check, mushroom.Destroyed && condition != null, "actual native ingestion consumes one " + id + " and creates poisoning");
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
                foreach (var id in new[] { "DestroyingAngel", "PoisonFireCoral" })
                {
                    WithClock(() => ValidateGraceAndDeath(id, check));
                    WithClock(() => ValidateTreatment(id, doctor, check));
                }
                WithClock(() => ValidateMedicineQuality(doctor, check));
                WithClock(() => ValidateRepeatedExposure(check));
                WithClock(() => ValidateFlyAgaricComa(check));
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
                Check(check, raw.ingestible.preferability == FoodPreferability.NeverForNutrition && !raw.IsNutritionGivingIngestible && !raw.IsDrug,
                    "harvested material is excluded from autonomous nutrition and drug taking " + id);
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
                    if (recipe != null) Check(check, !recipe.ingredients.Any(i => i.filter.Allows(raw)), "ordinary recipe excludes poison " + recipeName + " " + id);
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
                for (int i = 0; i < Ids.Length; i++)
                    fixtures.Add(GenSpawn.Spawn(Raw(Ids[i]), eater.Position + new IntVec3(i % 3, 0, 1 + i / 3), map));
                ThingDef selectedDef;
                foreach (bool desperate in new[] { false, true })
                {
                    var found = FoodUtility.BestFoodSourceOnMap(eater, eater, desperate, out selectedDef,
                        allowPlant: false, allowCorpse: false, allowDispenserFull: false, allowDispenserEmpty: false, forceScanWholeMap: true);
                    Check(check, found == null, "native map food search rejects all five poisons even with no safe food desperate=" + desperate);
                }
                var safe = GenSpawn.Spawn(ThingDefOf.MealSimple, eater.Position + new IntVec3(4, 0, 0), map);
                fixtures.Add(safe);
                var safeFound = FoodUtility.BestFoodSourceOnMap(eater, eater, true, out selectedDef,
                    allowPlant: false, allowCorpse: false, allowDispenserFull: false, allowDispenserEmpty: false, forceScanWholeMap: true);
                Check(check, safeFound == safe, "native search finds a safe ordinary meal while nearer poisons are ignored");
                foreach (var id in Ids) eater.inventory.innerContainer.TryAdd(ThingMaker.MakeThing(Raw(id)));
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

        private static void ValidateTreatment(string id, Pawn doctor, Action<bool, string> check)
        {
            var patient = Healthy();
            var condition = Eat(patient, id, check);
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
            int originalEnd = condition.RecoveryEndTick;
            for (int i = 0; i < 12; i++) Eat(patient, "SulfurTuft", check);
            Check(check, condition.ExposureCount == 4 && condition.PeakSeverity <= condition.Settings.maximumSeverity, "repeated doses are capped at four exposures and bounded severity");
            Advance(patient, condition.OnsetTick);
            Check(check, condition.RecoveryEndTick >= originalEnd && condition.RecoveryEndTick <= originalEnd + Day + 250,
                "repeated doses add at most one recovery day instead of unlimited stacked conditions");
        }

        private static void ValidateFlyAgaricComa(Action<bool, string> check)
        {
            var patient = Healthy();
            var condition = Eat(patient, "FlyAgaric", check);
            for (int dose = 1; dose < 4; dose++) Eat(patient, "FlyAgaric", check);
            Check(check, condition.ExposureCount == 4 && !condition.Settings.fatal && condition.PeakSeverity >= 0.85f,
                "four bounded fly agaric doses can reach the profound drowsiness stage");
            Advance(patient, condition.OnsetTick);
            int duration = condition.RecoveryEndTick - condition.OnsetTick;
            Advance(patient, condition.OnsetTick + duration / 4);
            float consciousness = patient.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness);
            Check(check, !patient.Dead && patient.Downed && consciousness <= 0.3f,
                "native health capacities produce temporary nonfatal unconsciousness after repeated fly agaric exposure level=" + consciousness);
            int deadline = condition.OnsetTick + duration + 500;
            while (Active(patient, condition) && !patient.Dead && Find.TickManager.TicksGame < deadline) Advance(patient, Find.TickManager.TicksGame + 250);
            Check(check, !patient.Dead && !patient.Downed && !Active(patient, condition),
                "native health tracker restores consciousness and removes recovered fly agaric poisoning");
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
                Check(check, Saved.Count == 5 && Saved.Values.Any(s => s.Phase == MushroomPoisonPhase.Latent.ToString()), "five species including initial latent poisoning prepared for save");
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
            Check(check, Saved.Count == 5, "expected poison save snapshots retained through native map reload");
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
    }
}
