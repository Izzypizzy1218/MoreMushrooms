using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMushrooms
{
    // These are game balance profiles, rather than claims about real fruiting calendars.
    public sealed class MushroomEcologyExtension : DefModExtension
    {
        public float springFactor = 1f;
        public float summerFactor = 1f;
        public float fallFactor = 1f;
        public float winterFactor = 0.35f;
        public bool ringEligible;
        public float regrowthChance;
        public float regrowthDelayDays = 4f;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (float factor in new[] { springFactor, summerFactor, fallFactor, winterFactor })
                if (!WildMushroomEcology.Finite(factor) || factor < 0f || factor > 5f)
                    yield return "Mushroom seasonal emergence factors must be finite and between 0 and 5.";
            if (!WildMushroomEcology.Finite(regrowthChance) || regrowthChance < 0f || regrowthChance > 1f)
                yield return "Mushroom regrowthChance must be between 0 and 1.";
            if (!WildMushroomEcology.Finite(regrowthDelayDays) || regrowthDelayDays < 1f || regrowthDelayDays > 60f)
                yield return "Mushroom regrowthDelayDays must be between 1 and 60.";
        }
    }

    public sealed class WildMushroomEcologyDef : Def
    {
        public int checkIntervalTicks = 2500;
        public int sampleAttempts = 24;
        public int dailyExtraBudget = 8;
        public float wildCapPer10000Cells = 80f;
        public int minWildCap = 80;
        public int maxWildCap = 1500;
        public float ringChancePerCheck = 0.015f;
        public float ringCooldownDays = 3f;
        public int ringMinCount = 4;
        public int ringMaxCount = 8;
        public float ringMinRadius = 2.5f;
        public float ringMaxRadius = 4.5f;
        public int maxPendingRegrowth = 64;
        public float regrowthExpiryDays = 8f;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (checkIntervalTicks < 250 || checkIntervalTicks > GenDate.TicksPerDay || sampleAttempts < 1 || sampleAttempts > 128)
                yield return "Wild mushroom checks must use a bounded interval and sample count.";
            if (dailyExtraBudget < 0 || dailyExtraBudget > 64 || minWildCap < 0 || maxWildCap < minWildCap || maxWildCap > 2000
                || !WildMushroomEcology.Finite(wildCapPer10000Cells) || wildCapPer10000Cells < 0f || wildCapPer10000Cells > 100f)
                yield return "Wild mushroom population and daily extra budgets must be bounded.";
            if (!WildMushroomEcology.Finite(ringChancePerCheck) || ringChancePerCheck < 0f || ringChancePerCheck > 1f
                || !WildMushroomEcology.Finite(ringCooldownDays) || ringCooldownDays < 1f || ringCooldownDays > 60f)
                yield return "Wild mushroom ring chance and cooldown are invalid.";
            if (ringMinCount < 3 || ringMaxCount < ringMinCount || ringMaxCount > 16
                || !WildMushroomEcology.Finite(ringMinRadius) || !WildMushroomEcology.Finite(ringMaxRadius)
                || ringMinRadius < 2f || ringMaxRadius < ringMinRadius || ringMaxRadius > 8f)
                yield return "Wild mushroom ring dimensions are invalid.";
            if (maxPendingRegrowth < 0 || maxPendingRegrowth > 256 || !WildMushroomEcology.Finite(regrowthExpiryDays)
                || regrowthExpiryDays < 1f || regrowthExpiryDays > 60f)
                yield return "Wild mushroom pending regrowth limits are invalid.";
        }
    }

    public sealed class MushroomMycelium : IExposable
    {
        public ThingDef plantDef;
        public IntVec3 cell;
        public string sourceId;
        public int dueTick;
        public int expiryTick;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref plantDef, "plantDef");
            Scribe_Values.Look(ref cell, "cell");
            Scribe_Values.Look(ref sourceId, "sourceId");
            Scribe_Values.Look(ref dueTick, "dueTick");
            Scribe_Values.Look(ref expiryTick, "expiryTick");
        }
    }

    [StaticConstructorOnStartup]
    public static class WildMushroomEcology
    {
        private static readonly WildMushroomEcologyDef fallback = new WildMushroomEcologyDef();
        private static readonly Func<WildPlantSpawner, ThingDef, float> nativeCommonality =
            AccessTools.MethodDelegate<Func<WildPlantSpawner, ThingDef, float>>(AccessTools.Method(typeof(WildPlantSpawner), "GetCommonalityOfPlant"));

        static WildMushroomEcology()
        {
            var harmony = new Harmony("izzypizzy.rimmushrooms.wild-ecology");
            harmony.Patch(AccessTools.Method(typeof(WildPlantSpawner), "PlantChoiceWeight"),
                postfix: new HarmonyMethod(typeof(WildMushroomEcology), nameof(PlantChoiceWeightPostfix)));
            // The native harvest driver emits this notification only for a successful,
            // positive yield. PlantCollected alone also runs when the harvest fails.
            harmony.Patch(AccessTools.Method(typeof(QuestManager), nameof(QuestManager.Notify_PlantHarvested)),
                postfix: new HarmonyMethod(typeof(WildMushroomEcology), nameof(SuccessfulHarvestPostfix)));
        }

        public static WildMushroomEcologyDef Settings => DefDatabase<WildMushroomEcologyDef>.GetNamedSilentFail("RMush_WildEcology") ?? fallback;
        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public static bool IsMushroom(ThingDef def) => def?.thingClass != null && typeof(Plant_Mushroom).IsAssignableFrom(def.thingClass);
        public static float NativeCommonality(ThingDef def, Map map) => def == null || map?.wildPlantSpawner == null ? 0f : nativeCommonality(map.wildPlantSpawner, def);
        public static bool IsNativeSpeciesAt(ThingDef def, IntVec3 cell, Map map) => map != null && cell.InBounds(map)
            && (map.BiomeAt(cell).AllWildPlants.Contains(def) || map.wildPlantSpawner.MutatorWildPlants.Contains(def));

        public static float SeasonMultiplier(ThingDef def, Season season, bool tropical = false)
        {
            var profile = def?.GetModExtension<MushroomEcologyExtension>();
            if (profile == null || tropical) return 1f;
            float factor;
            switch (season)
            {
                case Season.Spring: factor = profile.springFactor; break;
                case Season.Summer:
                case Season.PermanentSummer: factor = profile.summerFactor; break;
                case Season.Fall: factor = profile.fallFactor; break;
                case Season.Winter:
                case Season.PermanentWinter: factor = profile.winterFactor; break;
                default: factor = 1f; break;
            }
            return Finite(factor) ? Mathf.Clamp(factor, 0f, 5f) : 1f;
        }

        public static float SeasonMultiplierAt(ThingDef def, Map map)
        {
            if (map == null) return 1f;
            string biome = map.Biome?.defName ?? "";
            bool tropical = biome == "TropicalRainforest" || biome == "TropicalSwamp";
            // GenLocalDate already accounts for the hemisphere and permanent seasons.
            return SeasonMultiplier(def, GenLocalDate.Season(map), tropical);
        }

        public static float EffectiveWildWeight(ThingDef def, Map map, float baseWeight)
        {
            if (!IsMushroom(def)) return baseWeight;
            return Mathf.Max(0f, baseWeight) * SeasonMultiplierAt(def, map);
        }

        public static void PlantChoiceWeightPostfix(ThingDef plantDef, IntVec3 c, Map ___map, ref float __result)
        {
            if (!IsMushroom(plantDef)) return;
            var ecology = ___map.GetComponent<MapComponent_WildMushrooms>();
            // This draw is used by both map generation and native ongoing regrowth.
            // Existing plants and player sowing never pass through this method.
            __result = ecology == null || ecology.WildPopulationCount >= ecology.WildCap || !ecology.IsEligibleCell(c, plantDef)
                ? 0f : EffectiveWildWeight(plantDef, ___map, __result);
        }

        public static void SuccessfulHarvestPostfix(Pawn __0, Thing __1)
        {
            Pawn harvester = __0;
            Thing harvested = __1;
            if (!(harvester?.jobs?.curDriver is JobDriver_PlantHarvest)) return;
            var plant = harvester.CurJob?.GetTarget(Verse.AI.TargetIndex.A).Thing as Plant_Mushroom;
            if (plant != null && harvested?.def == plant.def.plant.harvestedThingDef && harvested.stackCount > 0)
                plant.NotifySuccessfulHarvest();
        }

        public static List<IntVec3> RingCells(IntVec3 center, float radius, int count, bool halfRing, float angle = 0f)
        {
            var result = new List<IntVec3>();
            if (!Finite(radius) || !Finite(angle) || radius < 2f || radius > 8f || count < 3 || count > 16) return result;
            float arc = halfRing ? Mathf.PI : 2f * Mathf.PI;
            for (int i = 0; i < count; i++)
            {
                float a = angle + arc * i / (halfRing ? count - 1 : count);
                var cell = center + new IntVec3(Mathf.RoundToInt(Mathf.Cos(a) * radius), 0, Mathf.RoundToInt(Mathf.Sin(a) * radius));
                if (!result.Contains(cell)) result.Add(cell);
            }
            return result;
        }
    }

    public sealed class MapComponent_WildMushrooms : MapComponent
    {
        private readonly HashSet<Plant_Mushroom> plants = new HashSet<Plant_Mushroom>();
        private List<MushroomMycelium> mycelia = new List<MushroomMycelium>();
        private int randomState;
        private int nextCheckTick;
        private int nextRingTick;
        private int budgetDay = -1;
        private int extraSpawnedToday;
        private int cachedPopulation;
        private int populationCacheTick = -1;

        public MapComponent_WildMushrooms(Map map) : base(map) { }

        public WildMushroomEcologyDef Settings => WildMushroomEcology.Settings;
        public int WildCap => Mathf.Clamp(Mathf.RoundToInt(map.cellIndices.NumGridCells / 10000f * Settings.wildCapPer10000Cells),
            Mathf.Max(0, Settings.minWildCap), Mathf.Max(Settings.minWildCap, Settings.maxWildCap));
        public int ExtraSpawnedToday { get { RefreshBudget(Find.TickManager.TicksGame); return extraSpawnedToday; } }
        public int PendingRegrowthCount => mycelia.Count;
        public IReadOnlyList<MushroomMycelium> PendingRegrowth => mycelia;
        public int RandomState => randomState;
        public int NextRingTick => nextRingTick;
        public int WildPopulationCount
        {
            get
            {
                int now = Find.TickManager.TicksGame;
                if (populationCacheTick != now)
                {
                    cachedPopulation = plants.Count(p => p.Spawned && p.Map == map && !p.sown);
                    populationCacheTick = now;
                }
                return cachedPopulation;
            }
        }

        public void Register(Plant_Mushroom plant)
        {
            if (plant != null) plants.Add(plant);
            populationCacheTick = -1;
        }

        public void Unregister(Plant_Mushroom plant)
        {
            if (plant != null) plants.Remove(plant);
            populationCacheTick = -1;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            plants.Clear();
            foreach (var plant in map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant_Mushroom>()) plants.Add(plant);
            populationCacheTick = -1;
            InitializeSchedule(Find.TickManager.TicksGame);
        }

        public override void MapGenerated()
        {
            base.MapGenerated();
            InitializeSchedule(Find.TickManager.TicksGame);
        }

        private void InitializeSchedule(int now)
        {
            if (randomState == 0) randomState = unchecked(map.uniqueID * 1103515245 + 12345);
            if (randomState == 0) randomState = 1;
            if (nextCheckTick <= 0) nextCheckTick = now + 1 + Mathf.Abs(map.uniqueID % Mathf.Max(250, Settings.checkIntervalTicks));
            if (nextRingTick <= 0) nextRingTick = now + DaysToTicks(Settings.ringCooldownDays);
            RefreshBudget(now);
        }

        public override void MapComponentTick()
        {
            int now = Find.TickManager.TicksGame;
            if (now >= nextCheckTick) TickEcology(now);
        }

        public void TickEcology(int now)
        {
            InitializeSchedule(now);
            nextCheckTick = now + Mathf.Clamp(Settings.checkIntervalTicks, 250, GenDate.TicksPerDay);
            ProcessRegrowth(now);
            if (now < nextRingTick || RemainingBudget(now) < Settings.ringMinCount || WildPopulationCount >= WildCap) return;
            if (NextFloat() >= Mathf.Clamp01(Settings.ringChancePerCheck)) return;
            for (int i = 0; i < Mathf.Clamp(Settings.sampleAttempts, 1, 128); i++)
            {
                IntVec3 center = map.cellIndices.IndexToCell(NextInt(map.cellIndices.NumGridCells));
                ThingDef species = PickRingSpecies(center);
                if (species == null) continue;
                if (TrySpawnRing(center, species, NextFloat() < 0.5f) > 0)
                {
                    nextRingTick = now + DaysToTicks(Settings.ringCooldownDays);
                    break;
                }
            }
        }

        private ThingDef PickRingSpecies(IntVec3 center)
        {
            var candidates = map.wildPlantSpawner.AllWildPlants.Where(d => IsRingSpecies(d) && WildMushroomEcology.IsNativeSpeciesAt(d, center, map)).ToList();
            float total = 0f;
            foreach (var def in candidates)
                total += WildMushroomEcology.EffectiveWildWeight(def, map, WildMushroomEcology.NativeCommonality(def, map));
            float choice = NextFloat() * total;
            if (total <= 0f) return null;
            foreach (var def in candidates)
            {
                choice -= WildMushroomEcology.EffectiveWildWeight(def, map, WildMushroomEcology.NativeCommonality(def, map));
                if (choice <= 0f) return def;
            }
            return null;
        }

        private static bool IsRingSpecies(ThingDef def)
        {
            if (!WildMushroomEcology.IsMushroom(def) || def.GetModExtension<MushroomEcologyExtension>()?.ringEligible != true) return false;
            var exposure = def.plant?.harvestedThingDef?.GetModExtension<MushroomExposureProperties>();
            return exposure == null || (exposure.poisonHediff == null && !exposure.psychoactive);
        }

        public bool IsEligibleCell(IntVec3 cell, ThingDef def) => IsEligibleHabitat(cell, def, null, true);

        public bool IsPreservedRegrowthHabitat(IntVec3 cell, ThingDef def)
        {
            if (!WildMushroomEcology.IsMushroom(def) || !cell.InBounds(map) || cell.Roofed(map) || !cell.Walkable(map)
                || cell.GetPlantToGrowSettable(map) != null || map.zoneManager.ZoneAt(cell) is Zone_Growing) return false;
            var terrain = cell.GetTerrain(map);
            if (terrain == null || terrain.IsFloor || terrain.IsWater || terrain.IsIce || terrain.fertility < def.plant.fertilityMin
                || map.fertilityGrid.FertilityAt(cell) < def.plant.fertilityMin) return false;
            // Soil conversion or construction destroys the saved patch. A passing
            // pawn, fallen harvest, other plant, snow or cold only delays fruiting.
            return !cell.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame);
        }

        private bool IsEligibleHabitat(IntVec3 cell, ThingDef def, Plant ignorePlant, bool requireGrowingTemperature)
        {
            if (!IsPreservedRegrowthHabitat(cell, def) || !PlantUtility.SnowAllowsPlanting(cell, map)
                || !PlantUtility.SandAllowsPlanting(cell, map)) return false;
            foreach (Thing thing in cell.GetThingList(map))
                if (thing != ignorePlant && (thing is Plant || thing is Building || thing is Blueprint || thing is Frame || thing is Pawn
                    || thing is Fire || thing.def.category == ThingCategory.Item)) return false;
            if (GenSpawn.WouldWipeAnythingWith(cell, Rot4.North, def, map, t => t != ignorePlant)) return false;
            if (requireGrowingTemperature)
            {
                float temperature = cell.GetTemperature(map);
                if (!WildMushroomEcology.Finite(temperature) || temperature <= def.plant.minGrowthTemperature || temperature >= def.plant.maxGrowthTemperature) return false;
            }
            return ignorePlant != null || def.CanEverPlantAt(cell, map);
        }

        public Plant_Mushroom TrySpawnWild(ThingDef def, IntVec3 cell, float growth = 0.15f, bool regenerated = false)
        {
            int now = Find.TickManager.TicksGame;
            if (RemainingBudget(now) <= 0 || WildPopulationCount >= WildCap || !IsEligibleCell(cell, def)
                || !WildMushroomEcology.IsNativeSpeciesAt(def, cell, map) || WildMushroomEcology.NativeCommonality(def, map) <= 0f
                || WildMushroomEcology.SeasonMultiplierAt(def, map) <= 0f) return null;
            var plant = ThingMaker.MakeThing(def) as Plant_Mushroom;
            if (plant == null) return null;
            plant.Growth = Mathf.Clamp(growth, 0.05f, 0.3f);
            plant.SetWildRegrowthGeneration(regenerated ? 1 : 0);
            // Empty candidate cells are checked above, including WouldWipeAnythingWith.
            // No existing thing is removed to make space for this feature.
            GenSpawn.Spawn(plant, cell, map, WipeMode.Vanish);
            extraSpawnedToday++;
            populationCacheTick = -1;
            return plant;
        }

        public int TrySpawnRing(IntVec3 center, ThingDef def, bool halfRing = false, int count = 0)
        {
            int now = Find.TickManager.TicksGame;
            if (!IsRingSpecies(def) || WildMushroomEcology.NativeCommonality(def, map) <= 0f) return 0;
            int requested = count > 0 ? Mathf.Clamp(count, Settings.ringMinCount, Settings.ringMaxCount)
                : Settings.ringMinCount + NextInt(Settings.ringMaxCount - Settings.ringMinCount + 1);
            requested = Mathf.Min(requested, RemainingBudget(now), WildCap - WildPopulationCount);
            if (requested < Settings.ringMinCount) return 0;
            float radius = Mathf.Lerp(Settings.ringMinRadius, Settings.ringMaxRadius, NextFloat());
            var cells = WildMushroomEcology.RingCells(center, radius, requested, halfRing, NextFloat() * 2f * Mathf.PI);
            // Require a readable complete ring/arc. An obstructed pattern is skipped
            // rather than clearing vegetation, creating a line, or moving its cells.
            if (cells.Count != requested || cells.Any(c => !IsEligibleCell(c, def))) return 0;
            int spawned = 0;
            foreach (var cell in cells)
                if (TrySpawnWild(def, cell, 0.1f + NextFloat() * 0.1f) != null) spawned++;
            return spawned;
        }

        public bool TryQueueRegrowth(Plant_Mushroom plant, bool bypassChance = false)
        {
            var profile = plant?.def.GetModExtension<MushroomEcologyExtension>();
            if (plant == null || !plant.Spawned || plant.Map != map || plant.sown || plant.WildRegrowthGeneration != 0 || plant.WildRegrowthSpent
                || profile == null || profile.regrowthChance <= 0f || !plant.HarvestableNow || !plant.CanYieldNow()
                || WildMushroomEcology.NativeCommonality(plant.def, map) <= 0f
                || !IsPreservedRegrowthHabitat(plant.Position, plant.def)) return false;
            // Every original wild plant gets one attempt, including a failed roll.
            plant.MarkWildRegrowthSpent();
            if (mycelia.Count >= Mathf.Clamp(Settings.maxPendingRegrowth, 0, 256)
                || mycelia.Any(m => m.cell == plant.Position || m.sourceId == plant.ThingID)
                || (!bypassChance && NextFloat() >= Mathf.Clamp01(profile.regrowthChance))) return false;
            int due = Find.TickManager.TicksGame + DaysToTicks(profile.regrowthDelayDays);
            mycelia.Add(new MushroomMycelium { plantDef = plant.def, cell = plant.Position, sourceId = plant.ThingID,
                dueTick = due, expiryTick = due + DaysToTicks(Settings.regrowthExpiryDays) });
            return true;
        }

        public void ProcessRegrowth(int now)
        {
            RefreshBudget(now);
            for (int i = mycelia.Count - 1; i >= 0; i--)
            {
                var mycelium = mycelia[i];
                if (mycelium == null || mycelium.plantDef == null || !mycelium.cell.InBounds(map) || now > mycelium.expiryTick
                    || !IsPreservedRegrowthHabitat(mycelium.cell, mycelium.plantDef))
                { mycelia.RemoveAt(i); continue; }
                if (now < mycelium.dueTick || RemainingBudget(now) <= 0 || WildPopulationCount >= WildCap) continue;
                if (TrySpawnWild(mycelium.plantDef, mycelium.cell, 0.1f, true) != null) mycelia.RemoveAt(i);
            }
        }

        private int RemainingBudget(int now)
        {
            RefreshBudget(now);
            return Mathf.Max(0, Mathf.Clamp(Settings.dailyExtraBudget, 0, 64) - extraSpawnedToday);
        }

        private void RefreshBudget(int now)
        {
            int today = Mathf.Max(0, now / GenDate.TicksPerDay);
            if (budgetDay == today) return;
            budgetDay = today;
            extraSpawnedToday = 0;
        }

        private static int DaysToTicks(float days) => Mathf.RoundToInt(Mathf.Clamp(WildMushroomEcology.Finite(days) ? days : 1f, 1f, 60f) * GenDate.TicksPerDay);
        private float NextFloat()
        {
            if (randomState == 0) randomState = unchecked(map.uniqueID * 1103515245 + 12345);
            uint x = (uint)randomState;
            if (x == 0) x = 1;
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            randomState = unchecked((int)x);
            return (x >> 8) / 16777216f;
        }
        private int NextInt(int exclusiveMax) => exclusiveMax <= 1 ? 0 : Mathf.Min(exclusiveMax - 1, (int)(NextFloat() * exclusiveMax));

        public override void ExposeData()
        {
            Scribe_Values.Look(ref randomState, "mushroomEcologyRandomState");
            Scribe_Values.Look(ref nextCheckTick, "mushroomEcologyNextCheckTick");
            Scribe_Values.Look(ref nextRingTick, "mushroomEcologyNextRingTick");
            Scribe_Values.Look(ref budgetDay, "mushroomEcologyBudgetDay", -1);
            Scribe_Values.Look(ref extraSpawnedToday, "mushroomEcologyExtraSpawnedToday");
            Scribe_Collections.Look(ref mycelia, "mushroomMycelia", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                int now = Find.TickManager.TicksGame;
                if (mycelia == null) mycelia = new List<MushroomMycelium>();
                var seenSources = new HashSet<string>();
                var seenCells = new HashSet<IntVec3>();
                mycelia = mycelia.Where(m => m != null && WildMushroomEcology.IsMushroom(m.plantDef)
                    && m.plantDef.GetModExtension<MushroomEcologyExtension>()?.regrowthChance > 0f && m.cell.InBounds(map)
                    && !m.sourceId.NullOrEmpty() && m.sourceId.Length <= 128 && m.dueTick >= 0 && m.expiryTick >= m.dueTick
                    && m.expiryTick >= now && m.dueTick <= now + 60 * GenDate.TicksPerDay && m.expiryTick <= now + 120 * GenDate.TicksPerDay
                    && seenSources.Add(m.sourceId) && seenCells.Add(m.cell)).Take(Mathf.Clamp(Settings.maxPendingRegrowth, 0, 256)).ToList();
                extraSpawnedToday = Mathf.Clamp(extraSpawnedToday, 0, Mathf.Clamp(Settings.dailyExtraBudget, 0, 64));
                if (budgetDay > now / GenDate.TicksPerDay) { budgetDay = now / GenDate.TicksPerDay; extraSpawnedToday = Mathf.Clamp(Settings.dailyExtraBudget, 0, 64); }
                if (nextCheckTick > now + GenDate.TicksPerDay) nextCheckTick = now + Mathf.Clamp(Settings.checkIntervalTicks, 250, GenDate.TicksPerDay);
                if (nextRingTick > now + 60 * GenDate.TicksPerDay) nextRingTick = now + DaysToTicks(Settings.ringCooldownDays);
                InitializeSchedule(now);
            }
        }
    }

    public static class WildMushroomEcologyDebug
    {
        [DebugAction("More Mushrooms", "Spawn wild mushroom ring", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void SpawnRing()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;
            var species = map.wildPlantSpawner.AllWildPlants.FirstOrDefault(d => d.GetModExtension<MushroomEcologyExtension>()?.ringEligible == true);
            int spawned = species == null ? 0 : map.GetComponent<MapComponent_WildMushrooms>().TrySpawnRing(UI.MouseCell(), species);
            Log.Message("[More Mushrooms] Wild ring spawned " + spawned + " plants; normal habitat, population and daily budgets apply.");
        }
    }
}
