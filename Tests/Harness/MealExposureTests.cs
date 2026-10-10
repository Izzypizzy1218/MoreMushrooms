using System;
using System.Collections.Generic;
using System.Linq;
using RimMushrooms;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMushroomsTests
{
    internal static class MealExposureTests
    {
        private static readonly Dictionary<string, float> Saved = new Dictionary<string, float>();
        private static void Check(Action<bool, string> check, bool value, string text) => check(value, "meal exposure: " + text);
        private static Thing Ingredient(string id, int count)
        {
            var thing = ThingMaker.MakeThing(ThingDef.Named(id)); thing.stackCount = count; return thing;
        }
        private static Thing Cook(Pawn worker, string recipe, params Thing[] ingredients)
            => GenRecipe.MakeRecipeProducts(DefDatabase<RecipeDef>.GetNamed(recipe), worker, ingredients.ToList(), ingredients[0], null).Single();
        private static float Dose(Thing food, string id) => food.TryGetComp<CompMushroomMeal>().Doses.Where(d => d.Key.defName == "RMush_Raw" + id).Sum(d => d.Value);
        private static Pawn Healthy(Map map)
        {
            var pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            foreach (var h in pawn.health.hediffSet.hediffs.ToList()) pawn.health.RemoveHediff(h);
            pawn.needs.food.CurLevelPercentage = 1f; pawn.needs.rest.CurLevelPercentage = 1f;
            GenSpawn.Spawn(pawn, map.Center + new IntVec3(32, 0, 4), map);
            return pawn;
        }
        public static void Run(Pawn worker, Map map, Action<bool, string> check)
        {
            Saved.Clear();
            var bulk = Cook(worker, "CookMealSimpleBulk", Ingredient("RMush_RawCubensis", 40));
            Check(check, bulk.stackCount == 4 && Mathf.Abs(Dose(bulk, "Cubensis") - 1f) < 0.00001f,
                "real bulk recipe divides forty mushrooms into four one-dose servings");
            var single = Cook(worker, "CookMealSimple", Ingredient("RMush_RawCubensis", 10));
            Check(check, single.stackCount == 1 && Mathf.Abs(Dose(single, "Cubensis") - Dose(bulk, "Cubensis")) < 0.00001f,
                "single and bulk cooking conserve exposure per serving");
            var split = bulk.SplitOff(2);
            Check(check, split.stackCount == 2 && bulk.stackCount == 2 && Dose(split, "Cubensis") == Dose(bulk, "Cubensis"), "split preserves per-serving dose");
            Check(check, bulk.TryAbsorbStack(split, true) && bulk.stackCount == 4, "identical batches merge without changing dose");
            var plain = Cook(worker, "CookMealSimple", Ingredient("RawPotatoes", 10));
            Check(check, !bulk.CanStackWith(plain) && !plain.CanStackWith(bulk), "psychedelic and ordinary meals cannot mix stacks");
            var diluted = Cook(worker, "CookMealSimpleBulk", Ingredient("RMush_RawCubensis", 10), Ingredient("RawPotatoes", 30));
            Check(check, Mathf.Abs(Dose(diluted, "Cubensis") - 0.25f) < 0.00001f && !diluted.CanStackWith(bulk), "diluted batch remains quarter-dose and distinct");
            var filter = new ThingFilter(); filter.SetAllow(ThingCategoryDefOf.Foods, true);
            filter.SetAllow(SpecialThingFilterDef.Named("RMush_AllowPsychoactiveMeals"), false);
            Check(check, filter.Allows(plain) && !filter.Allows(bulk) && !filter.Allows(diluted), "native food restriction excludes recorded psychedelic meals");
            var mixed = Cook(worker, "CookMealSimpleBulk", Ingredient("RMush_RawLibertyCap", 20), Ingredient("RMush_RawCubensis", 20));
            var patient = Healthy(map);
            try
            {
                mixed.Ingested(patient, 0.9f);
                var effect = patient.health.hediffSet.hediffs.OfType<Hediff_MushroomHallucination>().Single();
                var memory = patient.needs.mood.thoughts.memories.Memories.OfType<Thought_MushroomHallucination>().Single();
                var tolerance = patient.health.hediffSet.hediffs.OfType<Hediff_MushroomTolerance>().Single();
                Check(check, Mathf.Abs(effect.TotalDoses - 1f) < 0.00001f && memory.moodOffset == 15,
                    "mixed species aggregate into one exposure and full mood once");
                Check(check, Mathf.Abs(tolerance.Severity - 0.251f) < 0.00001f, "one meal does not add tolerance once per species");
            }
            finally { patient.Destroy(); }
            var poisonous = Cook(worker, "CookMealSimpleBulk", Ingredient("RMush_RawDeathCap", 1), Ingredient("RawPotatoes", 39));
            Check(check, Mathf.Abs(Dose(poisonous, "DeathCap") - 0.25f) < 0.00001f, "one lethal raw ingredient is divided across four portions");
            patient = Healthy(map);
            try
            {
                poisonous.Ingested(patient, 0.9f);
                var poison = patient.health.hediffSet.hediffs.OfType<Hediff_MushroomPoisoning>().Single();
                Check(check, poison.def.defName == "RMush_PoisonDeathCap" && Mathf.Abs(poison.ExposureUnits - 0.25f) < 0.00001f,
                    "meal ingestion applies actual poison fraction exactly once");
            }
            finally { patient.Destroy(); }
            patient = Healthy(map);
            var fly = Cook(worker, "CookMealSimple", Ingredient("RMush_RawFlyAgaric", 10));
            try
            {
                fly.Ingested(patient, 0.9f);
                Check(check, patient.health.hediffSet.hediffs.OfType<Hediff_MushroomPoisoning>().Single().ExposureUnits == 1f
                    && patient.health.hediffSet.hediffs.OfType<Hediff_MushroomHallucination>().Single().TotalDoses == 1f,
                    "fly agaric meal applies both poison and psychoactive effect once");
            }
            finally { patient.Destroy(); }
            var recipeDef = DefDatabase<RecipeDef>.GetNamed("CookMealSimple");
            var bill = recipeDef.MakeNewBill(); bill.ingredientFilter.CopyAllowancesFrom(recipeDef.fixedIngredientFilter);
            foreach (string id in new[] { "LibertyCap", "Cubensis", "PantherCap" }) bill.ingredientFilter.SetAllow(ThingDef.Named("RMush_Raw" + id), false);
            bill.ingredientFilter.SetAllow(ThingDef.Named("RMush_RawFlyAgaric"), false);
            MushroomFoodMigration.MigrateBill(bill);
            Check(check, new[] { "LibertyCap", "Cubensis", "PantherCap" }.All(id => bill.ingredientFilter.Allows(ThingDef.Named("RMush_Raw" + id)))
                && bill.ingredientFilter.Allows(ThingDef.Named("RMush_RawFlyAgaric")), "old broad vegetable bill adds all four newly food-eligible species");
            bill.ingredientFilter.SetAllow(ThingDef.Named("RMush_RawButton"), false);
            bill.ingredientFilter.SetAllow(ThingDef.Named("RMush_RawCubensis"), false);
            MushroomFoodMigration.MigrateBill(bill);
            Check(check, !bill.ingredientFilter.Allows(ThingDef.Named("RMush_RawCubensis")), "selective old bill is not expanded");
            int row = 0;
            foreach (var food in new[] { bulk, diluted, plain, mixed, poisonous })
            {
                GenSpawn.Spawn(food, map.Center + new IntVec3(33 + row, 0, 8), map); food.SetForbidden(true, false);
                Saved[food.ThingID] = Dose(food, food == poisonous ? "DeathCap" : "Cubensis"); row++;
            }
        }
        public static void VerifyLoaded(Map map, Action<bool, string> check)
        {
            foreach (var entry in Saved)
            {
                var food = map.listerThings.AllThings.Single(t => t.ThingID == entry.Key);
                string id = food.TryGetComp<CompMushroomMeal>().Doses.Any(d => d.Key.defName == "RMush_RawDeathCap") ? "DeathCap" : "Cubensis";
                Check(check, Mathf.Abs(Dose(food, id) - entry.Value) < 0.00001f, "save reload restores dose " + entry.Key);
            }
        }
    }
}
