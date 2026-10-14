using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMushrooms
{
    // These are readable game habitat rules, not a simulation of fungal hosts.
    // They apply to new wild emergence. Existing plants are never removed here.
    public static class MushroomHabitatRules
    {
        private static readonly HashSet<string> knownHabitats = new HashSet<string>(StringComparer.Ordinal)
        {
            "soil", "openField", "forestEdge", "treeBase", "stump", "pineStump",
            "insectHost", "mushroomHost", "indoorSoil", "exposedWood", "wetMargin"
        };

        public static bool IsKnownHabitat(string habitat) => habitat.NullOrEmpty() || knownHabitats.Contains(habitat);

        private static string Habitat(ThingDef def) => def?.GetModExtension<MushroomEcologyExtension>()?.habitat ?? "soil";
        private static int Radius(ThingDef def) => Mathf.Clamp(def?.GetModExtension<MushroomEcologyExtension>()?.habitatRadius ?? 2, 1, 4);

        public static bool AllowsRoof(ThingDef def) => Habitat(def) == "indoorSoil" || Habitat(def) == "exposedWood";

        public static bool AllowsFloor(ThingDef def, IntVec3 cell, Map map) => Habitat(def) == "exposedWood"
            && map != null && cell.InBounds(map) && IsWoodenFloor(cell.GetTerrain(map));

        public static bool MeetsHabitat(ThingDef def, IntVec3 cell, Map map)
        {
            if (def == null || map == null || !cell.InBounds(map) || !WildMushroomEcology.MeetsTreeHabitat(def, cell, map)) return false;
            int radius = Radius(def);
            switch (Habitat(def))
            {
                case null:
                case "":
                case "soil": return true;
                case "openField": return CountLivingTrees(cell, map, 5, 2) <= 1 && HasGrass(cell, map, 2);
                case "forestEdge":
                    int trees = CountLivingTrees(cell, map, 5, 4);
                    return trees >= 1 && trees <= 3;
                case "treeBase": return HasPlant(cell, map, radius, p => IsLivingTree(p) && p.Growth >= 0.5f);
                case "stump": return HasPlant(cell, map, radius, IsStumpOrDeadTree);
                // Core stumps do not retain the species of the felled tree. A
                // real stump is accepted; a still-living pine is an alternative.
                case "pineStump": return HasPlant(cell, map, radius, p => IsStumpOrDeadTree(p)
                    || (IsLivingTree(p) && p.def.defName == "Plant_TreePine" && p.Growth >= 0.5f));
                case "insectHost": return HasInsectHost(cell, map, radius);
                case "mushroomHost": return HasPlant(cell, map, radius, p => IsOtherWildMushroom(p, def));
                case "indoorSoil":
                    return cell.Roofed(map) && !cell.GetTerrain(map).IsFloor && map.glowGrid.GroundGlowAt(cell) <= 0.3f;
                case "exposedWood":
                    return HasRainExposure(cell, map, radius) && (AllowsFloor(def, cell, map) || HasWoodenBuilding(cell, map, radius));
                case "wetMargin":
                    foreach (IntVec3 nearby in NearbyCells(cell, map, radius))
                    {
                        TerrainDef terrain = nearby.GetTerrain(map);
                        if (terrain != null && (terrain.IsWater || terrain.HasTag("Mud") || terrain.defName == "MarshyTerrain")) return true;
                    }
                    return false;
                default: return false;
            }
        }

        private static bool IsLivingTree(Plant plant) => plant != null && !plant.Destroyed && plant.Spawned
            && plant.def.plant != null && plant.def.plant.IsTree && !plant.def.IsDeadPlant && !plant.def.plant.isStump;

        private static bool IsStumpOrDeadTree(Plant plant) => plant != null && !plant.Destroyed && plant.Spawned
            && plant.def.plant != null && (plant.def.plant.isStump || (plant.def.IsDeadPlant && plant.def.plant.IsTree));

        private static bool IsOtherWildMushroom(Plant plant, ThingDef def) => plant is Plant_Mushroom && plant.def != def
            && plant.Spawned && !plant.Destroyed && !plant.sown;

        private static bool HasPlant(IntVec3 cell, Map map, int radius, Predicate<Plant> predicate)
        {
            foreach (IntVec3 nearby in NearbyCells(cell, map, radius))
                foreach (Thing thing in nearby.GetThingList(map))
                    if (thing is Plant plant && predicate(plant)) return true;
            return false;
        }

        private static int CountLivingTrees(IntVec3 cell, Map map, int radius, int stopAt)
        {
            int count = 0;
            foreach (IntVec3 nearby in NearbyCells(cell, map, radius))
                foreach (Thing thing in nearby.GetThingList(map))
                    if (thing is Plant plant && IsLivingTree(plant) && ++count >= stopAt) return count;
            return count;
        }

        private static bool HasGrass(IntVec3 cell, Map map, int radius) => HasPlant(cell, map, radius, p => !p.sown && !p.def.IsDeadPlant
            && !p.def.plant.IsTree && !WildMushroomEcology.IsMushroom(p.def)
            && (p.def.defName == "Plant_Grass" || p.def.defName == "Plant_TallGrass"
                || (p.def.plant.wildOrder <= 1 && p.def.ingestible != null && (p.def.ingestible.foodType & FoodTypeFlags.Plant) != 0)));

        private static bool HasInsectHost(IntVec3 cell, Map map, int radius)
        {
            foreach (IntVec3 nearby in NearbyCells(cell, map, radius))
                foreach (Thing thing in nearby.GetThingList(map))
                    if (IsInsectHost(thing)) return true;
            return false;
        }

        private static bool IsInsectHost(Thing thing)
        {
            if (thing == null || !thing.Spawned || thing.Destroyed) return false;
            if (thing is Pawn pawn) return !pawn.Dead && pawn.RaceProps?.Insect == true;
            return thing is Corpse corpse && corpse.InnerPawn?.RaceProps?.Insect == true;
        }

        private static bool IsWoodyMaterial(ThingDef material)
        {
            if (material == null) return false;
            if (material == ThingDefOf.WoodLog) return true;
            if (material.stuffProps?.categories != null)
                foreach (StuffCategoryDef category in material.stuffProps.categories)
                    if (category.defName == "Woody") return true;
            return false;
        }

        private static bool MostlyWoodCost(BuildableDef def)
        {
            if (def?.costList == null || def.costList.Count == 0) return false;
            int total = 0, wood = 0;
            foreach (ThingDefCountClass cost in def.costList)
            {
                if (cost == null || cost.count <= 0) continue;
                total += cost.count;
                if (IsWoodyMaterial(cost.thingDef)) wood += cost.count;
            }
            return wood > 0 && wood * 2 >= total;
        }

        private static bool IsWoodenFloor(TerrainDef terrain)
        {
            if (terrain == null || !terrain.IsFloor) return false;
            // Ancient Core floors have no construction cost; recognise their
            // exact definitions rather than treating every flammable floor as wood.
            if (terrain.defName == "WoodPlankFloor" || terrain.defName == "AncientWoodPlankFloor"
                || terrain.defName == "Bridge" || terrain.defName == "AncientBridge") return true;
            return terrain.HasTag("Wood") || terrain.HasTag("Wooden") || terrain.HasTag("Woody")
                || terrain.HasTag("WoodFloor") || terrain.HasTag("WoodenFloor") || MostlyWoodCost(terrain);
        }

        private static bool IsWoodenBuilding(Thing thing) => thing is Building && thing.Spawned && !thing.Destroyed
            && (IsWoodyMaterial(thing.Stuff) || (thing.Stuff == null && MostlyWoodCost(thing.def)));

        private static bool HasWoodenBuilding(IntVec3 cell, Map map, int radius)
        {
            foreach (IntVec3 nearby in NearbyCells(cell, map, radius))
                foreach (Thing thing in nearby.GetThingList(map))
                    if (IsWoodenBuilding(thing)) return true;
            return false;
        }

        private static bool HasRainExposure(IntVec3 cell, Map map, int radius)
        {
            if (!cell.Roofed(map)) return true;
            foreach (IntVec3 nearby in NearbyCells(cell, map, radius))
                // An outside square across an intact wall is not a roof hole.
                if (!nearby.Roofed(map) && nearby.CanBeSeenOverFast(map) && GenSight.LineOfSight(cell, nearby, map, true)) return true;
            return false;
        }

        private static IEnumerable<IntVec3> NearbyCells(IntVec3 center, Map map, int radius)
        {
            for (int z = -radius; z <= radius; z++)
                for (int x = -radius; x <= radius; x++)
                {
                    if (x == 0 && z == 0) continue;
                    IntVec3 cell = center + new IntVec3(x, 0, z);
                    if (cell.InBounds(map)) yield return cell;
                }
        }

        private static bool MatchesAnchor(Thing thing, ThingDef def)
        {
            if (thing == null || !thing.Spawned || thing.Destroyed) return false;
            string habitat = Habitat(def);
            if (habitat == "insectHost") return IsInsectHost(thing);
            if (habitat == "indoorSoil") return thing is Building;
            if (habitat == "exposedWood") return IsWoodenBuilding(thing);
            if (!(thing is Plant plant)) return false;
            if (habitat == "mushroomHost") return IsOtherWildMushroom(plant, def);
            if (habitat == "stump") return IsStumpOrDeadTree(plant);
            if (habitat == "pineStump") return IsStumpOrDeadTree(plant)
                || (IsLivingTree(plant) && plant.def.defName == "Plant_TreePine" && plant.Growth >= 0.5f);
            var profile = def.GetModExtension<MushroomEcologyExtension>();
            if (!IsLivingTree(plant) || plant.Growth < (profile?.habitatTreeMinGrowth ?? 0.5f)) return false;
            string tree = profile?.habitatTree;
            return tree.NullOrEmpty() || tree == "any" || plant.def.defName == (tree == "pine" ? "Plant_TreePine" : "Plant_TreeBirch");
        }

        private static IEnumerable<Thing> AnchorThings(ThingDef def, Map map, int maximum, uint seed)
        {
            string habitat = Habitat(def);
            ThingRequestGroup[] groups = habitat == "insectHost" ? new[] { ThingRequestGroup.Pawn, ThingRequestGroup.Corpse }
                : habitat == "indoorSoil" || habitat == "exposedWood" ? new[] { ThingRequestGroup.BuildingArtificial }
                : new[] { ThingRequestGroup.Plant };
            int inspected = 0;
            foreach (ThingRequestGroup group in groups)
            {
                List<Thing> things = map.listerThings.ThingsInGroup(group);
                if (things.Count == 0) continue;
                int start = (int)(seed % (uint)things.Count);
                for (int offset = 0; offset < things.Count && inspected < maximum; offset++, inspected++)
                {
                    Thing thing = things[(start + offset) % things.Count];
                    if (MatchesAnchor(thing, def)) yield return thing;
                }
            }
        }

        public static IEnumerable<IntVec3> FindAnchorCells(ThingDef def, Map map, int maxCandidates = 128)
        {
            if (def == null || map == null || maxCandidates <= 0) yield break;
            int maximum = Mathf.Clamp(maxCandidates, 1, 256), found = 0;
            uint seed = unchecked((uint)(Gen.HashCombineInt(map.uniqueID, def.shortHash) ^ (Find.TickManager?.TicksGame ?? 0)));
            var seen = new HashSet<IntVec3>();
            var ecology = map.GetComponent<MapComponent_WildMushrooms>();
            int radius = Habitat(def) == "forestEdge" ? 5 : Radius(def);
            foreach (Thing anchor in AnchorThings(def, map, maximum * 16, seed))
                foreach (IntVec3 cell in NearbyCells(anchor.Position, map, radius))
                    if (seen.Add(cell) && (ecology != null ? ecology.IsEligibleCell(cell, def) : MeetsHabitat(def, cell, map)))
                    {
                        yield return cell;
                        if (++found >= maximum) yield break;
                    }
            // Empty ruins and wooden platforms may have no remaining building
            // anchor. Sample a bounded number of grid cells without global Rand
            // calls or a map-wide materialized cell list.
            string habitat = Habitat(def);
            if (habitat != "indoorSoil" && habitat != "exposedWood" && habitat != "openField" && habitat != "wetMargin" && habitat != "soil") yield break;
            int gridCount = map.cellIndices.NumGridCells;
            if (gridCount <= 0) yield break;
            for (int attempt = 0; attempt < maximum * 64; attempt++)
            {
                seed = unchecked(seed * 1664525u + 1013904223u);
                IntVec3 cell = map.cellIndices.IndexToCell((int)(seed % (uint)gridCount));
                if (seen.Add(cell) && (ecology != null ? ecology.IsEligibleCell(cell, def) : MeetsHabitat(def, cell, map)))
                {
                    yield return cell;
                    if (++found >= maximum) yield break;
                }
            }
        }
    }
}
