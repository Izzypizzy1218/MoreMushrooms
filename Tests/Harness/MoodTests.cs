using System;
using System.Linq;
using RimWorld;
using RimMushrooms;
using Verse;

namespace RimMushroomsTests
{
    internal static class MoodTests
    {
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
            check(memories[0].DurationTicks == 15000, context + " six hours / 15000 ticks");
        }
        public static void Run(Pawn pawn, Action<bool, string> check)
        {
            var items = DefDatabase<ThingDef>.AllDefs.Where(d => d.defName.StartsWith("RMush_Raw"))
                .OrderBy(d => d.ingestible.specialThoughtDirect.stages[0].baseMoodEffect).ToArray();
            check(items.Select(d => d.ingestible.specialThoughtDirect.stages[0].baseMoodEffect).SequenceEqual(new float[] {3,3,3,5,5,5,7,7,7,10}), "three mood groups plus matsutake premium");
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
                memory.age = 14999;
                check(!memory.ShouldDiscard, "present before six-hour boundary " + item.defName);
                memory.age = 15000;
                check(memory.ShouldDiscard, "expires at six-hour boundary " + item.defName);
                pawn.needs.mood.thoughts.memories.MemoryThoughtInterval();
                check(Bonuses(pawn).Length == 0, "expired memory removed by native interval " + item.defName);
            }
            foreach (var ingredients in new[] { items, items.Reverse().ToArray() })
            {
                Clear(pawn);
                Eat(pawn, Meal(ThingDefOf.MealSimple, ingredients), check);
                Expect(pawn, items[9], check, "mixed ingredients in either order");
            }
            var top = Bonuses(pawn).Single();
            top.age = 10000;
            Eat(pawn, Raw(items[0]), check);
            check(Bonuses(pawn).Single() == top && top.age == 10000, "weaker variety neither stacks nor extends stronger bonus");
            Eat(pawn, Raw(items[9]), check);
            check(Bonuses(pawn).Length == 1 && Bonuses(pawn)[0].age == 0, "same variety refreshes duration without stacking");
            Clear(pawn);
            Eat(pawn, Raw(items[0]), check);
            Eat(pawn, Raw(items[9]), check);
            Expect(pawn, items[9], check, "stronger replaces weaker");
            top = Bonuses(pawn).Single();
            top.age = 15000;
            Eat(pawn, Raw(items[0]), check);
            Expect(pawn, items[0], check, "expired stronger cannot block new weaker");
            foreach (var group in items.GroupBy(d => d.ingestible.specialThoughtDirect.stages[0].baseMoodEffect).Where(g => g.Count() > 1))
            {
                Clear(pawn);
                Eat(pawn, Raw(group.First()), check);
                Bonuses(pawn).Single().age = 7500;
                Eat(pawn, Raw(group.Last()), check);
                Expect(pawn, group.Last(), check, "different variety in same tier replaces label without stacking");
                check(Bonuses(pawn).Single().age == 0, "different variety in tier +" + group.Key + " refreshes six-hour duration");
            }
            Clear(pawn);
            Eat(pawn, Raw(ThingDef.Named("RawFungus")), check);
            Eat(pawn, Meal(ThingDefOf.MealSimple, ThingDefOf.RawPotatoes), check);
            check(Bonuses(pawn).Length == 0, "vanilla fungus and mushroom-free meals grant no custom bonus");
            var animal = PawnGenerator.GeneratePawn(PawnKindDef.Named("Muffalo"));
            check(animal.needs.mood == null, "animal has no mood need");
            check(Raw(items[9]).Ingested(animal, 0.05f) > 0, "animal safely eats mushroom without mood need");
            Eat(pawn, Raw(items[9]), check);
            Bonuses(pawn).Single().age = 5000;
            check(true, "active matsutake memory prepared for save/load at age 5000");
        }
        public static void VerifyLoaded(Map map, Action<bool, string> check)
        {
            var pawn = map.mapPawns.FreeColonistsSpawned.Single(p => p.needs.mood != null && Bonuses(p).Length > 0);
            var memory = Bonuses(pawn).Single();
            check(memory.def.defName == "RMush_AteMatsutake" && memory.MoodOffset() == 10f, "saved mushroom memory class and value restored");
            check(memory.age == 5000 && memory.DurationTicks == 15000, "saved remaining duration restored age=" + memory.age + " duration=" + memory.DurationTicks);
            check(LanguageDatabase.activeLanguage.folderName.StartsWith("Korean") ? memory.LabelCap == "송이의 솔숲 향" : memory.LabelCap == "Pine-scented matsutake", "translated mushroom memory label");
            memory.age = 15000;
            pawn.needs.mood.thoughts.memories.MemoryThoughtInterval();
            check(Bonuses(pawn).Length == 0, "loaded memory expires normally");
        }
    }
}
