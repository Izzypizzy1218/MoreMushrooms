using System;
using System.Collections.Generic;
using System.Linq;
using RimMushrooms;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMushroomsTests
{
    internal static class ExpansionTests
    {
        private static readonly string[] NewIds = {
            "Cauliflower", "PurpleBlewit", "BlackTruffle", "Porcini", "Morel", "BlackTrumpet", "GiantPuffball", "IndigoMilkcap",
            "VeiledStinkhorn", "ShaggyInkcap", "Lobster", "Reishi", "TurkeyTail", "Sanghuang", "Cordyceps", "Chaga", "LibertyCap",
            "PantherCap", "Cubensis", "DeathCap", "YellowDapperling", "JackOLantern", "DeadlyWebcap", "ZombieAntFungus",
            "HoneyFungus", "DryRot", "GhostFungus", "YellowBrain", "DevilsFingers", "BirdsNest", "Chlorophos" };
        private static readonly string[] Cultivated = { "Button", "Shiitake", "Oyster", "KingOyster", "Enoki", "WoodEar",
            "Beech", "Maitake", "LionsMane", "Cauliflower", "PurpleBlewit" };
        private static readonly Dictionary<string, Fixture> Saved = new Dictionary<string, Fixture>();
        private sealed class Fixture
        {
            public string Def, Texture;
            public int Stack;
            public float Growth;
            public bool Plant;
        }
        private static void Check(Action<bool, string> check, bool valid, string text) => check(valid, "expansion: " + text);
        private static ThingDef Raw(string id) => ThingDef.Named("RMush_Raw" + id);
        private static ThingDef Crop(string id) => ThingDef.Named("RMush_Plant" + id);

        public static void Run(Pawn farmer, Map map, Action<bool, string> check)
        {
            Saved.Clear();
            var rawDefs = DefDatabase<ThingDef>.AllDefs.Where(d => d.defName.StartsWith("RMush_Raw")).ToArray();
            var cropDefs = DefDatabase<ThingDef>.AllDefs.Where(d => d.defName.StartsWith("RMush_Plant")
                && d.defName != "RMush_PlantAssorted" && d.plant != null).ToArray();
            Check(check, rawDefs.Length == 46 && cropDefs.Length == 47 && cropDefs.Select(d => d.plant.harvestedThingDef).Distinct().Count() == 46,
                "exactly 46 harvested species and 47 plants including preserved wild enoki variant loaded");
            Check(check, cropDefs.Where(d => d.plant.Sowable).Select(d => d.defName.Substring("RMush_Plant".Length)).OrderBy(x => x)
                .SequenceEqual(Cultivated.OrderBy(x => x)), "exactly original nine plus cauliflower/purple blewit can be sown");
            foreach (var crop in cropDefs)
            {
                var raw = crop.plant.harvestedThingDef;
                Check(check, !crop.ConfigErrors().Any() && raw != null && !raw.ConfigErrors().Any()
                    && (raw.defName.Substring("RMush_Raw".Length) == crop.defName.Substring("RMush_Plant".Length)
                        || crop.defName == "RMush_PlantEnokiWild" && raw.defName == "RMush_RawEnoki"),
                    "resolved plant/raw configuration and harvest identity " + crop.defName);
                Check(check, crop.thingClass == typeof(Plant_Mushroom), "compatible mushroom plant class " + crop.defName);
                if (NewIds.Any(id => crop.defName == "RMush_Plant" + id)
                    || new[] { "FlyAgaric", "DestroyingAngel", "SulfurTuft", "Tsukiyotake", "PoisonFireCoral" }.Any(id => crop.defName == "RMush_Plant" + id))
                    Check(check, crop.ingestible == null, "no eating live expansion or poisonous plants " + crop.defName);
                var plant = (Plant)ThingMaker.MakeThing(crop);
                try
                {
                    plant.Growth = 1f;
                    int yield = plant.YieldNow();
                    float expected = crop.plant.harvestYield * (crop.plant.harvestYieldAffectedByDifficulty ? Find.Storyteller.difficulty.cropYieldFactor : 1f);
                    Check(check, yield >= Mathf.FloorToInt(expected) && yield <= Mathf.CeilToInt(expected),
                        "native mature harvest yield matches configured species " + crop.defName);
                    bool registeredWild = DefDatabase<BiomeDef>.AllDefs.Any(b => b.AllWildPlants.Contains(crop));
                    Check(check, crop.defName == "RMush_PlantEnoki" ? !registeredWild : registeredWild,
                        crop.defName == "RMush_PlantEnoki" ? "preserved white enoki is cultivation-only"
                            : "vanilla biome registration " + crop.defName);
                }
                finally { plant.Destroy(DestroyMode.Vanish); }
            }
            foreach (var id in Cultivated)
                Check(check, Crop(id).plant.sowTags.Contains("Ground") && Crop(id).plant.sowTags.Contains("Hydroponic"), "soil and hydroponic sow tags " + id);
            foreach (var recipe in DefDatabase<RecipeDef>.AllDefs)
                Check(check, !recipe.defName.StartsWith("RMush_") || !recipe.products.Any(p => p.thingDef.IsDrug)
                    && recipe.defName.IndexOf("Apprais", StringComparison.OrdinalIgnoreCase) < 0
                    && recipe.defName.IndexOf("Extract", StringComparison.OrdinalIgnoreCase) < 0,
                    "no added appraisal or extraction/drug crafting " + recipe.defName);

            var rect = new CellRect(map.Center.x - 18, map.Center.z + 17, 36, 7);
            Check(check, rect.Cells.All(c => c.InBounds(map)), "art/save fixtures fit isolated test map");
            foreach (var cell in rect.Cells)
            {
                foreach (var thing in cell.GetThingList(map).ToList()) if (!(thing is Pawn)) thing.Destroy(DestroyMode.Vanish);
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
                map.roofGrid.SetRoof(cell, null);
                map.snowGrid.SetDepth(cell, 0f);
            }
            int index = 0;
            foreach (var id in NewIds)
            {
                var plant = (Plant)ThingMaker.MakeThing(Crop(id));
                var random = plant.Graphic as Graphic_Random;
                Check(check, random != null && random.SubGraphicsCount == 2 && Enumerable.Range(0, 2)
                    .Select(i => random.SubGraphicAtIndex(i).MatSingle.mainTexture.name).OrderBy(n => n)
                    .SequenceEqual(new[] { id + "A", id + "B" }), "approved A/B plant sprites loaded " + id);
                plant.Destroy(DestroyMode.Vanish);
                foreach (float growth in new[] { 0.1f, 0.5f, 1f })
                {
                    plant = (Plant)ThingMaker.MakeThing(Crop(id));
                    plant.Growth = growth;
                    var cell = FixtureCell(map, index++);
                    GenSpawn.Spawn(plant, cell, map);
                    Check(check, plant.Graphic != BaseContent.BadGraphic && plant.Growth == growth,
                        "native plant growth fixture " + id + " " + growth);
                    Capture(plant, growth);
                }
                foreach (int count in new[] { 1, 26, 51 })
                {
                    var item = ThingMaker.MakeThing(Raw(id));
                    item.stackCount = count;
                    GenSpawn.Spawn(item, FixtureCell(map, index++), map);
                    string texture = item.Graphic.MatSingleFor(item).mainTexture.name;
                    string expected = count <= 25 ? "01Low" : count <= 50 ? "02Medium" : "03Full";
                    Check(check, texture == expected, "approved stack amount sprite " + id + " " + count);
                    Capture(item, 0f);
                }
            }
            Check(check, Saved.Count == 186, "31 species times three growth and three box fixtures saved");
            ValidateNewTasteMemories(map, check);
        }

        private static IntVec3 FixtureCell(Map map, int index) => map.Center + new IntVec3(-18 + index % 36, 0, 17 + index / 36);
        private static void Capture(Thing thing, float growth)
        {
            Saved.Add(thing.GetUniqueLoadID(), new Fixture { Def = thing.def.defName, Stack = thing.stackCount, Growth = growth,
                Plant = thing is Plant, Texture = thing.Graphic.MatSingleFor(thing).mainTexture.name });
        }

        private static void ValidateNewTasteMemories(Map map, Action<bool, string> check)
        {
            var foods = NewIds.Select(Raw).Where(d => d.ingestible?.specialThoughtDirect != null).ToArray();
            Check(check, foods.Length == 11, "eleven new edible species have taste memories");
            var pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            foreach (var effect in pawn.health.hediffSet.hediffs.ToList()) pawn.health.RemoveHediff(effect);
            try
            {
                foreach (var raw in foods)
                {
                    foreach (var memory in pawn.needs.mood.thoughts.memories.Memories.OfType<Thought_MushroomEnjoyment>().ToArray())
                        pawn.needs.mood.thoughts.memories.RemoveMemory(memory);
                    var food = ThingMaker.MakeThing(raw);
                    food.Ingested(pawn, 0.05f);
                    CheckTaste(pawn, raw, check, "raw");
                    foreach (var memory in pawn.needs.mood.thoughts.memories.Memories.OfType<Thought_MushroomEnjoyment>().ToArray())
                        pawn.needs.mood.thoughts.memories.RemoveMemory(memory);
                    var meal = ThingMaker.MakeThing(ThingDefOf.MealFine);
                    meal.TryGetComp<CompIngredients>().RegisterIngredient(raw);
                    meal.Ingested(pawn, 0.05f);
                    CheckTaste(pawn, raw, check, "native meal ingredient");
                }
            }
            finally { pawn.Destroy(DestroyMode.Vanish); }
        }
        private static void CheckTaste(Pawn pawn, ThingDef raw, Action<bool, string> check, string context)
        {
            var memories = pawn.needs.mood.thoughts.memories.Memories.OfType<Thought_MushroomEnjoyment>().ToArray();
            var expected = raw.ingestible.specialThoughtDirect;
            bool premium = new[] { "BlackTruffle", "Porcini", "Morel", "BlackTrumpet" }.Any(id => raw.defName == "RMush_Raw" + id);
            Check(check, memories.Length == 1 && memories[0].def == expected && memories[0].DurationTicks == (premium ? 120000 : 15000)
                && memories[0].MoodOffset() == expected.stages[0].baseMoodEffect,
                "single correct " + (premium ? "48-hour" : "six-hour") + " taste memory " + context + " " + raw.defName);
        }
        public static void VerifyLoaded(Map map, Action<bool, string> check)
        {
            Check(check, Saved.Count == 186, "expected expansion save snapshots retained");
            var things = map.listerThings.AllThings.ToDictionary(t => t.GetUniqueLoadID());
            foreach (var fixture in Saved)
            {
                Thing thing;
                bool found = things.TryGetValue(fixture.Key, out thing);
                Check(check, found && thing.def.defName == fixture.Value.Def && thing.stackCount == fixture.Value.Stack,
                    "native save restores species identity and stack " + fixture.Value.Def + " " + fixture.Key);
                if (!found) continue;
                if (fixture.Value.Plant) Check(check, thing is Plant && ((Plant)thing).Growth == fixture.Value.Growth,
                    "native save preserves growth fraction " + fixture.Value.Def + " " + fixture.Value.Growth);
                Check(check, thing.Graphic != BaseContent.BadGraphic && thing.Graphic.MatSingleFor(thing).mainTexture.name == fixture.Value.Texture,
                    "native save reloads approved correct sprite " + fixture.Value.Def + " " + fixture.Value.Texture);
            }
        }
    }
}
