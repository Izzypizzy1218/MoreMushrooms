using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMushrooms
{
    // Reuses only native-selected plants. No extra plants, growth rolls, ecology
    // budget or saved cluster ownership are needed for this spatial arrangement.
    internal static class MushroomSmallClusters
    {
        public static List<Plant_Mushroom> Connected(Map map, ThingDef def, IntVec3 root, int limit)
        {
            var result = new List<Plant_Mushroom>();
            if (map == null || def == null || !root.InBounds(map)) return result;
            limit = Mathf.Clamp(limit, 1, 256);
            var seen = new HashSet<Plant_Mushroom>();
            var pending = new Queue<Plant_Mushroom>();
            foreach (var plant in At(map, def, root))
                if (!plant.sown && seen.Add(plant)) pending.Enqueue(plant);
            while (pending.Count > 0 && result.Count < limit)
            {
                var plant = pending.Dequeue();
                result.Add(plant);
                foreach (var offset in GenAdj.AdjacentCells)
                    foreach (var adjacent in At(map, def, plant.Position + offset))
                        if (!adjacent.sown && seen.Add(adjacent)) pending.Enqueue(adjacent);
            }
            return result;
        }

        public static IntVec3 SelectCell(Map map, MapComponent_WildMushrooms ecology, ThingDef def, IntVec3 original)
        {
            if (map == null || ecology == null || def == null || !original.InBounds(map)) return original;
            int radius = Mathf.Clamp(ecology.Settings.smallClusterSearchRadius, 1, 40);
            int maximum = Mathf.Clamp(ecology.Settings.smallClusterMaxCount, 2, 5);
            var checkedGroups = new HashSet<Plant_Mushroom>();
            foreach (var anchor in Plants(map, def).Where(p => Distance(p.Position, original) <= radius)
                .OrderBy(p => p.Position.DistanceToSquared(original)).ThenBy(p => p.thingIDNumber))
            {
                if (!checkedGroups.Add(anchor)) continue;
                var group = Connected(map, def, anchor.Position, maximum + 1);
                foreach (var member in group) checkedGroups.Add(member);
                if (group.Count == 0 || group.Count >= TargetCount(map, ecology, group) || group.Count >= maximum) continue;
                IntVec3 destination = FindJoinCell(map, ecology, def, group, original, null);
                if (destination.IsValid) return destination;
            }
            // A capped or large old group must not grow simply because the native
            // draw landed next to it. Prefer a nearby isolated new seed instead.
            if (!TouchesOther(map, def, original, null, null)) return original;
            foreach (var cell in GenRadial.RadialCellsAround(original, radius, true))
                if (cell.InBounds(map) && SameBiome(map, original, cell) && ecology.IsEligibleCell(cell, def)
                    && WildMushroomEcology.IsNativeSpeciesAt(def, cell, map) && !TouchesOther(map, def, cell, null, null)) return cell;
            // Count preservation wins if the terrain offers no safe alternative.
            return original;
        }

        public static int Arrange(Map map, MapComponent_WildMushrooms ecology, List<Plant_Mushroom> nativePlants, out int groups)
        {
            int moved = 0;
            groups = 0;
            if (map == null || ecology == null || nativePlants.Count == 0) return moved;
            int maximum = Mathf.Clamp(ecology.Settings.smallClusterMaxCount, 2, 5);
            var movable = new HashSet<Plant_Mushroom>(nativePlants);
            var processed = new HashSet<Plant_Mushroom>();
            var ordered = nativePlants.OrderBy(p => p.def.defName, StringComparer.Ordinal).ThenBy(p => p.thingIDNumber).ToList();
            foreach (var anchor in ordered)
            {
                if (!LiveOn(anchor, map) || processed.Contains(anchor)) continue;
                var group = Connected(map, anchor.def, anchor.Position, maximum + 1);
                // Never break up a native large component, or move a scenario-
                // supplied plant that was not recorded by the native generation.
                if (group.Count > maximum || group.Any(p => !movable.Contains(p)))
                {
                    foreach (var member in group) processed.Add(member);
                    continue;
                }
                moved += Reshape(map, ecology, group, movable);
                int target = Mathf.Max(group.Count, TargetCount(map, ecology, group));
                // Map-generation donors may be anywhere in the same local biome.
                // Existing connected groups stay intact; only singleton donors move.
                var donors = ordered.Where(p => p != anchor && !processed.Contains(p) && LiveOn(p, map)
                        && p.def == anchor.def && !group.Contains(p) && SameBiome(map, anchor.Position, p.Position))
                    .OrderBy(p => p.Position.DistanceToSquared(anchor.Position)).ThenBy(p => p.thingIDNumber).ToList();
                foreach (var donor in donors)
                {
                    if (group.Count >= target || group.Count >= maximum) break;
                    if (Connected(map, donor.def, donor.Position, 2).Count != 1) continue;
                    IntVec3 destination = FindJoinCell(map, ecology, donor.def, group, donor.Position, donor);
                    if (!destination.IsValid || !Move(map, ecology, donor, destination)) continue;
                    group.Add(donor);
                    processed.Add(donor);
                    moved++;
                }
                // Avoid a lone final donor when an earlier same-species small
                // group still has room. This does not change a group's maximum.
                if (group.Count == 1)
                {
                    var destinationGroup = Plants(map, anchor.def).Where(p => p != anchor && SameBiome(map, anchor.Position, p.Position))
                        .OrderBy(p => p.Position.DistanceToSquared(anchor.Position)).ThenBy(p => p.thingIDNumber);
                    foreach (var other in destinationGroup)
                    {
                        var existing = Connected(map, anchor.def, other.Position, maximum + 1);
                        if (existing.Contains(anchor) || existing.Count < 2 || existing.Count >= maximum) continue;
                        IntVec3 destination = FindJoinCell(map, ecology, anchor.def, existing, anchor.Position, anchor);
                        if (!destination.IsValid || !Move(map, ecology, anchor, destination)) continue;
                        moved++;
                        break;
                    }
                }
                foreach (var member in group) processed.Add(member);
            }
            var counted = new HashSet<Plant_Mushroom>();
            foreach (var plant in ordered.Where(p => LiveOn(p, map)))
            {
                if (counted.Contains(plant)) continue;
                var group = Connected(map, plant.def, plant.Position, maximum + 1);
                foreach (var member in group) counted.Add(member);
                if (group.Count >= 2 && group.Count <= maximum && ShapeValid(group.Select(p => p.Position).ToList(), ecology.Settings.smallClusterCellRadius)) groups++;
            }
            return moved;
        }

        private static int Reshape(Map map, MapComponent_WildMushrooms ecology, List<Plant_Mushroom> group, HashSet<Plant_Mushroom> movable)
        {
            if (group.Count < 3 || group.Any(p => !movable.Contains(p))) return 0;
            int moved = 0;
            for (int attempt = 0; attempt < group.Count && !ShapeValid(group.Select(p => p.Position).ToList(), ecology.Settings.smallClusterCellRadius); attempt++)
            {
                bool changed = false;
                foreach (var plant in group.OrderByDescending(p => p.thingIDNumber))
                {
                    var remaining = group.Where(p => p != plant).ToList();
                    if (!CellsConnected(remaining.Select(p => p.Position).ToList())) continue;
                    IntVec3 destination = FindJoinCell(map, ecology, plant.def, remaining, plant.Position, plant);
                    if (!destination.IsValid || !Move(map, ecology, plant, destination)) continue;
                    moved++;
                    changed = true;
                    break;
                }
                if (!changed) break;
            }
            return moved;
        }

        private static IntVec3 FindJoinCell(Map map, MapComponent_WildMushrooms ecology, ThingDef def,
            List<Plant_Mushroom> group, IntVec3 original, Plant_Mushroom moving)
        {
            if (group.Count == 0 || group.Count >= Mathf.Clamp(ecology.Settings.smallClusterMaxCount, 2, 5)) return IntVec3.Invalid;
            var members = new HashSet<Plant_Mushroom>(group);
            var cells = new HashSet<IntVec3>();
            foreach (var member in group)
                foreach (var offset in GenAdj.AdjacentCells) cells.Add(member.Position + offset);
            int seed = group.Min(p => p.thingIDNumber);
            foreach (var cell in cells.OrderBy(c => CellOrder(c, seed)).ThenBy(c => c.x).ThenBy(c => c.z))
            {
                if (!cell.InBounds(map) || cell == moving?.Position || !SameBiome(map, original, cell)
                    || !ecology.IsEligibleCell(cell, def) || !WildMushroomEcology.IsNativeSpeciesAt(def, cell, map)
                    || TouchesOther(map, def, cell, members, moving)) continue;
                var shape = group.Select(p => p.Position).ToList();
                shape.Add(cell);
                if (ShapeValid(shape, ecology.Settings.smallClusterCellRadius)) return cell;
            }
            return IntVec3.Invalid;
        }

        private static bool Move(Map map, MapComponent_WildMushrooms ecology, Plant_Mushroom plant, IntVec3 destination)
        {
            if (!LiveOn(plant, map) || plant.Position == destination || !ecology.IsEligibleCell(destination, plant.def)) return false;
            IntVec3 original = plant.Position;
            Rot4 rotation = plant.Rotation;
            try
            {
                plant.DeSpawn();
                // Respawn the same instance without the native fresh-spawn
                // leafless check. ID, growth, age and all saved fields survive.
                GenSpawn.Spawn(plant, destination, map, rotation, WipeMode.Vanish, respawningAfterLoad: true);
                if (plant.Spawned && plant.Map == map && plant.Position == destination) return true;
                if (!plant.Spawned && !plant.Destroyed)
                    GenSpawn.Spawn(plant, original, map, rotation, WipeMode.Vanish, respawningAfterLoad: true);
                return false;
            }
            catch
            {
                if (!plant.Destroyed)
                {
                    if (plant.Spawned) plant.DeSpawn();
                    GenSpawn.Spawn(plant, original, map, rotation, WipeMode.Vanish, respawningAfterLoad: true);
                }
                throw;
            }
        }

        private static bool ShapeValid(List<IntVec3> cells, int radius)
        {
            if (cells.Count < 2) return true;
            radius = Mathf.Clamp(radius, 1, 4);
            if (cells.Max(c => c.x) - cells.Min(c => c.x) > radius || cells.Max(c => c.z) - cells.Min(c => c.z) > radius) return false;
            if (cells.Count < 3) return true;
            IntVec3 a = cells[0], b = cells[1];
            if (cells.Skip(2).All(c => (b.x - a.x) * (c.z - a.z) == (b.z - a.z) * (c.x - a.x))) return false;
            // A four-sided empty center reads as a miniature ring rather than a
            // clump. Existing ecology rings are not passed through this helper.
            var occupied = new HashSet<IntVec3>(cells);
            foreach (var cell in cells)
                foreach (var offset in GenAdj.CardinalDirections)
                {
                    var center = cell + offset;
                    if (!occupied.Contains(center) && GenAdj.CardinalDirections.All(d => occupied.Contains(center + d))) return false;
                }
            return true;
        }

        private static bool CellsConnected(List<IntVec3> cells)
        {
            if (cells.Count < 2) return true;
            var visited = new HashSet<IntVec3> { cells[0] };
            bool changed;
            do
            {
                changed = false;
                foreach (var cell in cells)
                    if (!visited.Contains(cell) && visited.Any(v => Distance(v, cell) == 1)) { visited.Add(cell); changed = true; }
            } while (changed);
            return visited.Count == cells.Count;
        }

        private static bool TouchesOther(Map map, ThingDef def, IntVec3 cell, HashSet<Plant_Mushroom> members, Plant_Mushroom moving)
        {
            foreach (var offset in GenAdj.AdjacentCells)
                foreach (var plant in At(map, def, cell + offset))
                    if (plant != moving && (members == null || !members.Contains(plant))) return true;
            return false;
        }

        private static int TargetCount(Map map, MapComponent_WildMushrooms ecology, List<Plant_Mushroom> group)
        {
            int min = Mathf.Clamp(ecology.Settings.smallClusterMinCount, 2, 5);
            int max = Mathf.Clamp(ecology.Settings.smallClusterMaxCount, min, 5);
            uint hash = unchecked((uint)(group.Min(p => p.thingIDNumber) * 1103515245 + map.uniqueID * 12345));
            return min + (int)((hash ^ (hash >> 16)) % (uint)(max - min + 1));
        }

        private static uint CellOrder(IntVec3 cell, int seed)
        {
            uint hash = unchecked((uint)(cell.x * 73856093 ^ cell.z * 19349663 ^ seed * 83492791));
            hash ^= hash >> 16;
            return hash;
        }

        private static bool SameBiome(Map map, IntVec3 a, IntVec3 b) => a.InBounds(map) && b.InBounds(map) && map.BiomeAt(a) == map.BiomeAt(b);
        private static int Distance(IntVec3 a, IntVec3 b) => Math.Max(Math.Abs(a.x - b.x), Math.Abs(a.z - b.z));
        private static bool LiveOn(Plant_Mushroom plant, Map map) => plant != null && plant.Spawned && !plant.Destroyed && !plant.sown && plant.Map == map;
        private static IEnumerable<Plant_Mushroom> Plants(Map map, ThingDef def) => map.listerThings.ThingsInGroup(ThingRequestGroup.Plant)
            .OfType<Plant_Mushroom>().Where(p => p.def == def && LiveOn(p, map));
        private static IEnumerable<Plant_Mushroom> At(Map map, ThingDef def, IntVec3 cell) => !cell.InBounds(map)
            ? Enumerable.Empty<Plant_Mushroom>() : cell.GetThingList(map).OfType<Plant_Mushroom>().Where(p => p.def == def && LiveOn(p, map) || p.def == def && p.Spawned && p.Map == map);
    }
}
