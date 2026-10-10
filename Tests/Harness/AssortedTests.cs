using System;
using System.Collections.Generic;
using System.Linq;
using RimMushrooms;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMushroomsTests
{
    internal static class AssortedTests
    {
        public static ThingDef Selection => ThingDef.Named("RMush_PlantAssorted");
        public static WorkGiver_GrowerSow Sower => (WorkGiver_GrowerSow)DefDatabase<WorkGiverDef>.GetNamed("GrowerSow").Worker;
        public static WorkGiver_GrowerHarvest Harvester => (WorkGiver_GrowerHarvest)DefDatabase<WorkGiverDef>.GetNamed("GrowerHarvest").Worker;

        // Keep production's native RandomElement selection intact. A deterministic
        // fixture seed lets actual jobs exercise every member rather than relying
        // on a few random harvests to happen to include the two new varieties.
        public static Job NativeSowJobForSpecies(Pawn pawn, IntVec3 cell, ThingDef expected)
        {
            for (int seed = 1000; seed < 3000; seed++)
            {
                Rand.PushState(seed);
                try
                {
                    var job = Sower.JobOnCell(pawn, cell);
                    if (job == null || job.def != JobDefOf.Sow || job.plantDefToSow == expected) return job;
                }
                finally { Rand.PopState(); }
            }
            throw new Exception("Native assorted random selection never chose " + expected.defName);
        }

        public static void Run(Map map, Pawn pawn, Zone_Growing zone, IntVec3 cell, Action<bool,string> check)
        {
            check(Sower is WorkGiver_AssortedMushroomSow && Harvester is WorkGiver_AssortedMushroomHarvest, "native growing workers resolve assorted support without Harmony");
            check(!Selection.ConfigErrors().Any() && Selection.plant.Sowable, "assorted selection resolved and sowable");
            check(PlantUtility.ValidPlantTypesForGrowers(new List<IPlantToGrowSettable> { zone }).Contains(Selection) && Command_SetPlantToGrow.IsPlantAvailable(Selection, map), "native growing-zone crop menu includes assorted mushrooms");
            check(Selection.label == (LanguageDatabase.activeLanguage.folderName.StartsWith("Korean") ? "모둠버섯" : "assorted mushrooms"), "translated assorted crop entry");
            var species = Selection.GetModExtension<AssortedMushroomSettings>().varieties;
            check(species.Count == 11 && species.Distinct().Count() == 11 && species.All(d => d.plant.Sowable)
                && !species.Any(d => d.defName.Contains("Matsutake") || d.defName.Contains("EnokiWild")), "assorted contains eleven distinct cultivated species and excludes wild-only varieties");
            check(new[] { "RMush_PlantCauliflower", "RMush_PlantPurpleBlewit" }.All(id => species.Any(d => d.defName == id)),
                "assorted includes cauliflower and purple blewit");
            int oldSkill = pawn.skills.GetSkill(SkillDefOf.Plants).Level;
            zone.SetPlantDefToGrow(Selection);
            zone.allowCut = false;
            try
            {
                pawn.skills.GetSkill(SkillDefOf.Plants).Level = 5;
                check(Sower.JobOnCell(pawn, cell) == null, "assorted skill 5 cannot sow");
                check(!Sower.PotentialWorkCellsGlobal(pawn).Contains(cell), "automatic sow scan excludes under-skilled assorted zone");
                pawn.skills.GetSkill(SkillDefOf.Plants).Level = 6;
                check(Sower.PotentialWorkCellsGlobal(pawn).Contains(cell), "automatic sow scan includes skill 6 assorted zone");
                var counts = species.ToDictionary(d => d, d => 0);
                Rand.PushState(820031);
                try
                {
                    for (int i = 0; i < 2200; i++)
                    {
                        var job = Sower.JobOnCell(pawn, cell);
                        if (job == null || job.def != JobDefOf.Sow || !counts.ContainsKey(job.plantDefToSow))
                            throw new Exception("Assorted generated invalid sow job at " + i);
                        counts[job.plantDefToSow]++;
                    }
                }
                finally { Rand.PopState(); }
                foreach (var pair in counts)
                    check(pair.Value >= 140 && pair.Value <= 260, "equal-probability native sow samples " + pair.Key.defName + "=" + pair.Value + "/2200");
                foreach (var def in species)
                {
                    var plant = (Plant)GenSpawn.Spawn(def, cell, map);
                    try
                    {
                        plant.sown = true; plant.Growth = 0.25f;
                        check(Sower.JobOnCell(pawn, cell) == null, "assorted never cuts/replants its growing " + def.defName);
                        plant.Growth = 1f;
                        check(Harvester.HasJobOnCell(pawn, cell), "automatic assorted harvest with cutting disabled " + def.defName);
                        check(Harvester.JobOnCell(pawn, cell).targetQueueA.Any(t => t.Thing == plant), "native harvest queue accepts assorted member " + def.defName);
                    }
                    finally { plant.Destroy(); }
                }
                zone.allowSow = false;
                check(Sower.JobOnCell(pawn, cell) == null, "assorted honors sow-disabled zone");
                zone.allowSow = true;
                zone.SetPlantDefToGrow(ThingDef.Named("RMush_PlantButton"));
                Sower.PotentialWorkCellsGlobal(pawn).ToList();
                check(Sower.JobOnCell(pawn, cell)?.plantDefToSow == zone.GetPlantDefToGrow(), "individual mushroom sow unchanged");
                zone.SetPlantDefToGrow(ThingDef.Named("Plant_Rice"));
                Sower.PotentialWorkCellsGlobal(pawn).ToList();
                check(Sower.JobOnCell(pawn, cell)?.plantDefToSow == zone.GetPlantDefToGrow(), "vanilla rice sow unchanged");
            }
            finally
            {
                zone.allowCut = true; zone.allowSow = true;
                zone.SetPlantDefToGrow(Selection);
                pawn.skills.GetSkill(SkillDefOf.Plants).Level = oldSkill;
            }
        }

        public static void VerifyLoaded(Map map, Action<bool,string> check)
        {
            var zone = map.zoneManager.AllZones.OfType<Zone_Growing>().Single(z => z.GetPlantDefToGrow() == Selection);
            var plant = zone.Cells.Select(c => c.GetPlant(map)).Single(p => p != null);
            check(Selection.GetModExtension<AssortedMushroomSettings>().varieties.Contains(plant.def) && plant.sown, "assorted zone selection and actual species persist after save/load");
            check(Math.Abs(plant.Growth - 0.37f) < 0.00001f, "assorted actual species growth persists after save/load");
            check(Sower.JobOnCell(map.mapPawns.FreeColonistsSpawned.First(p => !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Growing)), plant.Position) == null, "loaded assorted crop is not cut or replanted");
        }
    }
}
