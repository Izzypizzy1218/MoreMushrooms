using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using RimMushrooms;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMushroomsTests
{
    // Only the isolated smoke harness invokes this suite. Options gate native
    // selection, while ingredient filters and saved exposure stay independent.
    internal static class FoodSettingsTests
    {
        private static readonly string[] Pure = { "DestroyingAngel", "SulfurTuft", "Tsukiyotake", "PoisonFireCoral",
            "DeathCap", "YellowDapperling", "JackOLantern", "DeadlyWebcap", "GhostFungus" };
        private static readonly string[] Promoted = { "Reishi", "TurkeyTail", "Sanghuang", "Cordyceps", "Chaga",
            "ZombieAntFungus", "DryRot", "DevilsFingers", "BirdsNest", "Chlorophos" };
        private static readonly string[] Psycho = { "LibertyCap", "Cubensis", "FlyAgaric", "PantherCap" };
        private static string savedPawn, savedTable, savedMeal, savedPolicy, savedBill;
        private static ThingDef Raw(string id) => ThingDef.Named("RMush_Raw" + id);
        private static void Check(Action<bool, string> check, bool valid, string text) => check(valid, "food settings: " + text);
        private static string Allowances(ThingFilter filter) => string.Join(";", filter.AllowedThingDefs.Select(d => d.defName).OrderBy(s => s));
        private static bool Same(float a, float b) => Mathf.Abs(a - b) < .00001f;

        private static Pawn Healthy(Map map, IntVec3 cell)
        {
            // These patients also make real meals in this suite. Generate a
            // valid native cook rather than overriding a disabled stat or
            // letting a random incapable background invalidate the fixture.
            var request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                forceGenerateNewPawn: true, validatorPreGear: p => p.skills != null
                    && !p.skills.GetSkill(SkillDefOf.Cooking).TotallyDisabled
                    && !StatDefOf.FoodPoisonChance.Worker.IsDisabledFor(p));
            var pawn = PawnGenerator.GeneratePawn(request);
            foreach (var hediff in pawn.health.hediffSet.hediffs.ToList()) pawn.health.RemoveHediff(hediff);
            pawn.inventory.innerContainer.ClearAndDestroyContents();
            pawn.needs.food.CurLevelPercentage = .01f;
            pawn.needs.rest.CurLevelPercentage = 1f;
            GenSpawn.Spawn(pawn, cell, map);
            return pawn;
        }

        private static Thing Ingredient(string id, int count)
        {
            var item = ThingMaker.MakeThing(id.StartsWith("RMush_") ? ThingDef.Named(id) : Raw(id));
            item.stackCount = count;
            return item;
        }

        private static Thing Cook(Pawn worker, string id)
        {
            var mushrooms = Ingredient(id, 1);
            var potatoes = ThingMaker.MakeThing(ThingDefOf.RawPotatoes); potatoes.stackCount = 9;
            return GenRecipe.MakeRecipeProducts(DefDatabase<RecipeDef>.GetNamed("CookMealSimple"), worker,
                new List<Thing> { mushrooms, potatoes }, mushrooms, null).Single();
        }

        public static void Run(Map map, Action<bool, string> check)
        {
            var settings = MoreMushroomsMod.Settings;
            bool previousAuto = settings.forbidAutomaticPoisonousMushrooms;
            bool previousCooking = settings.forbidPoisonousCookingIngredients;
            var temporary = new List<Thing>();
            try
            {
                Check(check, !new MushroomFoodSettings().forbidAutomaticPoisonousMushrooms
                    && !new MushroomFoodSettings().forbidPoisonousCookingIngredients,
                    "both fresh mod settings default to unrestricted");
                TestSettingsPersistence(check);
                settings.forbidAutomaticPoisonousMushrooms = false;
                settings.forbidPoisonousCookingIngredients = false;
                var all = DefDatabase<ThingDef>.AllDefs.Where(d => d.defName.StartsWith("RMush_Raw")).ToArray();
                Check(check, all.Length == 46 && all.Where(MushroomFoodRules.IsPurePoisonMushroom).Select(d => d.defName)
                    .OrderBy(n => n).SequenceEqual(Pure.Select(id => "RMush_Raw" + id).OrderBy(n => n)),
                    "exactly nine pure poisons among all forty-six harvested foods");
                var recipe = DefDatabase<RecipeDef>.GetNamed("CookMealSimple");
                var newBill = recipe.MakeNewBill();
                foreach (var raw in all)
                {
                    Check(check, raw.IsNutritionGivingIngestible && !raw.IsDrug && Same(raw.GetStatValueAbstract(StatDefOf.Nutrition), .05f)
                        && raw.ingestible.foodType == FoodTypeFlags.Fungus,
                        "all harvested species retain normal fungus nutrition .05 " + raw.defName);
                    Check(check, recipe.fixedIngredientFilter.Allows(raw) && recipe.ingredients.Any(i => i.filter.Allows(raw))
                        && newBill.IsFixedOrAllowedIngredient(raw), "fresh native cooking bill accepts " + raw.defName);
                }
                var startingPolicy = Current.Game.foodRestrictionDatabase.DefaultFoodRestriction();
                foreach (var raw in all)
                    Check(check, startingPolicy.Allows(raw), "fresh native default food policy permits " + raw.defName);
                TestPromotedIngestion(map, check);

                var origin = map.Center + new IntVec3(34, 0, -14);
                var fixture = new CellRect(origin.x - 3, origin.z - 3, 9, 7);
                foreach (var cell in fixture)
                {
                    foreach (var thing in cell.GetThingList(map).ToList())
                        if (!(thing is Pawn))
                        {
                            if (thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);
                            else thing.DeSpawn();
                        }
                    map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
                    map.roofGrid.SetRoof(cell, null);
                    map.snowGrid.SetDepth(cell, 0f);
                }
                Check(check, fixture.Cells.All(c => c.GetThingList(map).All(t => t is Pawn)),
                    "food-policy fixture removes all preexisting objects including non-destroyable buildings");
                var stove = (Building_WorkTable)GenSpawn.Spawn(ThingDef.Named("FueledStove"), origin, map);
                stove.SetFaction(Faction.OfPlayer); temporary.Add(stove);
                var eater = Healthy(map, origin + new IntVec3(-2, 0, 0)); temporary.Add(eater);
                Check(check, !eater.skills.GetSkill(SkillDefOf.Cooking).TotallyDisabled
                    && !StatDefOf.FoodPoisonChance.Worker.IsDisabledFor(eater),
                    "native cooking fixture uses a capable cook with enabled food-poison chance stat");
                var policy = Current.Game.foodRestrictionDatabase.MakeNewFoodRestriction();
                policy.label = "More Mushrooms settings save fixture";
                policy.filter.SetAllow(SpecialThingFilterDef.Named("RMush_AllowPsychoactiveMeals"), true);
                eater.foodRestriction.CurrentFoodPolicy = policy;
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                var bill = recipe.MakeNewBill(); stove.BillStack.AddBill(bill);
                string policyBefore = Allowances(policy.filter), billBefore = Allowances(bill.ingredientFilter);
                var usable = typeof(WorkGiver_DoBill).GetMethod("IsUsableIngredient", BindingFlags.NonPublic | BindingFlags.Static);
                Check(check, usable != null, "resolved native cooking ingredient eligibility method");
                foreach (bool auto in new[] { false, true })
                    foreach (bool cooking in new[] { false, true })
                    {
                        settings.forbidAutomaticPoisonousMushrooms = auto;
                        settings.forbidPoisonousCookingIngredients = cooking;
                        foreach (var id in Pure.Concat(Promoted).Concat(Psycho).Concat(new[] { "Button" }))
                        {
                            var raw = Ingredient(id, 10);
                            try
                            {
                                bool pure = Pure.Contains(id);
                                Check(check, eater.WillEat(raw, careIfNotAcceptableForTitle: false, allowVenerated: true) == !(auto && pure)
                                    && eater.WillEat(raw.def, careIfNotAcceptableForTitle: false, allowVenerated: true) == !(auto && pure),
                                    "native Thing/Def automatic-eating gate " + id + " auto=" + auto + " cooking=" + cooking);
                                Check(check, bill.IsFixedOrAllowedIngredient(raw) == !(cooking && pure)
                                    && bill.IsFixedOrAllowedIngredient(raw.def) == !(cooking && pure)
                                    && (bool)usable.Invoke(null, new object[] { raw, bill }) == !(cooking && pure),
                                    "native bill and cooking ingredient scanner gate " + id + " auto=" + auto + " cooking=" + cooking);
                                Check(check, Building_NutrientPasteDispenser.IsAcceptableFeedstock(raw.def) == !(cooking && pure),
                                    "native nutrient-paste feedstock gate " + id + " cooking=" + cooking);
                            }
                            finally { raw.Destroy(DestroyMode.Vanish); }
                        }
                        Check(check, Allowances(policy.filter) == policyBefore && Allowances(bill.ingredientFilter) == billBefore,
                            "independent live toggles preserve the exact stored policy and bill allowances");
                    }
                TestSearchAndIngest(map, eater, origin + new IntVec3(-2, 0, 1), policy, check);
                TestNativeCookingSelection(map, eater, stove, bill, origin + new IntVec3(3, 0, 0), check);

                settings.forbidAutomaticPoisonousMushrooms = false;
                settings.forbidPoisonousCookingIngredients = false;
                var poisonMeal = Cook(eater, "DeathCap"); temporary.Add(poisonMeal);
                Check(check, Same(poisonMeal.TryGetComp<CompMushroomMeal>().Doses.Single().Value, 1f),
                    "native cooking retains one death-cap dose in the finished meal");
                var ingredientsOnly = ThingMaker.MakeThing(ThingDefOf.MealSimple); temporary.Add(ingredientsOnly);
                ingredientsOnly.TryGetComp<CompIngredients>().RegisterIngredient(Raw("DeathCap"));
                settings.forbidAutomaticPoisonousMushrooms = true;
                settings.forbidPoisonousCookingIngredients = true;
                Check(check, !eater.WillEat(poisonMeal) && !eater.WillEat(ingredientsOnly)
                    && !CaravanPawnsNeedsUtility.CanEatForNutritionNow(poisonMeal, eater),
                    "automatic restriction includes existing recorded meals, native ingredient-only meals and caravan food");
                TestCookedIngredient(poisonMeal, check);
                TestPoisonMealOutcome(map, poisonMeal, origin + new IntVec3(-2, 0, -2), check);

                foreach (string id in new[] { "DeathCap", "Reishi" })
                {
                    policy.filter.SetAllow(Raw(id), false); bill.ingredientFilter.SetAllow(Raw(id), false);
                }
                policyBefore = Allowances(policy.filter); billBefore = Allowances(bill.ingredientFilter);
                foreach (bool blocked in new[] { false, true, false })
                {
                    settings.forbidAutomaticPoisonousMushrooms = blocked;
                    settings.forbidPoisonousCookingIngredients = blocked;
                    foreach (string id in new[] { "DeathCap", "Reishi" })
                        Check(check, !eater.WillEat(Raw(id)) && !bill.IsFixedOrAllowedIngredient(Raw(id)),
                            "individual native food-policy and bill exclusions remain effective " + id + " options=" + blocked);
                    Check(check, Allowances(policy.filter) == policyBefore && Allowances(bill.ingredientFilter) == billBefore,
                        "option changes never overwrite selective user filters");
                }
                eater.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false); eater.pather.StopDead();
                eater.needs.food.CurLevelPercentage = 1f; eater.drafter.Drafted = true;
                GenSpawn.Spawn(poisonMeal, origin + new IntVec3(3, 0, 1), map); poisonMeal.SetForbidden(true, false);
                stove.SetForbidden(true, false);
                savedPawn = eater.ThingID; savedTable = stove.ThingID; savedMeal = poisonMeal.ThingID;
                savedPolicy = policyBefore; savedBill = billBefore;
                temporary.Remove(eater); temporary.Remove(stove); temporary.Remove(poisonMeal);
                Check(check, true, "selective food policy, bill and poisonous meal prepared for native save/reload");
            }
            finally
            {
                settings.forbidAutomaticPoisonousMushrooms = previousAuto;
                settings.forbidPoisonousCookingIngredients = previousCooking;
                foreach (var thing in temporary.AsEnumerable().Reverse()) if (!thing.Destroyed) thing.Destroy(DestroyMode.Vanish);
            }
        }

        private static void TestSearchAndIngest(Map map, Pawn eater, IntVec3 cell, FoodPolicy policy, Action<bool, string> check)
        {
            var settings = MoreMushroomsMod.Settings;
            string before = Allowances(policy.filter);
            policy.filter.SetDisallowAll(); policy.filter.SetAllow(Raw("DestroyingAngel"), true);
            var raw = GenSpawn.Spawn(Raw("DestroyingAngel"), cell, map); raw.stackCount = 10;
            try
            {
                foreach (bool blocked in new[] { false, true, false })
                {
                    settings.forbidAutomaticPoisonousMushrooms = blocked;
                    ThingDef selected;
                    var found = FoodUtility.BestFoodSourceOnMap(eater, eater, true, out selected,
                        allowPlant: false, allowCorpse: false, allowDispenserFull: false, allowDispenserEmpty: false,
                        forceScanWholeMap: true, minPrefOverride: FoodPreferability.RawBad);
                    Check(check, blocked ? found == null : found == raw,
                        "native map food search responds immediately to automatic restriction=" + blocked);
                    var carried = Ingredient("DestroyingAngel", 10); eater.inventory.innerContainer.TryAdd(carried);
                    Check(check, blocked ? FoodUtility.BestFoodInInventory(eater) == null : FoodUtility.BestFoodInInventory(eater) == carried,
                        "native inventory food search responds immediately to automatic restriction=" + blocked);
                    eater.inventory.innerContainer.ClearAndDestroyContents();
                }
                settings.forbidAutomaticPoisonousMushrooms = false;
                var automatic = JobMaker.MakeJob(JobDefOf.Ingest, raw); automatic.count = 1;
                eater.jobs.TryTakeOrderedJob(automatic, JobTag.Misc);
                automatic.playerForced = false;
                var driver = eater.jobs.curDriver;
                Check(check, eater.CurJob == automatic && driver != null, "native ingest driver starts before an option changes");
                int automaticId = automatic.loadID, rawBefore = raw.stackCount;
                Check(check, driver.globalFailConditions.Count > 0 && driver.globalFailConditions.All(f => f() == JobCondition.Ongoing),
                    "automatic ingest has only ongoing native fail conditions before restriction changes");
                settings.forbidAutomaticPoisonousMushrooms = true;
                var inventoryFood = Ingredient("DestroyingAngel", 1);
                eater.inventory.innerContainer.TryAdd(inventoryFood);
                Check(check, !FoodUtility.WillIngestFromInventoryNow(eater, inventoryFood)
                    && MushroomFoodRules.CanManuallyIngestFromInventory(eater, inventoryFood)
                    && !eater.WillEat(inventoryFood),
                    "manual inventory Consume bypasses only the automatic poison gate and restores it immediately");
                policy.filter.SetAllow(inventoryFood.def, false);
                Check(check, !MushroomFoodRules.CanManuallyIngestFromInventory(eater, inventoryFood),
                    "manual inventory Consume still respects an individual food-policy exclusion");
                policy.filter.SetAllow(inventoryFood.def, true);
                eater.inventory.innerContainer.ClearAndDestroyContents();
                Check(check, driver.globalFailConditions.Any(f => f() == JobCondition.Incompletable),
                    "turning automatic restriction on immediately makes the native ingest fail condition incompletable");
                var fail = typeof(JobDriver).GetMethod("CheckCurrentToilEndOrFail", BindingFlags.NonPublic | BindingFlags.Instance);
                bool ended = (bool)fail.Invoke(driver, null);
                // Native EndCurrentJob returns its Job to SimplePool before
                // finding a replacement, which can reuse the same object.
                // Compare the captured load ID and driver lifecycle instead.
                Check(check, ended && driver.ended && eater.jobs.curDriver != driver
                    && (eater.CurJob == null || eater.CurJob.loadID != automaticId)
                    && !raw.Destroyed && raw.stackCount == rawBefore
                    && !eater.health.hediffSet.hediffs.OfType<Hediff_MushroomPoisoning>().Any(),
                    "native ongoing automatic ingest stops without consuming poison; ended=" + ended + " driverEnded=" + driver.ended
                    + " oldJobId=" + automaticId + " currentJobId=" + eater.CurJob?.loadID
                    + " samePooledJobObject=" + ReferenceEquals(eater.CurJob, automatic)
                    + " currentJob=" + eater.CurJob + " rawDestroyed=" + raw.Destroyed + " rawStack=" + raw.stackCount);
                var forced = JobMaker.MakeJob(JobDefOf.Ingest, raw); forced.count = 1;
                eater.jobs.TryTakeOrderedJob(forced, JobTag.Misc);
                Check(check, eater.CurJob == forced && forced.playerForced
                    && eater.jobs.curDriver.globalFailConditions.All(f => f() == JobCondition.Ongoing),
                    "explicit native Consume order remains permitted while automatic poison eating is blocked");
                eater.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false); eater.pather.StopDead();
            }
            finally
            {
                if (eater.CurJob != null) eater.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                eater.pather.StopDead();
                if (!raw.Destroyed) raw.Destroy(DestroyMode.Vanish);
                policy.filter.SetDisallowAll();
                foreach (string name in before.Split(';')) policy.filter.SetAllow(ThingDef.Named(name), true);
            }
        }

        private static void TestNativeCookingSelection(Map map, Pawn eater, Building_WorkTable stove, Bill bill, IntVec3 cell, Action<bool, string> check)
        {
            var settings = MoreMushroomsMod.Settings;
            var source = GenSpawn.Spawn(Raw("DeathCap"), cell, map); source.stackCount = 10;
            var select = typeof(WorkGiver_DoBill).GetMethod("TryFindBestBillIngredients", BindingFlags.NonPublic | BindingFlags.Static);
            string before = Allowances(bill.ingredientFilter);
            try
            {
                bill.ingredientFilter.SetDisallowAll(); bill.ingredientFilter.SetAllow(source.def, true);
                foreach (bool blocked in new[] { false, true, false })
                {
                    settings.forbidPoisonousCookingIngredients = blocked;
                    var chosen = new List<ThingCount>();
                    bool found = (bool)select.Invoke(null, new object[] { bill, eater, stove, chosen, null });
                    Check(check, blocked ? !found && chosen.Count == 0 : found && chosen.Count == 1 && chosen[0].Thing == source && chosen[0].Count == 10,
                        "native map ingredient selection respects live cooking restriction=" + blocked);
                }
            }
            finally
            {
                source.Destroy(DestroyMode.Vanish); bill.ingredientFilter.SetDisallowAll();
                foreach (string name in before.Split(';')) bill.ingredientFilter.SetAllow(ThingDef.Named(name), true);
            }
        }

        private static void TestPoisonMealOutcome(Map map, Thing meal, IntVec3 cell, Action<bool, string> check)
        {
            var patient = Healthy(map, cell);
            try
            {
                var portion = Cook(patient, "DeathCap");
                Check(check, Same(portion.TryGetComp<CompMushroomMeal>().Doses.Single().Value,
                    meal.TryGetComp<CompMushroomMeal>().Doses.Single().Value),
                    "separate native recipe reproduces the saved meal's actual poison dose");
                portion.Ingested(patient, portion.GetStatValue(StatDefOf.Nutrition));
                var poison = patient.health.hediffSet.hediffs.OfType<Hediff_MushroomPoisoning>().ToArray();
                Check(check, poison.Length == 1 && poison[0].def.defName == "RMush_PoisonDeathCap" && Same(poison[0].ExposureUnits, 1f),
                    "both restriction options leave finished-meal ingestion toxicity and the original dose intact");
            }
            finally { patient.Destroy(DestroyMode.Vanish); }
        }

        private static void TestCookedIngredient(Thing poisonMeal, Action<bool, string> check)
        {
            // A local food recipe accepts finished meals as ingredients. It is
            // never registered or saved, and tests the native Bill instance API.
            var recipe = new RecipeDef { defName = "RMush_SettingsFixtureFoodRecipe", defaultIngredientFilter = new ThingFilter() };
            var ingredient = new IngredientCount();
            ingredient.filter.SetAllow(ThingDefOf.MealSimple, true); ingredient.filter.SetAllow(ThingDefOf.RawPotatoes, true);
            recipe.ingredients.Add(ingredient);
            recipe.products.Add(new ThingDefCountClass(ThingDefOf.MealSimple, 1));
            recipe.fixedIngredientFilter.CopyAllowancesFrom(ingredient.filter);
            recipe.defaultIngredientFilter.CopyAllowancesFrom(ingredient.filter);
            var bill = new Bill_Production(recipe);
            var plain = ThingMaker.MakeThing(ThingDefOf.MealSimple);
            try
            {
                foreach (bool blocked in new[] { false, true, false })
                {
                    MoreMushroomsMod.Settings.forbidPoisonousCookingIngredients = blocked;
                    Check(check, bill.IsFixedOrAllowedIngredient(poisonMeal) == !blocked && bill.IsFixedOrAllowedIngredient(plain),
                        "native food bill rejects recorded poisonous meal ingredients only when cooking restriction=" + blocked);
                }
            }
            finally { plain.Destroy(DestroyMode.Vanish); MoreMushroomsMod.Settings.forbidPoisonousCookingIngredients = true; }
        }

        private static void TestPromotedIngestion(Map map, Action<bool, string> check)
        {
            var patient = Healthy(map, map.Center + new IntVec3(32, 0, 4));
            try
            {
                foreach (string id in Promoted)
                {
                    foreach (var memory in patient.needs.mood.thoughts.memories.Memories.ToList())
                        patient.needs.mood.thoughts.memories.RemoveMemory(memory);
                    var food = Ingredient(id, 1);
                    float expectedNutrition = FoodUtility.NutritionForEater(patient, food);
                    float receivedNutrition = food.Ingested(patient, expectedNutrition);
                    Check(check, food.Destroyed && expectedNutrition > 0f && Same(receivedNutrition, expectedNutrition),
                        "native ingestion consumes newly edible harvest and returns its nutrition " + id);
                    Check(check, !patient.health.hediffSet.hediffs.Any(h => h is Hediff_MushroomPoisoning
                        || h is Hediff_MushroomHallucination || h is Hediff_MushroomTolerance)
                        && !patient.needs.mood.thoughts.memories.Memories.Any(m => m is Thought_MushroomEnjoyment
                            || m is Thought_MushroomHallucination),
                        "newly edible former resource adds no invented medicine, poison, hallucination or mushroom mood " + id);
                }
            }
            finally { patient.Destroy(DestroyMode.Vanish); }
        }

        private static void TestSettingsPersistence(Action<bool, string> check)
        {
            string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "food-settings-scribe.xml");
            Check(check, Scribe.mode == LoadSaveMode.Inactive, "native Scribe is idle before settings roundtrip");
            try
            {
                foreach (bool auto in new[] { false, true })
                    foreach (bool cooking in new[] { false, true })
                    {
                        var saved = new MushroomFoodSettings { forbidAutomaticPoisonousMushrooms = auto, forbidPoisonousCookingIngredients = cooking };
                        Scribe.saver.InitSaving(path, "settings"); saved.ExposeData(); Scribe.saver.FinalizeSaving();
                        var loaded = new MushroomFoodSettings { forbidAutomaticPoisonousMushrooms = !auto, forbidPoisonousCookingIngredients = !cooking };
                        Scribe.loader.InitLoading(path); loaded.ExposeData(); Scribe.loader.FinalizeLoading();
                        Check(check, loaded.forbidAutomaticPoisonousMushrooms == auto && loaded.forbidPoisonousCookingIngredients == cooking,
                            "native settings Scribe roundtrip preserves independent values auto=" + auto + " cooking=" + cooking);
                    }
                File.WriteAllText(path, "<settings />");
                var absent = new MushroomFoodSettings { forbidAutomaticPoisonousMushrooms = true, forbidPoisonousCookingIngredients = true };
                Scribe.loader.InitLoading(path); absent.ExposeData(); Scribe.loader.FinalizeLoading();
                Check(check, !absent.forbidAutomaticPoisonousMushrooms && !absent.forbidPoisonousCookingIngredients,
                    "missing legacy settings deserialize to default unrestricted behavior");
            }
            finally { if (Scribe.mode != LoadSaveMode.Inactive) Scribe.ForceStop(); }
        }

        public static void VerifyLoaded(Map map, Action<bool, string> check, bool legacy = false)
        {
            if (legacy) { VerifyLegacy(map, check); return; }
            var pawn = map.mapPawns.AllPawnsSpawned.Single(p => p.ThingID == savedPawn);
            var table = (Building_WorkTable)map.listerThings.AllThings.Single(t => t.ThingID == savedTable);
            var meal = map.listerThings.AllThings.Single(t => t.ThingID == savedMeal);
            Check(check, Allowances(pawn.foodRestriction.CurrentFoodPolicy.filter) == savedPolicy
                && Allowances(table.BillStack.Bills.Single().ingredientFilter) == savedBill,
                "native game save/reload preserves exact selective policy and bill allowances");
            Check(check, MushroomFoodRules.ContainsPurePoisonMushroom(meal)
                && meal.TryGetComp<CompMushroomMeal>().Doses.Count() == 1
                && meal.TryGetComp<CompMushroomMeal>().Doses.Single().Key == Raw("DeathCap")
                && Same(meal.TryGetComp<CompMushroomMeal>().Doses.Single().Value, 1f),
                "native game save/reload preserves the existing poisonous meal source and exact dose");
        }

        public static void VerifyLegacy(Map map, Action<bool, string> check)
        {
            string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "Saves", "MoreMushrooms-LegacyFixture.rws");
            var document = new XmlDocument { XmlResolver = null }; document.Load(path);
            int policies = 0, bills = 0;
            foreach (XmlNode policy in document.SelectNodes("//foodRestrictionDatabase/foodRestrictions/li"))
            {
                int id = int.Parse(policy["id"].InnerText, CultureInfo.InvariantCulture);
                var loaded = Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Single(p => p.id == id);
                CompareLegacyFilter(policy["filter"], loaded.filter, check, "food policy " + id); policies++;
            }
            foreach (XmlNode thing in document.SelectNodes("//maps/li/things/thing[billStack/bills/li/ingredientFilter]"))
            {
                var loaded = map.listerThings.AllThings.Single(t => t.ThingID == thing["id"].InnerText) as IBillGiver;
                foreach (XmlNode bill in thing.SelectNodes("billStack/bills/li"))
                {
                    string loadId = "Bill_" + bill["recipe"].InnerText + "_" + bill["loadID"].InnerText;
                    var loadedBill = loaded.BillStack.Bills.Single(b => b.GetUniqueLoadID() == loadId);
                    CompareLegacyFilter(bill["ingredientFilter"], loadedBill.ingredientFilter, check, "bill " + loadId); bills++;
                }
            }
            Check(check, policies > 0, "actual legacy save food policies are compared; policies=" + policies + " bills=" + bills);
        }

        private static void CompareLegacyFilter(XmlNode node, ThingFilter loaded, Action<bool, string> check, string label)
        {
            var expected = node.SelectNodes("allowedDefs/li").Cast<XmlNode>().Select(n => n.InnerText).OrderBy(n => n).ToArray();
            Check(check, expected.SequenceEqual(loaded.AllowedThingDefs.Select(d => d.defName).OrderBy(n => n)),
                "actual legacy " + label + " preserves every original allowed and excluded food definition");
        }
    }
}
