using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using RimMushrooms;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMushroomsTests
{
    // A separate, opt-in component. Never reads a user's ModSettings or API keys.
    // Launcher must supply a disposable profile/map and the original requested IDs.
    public sealed class FieldCompatibilityTests : GameComponent
    {
        private readonly List<string> checks = new List<string>();
        private readonly List<string> loadedMods = new List<string>();
        private readonly List<string> expectedIds = new List<string>();
        private readonly List<string> missingIds = new List<string>();
        private readonly List<string> patches = new List<string>();
        private readonly List<string> observations = new List<string>();
        private readonly List<string> limitations = new List<string>();
        private readonly HashSet<string> existingProductIds = new HashSet<string>();
        private readonly string[] cookingSources = { "RMush_RawCubensis", "RawPotatoes", "RMush_RawMatsutake" };
        private readonly List<Thing>[] cooked = { new List<Thing>(), new List<Thing>(), new List<Thing>() };
        private Map map;
        private Pawn worker, diner;
        private Plant plant;
        private Building_WorkTable stove;
        private Bill_Production bill;
        private IntVec3 origin, harvestCell;
        private ThingDef harvestRaw, recipeProduct;
        // Jobs return to a native object pool after completion. Identity must be
        // captured as values; retaining a Job reference can follow a later job.
        private int orderedJobId = -1;
        private JobDef orderedJobDef;
        private Thing ingestionPortion;
        private float selectedUnits, selectedIngredientCount, ingestedUnits, deadline;
        private int phase, round, frames, phaseFrames, phaseStartTick, harvestBefore;
        private bool initialized, finished;
        private string status = "RUNNING", failure = "";
        private string Profile => GenFilePaths.SaveDataFolderPath;
        private string Report => Path.Combine(Profile, "compatibility-report.txt");
        private string Result => Path.Combine(Profile, "compatibility-result.json");

        public FieldCompatibilityTests(Game game) { }

        public override void GameComponentUpdate()
        {
            if (finished || !GenCommandLine.CommandLineArgPassed("mushroomCompatibility")
                || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting) return;
            if (Find.CurrentMap == null) return;
            try
            {
                if (!initialized) { Initialize(Find.CurrentMap); initialized = true; StartHarvest(); return; }
                frames++; phaseFrames++;
                foreach (var window in Find.WindowStack.Windows.ToList()) window.Close(false);
                Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
                RequireAvailable(worker, "worker");
                if (phase == 3 || phase == 4) RequireAvailable(diner, "diner");
                if (Time.realtimeSinceStartup > deadline || phaseFrames > 24000
                    || Find.TickManager.TicksGame - phaseStartTick > 30000)
                    throw new TimeoutException("Native job timeout: " + Diagnostic());
                if (phaseFrames % 600 == 0) Trace("PROGRESS " + Diagnostic());
                if (phase == 1) PollHarvest();
                else if (phase == 2) PollCooking();
                else if (phase == 3) PollPsychoactiveIngestion();
                else if (phase == 4) PollFlavorIngestion();
            }
            catch (Exception exception)
            {
                failure = exception.ToString() + "\n" + Diagnostic();
                Finish(false);
            }
        }

        private void Initialize(Map testMap)
        {
            map = testMap;
            Directory.CreateDirectory(Profile);
            File.WriteAllText(Report, "More Mushrooms isolated field compatibility\n"
                + VersionControl.CurrentVersionStringWithRev + "\nProfile: " + Profile + "\n");
            limitations.Add("Representative native harvest/cooking/ingestion cases only; not an absolute compatibility guarantee for all gameplay or all mod combinations.");
            limitations.Add("Uses an isolated copied active-mod list with default settings for other mods; user ModSettings and external-service/API configuration are not copied or read.");
            limitations.Add("Controlled fresh adult human fixtures, cleared soil, clear weather and disabled storyteller; not combat, alien-race, production UI or long-term ecology validation.");
            limitations.Add("Live stats and recipe outputs are recorded rather than assumed equal to vanilla; Core and user-mod reports must be compared separately.");
            limitations.Add("No save/reload or nutrient-paste-dispenser scenario in this component; those require separate suites.");
            RecordModEnvironment();
            RecordHarmonyOwners();
            foreach (var window in Find.WindowStack.Windows.ToList()) window.Close(false);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            DebugSettings.enableStoryteller = false;
            map.Biome.constantOutdoorTemperature = 21f;
            map.weatherManager.TransitionTo(WeatherDefOf.Clear);
            origin = map.Center + new IntVec3(24, 0, 18);
            if (!CellRect.CenteredOn(origin, 9).FullyContainedWithin(new CellRect(0, 0, map.Size.x, map.Size.z)))
                origin = map.Center;
            int cleared = 0;
            foreach (var cell in CellRect.CenteredOn(origin, 8))
            {
                foreach (var thing in cell.GetThingList(map).ToList())
                    if (!(thing is Pawn))
                    {
                        if (thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);
                        else if (thing.Spawned) thing.DeSpawn();
                        cleared++;
                    }
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
                map.roofGrid.SetRoof(cell, null);
                map.snowGrid.SetDepth(cell, 0f);
            }
            foreach (var ambient in map.mapPawns.AllPawnsSpawned.Where(p => p.Faction != Faction.OfPlayer
                && (p.RaceProps.Animal || p.HostileTo(Faction.OfPlayer))).ToList()) ambient.DeSpawn();
            foreach (var colonist in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                colonist.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                if (colonist.workSettings != null)
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefs) colonist.workSettings.SetPriority(work, 0);
            }
            Observe("fixture", "clearedThings=" + cleared + ";origin=" + origin + ";temperature=" + origin.GetTemperature(map));
            worker = PreparePawn(origin + IntVec3.West, false);
            worker.skills.GetSkill(SkillDefOf.Plants).Level = 20;
            worker.skills.GetSkill(SkillDefOf.Cooking).Level = 20;
            Observe("workerStats", "PlantHarvestYield=" + Number(worker.GetStatValue(StatDefOf.PlantHarvestYield))
                + ";PlantWorkSpeed=" + Number(worker.GetStatValue(StatDefOf.PlantWorkSpeed))
                + ";CookingSpeed=" + Number(worker.GetStatValue(DefDatabase<StatDef>.GetNamed("CookSpeed"))));
            var stoveDef = ThingDef.Named("FueledStove");
            stove = ThingMaker.MakeThing(stoveDef, stoveDef.MadeFromStuff ? ThingDefOf.Steel : null) as Building_WorkTable;
            Check(stove != null, "fixture cooking table class", stoveDef.thingClass.FullName);
            stove.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(stove, origin + new IntVec3(3, 0, 0), map, Rot4.North);
            var fuel = stove.TryGetComp<CompRefuelable>();
            if (fuel != null) fuel.Refuel(fuel.Props.fuelCapacity);
            Check(stove.InteractionCell.Standable(map), "native stove interaction cell standable", stove.InteractionCell.ToString());
            WriteResult();
        }

        private Pawn PreparePawn(IntVec3 cell, bool hungry)
        {
            var request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                forceGenerateNewPawn: true, allowDead: false, allowDowned: false,
                canGeneratePawnRelations: false, allowAddictions: false, fixedBiologicalAge: 28f,
                forceNoIdeo: true, forceNoBackstory: true, forbidAnyTitle: true);
            var pawn = PawnGenerator.GeneratePawn(request);
            foreach (var condition in pawn.health.hediffSet.hediffs.ToList()) pawn.health.RemoveHediff(condition);
            Check(pawn.needs?.food != null && pawn.needs.rest != null && pawn.needs.mood != null
                && !pawn.Dead && !pawn.Downed, "healthy human fixture prepared before jobs", pawn.ThingID);
            pawn.needs.food.CurLevelPercentage = hungry ? 0.20f : 1f;
            pawn.needs.rest.CurLevelPercentage = 1f;
            pawn.needs.mood.CurLevelPercentage = 1f;
            GenSpawn.Spawn(pawn, cell, map);
            if (pawn.workSettings != null)
                foreach (var work in DefDatabase<WorkTypeDef>.AllDefs) pawn.workSettings.SetPriority(work, 0);
            return pawn;
        }

        private void StartHarvest()
        {
            harvestCell = origin + new IntVec3(-3, 0, 0);
            var plantDef = ThingDef.Named("RMush_PlantButton");
            var zone = new Zone_Growing(map.zoneManager);
            map.zoneManager.RegisterZone(zone); zone.AddCell(harvestCell); zone.SetPlantDefToGrow(plantDef);
            plant = (Plant)GenSpawn.Spawn(plantDef, harvestCell, map);
            plant.sown = true; plant.Growth = 1f;
            harvestRaw = plantDef.plant.harvestedThingDef;
            harvestBefore = RawCount(harvestRaw);
            Observe("plantStats", "def=" + plantDef.defName + ";growth=" + plant.Growth
                + ";growDays=" + plantDef.plant.growDays + ";harvestYield=" + plantDef.plant.harvestYield
                + ";harvestFailable=" + plantDef.plant.harvestFailable);
            var harvester = (WorkGiver_GrowerHarvest)DefDatabase<WorkGiverDef>.GetNamed("GrowerHarvest").Worker;
            Check(harvester.HasJobOnCell(worker, harvestCell, true), "native grower recognizes mature planted mushroom", harvestCell.ToString());
            var job = harvester.JobOnCell(worker, harvestCell, true);
            Check(job != null && job.def == JobDefOf.Harvest, "native harvest job generated", job?.ToString() ?? "null");
            StartJob(worker, job, 1, "harvest");
        }

        private void PollHarvest()
        {
            if (plant != null && plant.Spawned && plant.Growth >= 0.99f) return;
            if (OrderedJobStillRunning(worker)) return;
            int count = RawCount(harvestRaw);
            Check(count > harvestBefore, "actual ordered harvest produced mushrooms", "before=" + harvestBefore + ";after=" + count);
            round = 0; StartCooking();
        }

        private void StartCooking()
        {
            worker.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            stove.billStack.Clear();
            foreach (var t in map.listerThings.AllThings.Where(t => t.Position.DistanceTo(origin) <= 9
                && t.def.ingestible != null && t.def != harvestRaw).ToList())
                if (!cooked.Any(group => group.Contains(t))) t.Destroy(DestroyMode.Vanish);
            var source = ThingDef.Named(cookingSources[round]);
            var recipe = DefDatabase<RecipeDef>.GetNamed("CookMealSimpleBulk");
            Check(stove.def.AllRecipes.Contains(recipe), "native stove accepts bulk simple-meal recipe", recipe.defName);
            var newBill = recipe.MakeNewBill();
            var psychoIds = new[] { "RMush_RawLibertyCap", "RMush_RawCubensis", "RMush_RawFlyAgaric", "RMush_RawPantherCap" };
            Check(psychoIds.All(id => newBill.ingredientFilter.Allows(ThingDef.Named(id))),
                "new native cooking bill permits four psychoactive ingredients", string.Join(",", psychoIds));
            Observe("defaultBillFilterRound" + round, string.Join(";", psychoIds.Select(id => id + "=" + newBill.ingredientFilter.Allows(ThingDef.Named(id)))));
            bill = (Bill_Production)newBill;
            bill.ingredientFilter.SetDisallowAll(); bill.ingredientFilter.SetAllow(source, true);
            bill.ingredientSearchRadius = 12f;
            bill.repeatMode = BillRepeatModeDefOf.RepeatCount; bill.repeatCount = 1;
            bill.SetPawnRestriction(worker); bill.SetStoreMode(BillStoreModeDefOf.DropOnFloor);
            stove.billStack.AddBill(bill);
            for (int i = 0; i < 3; i++)
            {
                var ingredient = ThingMaker.MakeThing(source);
                ingredient.stackCount = Math.Min(75, source.stackLimit);
                GenSpawn.Spawn(ingredient, origin + new IntVec3(1, 0, 2 + i), map);
            }
            var giver = (WorkGiver_DoBill)DefDatabase<WorkGiverDef>.GetNamed("DoBillsCook").Worker;
            var job = giver.JobOnThing(worker, stove, true);
            Check(job != null && job.def == JobDefOf.DoBill && job.bill == bill,
                "native DoBill job selected for round " + round, job?.ToString() ?? "null;reason=" + JobFailReason.Reason);
            Check(job.targetQueueB != null && job.countQueue != null && job.targetQueueB.Count == job.countQueue.Count,
                "native DoBill has explicit ingredient quantities", "round=" + round);
            selectedIngredientCount = 0f;
            for (int i = 0; i < job.targetQueueB.Count; i++)
            {
                var ingredient = job.targetQueueB[i].Thing;
                Check(ingredient != null && ingredient.def == source, "native bill selected requested ingredient", ingredient?.def.defName ?? "null");
                selectedIngredientCount += job.countQueue[i];
            }
            selectedUnits = selectedIngredientCount / Mathf.Max(0.01f, source.GetModExtension<MushroomExposureProperties>()?.doseUnitCount ?? 1f);
            recipeProduct = recipe.products.First().thingDef;
            existingProductIds.Clear();
            foreach (var t in AllProducts()) existingProductIds.Add(t.ThingID);
            Observe("recipeRound" + round, "source=" + source.defName + ";ingredientCount=" + selectedIngredientCount
                + ";rawNutrition=" + Number(source.GetStatValueAbstract(StatDefOf.Nutrition))
                + ";recipe=" + recipe.defName + ";declaredOutput=" + string.Join(";", recipe.products.Select(p => p.thingDef.defName + ":" + p.count)));
            StartJob(worker, job, 2, "cooking round " + round);
        }

        private void PollCooking()
        {
            if (OrderedJobStillRunning(worker)) return;
            var products = AllProducts().Where(t => !existingProductIds.Contains(t.ThingID)).ToList();
            if (products.Count == 0) throw new InvalidOperationException("DoBill ended without a new native meal: " + Diagnostic());
            cooked[round].AddRange(products);
            Check(products.All(t => t.def == recipeProduct), "native cooking produced recipe output", "round=" + round);
            var source = ThingDef.Named(cookingSources[round]);
            var exposure = source.GetModExtension<MushroomExposureProperties>();
            float totalRecorded = products.Sum(t => Dose(t, source.defName) * t.stackCount);
            if (exposure != null)
                Check(Math.Abs(totalRecorded - selectedUnits) < 0.0001f,
                    "native cooking conserves actual exposure round " + round,
                    "inputUnits=" + Number(selectedUnits) + ";recorded=" + Number(totalRecorded));
            Observe("cookedRound" + round, "items=" + products.Sum(t => t.stackCount)
                + ";mealNutrition=" + Number(products[0].GetStatValue(StatDefOf.Nutrition))
                + ";doses=" + string.Join(";", products.Select(t => t.ThingID + ":" + t.stackCount + ":" + Doses(t))));
            foreach (var t in products)
            {
                if (t.Spawned) t.DeSpawn();
                if (t.holdingOwner != null) t.holdingOwner.Remove(t);
                t.SetForbidden(false, false);
            }
            if (++round < cookingSources.Length) { StartCooking(); return; }
            CheckPolicyAndStacks();
            diner = PreparePawn(origin + new IntVec3(-2, 0, 4), true);
            var meal = cooked[0][0];
            ingestionPortion = meal.stackCount > 1 ? meal.SplitOff(1) : meal;
            ingestedUnits = Dose(ingestionPortion, "RMush_RawCubensis");
            Check(ingestedUnits > 0, "native cooked ingestion portion has recorded exposure", Doses(ingestionPortion));
            StartIngestion(3);
        }

        private void CheckPolicyAndStacks()
        {
            var psycho = cooked[0][0]; var plain = cooked[1][0];
            Check(psycho.TryGetComp<CompMushroomMeal>() != null, "native cooked meal has exposure comp", psycho.def.defName);
            var policy = new FoodPolicy(32663, "Compatibility fixture");
            policy.filter.SetAllow(ThingCategoryDefOf.Foods, true);
            policy.filter.SetAllow(psycho.def, true); policy.filter.SetAllow(plain.def, true);
            var toggle = SpecialThingFilterDef.Named("RMush_AllowPsychoactiveMeals");
            var policyPawn = PreparePawn(origin + new IntVec3(-3, 0, 4), false);
            policyPawn.foodRestriction.CurrentFoodPolicy = policy;
            policy.filter.SetAllow(toggle, true);
            Check(policy.Allows(psycho) && policyPawn.WillEat(psycho), "food policy inclusion reaches native WillEat", psycho.def.defName);
            policy.filter.SetAllow(toggle, false);
            Check(!policy.Allows(psycho) && !policyPawn.WillEat(psycho)
                && policy.Allows(plain) && policyPawn.WillEat(plain), "food policy exclusion blocks psycho meal but retains plain meal", plain.def.defName);
            policyPawn.DeSpawn();
            Check(!psycho.CanStackWith(plain) && !plain.CanStackWith(psycho), "psycho and plain native cooked meals cannot stack", "native CanStackWith both directions");
            if (psycho.stackCount > 1)
            {
                float before = Dose(psycho, "RMush_RawCubensis"); int count = psycho.stackCount;
                var split = psycho.SplitOff(1);
                Check(Math.Abs(Dose(split, "RMush_RawCubensis") - before) < 0.00001f
                    && Math.Abs(Dose(psycho, "RMush_RawCubensis") - before) < 0.00001f,
                    "native SplitOff conserves per-serving provenance", "dose=" + Number(before));
                Check(psycho.CanStackWith(split) && psycho.TryAbsorbStack(split, true) && psycho.stackCount == count
                    && Math.Abs(Dose(psycho, "RMush_RawCubensis") - before) < 0.00001f,
                    "native matching stacks merge without exposure amplification", "count=" + count);
            }
            else limitations.Add("Live recipe produced one item in its first stack; split/merge could not be exercised for that output.");
        }

        private void StartIngestion(int nextPhase)
        {
            GenSpawn.Spawn(ingestionPortion, diner.Position + IntVec3.East, map);
            var policy = new FoodPolicy(32664 + nextPhase, "Compatibility ingestion");
            policy.filter.SetAllow(ThingCategoryDefOf.Foods, true); policy.filter.SetAllow(ingestionPortion.def, true);
            policy.filter.SetAllow(SpecialThingFilterDef.Named("RMush_AllowPsychoactiveMeals"), true);
            diner.foodRestriction.CurrentFoodPolicy = policy;
            var job = JobMaker.MakeJob(JobDefOf.Ingest, ingestionPortion); job.count = 1;
            StartJob(diner, job, nextPhase, "native meal ingestion " + nextPhase);
        }

        private void PollPsychoactiveIngestion()
        {
            var hallucination = diner.health.hediffSet.hediffs.OfType<Hediff_MushroomHallucination>().FirstOrDefault();
            if (hallucination == null)
            {
                if (!OrderedJobStillRunning(diner)) throw new InvalidOperationException("Native ingestion ended without hallucination: " + Diagnostic());
                return;
            }
            var memories = diner.needs.mood.thoughts.memories.Memories.OfType<Thought_MushroomHallucination>().ToList();
            Check(memories.Count == 1, "native meal ingestion gives one psychoactive memory", "count=" + memories.Count);
            var live = ThingDef.Named("RMush_RawCubensis").GetModExtension<MushroomExposureProperties>();
            int expectedMood = hallucination.Panic ? -8 : Mathf.Max(1, Mathf.RoundToInt(live.moodBonus * Mathf.Clamp01(Mathf.Clamp(ingestedUnits, 0.01f, 4f))));
            int expectedDuration = Mathf.RoundToInt(Mathf.Clamp(live.moodDurationHours, 0.1f, 6f) * 2500f);
            Check(memories[0].moodOffset == expectedMood && memories[0].DurationTicks == expectedDuration,
                "native psychoactive memory follows live source mood and duration",
                "mood=" + memories[0].moodOffset + ";expected=" + expectedMood + ";duration=" + memories[0].DurationTicks);
            Check(Math.Abs(hallucination.TotalDoses - Mathf.Min(4f, ingestedUnits)) < 0.0001f,
                "native ingestion applies recorded units exactly once", "expected=" + Number(ingestedUnits) + ";actual=" + Number(hallucination.TotalDoses));
            Check(hallucination.EndTick - hallucination.EpisodeStartTick >= 15000
                && hallucination.EndTick - hallucination.EpisodeStartTick <= 60000,
                "native ingestion schedules bounded episode", "ticks=" + (hallucination.EndTick - hallucination.EpisodeStartTick));
            Observe("psychoactiveIngestion", "mood=" + memories[0].moodOffset + ";memoryDuration=" + memories[0].DurationTicks
                + ";dose=" + Number(hallucination.TotalDoses) + ";panic=" + hallucination.Panic
                + ";mentalState=" + diner.MentalStateDef?.defName + ";startAttempted=" + hallucination.StartAttempted);
            diner = PreparePawn(origin + new IntVec3(-2, 0, 6), true);
            var meal = cooked[2][0]; ingestionPortion = meal.stackCount > 1 ? meal.SplitOff(1) : meal;
            StartIngestion(4);
        }

        private void PollFlavorIngestion()
        {
            var memories = diner.needs.mood.thoughts.memories.Memories.OfType<Thought_MushroomEnjoyment>().ToList();
            if (memories.Count == 0)
            {
                if (!OrderedJobStillRunning(diner)) throw new InvalidOperationException("Native ingestion ended without flavor memory: " + Diagnostic());
                return;
            }
            var expected = ThingDef.Named("RMush_RawMatsutake").ingestible.specialThoughtAsIngredient;
            Check(memories.Count == 1 && memories[0].def == expected, "native cooked matsutake produces its flavor memory", string.Join(",", memories.Select(m => m.def.defName)));
            Check(memories[0].CurStage.baseMoodEffect == expected.stages[0].baseMoodEffect
                && memories[0].DurationTicks == Mathf.RoundToInt(expected.durationDays * 60000f),
                "native flavor memory follows live thought mood and duration",
                "baseMood=" + memories[0].CurStage.baseMoodEffect + ";duration=" + memories[0].DurationTicks);
            Observe("flavorIngestion", "def=" + memories[0].def.defName + ";baseMood=" + memories[0].CurStage.baseMoodEffect
                + ";effectiveMood=" + memories[0].MoodOffset() + ";durationTicks=" + memories[0].DurationTicks);
            Finish(true);
        }

        private void StartJob(Pawn pawn, Job job, int nextPhase, string label)
        {
            RequireAvailable(pawn, label);
            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            job.playerForced = true;
            orderedJobId = job.loadID;
            orderedJobDef = job.def;
            phase = nextPhase; phaseFrames = 0; phaseStartTick = Find.TickManager.TicksGame;
            deadline = Time.realtimeSinceStartup + 120f;
            pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            if (!OrderedJobStillRunning(pawn))
                throw new InvalidOperationException("Native ordered job was not accepted: " + Diagnostic());
            Trace("START " + label + " " + Diagnostic());
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
            WriteResult();
        }

        private bool OrderedJobStillRunning(Pawn pawn) => pawn?.CurJob != null
            && pawn.CurJob.loadID == orderedJobId && pawn.CurJob.def == orderedJobDef;

        private IEnumerable<Thing> AllProducts()
        {
            var values = map.listerThings.ThingsOfDef(recipeProduct).Where(t => t.Position.DistanceTo(origin) <= 12).ToList();
            var carried = worker.carryTracker.CarriedThing;
            if (carried != null && carried.def == recipeProduct) values.Add(carried);
            return values.Distinct();
        }

        private int RawCount(ThingDef def) => map.listerThings.ThingsOfDef(def).Where(t => t.Position.DistanceTo(origin) <= 9).Sum(t => t.stackCount);
        private static float Dose(Thing thing, string id) => thing.TryGetComp<CompMushroomMeal>()?.Doses.Where(d => d.Key.defName == id).Sum(d => d.Value) ?? 0f;
        private static string Doses(Thing thing) => string.Join(",", thing.TryGetComp<CompMushroomMeal>()?.Doses.Select(d => d.Key.defName + "=" + Number(d.Value)) ?? Enumerable.Empty<string>());
        private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static string NormalizeId(string value) => value.Trim().ToLowerInvariant();

        private void RecordModEnvironment()
        {
            var running = LoadedModManager.RunningModsListForReading;
            foreach (var mod in running)
            {
                loadedMods.Add("{\"id\":" + Json(mod.PackageIdPlayerFacing) + ",\"internalId\":" + Json(mod.PackageId)
                    + ",\"name\":" + Json(mod.Name) + "}");
                Trace("LOADED " + mod.PackageIdPlayerFacing + " | " + mod.Name);
            }
            string expectedFile = Path.Combine(Profile, "compatibility-expected-mod-ids.txt");
            if (File.Exists(expectedFile))
                expectedIds.AddRange(File.ReadAllLines(expectedFile).Where(line => !string.IsNullOrWhiteSpace(line)).Select(NormalizeId).Distinct());
            else
            {
                limitations.Add("Immutable launcher ID snapshot absent; expected IDs read after startup, so auto-removed missing mods cannot be fully detected.");
                var config = new XmlDocument(); config.Load(Path.Combine(Profile, "Config", "ModsConfig.xml"));
                expectedIds.AddRange(config.SelectNodes("/ModsConfigData/activeMods/li").Cast<XmlNode>().Select(node => NormalizeId(node.InnerText)).Distinct());
            }
            var actual = new HashSet<string>(running.SelectMany(mod => new[] { NormalizeId(mod.PackageId), NormalizeId(mod.PackageIdPlayerFacing) }));
            missingIds.AddRange(expectedIds.Where(id => !actual.Contains(id)));
            Observe("modCounts", "requested=" + expectedIds.Count + ";loaded=" + running.Count + ";missing=" + missingIds.Count);
            Check(missingIds.Count == 0, "all requested active mod IDs are loaded", string.Join(",", missingIds));
        }

        private void RecordHarmonyOwners()
        {
            var harmony = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("HarmonyLib.Harmony", false)).FirstOrDefault(t => t != null);
            var getInfo = harmony?.GetMethod("GetPatchInfo", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(MethodBase) }, null);
            if (getInfo == null) { limitations.Add("Harmony patch-info reflection unavailable; patch owners not enumerated."); return; }
            var targets = new[] {
                new KeyValuePair<Type, string>(typeof(Thing), "Ingested"),
                new KeyValuePair<Type, string>(typeof(Thing), "SplitOff"),
                new KeyValuePair<Type, string>(typeof(Thing), "CanStackWith"),
                new KeyValuePair<Type, string>(typeof(Thing), "TryAbsorbStack"),
                new KeyValuePair<Type, string>(typeof(ThingWithComps), "SplitOff"),
                new KeyValuePair<Type, string>(typeof(ThingWithComps), "CanStackWith"),
                new KeyValuePair<Type, string>(typeof(ThingWithComps), "TryAbsorbStack"),
                new KeyValuePair<Type, string>(typeof(GenRecipe), "MakeRecipeProducts"),
                new KeyValuePair<Type, string>(typeof(WorkGiver_DoBill), "JobOnThing"),
                new KeyValuePair<Type, string>(typeof(WorkGiver_GrowerHarvest), "HasJobOnCell"),
                new KeyValuePair<Type, string>(typeof(WorkGiver_GrowerHarvest), "JobOnCell"),
                new KeyValuePair<Type, string>(typeof(FoodUtility), "WillEat"),
                new KeyValuePair<Type, string>(typeof(ThingFilter), "Allows"),
                new KeyValuePair<Type, string>(typeof(Plant), "PlantCollected") };
            foreach (var target in targets)
            foreach (var method in target.Key.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Where(m => m.Name == target.Value))
            {
                var owners = new List<string>();
                var info = getInfo.Invoke(null, new object[] { method });
                if (info != null)
                    foreach (string kind in new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" })
                    {
                        object collection = info.GetType().GetField(kind)?.GetValue(info)
                            ?? info.GetType().GetProperty(kind)?.GetValue(info, null);
                        if (!(collection is IEnumerable entries)) continue;
                        foreach (var patch in entries)
                        {
                            object owner = patch.GetType().GetField("owner")?.GetValue(patch)
                                ?? patch.GetType().GetProperty("owner")?.GetValue(patch, null);
                            if (owner != null) owners.Add(kind + ":" + owner);
                        }
                    }
                string name = target.Key.FullName + "." + method;
                patches.Add("{\"method\":" + Json(name) + ",\"owners\":" + JsonArray(owners.Distinct()) + "}");
                Trace("PATCHES " + name + " | " + string.Join(",", owners.Distinct()));
            }
        }

        private void RequireAvailable(Pawn pawn, string role)
        {
            if (pawn == null || pawn.Dead || pawn.Destroyed || !pawn.Spawned || pawn.Downed
                || pawn.needs?.food == null || pawn.needs.rest == null || pawn.needs.mood == null)
                throw new InvalidOperationException(role + " became unavailable; no healing/revival applied: " + PawnDiagnostic(pawn));
        }

        private string Diagnostic() => "phase=" + phase + ";round=" + round + ";phaseFrames=" + phaseFrames
            + ";tick=" + Find.TickManager.TicksGame + ";worker=" + PawnDiagnostic(worker)
            + ";diner=" + PawnDiagnostic(diner) + ";ordered=" + orderedJobDef?.defName + " (Job_" + orderedJobId + ")"
            + ";stove=" + stove?.Position + ";billRemaining=" + bill?.repeatCount;
        private static string PawnDiagnostic(Pawn pawn) => pawn == null ? "null" : pawn.ThingID + ";race=" + pawn.def.defName
            + ";dead=" + pawn.Dead + ";downed=" + pawn.Downed + ";spawned=" + pawn.Spawned
            + ";position=" + pawn.Position + ";job=" + pawn.CurJob + ";toil=" + pawn.jobs?.curDriver?.CurToilIndex
            + ";mental=" + pawn.MentalStateDef?.defName + ";food=" + pawn.needs?.food?.CurLevelPercentage
            + ";rest=" + pawn.needs?.rest?.CurLevelPercentage + ";health=" + string.Join(",", pawn.health?.hediffSet?.hediffs.Select(h => h.def.defName + ":" + h.Severity) ?? Enumerable.Empty<string>());
        private void Check(bool condition, string name, string detail)
        {
            checks.Add("{\"name\":" + Json(name) + ",\"passed\":" + (condition ? "true" : "false") + ",\"detail\":" + Json(detail) + "}");
            Trace((condition ? "PASS " : "FAIL ") + name + " | " + detail);
            WriteResult();
            if (!condition) throw new InvalidOperationException(name + ": " + detail);
        }
        private void Observe(string key, string value) { observations.Add("{\"name\":" + Json(key) + ",\"value\":" + Json(value) + "}"); Trace("OBSERVE " + key + " | " + value); }
        private void Trace(string text) { File.AppendAllText(Report, text + Environment.NewLine); Log.Message("[Mushroom compatibility] " + text); }
        private void Finish(bool passed)
        {
            finished = true; status = passed ? "PASS" : "FAIL";
            Trace("RESULT " + status + (passed ? "" : "\n" + failure)); WriteResult();
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Application.Quit(passed ? 0 : 2);
        }
        private void WriteResult()
        {
            string value = "{\n\"schema\":1,\"status\":" + Json(status) + ",\"gameVersion\":" + Json(VersionControl.CurrentVersionStringWithRev)
                + ",\"profile\":" + Json(Profile) + ",\"phase\":" + phase + ",\"frames\":" + frames
                + ",\"expectedIds\":" + JsonArray(expectedIds) + ",\"loadedMods\":[" + string.Join(",", loadedMods)
                + "],\"missingIds\":" + JsonArray(missingIds) + ",\"patches\":[" + string.Join(",", patches)
                + "],\"checks\":[" + string.Join(",", checks) + "],\"observations\":[" + string.Join(",", observations)
                + "],\"limitations\":" + JsonArray(limitations) + ",\"failure\":" + Json(failure) + "\n}";
            File.WriteAllText(Result, value, new UTF8Encoding(false));
        }
        private static string JsonArray(IEnumerable<string> values) => "[" + string.Join(",", values.Select(Json)) + "]";
        private static string Json(string value)
        {
            if (value == null) return "null";
            var buffer = new StringBuilder("\"");
            foreach (char c in value)
                if (c == '\\') buffer.Append("\\\\");
                else if (c == '"') buffer.Append("\\\"");
                else if (c == '\n') buffer.Append("\\n");
                else if (c == '\r') buffer.Append("\\r");
                else if (c == '\t') buffer.Append("\\t");
                else if (c < 32) buffer.Append("\\u" + ((int)c).ToString("x4"));
                else buffer.Append(c);
            return buffer.Append('"').ToString();
        }
    }
}
