using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMushrooms
{
    public sealed class MushroomFoodSettings : ModSettings
    {
        public bool forbidAutomaticPoisonousMushrooms;
        public bool forbidPoisonousCookingIngredients;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref forbidAutomaticPoisonousMushrooms, "forbidAutomaticPoisonousMushrooms", false);
            Scribe_Values.Look(ref forbidPoisonousCookingIngredients, "forbidPoisonousCookingIngredients", false);
        }
    }

    public sealed class MoreMushroomsMod : Mod
    {
        public static MushroomFoodSettings Settings { get; private set; } = new MushroomFoodSettings();

        public MoreMushroomsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<MushroomFoodSettings>();
        }

        public override string SettingsCategory() => "More Mushrooms";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            var settings = Settings;
            listing.Begin(inRect);
            listing.Label("RMush_SettingsFoodSettingsNote".Translate());
            listing.Gap(12f);
            listing.CheckboxLabeled("RMush_SettingsAutoPoison".Translate(), ref settings.forbidAutomaticPoisonousMushrooms,
                "RMush_SettingsAutoPoisonTip".Translate());
            listing.Gap(6f);
            listing.CheckboxLabeled("RMush_SettingsCookingPoison".Translate(), ref settings.forbidPoisonousCookingIngredients,
                "RMush_SettingsCookingPoisonTip".Translate());
            listing.End();
        }
    }

    // These are additional live gates. They never enable a policy/filter entry,
    // rewrite a bill, remove an ingredient dose, or alter an ingestion outcome.
    [StaticConstructorOnStartup]
    public static class MushroomFoodRules
    {
        [ThreadStatic] private static Pawn manualInventoryPawn;
        [ThreadStatic] private static Thing manualInventoryFood;

        static MushroomFoodRules()
        {
            var harmony = new Harmony("izzypizzy.rimmushrooms.food-settings");
            harmony.Patch(AccessTools.Method(typeof(FoodUtility), nameof(FoodUtility.WillEat),
                new[] { typeof(Pawn), typeof(Thing), typeof(Pawn), typeof(bool), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(MushroomFoodRules), nameof(WillEatThingPostfix)));
            harmony.Patch(AccessTools.Method(typeof(FoodUtility), nameof(FoodUtility.WillEat),
                new[] { typeof(Pawn), typeof(ThingDef), typeof(Pawn), typeof(bool), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(MushroomFoodRules), nameof(WillEatDefPostfix)));
            // Caravan meals use only the def overload internally, so their
            // instance-level ingredient ledger needs this additional gate.
            harmony.Patch(AccessTools.Method(typeof(CaravanPawnsNeedsUtility), nameof(CaravanPawnsNeedsUtility.CanEatForNutritionNow),
                new[] { typeof(Thing), typeof(Pawn) }),
                postfix: new HarmonyMethod(typeof(MushroomFoodRules), nameof(CaravanThingPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Bill), nameof(Bill.IsFixedOrAllowedIngredient), new[] { typeof(Thing) }),
                postfix: new HarmonyMethod(typeof(MushroomFoodRules), nameof(BillThingPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Bill), nameof(Bill.IsFixedOrAllowedIngredient), new[] { typeof(ThingDef) }),
                postfix: new HarmonyMethod(typeof(MushroomFoodRules), nameof(BillDefPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Building_NutrientPasteDispenser), nameof(Building_NutrientPasteDispenser.IsAcceptableFeedstock)),
                postfix: new HarmonyMethod(typeof(MushroomFoodRules), nameof(FeedstockPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Building_NutrientPasteDispenser), nameof(Building_NutrientPasteDispenser.FindFeedInAnyHopper)),
                prefix: new HarmonyMethod(typeof(MushroomFoodRules), nameof(FindFeedPrefix)));
            harmony.Patch(AccessTools.Method(typeof(Building_NutrientPasteDispenser), nameof(Building_NutrientPasteDispenser.HasEnoughFeedstockInHoppers)),
                prefix: new HarmonyMethod(typeof(MushroomFoodRules), nameof(HasEnoughFeedPrefix)));
            // Native humanlike food searches validate a dispenser as a building,
            // without calling WillEat(Thing). Compose its existing validator so
            // rejected dispensers cannot repeatedly make then discard poison meals.
            harmony.Patch(AccessTools.Method(typeof(FoodUtility), "SpawnedFoodSearchInnerScan"),
                prefix: new HarmonyMethod(typeof(MushroomFoodRules), nameof(FoodSearchPrefix)));
            harmony.Patch(AccessTools.Method(typeof(JobDriver_Ingest), "MakeNewToils"),
                postfix: new HarmonyMethod(typeof(MushroomFoodRules), nameof(IngestToilsPostfix)));
            harmony.Patch(AccessTools.Method(typeof(ITab_Pawn_Gear), "DrawThingRow"),
                transpiler: new HarmonyMethod(typeof(MushroomFoodRules), nameof(InventoryMenuTranspiler)));
            harmony.Patch(AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(Pawn_InventoryTracker), nameof(Pawn_InventoryTracker.GetGizmos))),
                transpiler: new HarmonyMethod(typeof(MushroomFoodRules), nameof(InventoryMenuTranspiler)));
        }

        public static bool IsPurePoisonMushroom(ThingDef def)
        {
            if (def?.defName == null || !def.defName.StartsWith("RMush_Raw", StringComparison.Ordinal)) return false;
            var exposure = def.GetModExtension<MushroomExposureProperties>();
            return exposure?.poisonHediff != null && !exposure.psychoactive;
        }

        public static bool ContainsPurePoisonMushroom(Thing food)
        {
            if (food == null) return false;
            if (IsPurePoisonMushroom(food.def)) return true;
            var ledger = food.TryGetComp<CompMushroomMeal>();
            if (ledger != null && ledger.Doses.Any(d => d.Value > 0f && !float.IsNaN(d.Value)
                && !float.IsInfinity(d.Value) && IsPurePoisonMushroom(d.Key))) return true;
            // Old meals can retain native ingredient identities without a dose
            // ledger. Do not erase this evidence or invent a poison dose for it.
            var ingredients = food.TryGetComp<CompIngredients>();
            return ingredients?.ingredients != null && ingredients.ingredients.Any(IsPurePoisonMushroom);
        }

        public static bool BlocksAutomaticEating(ThingDef food) =>
            MoreMushroomsMod.Settings.forbidAutomaticPoisonousMushrooms && IsPurePoisonMushroom(food);

        public static bool BlocksAutomaticEating(Thing food)
        {
            if (!MoreMushroomsMod.Settings.forbidAutomaticPoisonousMushrooms || food == null) return false;
            var dispenser = food as Building_NutrientPasteDispenser;
            return dispenser != null ? DispenserContainsPurePoisonFeedstock(dispenser) : ContainsPurePoisonMushroom(food);
        }

        public static bool BlocksCookingIngredient(ThingDef ingredient, RecipeDef recipe) =>
            MoreMushroomsMod.Settings.forbidPoisonousCookingIngredients && IsPurePoisonMushroom(ingredient)
            && IsFoodRecipe(recipe);

        public static bool BlocksCookingIngredient(Thing ingredient, RecipeDef recipe) =>
            MoreMushroomsMod.Settings.forbidPoisonousCookingIngredients && ContainsPurePoisonMushroom(ingredient)
            && IsFoodRecipe(recipe);

        private static bool IsFoodRecipe(RecipeDef recipe) =>
            recipe?.products != null && recipe.products.Any(p => p.thingDef?.IsNutritionGivingIngestible == true);

        public static bool CanManuallyIngestFromInventory(Pawn pawn, Thing food)
        {
            if (pawn == null || food == null) return false;
            Pawn previousPawn = manualInventoryPawn;
            Thing previousFood = manualInventoryFood;
            try
            {
                manualInventoryPawn = pawn; manualInventoryFood = food;
                // Keep all native suitability and food-policy restrictions.
                // Only our automatic-selection gate is bypassed for this pair.
                return FoodUtility.WillIngestFromInventoryNow(pawn, food);
            }
            finally { manualInventoryPawn = previousPawn; manualInventoryFood = previousFood; }
        }

        public static bool DispenserContainsPurePoisonFeedstock(Building_NutrientPasteDispenser dispenser)
        {
            if (dispenser?.Spawned != true) return false;
            foreach (IntVec3 cell in dispenser.AdjCellsCardinalInBounds)
            {
                List<Thing> things = cell.GetThingList(dispenser.Map);
                // Native feedstock needs an actual hopper on the same cell.
                // A loose mushroom beside a dispenser is not a usable hopper.
                if (!things.Any(t => t.IsHopper())) continue;
                foreach (Thing candidate in things)
                    if (candidate.stackCount > 0 && NativeFeedstockDefinition(candidate.def)
                        && ContainsPurePoisonMushroom(candidate)) return true;
            }
            return false;
        }

        private static bool NativeFeedstockDefinition(ThingDef def)
        {
            // The verified Core 1.6 base rule, deliberately independent of the
            // cooking toggle: auto-block is conservative while poison remains
            // in a real hopper, even when another setting makes it unusable.
            return def?.IsNutritionGivingIngestible == true && def.ingestible.preferability != FoodPreferability.Undefined
                && (def.ingestible.foodType & FoodTypeFlags.Plant) != FoodTypeFlags.Plant
                && (def.ingestible.foodType & FoodTypeFlags.Tree) != FoodTypeFlags.Tree;
        }

        private static void WillEatThingPostfix(Pawn p, Thing food, ref bool __result)
        {
            if (__result && !(p == manualInventoryPawn && ReferenceEquals(food, manualInventoryFood)) && BlocksAutomaticEating(food)) __result = false;
        }

        private static void WillEatDefPostfix(Pawn p, ThingDef food, ref bool __result)
        {
            if (__result && !(p == manualInventoryPawn && food == manualInventoryFood?.def) && BlocksAutomaticEating(food)) __result = false;
        }

        private static void CaravanThingPostfix(Thing food, ref bool __result)
        {
            if (__result && BlocksAutomaticEating(food)) __result = false;
        }

        private static void BillThingPostfix(Bill __instance, Thing thing, ref bool __result)
        {
            if (__result && BlocksCookingIngredient(thing, __instance.recipe)) __result = false;
        }

        private static void BillDefPostfix(Bill __instance, ThingDef def, ref bool __result)
        {
            if (__result && BlocksCookingIngredient(def, __instance.recipe)) __result = false;
        }

        private static void FeedstockPostfix(ThingDef def, ref bool __result)
        {
            if (__result && MoreMushroomsMod.Settings.forbidPoisonousCookingIngredients && IsPurePoisonMushroom(def)) __result = false;
        }

        private static IEnumerable<Thing> AllowedHopperFeedstocks(Building_NutrientPasteDispenser dispenser)
        {
            if (dispenser?.Spawned != true) yield break;
            foreach (IntVec3 cell in dispenser.AdjCellsCardinalInBounds)
            {
                Thing feed = null; bool hopper = false;
                foreach (Thing candidate in cell.GetThingList(dispenser.Map))
                {
                    // Preserve Core's cell order and last eligible food within
                    // each real hopper, after excluding poisonous instances.
                    if (candidate.IsHopper()) hopper = true;
                    if (candidate.stackCount > 0 && Building_NutrientPasteDispenser.IsAcceptableFeedstock(candidate.def)
                        && !ContainsPurePoisonMushroom(candidate)) feed = candidate;
                }
                if (hopper && feed != null) yield return feed;
            }
        }

        private static bool FindFeedPrefix(Building_NutrientPasteDispenser __instance, ref Thing __result)
        {
            if (!MoreMushroomsMod.Settings.forbidPoisonousCookingIngredients) return true;
            __result = AllowedHopperFeedstocks(__instance).FirstOrDefault();
            return false;
        }

        private static bool HasEnoughFeedPrefix(Building_NutrientPasteDispenser __instance, ref bool __result)
        {
            if (!MoreMushroomsMod.Settings.forbidPoisonousCookingIngredients) return true;
            float nutrition = 0f; __result = false;
            foreach (Thing feed in AllowedHopperFeedstocks(__instance))
            {
                nutrition += feed.stackCount * feed.GetStatValue(StatDefOf.Nutrition);
                if (nutrition >= __instance.def.building.nutritionCostPerDispense) { __result = true; break; }
            }
            return false;
        }

        private static IEnumerable<CodeInstruction> InventoryMenuTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = AccessTools.Method(typeof(FoodUtility), nameof(FoodUtility.WillIngestFromInventoryNow));
            var manual = AccessTools.Method(typeof(MushroomFoodRules), nameof(CanManuallyIngestFromInventory));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, original))
                {
                    instruction.operand = manual; replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1) throw new InvalidOperationException("Expected exactly one native inventory Consume eligibility call per manual UI method; found " + replaced);
        }

        private static void FoodSearchPrefix(ref Predicate<Thing> validator)
        {
            if (!MoreMushroomsMod.Settings.forbidAutomaticPoisonousMushrooms) return;
            Predicate<Thing> native = validator;
            validator = candidate => (native == null || native(candidate)) && !BlocksAutomaticEating(candidate);
        }

        private static void IngestToilsPostfix(JobDriver_Ingest __instance)
        {
            // Native direct Consume orders set playerForced and remain allowed.
            // This live condition also handles a toggle during an automatic
            // ingest job and a dispenser whose contents changed after selection.
            __instance.AddFailCondition(() => __instance.job != null && !__instance.job.playerForced
                && BlocksAutomaticEating(__instance.job.targetA.Thing));
        }
    }
}
