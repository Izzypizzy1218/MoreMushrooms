using System;
using System.Collections;
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
    // Opt-in disposable-map experiment. The real TickManager runs the world and
    // pawn AI; this component never manually ticks jobs, health, needs or time.
    public sealed class FieldWorkBalanceTests : GameComponent
    {
        private const int Hour = 2500, Day = 60000, Seed = 640611;
        private readonly List<Trial> trials = new List<Trial>();
        private readonly List<string> errors = new List<string>();
        private readonly List<string> failures = new List<string>();
        private readonly List<Dictionary<string, object>> events = new List<Dictionary<string, object>>();
        private readonly List<Dictionary<string, object>> samples = new List<Dictionary<string, object>>();
        private readonly List<string> limitations = new List<string>();
        private readonly List<string> loadedMods = new List<string>();
        private Map map;
        private int fixtureTick, lastWorldTick, frameCount;
        private bool initialized, finished;
        private float deadline;
        private string status = "RUNNING";
        private string Folder => GenFilePaths.SaveDataFolderPath;
        private string ResultPath => Path.Combine(Folder, "field-work-balance-result.json");

        private sealed class Trial
        {
            public string Name, Source, LastState;
            public Pawn Pawn;
            public Thing InitialFood;
            public Building_WorkTable Table;
            public Building_Bed Bed;
            public CellRect Interior;
            public Bill_Production Bill;
            public int InitialIngestJobId = -1;
            public JobDriver_DoBill PreviousRecipeDriver;
            public int PreviousRecipeTicks;
            public int StartTick = -1, EndTick = -1, Ticks, NextSample = 0;
            public int DoBillTicks, RecipeWorkTicks, MentalTicks, MushroomMentalTicks;
            public int SleepTicks, DownedTicks, DeadTicks, FoodPoisonTicks, EatingTicks;
            public int IdleTicks, OtherTicks, UnavailableUnionTicks, MealsAtStart, MealsConsumed;
            public int ProductAtStart, ProductAtEnd, ChunksAtStart, ChunksAtEnd;
            public int InitialScheduledTicks = -1, FirstMentalTick = -1, FirstRecoveryTick = -1;
            public int FirstSleepTick = -1, FirstFoodPoisonTick = -1, FirstDownedTick = -1;
            public float InitialNutritionRecord, InitialFoodPerItem, MinFood = 1f, MinRest = 1f;
            public double MushroomMoodPointTicks;
            public Dictionary<string, object> Baseline;
            public bool Complete => EndTick >= 0;
        }

        public FieldWorkBalanceTests(Game game) { }

        public override void GameComponentUpdate()
        {
            if (finished || !GenCommandLine.CommandLineArgPassed("mushroomFieldWorkBalance")
                || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting
                || Find.CurrentMap == null) return;
            try
            {
                if (!initialized)
                {
                    Initialize(Find.CurrentMap);
                    initialized = true;
                    return;
                }
                frameCount++;
                // Dialogs are dismissed only to let native time advance. No pawn
                // jobs, needs, sleep, health or exposure timers are changed here.
                foreach (var window in Find.WindowStack.Windows.ToList()) window.Close(false);
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
                if (errors.Count > 0) throw new InvalidOperationException("Native error(s): " + string.Join("\n", errors));
                if (Time.realtimeSinceStartup > deadline)
                    throw new TimeoutException("Native 24-hour work observation exceeded 15 wall-clock minutes. " + Diagnostic());
                if (frameCount % 900 == 0)
                {
                    Trace("PROGRESS " + Diagnostic());
                    WriteReports(false);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception + "\n" + Diagnostic());
                Finish(false);
            }
        }

        public override void GameComponentTick()
        {
            if (!initialized || finished) return;
            try
            {
                int now = Find.TickManager.TicksGame;
                Require(now == lastWorldTick + 1, "native observation advances exactly one game tick; previous="
                    + lastWorldTick + ";now=" + now);
                lastWorldTick = now;
                if (errors.Count > 0) throw new InvalidOperationException("Native error(s): " + string.Join("\n", errors));
                foreach (var trial in trials)
                {
                    if (trial.Complete) continue;
                    if (trial.StartTick < 0)
                    {
                        PollInitialIngestion(trial, now);
                        continue;
                    }
                    ObserveTick(trial, now);
                }
                if (trials.Count == 3 && trials.All(t => t.Complete))
                {
                    ValidateMeasurement();
                    Finish(true);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception + "\n" + Diagnostic());
                Finish(false);
            }
        }

        private void Initialize(Map target)
        {
            map = target;
            Directory.CreateDirectory(Folder);
            Application.logMessageReceived += CaptureNativeError;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            foreach (var window in Find.WindowStack.Windows.ToList()) window.Close(false);
            fixtureTick = lastWorldTick = Find.TickManager.TicksGame;
            deadline = Time.realtimeSinceStartup + 900f;
            DebugSettings.enableStoryteller = false;
            DebugSettings.fastCrafting = false;
            map.Biome.constantOutdoorTemperature = 21f;
            map.weatherManager.TransitionTo(WeatherDefOf.Clear);
            // Remove pre-existing pawns before fixture creation. This is a fresh
            // isolated map; neither this nor any other suite uses the user's save.
            foreach (var ambient in map.mapPawns.AllPawnsSpawned.ToList()) ambient.DeSpawn();
            loadedMods.AddRange(LoadedModManager.RunningModsListForReading.Select(m => m.PackageId));
            limitations.Add("Three matched adult human pawns from one fixed-seed preparation, one observation per food, each followed for 60,000 native ticks (24 game hours). Three pawns are not three replicates per treatment.");
            limitations.Add("A small controlled stonecutting comparison, not a general settlement labor value, population probability, medical study, or all-mod compatibility guarantee.");
            limitations.Add("The isolated profile's native autosave interval is " + F(Prefs.AutosaveIntervalDays)
                + " game days. For batch-mode observation it is configured beyond this one-day trial because a synchronous autosave waits for a GUI repaint. This experiment does not test save/load or autosaving.");
            limitations.Add("Each pawn has an identical enclosed room, an assigned normal-quality bed, one stonecutting table, 55 granite chunks, 30 fresh simple meals and a fueled torch lamp. Logistics, other workers, raids, treatment, social interaction and alternative jobs are absent.");
            limitations.Add("Only the initial native Ingest job is ordered. Crafting priority 1 and an Anything timetable let normal PawnJob AI choose bill work, food, sleep and other needs. The component never issues follow-up work orders or interrupts eating or sleeping.");
            limitations.Add("Initial health, traits, skills, genes, nutrition, rest and mood are prepared once before ingestion. No healing, tending, nutrition/rest restoration, forced waking, mental-state recovery, manual job/health/need ticks, exposure-timer edits or clock changes occur during observation.");
            limitations.Add("Raw food's native food-poisoning chance is retained. Food-poisoning, mushroom poisoning, rest, hunger and unrelated mental states can affect output; this one sample cannot separate their causal contributions.");
            limitations.Add("DoBill ticks include walking and ingredient handling; recipe-work ticks additionally use the native JobDriver_DoBill counter. Neither metric alone guarantees a finished product. Stone blocks actually present in that room/carried by its pawn are counted separately.");
            limitations.Add("Per-tick states can overlap: mental-state, sleep, downed and food-poisoning hours must not be added together. Unavailable-union hours count any mental state, sleep, downing or death once per tick.");
            limitations.Add("Mushroom mood-point-hours integrate only RMush memory offsets. They are not net overall mood, a recommendation to consume mushrooms, or a conversion between mood and labor value.");
            limitations.Add("Only fixture preparation uses the stated Rand seed in a same-frame PushState/PopState scope. Actual ingestion, duration sampling and the subsequent 24 hours use the normal game's RNG. The harness does not fix or claim deterministic global randomness across frames; the world seed is recorded separately.");
            Rand.PushState(Seed);
            try
            {
            var sources = new[] { "RawPotatoes", "RMush_RawCubensis", "RMush_RawPantherCap" };
            var names = new[] { "control-potatoes", "cubensis-first-standard", "panther-first-standard" };
            var recipe = DefDatabase<RecipeDef>.GetNamed("Make_StoneBlocksAny");
            Require(recipe.workAmount > 0f && recipe.workSkillLearnFactor == 0f,
                "native stonecutting recipe has positive work and no skill learning drift");
            for (int i = 0; i < 3; i++)
            {
                var center = map.Center + new IntVec3((i - 1) * 19, 0, 18);
                var outer = CellRect.CenteredOn(center, 8);
                Require(outer.FullyContainedWithin(new CellRect(0, 0, map.Size.x, map.Size.z)), "three-room fixture fits map");
                var trial = new Trial { Name = names[i], Source = sources[i], Interior = CellRect.CenteredOn(center, 7) };
                trials.Add(trial);
                PrepareRoom(trial, outer, center, recipe);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            var rooms = trials.Select(t => t.Pawn.GetRoom()).ToArray();
            Require(rooms.All(r => r != null && !r.UsesOutdoorTemperature) && rooms.Distinct().Count() == 3,
                "three separate native roofed rooms are available");
            // The generated map can retain its old air temperature after the
            // biome override. Normalize room air once now, never during a trial.
            foreach (var trial in trials)
            {
                trial.Pawn.GetRoom().Temperature = 21f;
                trial.Baseline["temperatureBeforeIngestion"] = trial.Pawn.Position.GetTemperature(map);
            }
            Require(trials.All(t => Math.Abs(t.InitialFoodPerItem - trials[0].InitialFoodPerItem) < 0.00001f),
                "all three initial foods have equal per-item nutrition");
            ComparePreparedPawns();
            foreach (var trial in trials) StartIngestion(trial);
            Trace("PREPARED seed=" + Seed + "; native recipe=" + recipe.defName + ";work=" + recipe.workAmount
                + ";globalTick=" + fixtureTick + ";roomTemperature=" + trials[0].Pawn.Position.GetTemperature(map));
            WriteReports(false);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }
            finally { Rand.PopState(); }
        }

        private void PrepareRoom(Trial trial, CellRect outer, IntVec3 center, RecipeDef recipe)
        {
            foreach (var cell in outer.Cells)
            {
                foreach (var thing in cell.GetThingList(map).ToList())
                    if (thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);
                    else if (thing.Spawned) thing.DeSpawn();
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.PavedTile);
                map.roofGrid.SetRoof(cell, null);
                map.snowGrid.SetDepth(cell, 0f);
                if (!trial.Interior.Contains(cell))
                {
                    var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite);
                    wall.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(wall, cell, map);
                }
            }
            foreach (var cell in trial.Interior.Cells) map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
            // The room is wider than vanilla's unsupported-roof limit. A real
            // native column supports its center without changing pawn safety.
            var column = ThingMaker.MakeThing(ThingDefOf.Column, ThingDefOf.BlocksGranite);
            column.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(column, center + new IntVec3(3, 0, -2), map);
            Require(trial.Interior.Cells.All(c => RoofCollapseUtility.WithinRangeOfRoofHolder(c, map)),
                "every fixture roof cell has native structural support");
            var tableDef = ThingDef.Named("TableStonecutter");
            trial.Table = ThingMaker.MakeThing(tableDef, tableDef.MadeFromStuff ? ThingDefOf.Steel : null) as Building_WorkTable;
            Require(trial.Table != null && tableDef.AllRecipes.Contains(recipe), "native stonecutting worktable accepts recipe");
            trial.Table.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(trial.Table, center + new IntVec3(0, 0, 1), map, Rot4.North);
            Require(trial.Table.InteractionCell.Standable(map), "native table interaction cell is standable");
            trial.Bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
            trial.Bed.SetFaction(Faction.OfPlayer);
            trial.Bed.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Colony);
            GenSpawn.Spawn(trial.Bed, center + new IntVec3(-5, 0, 4), map, Rot4.South);
            var lamp = ThingMaker.MakeThing(ThingDef.Named("TorchLamp"));
            lamp.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(lamp, center + new IntVec3(2, 0, 2), map);
            var fuel = lamp.TryGetComp<CompRefuelable>();
            if (fuel != null) fuel.Refuel(fuel.Props.fuelCapacity);
            foreach (var cell in trial.Interior.Cells.Where(c => c.z >= center.z + 3 && c.x >= center.x - 3).Take(55))
                GenSpawn.Spawn(ThingDef.Named("ChunkGranite"), cell, map);
            Require(Count(trial, ThingDef.Named("ChunkGranite")) == 55, "55 independent native granite chunks supplied");
            for (int i = 0; i < 3; i++)
            {
                var meals = ThingMaker.MakeThing(ThingDefOf.MealSimple);
                meals.stackCount = 10;
                GenSpawn.Spawn(meals, center + new IntVec3(-5 + i, 0, -4), map);
            }
            trial.Pawn = PreparePawn(center + new IntVec3(-1, 0, -1));
            Require(trial.Pawn.ownership.ClaimBedIfNonMedical(trial.Bed), "own native bed assigned before ingestion");
            Area_Allowed allowed;
            Require(map.areaManager.TryMakeNewAllowed(out allowed), "dedicated allowed area created");
            allowed.SetLabel("Work trial " + trial.Name);
            foreach (var cell in trial.Interior.Cells) allowed[cell] = true;
            trial.Pawn.playerSettings.AreaRestrictionInPawnCurrentMap = allowed;
            trial.Bill = (Bill_Production)recipe.MakeNewBill();
            trial.Bill.ingredientFilter.SetDisallowAll();
            trial.Bill.ingredientFilter.SetAllow(ThingDef.Named("ChunkGranite"), true);
            trial.Bill.ingredientSearchRadius = 12f;
            trial.Bill.repeatMode = BillRepeatModeDefOf.Forever;
            trial.Bill.SetPawnRestriction(trial.Pawn);
            trial.Bill.SetStoreMode(BillStoreModeDefOf.DropOnFloor);
            trial.Table.billStack.AddBill(trial.Bill);
            var giver = (WorkGiver_DoBill)DefDatabase<WorkGiverDef>.GetNamed("DoBillsStonecut").Worker;
            var diagnosticJob = giver.JobOnThing(trial.Pawn, trial.Table, false);
            Require(diagnosticJob != null && diagnosticJob.def == JobDefOf.DoBill && diagnosticJob.bill == trial.Bill,
                "native work giver can select its assigned stonecutting bill;reason=" + JobFailReason.Reason);
            trial.InitialFood = ThingMaker.MakeThing(ThingDef.Named(trial.Source));
            trial.InitialFood.stackCount = 10;
            GenSpawn.Spawn(trial.InitialFood, center + new IntVec3(-1, 0, -2), map);
            trial.InitialFoodPerItem = trial.InitialFood.GetStatValue(StatDefOf.Nutrition);
            trial.InitialNutritionRecord = trial.Pawn.records.GetValue(RecordDefOf.NutritionEaten);
            trial.Pawn.foodRestriction.CurrentFoodPolicy.filter.SetAllow(ThingDefOf.MealSimple, true);
            trial.Pawn.foodRestriction.CurrentFoodPolicy.filter.SetAllow(ThingDef.Named(trial.Source), true);
            trial.MealsAtStart = Count(trial, ThingDefOf.MealSimple);
            trial.ChunksAtStart = Count(trial, ThingDef.Named("ChunkGranite"));
            trial.Baseline = PawnBaseline(trial);
        }

        private Pawn PreparePawn(IntVec3 cell)
        {
            var request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                forceGenerateNewPawn: true, allowDead: false, allowDowned: false,
                canGeneratePawnRelations: false, allowAddictions: false, allowFood: false,
                fixedBiologicalAge: 28f, fixedChronologicalAge: 28f, fixedGender: Gender.Male,
                forceNoIdeo: true, forceNoBackstory: true, forbidAnyTitle: true,
                forceBaselinerChance: 1f, maximumAgeTraits: 0, forceNoGear: true);
            var pawn = PawnGenerator.GeneratePawn(request);
            NormalizePreparedGenes(pawn);
            foreach (var hediff in pawn.health.hediffSet.hediffs.ToList()) pawn.health.RemoveHediff(hediff);
            foreach (var trait in pawn.story.traits.allTraits.ToList()) pawn.story.traits.RemoveTrait(trait);
            foreach (var skill in pawn.skills.skills)
            {
                skill.Level = 10;
                skill.passion = Passion.None;
                skill.xpSinceLastLevel = 0f;
                skill.xpSinceMidnight = 0f;
            }
            Require(pawn.RaceProps.Humanlike && !pawn.Dead && !pawn.Downed && pawn.needs?.food != null
                && pawn.needs.rest != null && pawn.needs.mood != null, "fresh healthy adult human fixture");
            foreach (var need in pawn.needs.AllNeeds) need.CurLevelPercentage = 1f;
            pawn.needs.food.CurLevelPercentage = 0.40f;
            pawn.needs.rest.CurLevelPercentage = 1f;
            pawn.needs.mood.CurLevelPercentage = 0.75f;
            if (pawn.needs.joy != null) pawn.needs.joy.CurLevelPercentage = 1f;
            GenSpawn.Spawn(pawn, cell, map);
            pawn.playerSettings.selfTend = false;
            foreach (var work in DefDatabase<WorkTypeDef>.AllDefs) pawn.workSettings.SetPriority(work, 0);
            pawn.workSettings.SetPriority(WorkTypeDefOf.Crafting, 1);
            for (int hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
            Require(!pawn.WorkTypeIsDisabled(WorkTypeDefOf.Crafting), "matched pawn can perform native crafting");
            return pawn;
        }

        private void NormalizePreparedGenes(Pawn pawn)
        {
            var reference = trials.Count == 0 ? null : trials[0].Pawn;
            if (reference == null) return;
            Require((reference.genes == null) == (pawn.genes == null), "matched human fixtures have comparable gene trackers");
            if (pawn.genes == null) return;
            // Baseliners still receive different native skin/hair genes. Copy
            // the control's exact endogene/xenogene definitions before health,
            // skills and needs preparation; no genes change after ingestion.
            var endogenes = reference.genes.Endogenes.Select(g => g.def).ToArray();
            var xenogenes = reference.genes.Xenogenes.Select(g => g.def).ToArray();
            foreach (var gene in pawn.genes.GenesListForReading.ToList()) pawn.genes.RemoveGene(gene);
            foreach (var definition in endogenes) pawn.genes.AddGene(definition, xenogene: false);
            foreach (var definition in xenogenes) pawn.genes.AddGene(definition, xenogene: true);
        }

        private Dictionary<string, object> PawnBaseline(Trial trial)
        {
            var pawn = trial.Pawn;
            return new Dictionary<string, object> {
                { "pawnId", pawn.GetUniqueLoadID() }, { "humanRace", pawn.def.defName },
                { "biologicalAge", pawn.ageTracker.AgeBiologicalYearsFloat },
                { "chronologicalAge", pawn.ageTracker.AgeChronologicalYearsFloat }, { "gender", pawn.gender.ToString() },
                { "traits", pawn.story.traits.allTraits.Select(t => t.def.defName + ":" + t.Degree).ToArray() },
                { "childhood", pawn.story.Childhood?.defName }, { "adulthood", pawn.story.Adulthood?.defName },
                { "genes", pawn.genes?.GenesListForReading.Select(g => g.def.defName).OrderBy(s => s).ToArray() ?? new string[0] },
                { "endogenes", pawn.genes?.Endogenes.Select(g => g.def.defName).OrderBy(s => s).ToArray() ?? new string[0] },
                { "xenogenes", pawn.genes?.Xenogenes.Select(g => g.def.defName).OrderBy(s => s).ToArray() ?? new string[0] },
                { "geneActiveStates", pawn.genes?.GenesListForReading.Select(g => g.def.defName + ":" + g.Active).OrderBy(s => s).ToArray() ?? new string[0] },
                { "skills", pawn.skills.skills.ToDictionary(s => s.def.defName, s => (object)s.Level) },
                { "generalLaborSpeed", pawn.GetStatValue(StatDefOf.GeneralLaborSpeed, cacheStaleAfterTicks: 0) },
                { "moveSpeed", pawn.GetStatValue(StatDefOf.MoveSpeed, cacheStaleAfterTicks: 0) },
                { "manipulation", pawn.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation) },
                { "consciousness", pawn.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness) },
                { "moving", pawn.health.capacities.GetLevel(PawnCapacityDefOf.Moving) },
                { "restFallRateFactor", pawn.GetStatValue(StatDefOf.RestFallRateFactor) },
                { "food", pawn.needs.food.CurLevelPercentage }, { "rest", pawn.needs.rest.CurLevelPercentage },
                { "mood", pawn.needs.mood.CurLevelPercentage }, { "rawNutritionPerItem", trial.InitialFoodPerItem },
                { "allInitialNeeds", pawn.needs.AllNeeds.ToDictionary(n => n.def.defName, n => (object)n.CurLevelPercentage) },
                { "foodPoisonChanceFixedHuman", trial.InitialFood.GetStatValue(StatDefOf.FoodPoisonChanceFixedHuman) },
                { "tableWorkSpeedFactor", trial.Table.GetStatValue(StatDefOf.WorkTableWorkSpeedFactor) },
                { "bedRestEffectiveness", trial.Bed.GetStatValue(StatDefOf.BedRestEffectiveness) },
                { "chunks", trial.ChunksAtStart }, { "safeMealCount", trial.MealsAtStart },
                { "localHourBeforeIngestion", GenLocalDate.HourOfDay(pawn) },
                { "temperatureBeforeIngestion", pawn.Position.GetTemperature(map) }
            };
        }

        private void ComparePreparedPawns()
        {
            var reference = trials[0].Baseline;
            foreach (var trial in trials.Skip(1))
                foreach (string key in new[] { "humanRace", "biologicalAge", "chronologicalAge", "gender", "traits",
                    "childhood", "adulthood", "genes", "endogenes", "xenogenes", "geneActiveStates", "skills", "generalLaborSpeed", "moveSpeed", "manipulation",
                    "consciousness", "moving", "restFallRateFactor", "food", "rest", "mood", "rawNutritionPerItem",
                    "allInitialNeeds", "tableWorkSpeedFactor", "bedRestEffectiveness", "chunks", "safeMealCount",
                    "temperatureBeforeIngestion" })
                    Require(Json(reference[key]) == Json(trial.Baseline[key]), "equal prepared ability/resource field " + key
                        + ";control=" + Json(reference[key]) + ";" + trial.Name + "=" + Json(trial.Baseline[key]));
        }

        private void StartIngestion(Trial trial)
        {
            var job = JobMaker.MakeJob(JobDefOf.Ingest, trial.InitialFood);
            job.count = 10;
            job.ingestTotalCount = true;
            job.playerForced = true;
            trial.InitialIngestJobId = job.loadID;
            trial.Pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            Require(trial.Pawn.CurJob != null && trial.Pawn.CurJob.loadID == trial.InitialIngestJobId
                && trial.Pawn.CurJob.def == JobDefOf.Ingest, "initial native 10-item Ingest accepted for " + trial.Name);
        }

        private void PollInitialIngestion(Trial trial, int now)
        {
            Require(now - fixtureTick <= 10000, "native initial ingestion completes within four game hours: " + trial.Name);
            var hall = trial.Pawn.health.hediffSet.GetFirstHediffOfDef(HediffDef.Named("RMush_Hallucination")) as Hediff_MushroomHallucination;
            bool psycho = trial.Source != "RawPotatoes";
            if (psycho ? hall == null : !trial.InitialFood.Destroyed) return;
            float eaten = trial.Pawn.records.GetValue(RecordDefOf.NutritionEaten) - trial.InitialNutritionRecord;
            Require(trial.InitialFood.Destroyed && Math.Abs(eaten - 10f * trial.InitialFoodPerItem) < 0.001f,
                "native job consumed all ten nutritionally matched items: " + trial.Name + ";nutrition=" + eaten);
            trial.StartTick = now;
            trial.ProductAtStart = Count(trial, ThingDefOf.BlocksGranite);
            if (psycho)
            {
                Require(hall != null && Math.Abs(hall.TotalDoses - 1f) < 0.001f && hall.EpisodeStartTick == now,
                    "first native exposure is exactly one standard dose");
                Require(trial.Pawn.MentalState is MentalState_MushroomWander,
                    "initial awake first exposure entered native mushroom mental state");
                trial.InitialScheduledTicks = hall.EndTick - hall.EpisodeStartTick;
            }
            trial.LastState = State(trial);
            Event(trial, "ingested-and-observation-started", now, "nativeNutrition=" + eaten
                + ";standardDoses=" + (hall?.TotalDoses ?? 0f) + ";scheduledTicks=" + trial.InitialScheduledTicks);
            Snapshot(trial, now);
        }

        private void ObserveTick(Trial trial, int now)
        {
            var pawn = trial.Pawn;
            Require(now - trial.StartTick == trial.Ticks + 1, "continuous native per-pawn observation for " + trial.Name);
            trial.Ticks++;
            bool dead = pawn.Dead, down = pawn.Downed, sleep = !dead && !pawn.Awake();
            bool mental = !dead && pawn.InMentalState, mushroom = pawn.MentalState is MentalState_MushroomWander;
            bool poison = pawn.health.hediffSet.hediffs.Any(h => h.def == HediffDefOf.FoodPoisoning);
            bool doingBill = pawn.CurJobDef == JobDefOf.DoBill && pawn.CurJob.bill == trial.Bill;
            bool eating = pawn.CurJobDef == JobDefOf.Ingest;
            if (doingBill) trial.DoBillTicks++;
            if (mental) trial.MentalTicks++;
            if (mushroom) trial.MushroomMentalTicks++;
            if (sleep) trial.SleepTicks++;
            if (down) trial.DownedTicks++;
            if (dead) trial.DeadTicks++;
            if (poison) trial.FoodPoisonTicks++;
            if (eating) trial.EatingTicks++;
            if (mental || sleep || down || dead) trial.UnavailableUnionTicks++;
            if (IsIdle(pawn)) trial.IdleTicks++;
            else if (!doingBill && !mental && !sleep && !down && !dead && !eating) trial.OtherTicks++;
            if (mushroom && trial.FirstMentalTick < 0) trial.FirstMentalTick = now;
            if (!mushroom && trial.FirstMentalTick >= 0 && trial.FirstRecoveryTick < 0) trial.FirstRecoveryTick = now;
            if (sleep && trial.FirstSleepTick < 0) trial.FirstSleepTick = now;
            if (poison && trial.FirstFoodPoisonTick < 0) trial.FirstFoodPoisonTick = now;
            if (down && trial.FirstDownedTick < 0) trial.FirstDownedTick = now;
            if (pawn.needs?.food != null) trial.MinFood = Math.Min(trial.MinFood, pawn.needs.food.CurLevelPercentage);
            if (pawn.needs?.rest != null) trial.MinRest = Math.Min(trial.MinRest, pawn.needs.rest.CurLevelPercentage);
            trial.MushroomMoodPointTicks += MushroomMood(pawn);
            UpdateRecipeTicks(trial);
            string state = State(trial);
            if (state != trial.LastState)
            {
                Event(trial, "state-changed", now, trial.LastState + " -> " + state);
                trial.LastState = state;
            }
            if (trial.Ticks >= trial.NextSample + Hour)
            {
                trial.NextSample = trial.Ticks;
                Snapshot(trial, now);
            }
            if (trial.Ticks == Day)
            {
                trial.EndTick = now;
                trial.ProductAtEnd = Count(trial, ThingDefOf.BlocksGranite);
                trial.ChunksAtEnd = Count(trial, ThingDef.Named("ChunkGranite"));
                trial.MealsConsumed = trial.MealsAtStart - Count(trial, ThingDefOf.MealSimple);
                Event(trial, "observation-complete", now, "blocks=" + (trial.ProductAtEnd - trial.ProductAtStart)
                    + ";nativeDoBillTicks=" + trial.DoBillTicks + ";recipeWorkTicks=" + trial.RecipeWorkTicks);
                Trace("COMPLETED " + trial.Name + ";blocks=" + (trial.ProductAtEnd - trial.ProductAtStart)
                    + ";mentalHours=" + Hours(trial.MentalTicks) + ";sleepHours=" + Hours(trial.SleepTicks));
                WriteReports(false);
            }
        }

        private void UpdateRecipeTicks(Trial trial)
        {
            // Retain the previous driver to include its final work increment even
            // when the native job completed/was interrupted earlier in this tick.
            if (trial.PreviousRecipeDriver != null)
            {
                int total = trial.PreviousRecipeDriver.ticksSpentDoingRecipeWork;
                trial.RecipeWorkTicks += Math.Max(0, total - trial.PreviousRecipeTicks);
                trial.PreviousRecipeTicks = total;
            }
            var current = trial.Pawn.jobs.curDriver as JobDriver_DoBill;
            if (current != trial.PreviousRecipeDriver)
            {
                trial.PreviousRecipeDriver = current;
                trial.PreviousRecipeTicks = current?.ticksSpentDoingRecipeWork ?? 0;
                trial.RecipeWorkTicks += trial.PreviousRecipeTicks;
            }
        }

        private void ValidateMeasurement()
        {
            Require(trials.Count == 3 && trials.All(t => t.Ticks == Day), "exactly three complete one-day native trials");
            Require(trials[0].DoBillTicks > 0 && trials[0].RecipeWorkTicks > 0
                && trials[0].ProductAtEnd > trials[0].ProductAtStart, "native control AI performs stonecutting and produces actual stone blocks");
            Require(trials.All(t => t.ChunksAtEnd > 0), "no treatment exhausted its supplied stone chunks");
            Require(trials.All(t => Count(t, ThingDefOf.MealSimple) > 0), "safe meal supply remains at observation end");
            Require(errors.Count == 0, "no native errors captured");
            foreach (var trial in trials.Skip(1))
            {
                var exposure = ThingDef.Named(trial.Source).GetModExtension<MushroomExposureProperties>();
                Require(exposure != null && trial.InitialScheduledTicks >= Mathf.RoundToInt(exposure.hallucinationHoursMin * Hour)
                    && trial.InitialScheduledTicks <= Mathf.RoundToInt(exposure.hallucinationHoursMax * Hour),
                    "actual first-standard scheduled duration lies within live species range: " + trial.Name);
                Require(trial.MushroomMentalTicks > 0, "native mushroom mental state actually observed: " + trial.Name);
            }
        }

        private static bool IsIdle(Pawn pawn) => pawn.CurJob == null || pawn.CurJobDef == JobDefOf.Wait_Wander
            || pawn.CurJobDef == JobDefOf.GotoWander || pawn.CurJobDef == JobDefOf.Wait;

        private static float MushroomMood(Pawn pawn)
        {
            if (pawn.needs?.mood?.thoughts?.memories == null) return 0f;
            return pawn.needs.mood.thoughts.memories.Memories.Where(m => m.def.defName.StartsWith("RMush_", StringComparison.Ordinal))
                .Sum(m => m.MoodOffset());
        }

        private int Count(Trial trial, ThingDef def)
        {
            int total = trial.Interior.Cells.SelectMany(c => c.GetThingList(map)).Where(t => t.def == def).Sum(t => t.stackCount);
            var pawn = trial.Pawn;
            if (pawn?.carryTracker?.CarriedThing?.def == def) total += pawn.carryTracker.CarriedThing.stackCount;
            if (pawn?.inventory != null) total += pawn.inventory.innerContainer.Where(t => t.def == def).Sum(t => t.stackCount);
            return total;
        }

        private static string State(Trial trial)
        {
            var pawn = trial.Pawn;
            return "job=" + (pawn.CurJobDef?.defName ?? "none") + ";mental=" + (pawn.MentalState?.def.defName ?? "none")
                + ";asleep=" + !pawn.Awake() + ";down=" + pawn.Downed + ";dead=" + pawn.Dead
                + ";foodPoison=" + pawn.health.hediffSet.HasHediff(HediffDefOf.FoodPoisoning);
        }

        private void Snapshot(Trial trial, int now)
        {
            var pawn = trial.Pawn;
            samples.Add(new Dictionary<string, object> {
                { "case", trial.Name }, { "tick", now }, { "elapsedHours", Hours(now - trial.StartTick) },
                { "blocksPresent", Count(trial, ThingDefOf.BlocksGranite) }, { "chunksRemaining", Count(trial, ThingDef.Named("ChunkGranite")) },
                { "safeMealsRemaining", Count(trial, ThingDefOf.MealSimple) }, { "doBillHours", Hours(trial.DoBillTicks) },
                { "recipeWorkHours", Hours(trial.RecipeWorkTicks) }, { "mentalHours", Hours(trial.MentalTicks) },
                { "sleepHours", Hours(trial.SleepTicks) }, { "downedHours", Hours(trial.DownedTicks) },
                { "foodPoisoningHours", Hours(trial.FoodPoisonTicks) }, { "state", State(trial) },
                { "food", pawn.needs.food.CurLevelPercentage }, { "rest", pawn.needs.rest.CurLevelPercentage },
                { "mood", pawn.needs.mood.CurLevelPercentage }, { "mushroomMood", MushroomMood(pawn) },
                { "generalLaborSpeed", pawn.GetStatValue(StatDefOf.GeneralLaborSpeed) },
                { "moveSpeed", pawn.GetStatValue(StatDefOf.MoveSpeed) }, { "temperature", pawn.Position.GetTemperature(map) },
                { "health", string.Join(";", pawn.health.hediffSet.hediffs.Select(h => h.def.defName + ":" + F(h.Severity))) }
            });
        }

        private void Event(Trial trial, string name, int now, string detail)
        {
            events.Add(new Dictionary<string, object> {
                { "case", trial.Name }, { "event", name }, { "tick", now },
                { "elapsedHours", trial.StartTick < 0 ? (object)null : Hours(now - trial.StartTick) }, { "detail", detail }
            });
        }

        private Dictionary<string, object> Row(Trial trial)
        {
            int blocks = trial.Complete ? trial.ProductAtEnd - trial.ProductAtStart
                : map == null || trial.Pawn == null ? 0 : Count(trial, ThingDefOf.BlocksGranite) - trial.ProductAtStart;
            int control = trials.Count > 0 && trials[0].Complete ? trials[0].ProductAtEnd - trials[0].ProductAtStart : 0;
            return new Dictionary<string, object> {
                { "case", trial.Name }, { "source", trial.Source }, { "replicatesPerTreatment", 1 },
                { "initialIngestJobId", trial.InitialIngestJobId },
                { "prepared", trial.Baseline }, { "startTick", trial.StartTick }, { "endTick", trial.EndTick },
                { "nativeTicksObserved", trial.Ticks }, { "complete", trial.Complete }, { "productsStoneBlocks", blocks },
                { "blocksRelativeToControl", control > 0 ? (object)((double)blocks / control) : null },
                { "doBillTicks", trial.DoBillTicks }, { "recipeWorkTicks", trial.RecipeWorkTicks },
                { "doBillHours", Hours(trial.DoBillTicks) }, { "recipeWorkHours", Hours(trial.RecipeWorkTicks) },
                { "mentalStateHours", Hours(trial.MentalTicks) }, { "mushroomMentalHours", Hours(trial.MushroomMentalTicks) },
                { "sleepHours", Hours(trial.SleepTicks) }, { "downedHours", Hours(trial.DownedTicks) },
                { "deadHours", Hours(trial.DeadTicks) }, { "foodPoisoningHours", Hours(trial.FoodPoisonTicks) },
                { "eatingHours", Hours(trial.EatingTicks) }, { "idleHours", Hours(trial.IdleTicks) },
                { "otherHours", Hours(trial.OtherTicks) }, { "unavailableUnionHours", Hours(trial.UnavailableUnionTicks) },
                { "initialScheduledHours", trial.InitialScheduledTicks < 0 ? (object)null : Hours(trial.InitialScheduledTicks) },
                { "firstMentalAtHours", Relative(trial, trial.FirstMentalTick) },
                { "firstMentalRecoveryAtHours", Relative(trial, trial.FirstRecoveryTick) },
                { "firstSleepAtHours", Relative(trial, trial.FirstSleepTick) },
                { "firstFoodPoisoningAtHours", Relative(trial, trial.FirstFoodPoisonTick) },
                { "firstDownedAtHours", Relative(trial, trial.FirstDownedTick) },
                { "minimumFood", trial.MinFood }, { "minimumRest", trial.MinRest },
                { "mushroomMoodPointHours", trial.MushroomMoodPointTicks / Hour },
                { "simpleMealsConsumed", trial.MealsConsumed }, { "chunksAtEnd", trial.ChunksAtEnd },
                { "stateAtEndOrFailure", trial.Pawn == null ? null : State(trial) }
            };
        }

        private static object Relative(Trial trial, int tick) => tick < 0 || trial.StartTick < 0 ? null : (object)Hours(tick - trial.StartTick);
        private static double Hours(int ticks) => ticks / (double)Hour;
        private static string F(float number) => number.ToString("0.####", CultureInfo.InvariantCulture);

        private void CaptureNativeError(string condition, string stackTrace, LogType type)
        {
            if (!finished && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert))
                if (errors.Count < 100) errors.Add(condition + "\n" + stackTrace);
        }

        private string Diagnostic() => "gameTick=" + Find.TickManager.TicksGame + ";frames=" + frameCount
            + ";speed=" + Find.TickManager.CurTimeSpeed + ";" + string.Join(" | ", trials.Select(t => t.Name
                + ";start=" + t.StartTick + ";ticks=" + t.Ticks + ";" + (t.Pawn == null ? "unspawned" : State(t))));

        private void Finish(bool successful)
        {
            if (finished) return;
            finished = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Application.logMessageReceived -= CaptureNativeError;
            status = successful && errors.Count == 0 && failures.Count == 0 ? "PASS" : "FAIL";
            try { WriteReports(true); }
            catch (Exception exception) { status = "FAIL"; Log.Error("[Mushroom Field Work Balance] report write failed: " + exception); }
            Trace("RESULT " + status + ";completed=" + trials.Count(t => t.Complete) + "/3; nativeErrors=" + errors.Count
                + ";failures=" + failures.Count + ";profile=" + Folder);
            Application.Quit(status == "PASS" ? 0 : 1);
        }

        private void WriteReports(bool complete)
        {
            Directory.CreateDirectory(Folder);
            var rows = trials.Select(Row).ToList();
            var root = new Dictionary<string, object> {
                { "schemaVersion", 1 }, { "result", complete ? status : "RUNNING" }, { "complete", complete },
                { "gameVersion", VersionControl.CurrentVersionStringWithRev }, { "commandLineOptIn", "-mushroomFieldWorkBalance" },
                { "fixturePreparationRandSeed", Seed }, { "nativeWorldSeed", Find.World.info.seedString },
                { "autosaveIntervalDays", Prefs.AutosaveIntervalDays },
                { "preparedAtTick", fixtureTick }, { "loadedModIds", loadedMods },
                { "method", "Native TickManager and PawnJob AI; native initial Ingest; autonomous Anything/Crafting AI; three independent 60,000-tick windows beginning at each actual ingestion; no manual simulation or trial-time interventions." },
                { "recipe", "Make_StoneBlocksAny" }, { "requestedTimeSpeed", "Ultrafast" },
                { "limitations", limitations }, { "failures", failures }, { "nativeErrors", errors },
                { "cases", rows }, { "hourlySamples", samples }, { "events", events }
            };
            File.WriteAllText(ResultPath, Json(root), new UTF8Encoding(false));
            WriteCsv(Path.Combine(Folder, "field-work-balance-cases.csv"), rows);
            WriteCsv(Path.Combine(Folder, "field-work-balance-hourly.csv"), samples);
            WriteCsv(Path.Combine(Folder, "field-work-balance-events.csv"), events);
            var text = new StringBuilder("More Mushrooms native field work balance: ").Append(complete ? status : "RUNNING")
                .AppendLine().AppendLine("One pawn per treatment; 24 game hours each; preparation seed " + Seed
                    + "; subsequent native RNG is not fixed by this harness")
                .AppendLine("Isolated profile autosave interval: " + F(Prefs.AutosaveIntervalDays)
                    + " game days; save/load and autosave behavior are outside this observation.")
                .AppendLine("Native stonecutting, hunger/rest/health/mental-state AI. Output ratios are this small fixture's observations, not settlement labor guarantees.");
            foreach (var trial in trials)
                text.AppendLine(trial.Name + ": observed=" + F((float)Hours(trial.Ticks)) + "h;blocks=" + Row(trial)["productsStoneBlocks"]
                    + ";DoBill=" + F((float)Hours(trial.DoBillTicks)) + "h;recipe work=" + F((float)Hours(trial.RecipeWorkTicks))
                    + "h;mental=" + F((float)Hours(trial.MentalTicks)) + "h;sleep=" + F((float)Hours(trial.SleepTicks))
                    + "h;down=" + F((float)Hours(trial.DownedTicks)) + "h;food poisoning=" + F((float)Hours(trial.FoodPoisonTicks)) + "h");
            text.AppendLine().AppendLine(string.Join(Environment.NewLine, limitations));
            if (failures.Count > 0) text.AppendLine("FAILURES:").AppendLine(string.Join(Environment.NewLine, failures));
            if (errors.Count > 0) text.AppendLine("NATIVE ERRORS:").AppendLine(string.Join(Environment.NewLine, errors));
            File.WriteAllText(Path.Combine(Folder, "field-work-balance-report.txt"), text.ToString(), new UTF8Encoding(false));
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

        private static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";

        private static string Json(object value)
        {
            if (value == null) return "null";
            if (value is string str)
            {
                var output = new StringBuilder("\"");
                foreach (char c in str)
                    if (c == '\\') output.Append("\\\\");
                    else if (c == '"') output.Append("\\\"");
                    else if (c == '\n') output.Append("\\n");
                    else if (c == '\r') output.Append("\\r");
                    else if (c == '\t') output.Append("\\t");
                    else if (c < 32) output.Append("\\u" + ((int)c).ToString("x4"));
                    else output.Append(c);
                return output.Append('"').ToString();
            }
            if (value is bool flag) return flag ? "true" : "false";
            if (value is IDictionary dictionary)
            {
                var fields = new List<string>();
                foreach (DictionaryEntry entry in dictionary) fields.Add(Json(entry.Key.ToString()) + ":" + Json(entry.Value));
                return "{" + string.Join(",", fields) + "}";
            }
            if (value is IEnumerable sequence)
            {
                var fields = new List<string>();
                foreach (var item in sequence) fields.Add(Json(item));
                return "[" + string.Join(",", fields) + "]";
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail);
        }
        private static void Trace(string detail) => Log.Message("[Mushroom Field Work Balance] " + detail);
    }
}
