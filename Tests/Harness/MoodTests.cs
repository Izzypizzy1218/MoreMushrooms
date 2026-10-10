using System;
using System.Linq;
using RimWorld;
using RimMushrooms;
using Verse;

namespace RimMushroomsTests
{
    internal static class MoodTests
    {
        private static readonly string[] Premium = { "Matsutake", "BlackTruffle", "Porcini", "Morel", "BlackTrumpet" };
        private static bool IsPremium(ThingDef raw) => Premium.Any(id => raw.defName == "RMush_Raw" + id);
        private static int Duration(ThingDef raw) => IsPremium(raw) ? 120000 : 15000;
        private static Thought_MushroomEnjoyment[] Bonuses(Pawn pawn) => pawn.needs.mood.thoughts.memories.Memories.OfType<Thought_MushroomEnjoyment>().ToArray();
        private static void Clear(Pawn pawn)
        {
            foreach (var memory in Bonuses(pawn)) pawn.needs.mood.thoughts.memories.RemoveMemory(memory);
        }
        private static Thing Raw(ThingDef def) => ThingMaker.MakeThing(def);
        private static Thing Meal(ThingDef mealDef, params ThingDef[] ingredients)
        {
            var food = ThingMaker.MakeThing(mealDef);
            foreach (var ingredient in ingredients) food.TryGetComp<CompIngredients>().RegisterIngredient(ingredient);
            return food;
        }
        private static void Eat(Pawn pawn, Thing food, Action<bool, string> check)
        {
            var native = FoodUtility.ThoughtsFromIngesting(pawn, food, food.def)
                .Where(t => !typeof(Thought_MushroomEnjoyment).IsAssignableFrom(t.thought.ThoughtClass))
                .Select(t => t.thought).ToArray();
            check(food.Ingested(pawn, 0.05f) > 0, "actual Ingested consumes " + food.def.defName);
            check(native.All(d => pawn.needs.mood.thoughts.memories.Memories.Any(m => m.def == d)), "native meal/raw/ideology memories retained " + food.def.defName);
        }
        private static void Expect(Pawn pawn, ThingDef ingredient, Action<bool, string> check, string context)
        {
            var memories = Bonuses(pawn);
            var expected = ingredient.ingestible.specialThoughtDirect;
            check(memories.Length == 1 && memories[0].def == expected, context + " one bonus " + ingredient.defName);
            check(memories[0].MoodOffset() == expected.stages[0].baseMoodEffect, context + " mood +" + expected.stages[0].baseMoodEffect);
            check(memories[0].DurationTicks == Duration(ingredient), context + " " + (IsPremium(ingredient) ? "48 hours / 120000 ticks" : "six hours / 15000 ticks"));
        }
        public static void Run(Pawn pawn, Action<bool, string> check)
        {
            var items = DefDatabase<ThingDef>.AllDefs.Where(d => d.defName.StartsWith("RMush_Raw")
                && d.ingestible?.specialThoughtDirect?.ThoughtClass == typeof(Thought_MushroomEnjoyment))
                .OrderBy(d => d.ingestible.specialThoughtDirect.stages[0].baseMoodEffect).ThenBy(d => d.defName).ToArray();
            var premium = items.Where(IsPremium).ToArray();
            var ordinary = items.Where(d => !IsPremium(d)).ToArray();
            var matsutake = ThingDef.Named("RMush_RawMatsutake");
            check(items.Length == 21 && premium.Length == 5 && ordinary.Length == 16, "21 edible taste memories include exactly five premium wild species");
            check(premium.All(d => d.ingestible.specialThoughtDirect.stages[0].baseMoodEffect == 15f), "all five premium edible species give +15 mood");
            check(ordinary.All(d => new[] { 3f, 5f, 7f }.Contains(d.ingestible.specialThoughtDirect.stages[0].baseMoodEffect)), "ordinary edible species retain +3/+5/+7 mood tiers");
            foreach (var item in items)
            {
                check(!item.ingestible.specialThoughtDirect.ConfigErrors().Any(), "memory def config " + item.defName);
                Clear(pawn);
                Eat(pawn, Raw(item), check);
                Expect(pawn, item, check, "raw");
                Clear(pawn);
                Eat(pawn, Meal(ThingDefOf.MealFine, item), check);
                Expect(pawn, item, check, "cooked fine meal");
                var memory = Bonuses(pawn).Single();
                if (IsPremium(item))
                {
                    memory.age = 15000;
                    check(!memory.ShouldDiscard, "premium memory survives the former six-hour expiry " + item.defName);
                    Eat(pawn, Raw(ordinary[0]), check);
                    check(Bonuses(pawn).Single() == memory && memory.age == 15000,
                        "ordinary food cannot replace or refresh premium memory " + item.defName);
                }
                memory.age = Duration(item) - 1;
                check(!memory.ShouldDiscard, "present one tick before configured taste expiry " + item.defName);
                memory.age = Duration(item);
                check(memory.ShouldDiscard, "expires at configured taste boundary " + item.defName);
                pawn.needs.mood.thoughts.memories.MemoryThoughtInterval();
                check(Bonuses(pawn).Length == 0, "expired memory removed by native interval " + item.defName);
            }
            var withMatsutake = ordinary.Concat(new[] { matsutake }).ToArray();
            foreach (var ingredients in new[] { withMatsutake, withMatsutake.Reverse().ToArray() })
            {
                Clear(pawn);
                Eat(pawn, Meal(ThingDefOf.MealSimple, ingredients), check);
                Expect(pawn, matsutake, check, "mixed ordinary/premium ingredients in either order");
            }
            foreach (var ingredients in new[] { premium, premium.Reverse().ToArray() })
            {
                Clear(pawn);
                Eat(pawn, Meal(ThingDefOf.MealFine, ingredients), check);
                var mixed = Bonuses(pawn);
                check(mixed.Length == 1 && mixed[0].MoodOffset() == 15f && mixed[0].DurationTicks == 120000
                    && premium.Any(d => d.ingestible.specialThoughtDirect == mixed[0].def), "five premium ingredients produce one +15/48-hour memory in either order");
            }
            Clear(pawn);
            Eat(pawn, Raw(matsutake), check);
            var top = Bonuses(pawn).Single();
            top.age = 10000;
            Eat(pawn, Raw(items[0]), check);
            check(Bonuses(pawn).Single() == top && top.age == 10000, "weaker variety neither stacks nor extends stronger bonus");
            Eat(pawn, Raw(matsutake), check);
            check(Bonuses(pawn).Length == 1 && Bonuses(pawn)[0].age == 0, "same variety refreshes duration without stacking");
            Clear(pawn);
            Eat(pawn, Raw(items[0]), check);
            Eat(pawn, Raw(matsutake), check);
            Expect(pawn, matsutake, check, "stronger replaces weaker");
            top = Bonuses(pawn).Single();
            top.age = Duration(matsutake);
            Eat(pawn, Raw(items[0]), check);
            Expect(pawn, items[0], check, "expired stronger cannot block new weaker");
            foreach (var group in items.GroupBy(d => d.ingestible.specialThoughtDirect.stages[0].baseMoodEffect).Where(g => g.Count() > 1))
            {
                Clear(pawn);
                Eat(pawn, Raw(group.First()), check);
                Bonuses(pawn).Single().age = Duration(group.First()) / 2;
                Eat(pawn, Raw(group.Last()), check);
                Expect(pawn, group.Last(), check, "different variety in same tier replaces label without stacking");
                check(Bonuses(pawn).Single().age == 0, "different variety in tier +" + group.Key + " refreshes configured duration");
            }
            Clear(pawn);
            Eat(pawn, Raw(ThingDef.Named("RawFungus")), check);
            Eat(pawn, Meal(ThingDefOf.MealSimple, ThingDefOf.RawPotatoes), check);
            check(Bonuses(pawn).Length == 0, "vanilla fungus and mushroom-free meals grant no custom bonus");
            var animal = PawnGenerator.GeneratePawn(PawnKindDef.Named("Muffalo"));
            check(animal.needs.mood == null, "animal has no mood need");
            check(Raw(matsutake).Ingested(animal, 0.05f) > 0, "animal safely eats mushroom without mood need");
            Eat(pawn, Raw(matsutake), check);
            Bonuses(pawn).Single().age = 5000;
            check(true, "active matsutake memory prepared for save/load at age 5000");
        }
        public static void VerifyLoaded(Map map, Action<bool, string> check, bool legacy = false)
        {
            var pawn = map.mapPawns.FreeColonistsSpawned.Single(p => p.needs.mood != null && Bonuses(p).Length > 0);
            var memory = Bonuses(pawn).Single();
            check(memory.def.defName == "RMush_AteMatsutake" && memory.MoodOffset() == 15f, "saved premium mushroom memory uses current +15 definition");
            check(memory.age == 5000 && memory.DurationTicks == 120000,
                (legacy ? "legacy six-hour memory keeps saved age and adopts current 48-hour duration" : "saved 48-hour memory age and duration restored")
                + " age=" + memory.age + " duration=" + memory.DurationTicks);
            check(LanguageDatabase.activeLanguage.folderName.StartsWith("Korean") ? memory.LabelCap == "송이의 솔숲 향" : memory.LabelCap == "Pine-scented matsutake", "translated mushroom memory label");
            memory.age = 120000;
            pawn.needs.mood.thoughts.memories.MemoryThoughtInterval();
            check(Bonuses(pawn).Length == 0, "loaded memory expires normally");
        }
    }
}
