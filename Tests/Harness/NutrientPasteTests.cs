using System;
using System.Collections.Generic;
using System.Linq;
using RimMushrooms;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMushroomsTests
{
    // A short synchronous fixture in the isolated test map. Power is controlled
    // only for this test; feedstock selection, consumption, paste creation and
    // ingestion all use the installed vanilla game methods.
    internal static class NutrientPasteTests
    {
        private static void Check(Action<bool, string> check, bool valid, string text)
            => check(valid, "nutrient paste: " + text);

        private static float Dose(Thing meal, string species)
            => meal.TryGetComp<CompMushroomMeal>()?.Doses
                .Where(d => d.Key.defName == "RMush_Raw" + species).Sum(d => d.Value) ?? 0f;

        private static int Remaining(Thing item) => item.Destroyed ? 0 : item.stackCount;

        private static Thing Feed(string defName, int count, IntVec3 cell, Map map, List<Thing> created)
        {
            var item = ThingMaker.MakeThing(ThingDef.Named(defName));
            item.stackCount = count;
            created.Add(item);
            GenSpawn.Spawn(item, cell, map);
            return item;
        }

        private static Pawn Patient(Map map, IntVec3 position, List<Thing> created)
        {
            var patient = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            foreach (var effect in patient.health.hediffSet.hediffs.ToList()) patient.health.RemoveHediff(effect);
            foreach (var memory in patient.needs.mood.thoughts.memories.Memories.ToList())
                patient.needs.mood.thoughts.memories.RemoveMemory(memory);
            patient.needs.food.CurLevelPercentage = 1f;
            patient.needs.rest.CurLevelPercentage = 1f;
            created.Add(patient);
            GenSpawn.Spawn(patient, position, map);
            return patient;
        }

        private static void ClearFeed(IEnumerable<Thing> created)
        {
            foreach (var item in created.Where(t => !t.Destroyed && t.def.category == ThingCategory.Item).ToList())
                item.Destroy(DestroyMode.Vanish);
        }

        public static void Run(Pawn worker, Map map, Action<bool, string> check)
        {
            var center = map.Center + new IntVec3(45, 0, -20);
            var cells = CellRect.CenteredOn(center, 9, 9).Cells.ToList();
            Check(check, cells.All(c => c.InBounds(map)), "fixture is inside the isolated map");
            Check(check, !cells.SelectMany(c => c.GetThingList(map)).Any(t => t is Pawn),
                "fixture does not displace existing pawns");
            var preserved = cells.SelectMany(c => c.GetThingList(map)).Distinct()
                .Select(t => new PreservedThing { Thing = t, Position = t.Position, Rotation = t.Rotation }).ToList();
            var created = new List<Thing>();
            Building_NutrientPasteDispenser dispenser = null;
            bool previousPower = false;
            try
            {
                foreach (var original in preserved) original.Thing.DeSpawn();
                dispenser = (Building_NutrientPasteDispenser)ThingMaker.MakeThing(ThingDef.Named("NutrientPasteDispenser"));
                dispenser.SetFaction(Faction.OfPlayer);
                created.Add(dispenser);
                GenSpawn.Spawn(dispenser, center, map, Rot4.North);
                previousPower = dispenser.powerComp.PowerOn;
                var hopperCells = dispenser.AdjCellsCardinalInBounds.Take(2).ToArray();
                Check(check, hopperCells.Length == 2 && hopperCells.All(c => cells.Contains(c)),
                    "two native adjacent hopper cells are available");
                foreach (var cell in hopperCells)
                {
                    var hopper = (Building)ThingMaker.MakeThing(ThingDef.Named("Hopper"));
                    hopper.SetFaction(Faction.OfPlayer);
                    created.Add(hopper);
                    GenSpawn.Spawn(hopper, cell, map, Rot4.North);
                }
                foreach (string species in new[] { "LibertyCap", "Cubensis", "FlyAgaric", "PantherCap" })
                    Check(check, Building_NutrientPasteDispenser.IsAcceptableFeedstock(ThingDef.Named("RMush_Raw" + species)),
                        "native dispenser accepts psychoactive raw fungus " + species);

                // The first hopper is exhausted and the second is only partly
                // consumed, exercising both native Destroy and SplitOff paths.
                var liberty = Feed("RMush_RawLibertyCap", 2, hopperCells[0], map, created);
                var cubensis = Feed("RMush_RawCubensis", 20, hopperCells[1], map, created);
                dispenser.powerComp.PowerOn = false;
                Check(check, dispenser.TryDispenseFood() == null && Remaining(liberty) == 2 && Remaining(cubensis) == 20,
                    "unpowered dispenser consumes no ingredients and returns no meal");
                dispenser.powerComp.PowerOn = true;
                Check(check, dispenser.CanDispenseNow, "powered dispenser detects actual feedstock in two hoppers");
                var mixed = dispenser.TryDispenseFood();
                if (mixed != null) created.Add(mixed);
                int libertyCount = 2 - Remaining(liberty), cubensisCount = 20 - Remaining(cubensis);
                Check(check, mixed != null && mixed.def == ThingDefOf.MealNutrientPaste && mixed.stackCount == 1,
                    "native dispenser creates one ordinary nutrient paste meal");
                Check(check, libertyCount == 2 && cubensisCount > 0 && cubensisCount < 20,
                    "native feedstock consumption spans destroyed and partial source stacks");
                Check(check, Mathf.Abs(Dose(mixed, "LibertyCap") - libertyCount / 10f) < 0.00001f
                    && Mathf.Abs(Dose(mixed, "Cubensis") - cubensisCount / 10f) < 0.00001f,
                    "paste ledger records exact consumed raw quantity for each source");
                float feedNutrition = libertyCount * liberty.GetStatValue(StatDefOf.Nutrition)
                    + cubensisCount * cubensis.GetStatValue(StatDefOf.Nutrition);
                Check(check, feedNutrition + .0001f >= dispenser.def.building.nutritionCostPerDispense
                    && feedNutrition < dispenser.def.building.nutritionCostPerDispense + .05f,
                    "observed feedstock consumption obeys native nutrition cost");
                Check(check, mixed.TryGetComp<CompIngredients>().ingredients.Contains(liberty.def)
                    && mixed.TryGetComp<CompIngredients>().ingredients.Contains(cubensis.def),
                    "native ingredient labels retain both mushroom species");
                var patient = Patient(map, center + new IntVec3(3, 0, 3), created);
                mixed.Ingested(patient, mixed.GetStatValue(StatDefOf.Nutrition));
                var episodes = patient.health.hediffSet.hediffs.OfType<Hediff_MushroomHallucination>().ToArray();
                var memories = patient.needs.mood.thoughts.memories.Memories.OfType<Thought_MushroomHallucination>().ToArray();
                var tolerances = patient.health.hediffSet.hediffs.OfType<Hediff_MushroomTolerance>().ToArray();
                float combinedDose = (libertyCount + cubensisCount) / 10f;
                Check(check, episodes.Length == 1 && Mathf.Abs(episodes[0].TotalDoses - combinedDose) < .00001f
                    && memories.Length == 1 && memories[0].moodOffset == Mathf.RoundToInt(15f * Mathf.Clamp01(combinedDose)),
                    "native paste ingestion applies one combined fractional psychedelic episode and mood");
                float initialTolerance = HediffDef.Named("RMush_PsychedelicTolerance").initialSeverity;
                Check(check, tolerances.Length == 1 && Mathf.Abs(tolerances[0].Severity - (initialTolerance + combinedDose * .25f)) < .00001f,
                    "two-source paste adds shared tolerance once");
                patient.Destroy(DestroyMode.Vanish);
                ClearFeed(created);

                var potatoes = Feed("RawPotatoes", 20, hopperCells[0], map, created);
                var plain = dispenser.TryDispenseFood();
                if (plain != null) created.Add(plain);
                Check(check, plain != null && plain.TryGetComp<CompMushroomMeal>() != null
                    && !plain.TryGetComp<CompMushroomMeal>().Doses.Any() && Remaining(potatoes) < 20,
                    "ordinary potato paste has an empty mushroom ledger");
                patient = Patient(map, center + new IntVec3(3, 0, 3), created);
                plain.Ingested(patient, plain.GetStatValue(StatDefOf.Nutrition));
                Check(check, !patient.health.hediffSet.hediffs.OfType<Hediff_MushroomHallucination>().Any()
                    && !patient.health.hediffSet.hediffs.OfType<Hediff_MushroomPoisoning>().Any(),
                    "ordinary native paste ingestion produces no mushroom effects");
                patient.Destroy(DestroyMode.Vanish);
                ClearFeed(created);

                var fly = Feed("RMush_RawFlyAgaric", 2, hopperCells[0], map, created);
                potatoes = Feed("RawPotatoes", 20, hopperCells[1], map, created);
                var neurological = dispenser.TryDispenseFood();
                if (neurological != null) created.Add(neurological);
                int flyCount = 2 - Remaining(fly);
                Check(check, neurological != null && flyCount == 2 && Remaining(potatoes) < 20
                    && Mathf.Abs(Dose(neurological, "FlyAgaric") - flyCount / 10f) < .00001f,
                    "mixed fly agaric and potato paste records only the consumed mushroom fraction");
                patient = Patient(map, center + new IntVec3(3, 0, 3), created);
                neurological.Ingested(patient, neurological.GetStatValue(StatDefOf.Nutrition));
                var poisons = patient.health.hediffSet.hediffs.OfType<Hediff_MushroomPoisoning>().ToArray();
                episodes = patient.health.hediffSet.hediffs.OfType<Hediff_MushroomHallucination>().ToArray();
                memories = patient.needs.mood.thoughts.memories.Memories.OfType<Thought_MushroomHallucination>().ToArray();
                Check(check, poisons.Length == 1 && poisons[0].def.defName == "RMush_PoisonFlyAgaric"
                    && Mathf.Abs(poisons[0].ExposureUnits - flyCount / 10f) < .00001f
                    && episodes.Length == 1 && Mathf.Abs(episodes[0].TotalDoses - flyCount / 10f) < .00001f
                    && memories.Length == 1 && memories[0].moodOffset == Mathf.RoundToInt(10f * flyCount / 10f),
                    "native paste ingestion delivers fractional neurological poison and hallucination exactly once");
            }
            finally
            {
                if (dispenser != null && !dispenser.Destroyed) dispenser.powerComp.PowerOn = previousPower;
                foreach (var item in created.AsEnumerable().Reverse())
                    if (!item.Destroyed) item.Destroy(DestroyMode.Vanish);
                foreach (var original in preserved)
                    if (!original.Thing.Destroyed && !original.Thing.Spawned)
                        GenSpawn.Spawn(original.Thing, original.Position, map, original.Rotation);
            }
        }

        private sealed class PreservedThing
        {
            public Thing Thing;
            public IntVec3 Position;
            public Rot4 Rotation;
        }
    }
}
