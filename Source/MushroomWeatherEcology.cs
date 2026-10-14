using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMushrooms
{
    // All special fruiting consumes the existing map-wide extra budget. The
    // frequencies are provisional until the requested final integration pass.
    public sealed partial class MapComponent_WildMushrooms
    {
        private int nextEnvironmentTick;
        private int nextTreeAssistTick;
        private int nextRainClusterTick;
        private int rainStartedTick = -1;
        private int lastRainSampleTick = -1;
        private int rainWindowUntil = -1;
        private bool rainQualified;
        private bool rainEpisodeUsed;

        public int RainWindowUntil => rainWindowUntil;
        public bool RainEpisodeUsed => rainEpisodeUsed;
        public int NextEnvironmentTick => nextEnvironmentTick;
        public int NextTreeAssistTick => nextTreeAssistTick;

        private void InitializeSpecialSchedule(int now)
        {
            if (nextEnvironmentTick <= 0) nextEnvironmentTick = now + DaysToTicks(Settings.environmentCooldownDays);
            if (nextTreeAssistTick <= 0) nextTreeAssistTick = now + DaysToTicks(Settings.treeAssistCooldownDays);
        }

        private void TickSpecialEcology(int now)
        {
            ObserveRain(now);
            if (RemainingBudget(now) <= 0 || WildPopulationCount >= WildCap) return;
            if (now >= nextEnvironmentTick && NextFloat() < Mathf.Clamp01(Settings.environmentChancePerCheck))
            {
                if (TryAnchoredEmergence(false, Settings.environmentMinCount, Settings.environmentMaxCount) > 0)
                    nextEnvironmentTick = now + DaysToTicks(Settings.environmentCooldownDays);
            }
            if (RemainingBudget(now) <= 0 || WildPopulationCount >= WildCap) return;
            if (now >= nextTreeAssistTick && NextFloat() < Mathf.Clamp01(Settings.treeAssistChancePerCheck))
            {
                if (TryAnchoredEmergence(true, Settings.treeAssistMinCount, Settings.treeAssistMaxCount) > 0)
                    nextTreeAssistTick = now + DaysToTicks(Settings.treeAssistCooldownDays);
            }
            if (RemainingBudget(now) < Settings.rainClusterMinCount || WildPopulationCount >= WildCap
                || IsRaining || rainStartedTick >= 0 || rainWindowUntil < now || rainEpisodeUsed
                || now < nextRainClusterTick || NextFloat() >= Mathf.Clamp01(Settings.rainChancePerCheck)) return;
            int spawned = TryRainCluster();
            if (spawned > 0)
            {
                rainEpisodeUsed = true;
                nextRainClusterTick = now + DaysToTicks(Settings.rainCooldownDays);
            }
        }

        private bool IsRaining => map.weatherManager != null && map.weatherManager.RainRate >= 0.1f;
        private static int HoursToTicks(float hours) => Mathf.RoundToInt(Mathf.Clamp(hours, 1f, 72f) * GenDate.TicksPerHour);

        private void ObserveRain(int now)
        {
            // An unloaded map or old save has no measured rain history. Never
            // infer an entire storm from the weather at the moment it is loaded.
            int interval = Mathf.Clamp(Settings.checkIntervalTicks, 250, GenDate.TicksPerDay);
            if (lastRainSampleTick < 0 || now < lastRainSampleTick || now - lastRainSampleTick > interval * 2)
            {
                rainStartedTick = -1;
                rainQualified = false;
            }
            lastRainSampleTick = now;
            if (IsRaining)
            {
                if (rainStartedTick < 0) rainStartedTick = now;
                if (now - rainStartedTick >= HoursToTicks(Settings.rainMinHours))
                {
                    if (!rainQualified) { rainQualified = true; rainEpisodeUsed = false; }
                    rainWindowUntil = now + HoursToTicks(Settings.rainWindowHours);
                }
            }
            else
            {
                if (rainQualified) rainWindowUntil = now + HoursToTicks(Settings.rainWindowHours);
                rainStartedTick = -1;
                rainQualified = false;
            }
        }

        private List<ThingDef> SpecialSpecies(bool trees)
        {
            return map.wildPlantSpawner.AllWildPlants.Where(d =>
            {
                if (!WildMushroomEcology.IsMushroom(d) || WildMushroomEcology.NativeCommonality(d, map) <= 0f
                    || WildMushroomEcology.SeasonMultiplierAt(d, map) <= 0f) return false;
                var profile = d.GetModExtension<MushroomEcologyExtension>();
                if (profile == null) return false;
                return trees ? !profile.habitatTree.NullOrEmpty()
                    : profile.habitat == "insectHost" || profile.habitat == "indoorSoil" || profile.habitat == "exposedWood";
            }).Distinct().ToList();
        }

        private ThingDef PickSpecialSpecies(List<ThingDef> candidates, bool scarceTrees)
        {
            if (candidates.Count == 0) return null;
            var weights = new List<float>(candidates.Count);
            float total = 0f;
            foreach (ThingDef def in candidates)
            {
                // Assist only the six original tree profiles. Less represented
                // species receive more chances without altering native weights.
                float weight = scarceTrees
                    ? WildMushroomEcology.SeasonMultiplierAt(def, map) / (1f + plants.Count(p => p.Spawned && p.Map == map && !p.sown && p.def == def))
                    : WildMushroomEcology.EffectiveWildWeight(def, map, WildMushroomEcology.NativeCommonality(def, map));
                weights.Add(weight);
                total += weight;
            }
            if (total <= 0f) return null;
            float choice = NextFloat() * total;
            for (int i = 0; i < candidates.Count; i++)
            {
                choice -= weights[i];
                if (choice <= 0f) return candidates[i];
            }
            return candidates[candidates.Count - 1];
        }

        private int TryAnchoredEmergence(bool trees, int minimum, int maximum)
        {
            var candidates = SpecialSpecies(trees);
            while (candidates.Count > 0)
            {
                ThingDef species = PickSpecialSpecies(candidates, trees);
                if (species == null) break;
                candidates.Remove(species);
                var cells = MushroomHabitatRules.FindAnchorCells(species, map, 128).Distinct().Take(128).ToList();
                if (cells.Count == 0) continue;
                int offset = NextInt(cells.Count);
                for (int i = 0; i < cells.Count; i++)
                {
                    IntVec3 center = cells[(i + offset) % cells.Count];
                    if (!IsEligibleCell(center, species) || !WildMushroomEcology.IsNativeSpeciesAt(species, center, map)) continue;
                    int count = TrySpawnCompactCluster(species, center, minimum, maximum);
                    if (count > 0) return count;
                }
            }
            return 0;
        }

        private int TryRainCluster()
        {
            var candidates = map.wildPlantSpawner.AllWildPlants.Where(d => WildMushroomEcology.IsMushroom(d)
                && d.GetModExtension<MushroomEcologyExtension>()?.habitat != "indoorSoil"
                && WildMushroomEcology.NativeCommonality(d, map) > 0f).Distinct().ToList();
            for (int attempt = 0; attempt < Mathf.Clamp(Settings.sampleAttempts, 1, 128); attempt++)
            {
                IntVec3 center = map.cellIndices.IndexToCell(NextInt(map.cellIndices.NumGridCells));
                var eligible = candidates.Where(d => WildMushroomEcology.IsNativeSpeciesAt(d, center, map)
                    && IsEligibleCell(center, d) && (!center.Roofed(map) || d.GetModExtension<MushroomEcologyExtension>()?.habitat == "exposedWood")).ToList();
                ThingDef species = PickSpecialSpecies(eligible, false);
                if (species == null) continue;
                int spawned = TrySpawnCompactCluster(species, center, Settings.rainClusterMinCount, Settings.rainClusterMaxCount);
                if (spawned > 0) return spawned;
            }
            return 0;
        }

        private int TrySpawnCompactCluster(ThingDef species, IntVec3 center, int minimum, int maximum)
        {
            int now = Find.TickManager.TicksGame;
            int requested = minimum + NextInt(Mathf.Max(1, maximum - minimum + 1));
            requested = Mathf.Min(requested, RemainingBudget(now), WildCap - WildPopulationCount);
            if (requested < minimum || !IsEligibleCell(center, species) || HasExistingNeighbour(species, center)) return 0;
            var cells = new List<IntVec3> { center };
            while (cells.Count < requested)
            {
                var adjacent = cells.SelectMany(c => GenAdj.AdjacentCells.Select(d => c + d)).Distinct()
                    .Where(c => c.InBounds(map) && !cells.Contains(c) && map.BiomeAt(c) == map.BiomeAt(center)
                        && WildMushroomEcology.IsNativeSpeciesAt(species, c, map) && IsEligibleCell(c, species)
                        && !HasExistingNeighbour(species, c) && CompactShape(cells, c)).ToList();
                if (adjacent.Count == 0) return 0;
                cells.Add(adjacent[NextInt(adjacent.Count)]);
            }
            int count = 0;
            foreach (IntVec3 cell in cells)
                if (TrySpawnWild(species, cell, 0.1f + NextFloat() * 0.1f) != null) count++;
            return count;
        }

        private bool HasExistingNeighbour(ThingDef species, IntVec3 cell)
            => GenAdj.AdjacentCells.Any(offset => (cell + offset).InBounds(map)
                && (cell + offset).GetThingList(map).OfType<Plant_Mushroom>().Any(p => p.def == species));

        private static bool CompactShape(List<IntVec3> cells, IntVec3 addition)
        {
            var shape = new List<IntVec3>(cells) { addition };
            if (shape.Max(c => c.x) - shape.Min(c => c.x) > 3 || shape.Max(c => c.z) - shape.Min(c => c.z) > 3) return false;
            if (shape.Count < 3) return true;
            IntVec3 a = shape[0], b = shape[1];
            if (shape.Skip(2).All(c => (b.x - a.x) * (c.z - a.z) == (b.z - a.z) * (c.x - a.x))) return false;
            var occupied = new HashSet<IntVec3>(shape);
            return !shape.SelectMany(c => GenAdj.CardinalDirections.Select(d => c + d)).Any(center => !occupied.Contains(center)
                && GenAdj.CardinalDirections.All(d => occupied.Contains(center + d)));
        }

        private void ExposeSpecialData()
        {
            Scribe_Values.Look(ref nextEnvironmentTick, "mushroomEnvironmentNextTick");
            Scribe_Values.Look(ref nextTreeAssistTick, "mushroomTreeAssistNextTick");
            Scribe_Values.Look(ref nextRainClusterTick, "mushroomRainNextTick");
            Scribe_Values.Look(ref rainStartedTick, "mushroomRainStartedTick", -1);
            Scribe_Values.Look(ref lastRainSampleTick, "mushroomRainLastSampleTick", -1);
            Scribe_Values.Look(ref rainWindowUntil, "mushroomRainWindowUntil", -1);
            Scribe_Values.Look(ref rainQualified, "mushroomRainQualified");
            Scribe_Values.Look(ref rainEpisodeUsed, "mushroomRainEpisodeUsed");
            if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
            int now = Find.TickManager.TicksGame;
            if (rainStartedTick > now || rainStartedTick < -1) rainStartedTick = -1;
            if (lastRainSampleTick > now || lastRainSampleTick < -1) lastRainSampleTick = -1;
            if (rainWindowUntil > now + HoursToTicks(Settings.rainWindowHours) || rainWindowUntil < -1) rainWindowUntil = -1;
            if (nextEnvironmentTick > now + 60 * GenDate.TicksPerDay || nextEnvironmentTick < 0) nextEnvironmentTick = 0;
            if (nextTreeAssistTick > now + 60 * GenDate.TicksPerDay || nextTreeAssistTick < 0) nextTreeAssistTick = 0;
            if (nextRainClusterTick > now + 60 * GenDate.TicksPerDay || nextRainClusterTick < 0) nextRainClusterTick = 0;
        }
    }
}
