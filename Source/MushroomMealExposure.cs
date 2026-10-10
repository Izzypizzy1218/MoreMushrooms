using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMushrooms
{
    public sealed class MushroomExposureProperties : DefModExtension
    {
        public HediffDef poisonHediff;
        public bool psychoactive;
        public int moodBonus;
        public float moodDurationHours = 6f;
        public float hallucinationHoursMin;
        public float hallucinationHoursMax;
        public float doseUnitCount = 1f;
    }

    public sealed class IngestionOutcomeDoer_MushroomExposure : IngestionOutcomeDoer
    {
        protected override void DoIngestionOutcomeSpecial(Pawn pawn, Thing ingested, int ingestedCount)
        {
            MushroomMeals.Apply(pawn, ingested.def, ingestedCount / Mathf.Max(0.01f,
                ingested.def.GetModExtension<MushroomExposureProperties>()?.doseUnitCount ?? 1f));
        }
    }

    public sealed class IngestionOutcomeDoer_MushroomMeal : IngestionOutcomeDoer
    {
        protected override void DoIngestionOutcomeSpecial(Pawn pawn, Thing ingested, int ingestedCount)
        {
            var comp = ingested.TryGetComp<CompMushroomMeal>();
            if (comp == null) return;
            MushroomMeals.ApplyMixture(pawn, comp.Doses.Select(d => new KeyValuePair<ThingDef, float>(d.Key, d.Value * ingestedCount)).ToList());
        }
    }

    public sealed class CompProperties_MushroomMeal : CompProperties
    {
        public CompProperties_MushroomMeal() { compClass = typeof(CompMushroomMeal); }
    }

    // Doses are per individual finished item, so splitting and identical-batch
    // merging never amplify or dilute an exposure. Normal ingredient labels stay native.
    public sealed class CompMushroomMeal : ThingComp
    {
        private List<ThingDef> sources = new List<ThingDef>();
        private List<float> units = new List<float>();
        public IEnumerable<KeyValuePair<ThingDef, float>> Doses
        {
            get { for (int i = 0; i < sources.Count; i++) yield return new KeyValuePair<ThingDef, float>(sources[i], units[i]); }
        }
        public bool Psychoactive => sources.Any(d => d.GetModExtension<MushroomExposureProperties>()?.psychoactive == true);
        public void SetDoses(IEnumerable<KeyValuePair<ThingDef, float>> doses)
        {
            var sorted = doses.Where(d => d.Key != null && d.Value > 0.000001f).OrderBy(d => d.Key.defName).ToList();
            sources = sorted.Select(d => d.Key).ToList();
            units = sorted.Select(d => d.Value).ToList();
        }
        public override void PostExposeData()
        {
            Scribe_Collections.Look(ref sources, "mushroomExposureSources", LookMode.Def);
            Scribe_Collections.Look(ref units, "mushroomExposureUnitsPerItem", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (sources == null || units == null || sources.Count != units.Count) { sources = new List<ThingDef>(); units = new List<float>(); }
                for (int i = sources.Count - 1; i >= 0; i--)
                    if (sources[i] == null || float.IsNaN(units[i]) || float.IsInfinity(units[i]) || units[i] <= 0) { sources.RemoveAt(i); units.RemoveAt(i); }
            }
        }
        public override void PostSplitOff(Thing piece)
        {
            if (piece != parent) piece.TryGetComp<CompMushroomMeal>()?.SetDoses(Doses);
        }
        public override bool AllowStackWith(Thing other)
        {
            var comp = other.TryGetComp<CompMushroomMeal>();
            if (comp == null || sources.Count != comp.sources.Count) return false;
            for (int i = 0; i < sources.Count; i++)
                if (sources[i] != comp.sources[i] || Mathf.Abs(units[i] - comp.units[i]) > 0.00001f) return false;
            return true;
        }
    }

    public sealed class SpecialThingFilterWorker_PsychoactiveMushroomMeal : SpecialThingFilterWorker
    {
        public override bool Matches(Thing t) => t.TryGetComp<CompMushroomMeal>()?.Psychoactive == true;
        public override bool CanEverMatch(ThingDef def) => def.ingestible != null && def.HasComp(typeof(CompIngredients));
        public override bool AlwaysMatches(ThingDef def) => false;
    }

    [StaticConstructorOnStartup]
    public static class MushroomMeals
    {
        static MushroomMeals()
        {
            foreach (var def in DefDatabase<ThingDef>.AllDefs.Where(d => d.ingestible != null && d.HasComp(typeof(CompIngredients))))
            {
                if (!def.comps.Any(c => c is CompProperties_MushroomMeal)) def.comps.Add(new CompProperties_MushroomMeal());
                if (def.ingestible.outcomeDoers == null) def.ingestible.outcomeDoers = new List<IngestionOutcomeDoer>();
                if (!def.ingestible.outcomeDoers.Any(o => o is IngestionOutcomeDoer_MushroomMeal)) def.ingestible.outcomeDoers.Add(new IngestionOutcomeDoer_MushroomMeal());
            }
            new Harmony("izzypizzy.rimmushrooms.meal-exposure").Patch(
                AccessTools.Method(typeof(GenRecipe), nameof(GenRecipe.MakeRecipeProducts)),
                postfix: new HarmonyMethod(typeof(MushroomMeals), nameof(RecipeProductsPostfix)));
            new Harmony("izzypizzy.rimmushrooms.meal-exposure").Patch(
                AccessTools.Method(typeof(Building_NutrientPasteDispenser), nameof(Building_NutrientPasteDispenser.TryDispenseFood)),
                prefix: new HarmonyMethod(typeof(MushroomMeals), nameof(PastePrefix)),
                postfix: new HarmonyMethod(typeof(MushroomMeals), nameof(PastePostfix)));
        }
        public static void Apply(Pawn pawn, ThingDef source, float doses)
        {
            if (pawn == null || pawn.Dead || doses <= 0) return;
            var props = source.GetModExtension<MushroomExposureProperties>();
            if (props == null) return;
            if (props.poisonHediff != null) MushroomPoisoning.ApplyExposure(pawn, source, props.poisonHediff, doses);
            if (props.psychoactive) MushroomPsychoactive.Apply(pawn, source, doses);
        }
        public static void ApplyMixture(Pawn pawn, IEnumerable<KeyValuePair<ThingDef, float>> doses)
        {
            var actual = doses.Where(d => d.Key != null && d.Value > 0 && !float.IsNaN(d.Value) && !float.IsInfinity(d.Value)).ToList();
            if (pawn == null || pawn.Dead || actual.Count == 0) return;
            foreach (var dose in actual)
            {
                var props = dose.Key.GetModExtension<MushroomExposureProperties>();
                if (props?.poisonHediff != null) MushroomPoisoning.ApplyExposure(pawn, dose.Key, props.poisonHediff, dose.Value);
            }
            MushroomPsychoactive.ApplyMixture(pawn, actual);
        }
        private static void RecipeProductsPostfix(ref IEnumerable<Thing> __result, List<Thing> ingredients)
        {
            __result = RecordProducts(__result, ingredients);
        }
        private static IEnumerable<Thing> RecordProducts(IEnumerable<Thing> products, List<Thing> ingredients)
        {
            var total = new Dictionary<ThingDef, float>();
            foreach (var ingredient in ingredients)
            {
                var ext = ingredient.def.GetModExtension<MushroomExposureProperties>();
                if (ext != null) Add(total, ingredient.def, ingredient.stackCount / Mathf.Max(0.01f, ext.doseUnitCount));
                var comp = ingredient.TryGetComp<CompMushroomMeal>();
                if (comp != null) foreach (var dose in comp.Doses) Add(total, dose.Key, dose.Value * ingredient.stackCount);
            }
            var result = products.ToList();
            var foods = result.Where(p => p.TryGetComp<CompMushroomMeal>() != null).ToList();
            float nutrition = foods.Sum(p => Mathf.Max(0.001f, p.GetStatValue(StatDefOf.Nutrition)) * p.stackCount);
            if (nutrition > 0)
                foreach (var food in foods)
                    food.TryGetComp<CompMushroomMeal>().SetDoses(total.Select(d => new KeyValuePair<ThingDef, float>(d.Key,
                        d.Value * Mathf.Max(0.001f, food.GetStatValue(StatDefOf.Nutrition)) / nutrition)));
            foreach (var item in result) yield return item;
        }
        private static void Add(Dictionary<ThingDef, float> values, ThingDef source, float value)
        {
            float old; values.TryGetValue(source, out old); values[source] = old + value;
        }
        private static void PastePrefix(Building_NutrientPasteDispenser __instance, out Dictionary<Thing, int> __state)
        {
            __state = new Dictionary<Thing, int>();
            if (!__instance.Spawned || !__instance.CanDispenseNow) return;
            foreach (var cell in __instance.AdjCellsCardinalInBounds)
                foreach (var food in cell.GetThingList(__instance.Map))
                    if (Building_NutrientPasteDispenser.IsAcceptableFeedstock(food.def)) __state[food] = food.stackCount;
        }
        private static void PastePostfix(Thing __result, Dictionary<Thing, int> __state)
        {
            var ledger = __result?.TryGetComp<CompMushroomMeal>();
            if (ledger == null) return;
            var total = new Dictionary<ThingDef, float>();
            foreach (var entry in __state)
            {
                var food = entry.Key;
                int consumed = entry.Value - (food.Destroyed ? 0 : food.stackCount);
                if (consumed <= 0) continue;
                var ext = food.def.GetModExtension<MushroomExposureProperties>();
                if (ext != null) Add(total, food.def, consumed / Mathf.Max(0.01f, ext.doseUnitCount));
                var comp = food.TryGetComp<CompMushroomMeal>();
                if (comp != null) foreach (var dose in comp.Doses) Add(total, dose.Key, dose.Value * consumed);
            }
            ledger.SetDoses(total);
        }
    }

    public sealed class MushroomFoodMigration : GameComponent
    {
        private bool expansionFoodInitialized;
        public MushroomFoodMigration(Game game) { }
        public override void ExposeData() { Scribe_Values.Look(ref expansionFoodInitialized, "moreMushroomsExpansionFoodInitialized", false); }
        public override void StartedNewGame() { expansionFoodInitialized = true; }
        public override void LoadedGame()
        {
            if (expansionFoodInitialized) return;
            foreach (var map in Find.Maps)
                foreach (var giver in map.listerThings.AllThings.OfType<IBillGiver>())
                    foreach (var bill in giver.BillStack.Bills) MigrateBill(bill);
            expansionFoodInitialized = true;
        }
        public static void MigrateBill(Bill bill)
        {
            // Old saves list individual allowed Defs. A wholly enabled legacy
            // vegetable list can accept new food species; selective lists stay selective.
            string[] anchors = { "RawRice", "RawCorn", "RawPotatoes", "RMush_RawButton", "RMush_RawShiitake", "RMush_RawOyster", "RMush_RawKingOyster", "RMush_RawEnoki", "RMush_RawWoodEar", "RMush_RawBeech", "RMush_RawMaitake", "RMush_RawLionsMane", "RMush_RawMatsutake" };
            if (bill?.ingredientFilter == null || !anchors.All(n => bill.ingredientFilter.Allows(ThingDef.Named(n)))) return;
            // Fly agaric was outside every native food recipe in v0.4; its old
            // absence on an otherwise broad food bill was an automatic restriction.
            var legacy = new HashSet<string>(anchors.Concat(new[] { "RMush_RawDestroyingAngel", "RMush_RawSulfurTuft", "RMush_RawTsukiyotake", "RMush_RawPoisonFireCoral" }));
            foreach (var food in DefDatabase<ThingDef>.AllDefs.Where(d => d.defName.StartsWith("RMush_Raw") && !legacy.Contains(d.defName) && d.IsNutritionGivingIngestible))
                if (bill.recipe.fixedIngredientFilter.Allows(food)) bill.ingredientFilter.SetAllow(food, true);
        }
    }
}
