#if RIMWORLD17
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimMushrooms;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMushroomsTests
{
    // Short compatibility fixtures inside the explicit isolated smoke profile.
    // They exercise native 1.7 connections, not long-term emergence balance.
    internal static class Port17CompatibilityTests
    {
        internal static void Run(Map map, Action<bool, string> check)
        {
            Action<bool, string> verify = (valid, message) => check(valid, "1.7 compatibility: " + message);
            VerifyCatalog(verify);
            VerifyHarmony(verify);
            VerifySpecialHabitats(map, verify);
            VerifyObservedRain(map, verify);
        }

        private static ThingDef Crop(string id) => ThingDef.Named("RMush_Plant" + id);

        private static void VerifyCatalog(Action<bool, string> check)
        {
            var raw = DefDatabase<ThingDef>.AllDefs.Where(d => d.defName.StartsWith("RMush_Raw", StringComparison.Ordinal)).ToArray();
            var plants = DefDatabase<ThingDef>.AllDefs.Where(d => d.defName.StartsWith("RMush_Plant", StringComparison.Ordinal)).ToArray();
            var assorted = ThingDef.Named("RMush_PlantAssorted");
            check(raw.Length == 46 && raw.All(d => d.ingestible != null), "all 46 mushroom ingredients load as ingestible definitions");
            check(plants.Length == 48 && plants.Count(d => d != assorted) == 47,
                "47 actual mushroom plants and the assorted selection load without duplicate definitions");
            check(plants.All(d => d.thingClass == typeof(Plant_Mushroom))
                && plants.Where(d => d != assorted).All(d => d.GetModExtension<MushroomEcologyExtension>() != null),
                "every actual mushroom plant retains its saved class and ecology profile");
            var cultivable = plants.Where(d => d != assorted && d.plant.Sowable).ToArray();
            check(cultivable.Length == 11 && cultivable.All(d => Mathf.Abs(d.plant.growDays - 6f) < 0.00001f)
                && assorted.plant.growDays == 6f, "all 11 cultivable mushrooms and assorted selection retain six-day base growth");
            check(Crop("EnokiWild").plant.growDays == 5f && Crop("Matsutake").plant.growDays == 12f
                && !Crop("EnokiWild").plant.Sowable && !Crop("Matsutake").plant.Sowable,
                "separate wild enoki and matsutake retain five/twelve days and wild-only status");
            var varieties = assorted.GetModExtension<AssortedMushroomSettings>().varieties;
            check(varieties.Count == 11 && new HashSet<ThingDef>(varieties).SetEquals(cultivable),
                "assorted selection resolves exactly the 11 cultivable varieties");
        }

        private static void VerifyHarmony(Action<bool, string> check)
        {
            foreach (Type type in new[] { typeof(WildMushroomEcology), typeof(MushroomMeals), typeof(MushroomFoodRules) })
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            string ecology = "izzypizzy.rimmushrooms.wild-ecology";
            VerifyPatch(typeof(WildPlantSpawner), "PlantChoiceWeight", ecology, check);
            VerifyPatch(typeof(WildPlantSpawner), nameof(WildPlantSpawner.CheckSpawnWildPlantAt), ecology, check);
            VerifyPatch(typeof(WildPlantSpawner), nameof(WildPlantSpawner.SpawnPlant), ecology, check);
            VerifyPatch(typeof(QuestManager), nameof(QuestManager.Notify_PlantHarvested), ecology, check);
            string meals = "izzypizzy.rimmushrooms.meal-exposure";
            VerifyPatch(typeof(GenRecipe), nameof(GenRecipe.MakeRecipeProducts), meals, check);
            VerifyPatch(typeof(Building_NutrientPasteDispenser), nameof(Building_NutrientPasteDispenser.TryDispenseFood), meals, check);
            string food = "izzypizzy.rimmushrooms.food-settings";
            VerifyPatch(typeof(FoodUtility), nameof(FoodUtility.WillEat), food, check,
                new[] { typeof(Pawn), typeof(Thing), typeof(Pawn), typeof(bool), typeof(bool) });
            VerifyPatch(typeof(FoodUtility), nameof(FoodUtility.WillEat), food, check,
                new[] { typeof(Pawn), typeof(ThingDef), typeof(Pawn), typeof(bool), typeof(bool) });
            VerifyPatch(typeof(CaravanPawnsNeedsUtility), nameof(CaravanPawnsNeedsUtility.CanEatForNutritionNow), food, check,
                new[] { typeof(Thing), typeof(Pawn) });
            VerifyPatch(typeof(Bill), nameof(Bill.IsFixedOrAllowedIngredient), food, check, new[] { typeof(Thing) });
            VerifyPatch(typeof(Bill), nameof(Bill.IsFixedOrAllowedIngredient), food, check, new[] { typeof(ThingDef) });
            VerifyPatch(typeof(Building_NutrientPasteDispenser), nameof(Building_NutrientPasteDispenser.IsAcceptableFeedstock), food, check);
            VerifyPatch(typeof(Building_NutrientPasteDispenser), nameof(Building_NutrientPasteDispenser.FindFeedInAnyHopper), food, check);
            VerifyPatch(typeof(Building_NutrientPasteDispenser), nameof(Building_NutrientPasteDispenser.HasEnoughFeedstockInHoppers), food, check);
            VerifyPatch(typeof(FoodUtility), "SpawnedFoodSearchInnerScan", food, check);
            VerifyPatch(typeof(JobDriver_Ingest), "MakeNewToils", food, check);
            VerifyPatch(typeof(ITab_Pawn_Gear), "DrawThingRow", food, check);
            MethodInfo inventory = AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(Pawn_InventoryTracker), nameof(Pawn_InventoryTracker.GetGizmos)));
            check(inventory != null && Harmony.GetPatchInfo(inventory)?.Owners.Contains(food) == true,
                "inventory gizmo iterator has the food-settings Harmony owner");
        }

        private static void VerifyPatch(Type type, string method, string owner, Action<bool, string> check, Type[] args = null)
        {
            MethodInfo target = AccessTools.Method(type, method, args);
            check(target != null && Harmony.GetPatchInfo(target)?.Owners.Contains(owner) == true,
                type.Name + "." + method + (args == null ? "" : "(" + string.Join(",", args.Select(t => t.Name)) + ")")
                + " has Harmony owner " + owner);
        }

        private static void VerifySpecialHabitats(Map map, Action<bool, string> check)
        {
            IntVec3 cell = map.Center + new IntVec3(-35, 0, 45);
            var patch = CellRect.CenteredOn(cell, 11, 11).Cells.ToArray();
            check(patch.All(c => c.InBounds(map) && c.GetThingList(map).All(t => !(t is Pawn))),
                "small host/wood fixture is separate from pawns and existing crop/art fixtures");
            var terrain = patch.ToDictionary(c => c, c => c.GetTerrain(map));
            var roofs = patch.ToDictionary(c => c, c => map.roofGrid.RoofAt(c));
            float oldGlow = map.skyManager.CurSkyGlow;
            float? oldBiomeTemperature = map.Biome.constantOutdoorTemperature;
            var made = new List<Thing>();
            try
            {
                foreach (IntVec3 c in patch)
                {
                    foreach (Thing thing in c.GetThingList(map).ToArray())
                    {
                        if (thing.def.destroyable) thing.Destroy(DestroyMode.Vanish);
                        else thing.DeSpawn();
                    }
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                    map.roofGrid.SetRoof(c, null);
                    map.snowGrid.SetDepth(c, 0f);
                }
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                map.Biome.constantOutdoorTemperature = 21f;
                foreach (var room in patch.Select(c => c.GetRoom(map)).Where(r => r != null).Distinct()) room.Temperature = 21f;
                map.skyManager.ForceSetCurSkyGlow(0f);
                check(map.GetComponent<MapComponent_WildMushrooms>() != null, "wild ecology component registers on the native 1.7 map");

                check(!MushroomHabitatRules.MeetsHabitat(Crop("Cordyceps"), cell, map)
                    && !MushroomHabitatRules.MeetsHabitat(Crop("ZombieAntFungus"), cell, map), "insect-linked species require an actual nearby host");
                Pawn insect = PawnGenerator.GeneratePawn(PawnKindDef.Named("Megascarab"));
                made.Add(insect);
                GenSpawn.Spawn(insect, cell + IntVec3.East, map);
                check(MushroomHabitatRules.MeetsHabitat(Crop("Cordyceps"), cell, map)
                    && MushroomHabitatRules.MeetsHabitat(Crop("ZombieAntFungus"), cell, map), "native living insect satisfies both insect-linked habitat profiles");
                insect.Destroy(DestroyMode.Vanish);
                check(!MushroomHabitatRules.MeetsHabitat(Crop("Cordyceps"), cell, map), "removing the insect closes its host habitat");

                check(!MushroomHabitatRules.MeetsHabitat(Crop("Lobster"), cell, map), "lobster fungus requires another wild mushroom");
                var host = (Plant_Mushroom)GenSpawn.Spawn(Crop("Button"), cell + IntVec3.East, map);
                made.Add(host);
                check(MushroomHabitatRules.MeetsHabitat(Crop("Lobster"), cell, map), "native unsown mushroom satisfies lobster habitat");
                host.sown = true;
                check(!MushroomHabitatRules.MeetsHabitat(Crop("Lobster"), cell, map), "cultivated mushroom does not become a wild lobster host");
                host.Destroy(DestroyMode.Vanish);

                ThingDef stumpDef = DefDatabase<ThingDef>.AllDefs.First(d => d.plant?.isStump == true);
                Thing stump = GenSpawn.Spawn(stumpDef, cell + IntVec3.East, map);
                made.Add(stump);
                check(MushroomHabitatRules.MeetsHabitat(Crop("TurkeyTail"), cell, map), "native stump satisfies a wood-linked mushroom habitat");
                stump.Destroy(DestroyMode.Vanish);

                var dryRot = Crop("DryRot");
                check(!MushroomHabitatRules.MeetsHabitat(dryRot, cell, map), "plain soil without wood is not a dry-rot substrate");
                map.terrainGrid.SetTerrain(cell, TerrainDef.Named("WoodPlankFloor"));
                check(MushroomHabitatRules.AllowsFloor(dryRot, cell, map) && MushroomHabitatRules.MeetsHabitat(dryRot, cell, map),
                    "native exposed wooden floor is accepted by the dedicated wood substrate rule");
                var woodPlant = (Plant_Mushroom)GenSpawn.Spawn(dryRot, cell, map);
                made.Add(woodPlant);
                float woodRate = woodPlant.GrowthRateFactor_Temperature * woodPlant.GrowthRateFactor_Light
                    * woodPlant.GrowthRateFactor_NoxiousHaze * woodPlant.GrowthRateFactor_Drought * woodPlant.BrightLightGrowthFactor;
                check(map.fertilityGrid.FertilityAt(cell) == 0f && woodRate > 0f
                    && Mathf.Abs(woodPlant.GrowthRate - woodRate) < 0.00001f,
                    "dry-rot plant actually grows without a soil-fertility multiplier on native wooden floor at 21C");
                woodPlant.Destroy(DestroyMode.Vanish);
                foreach (IntVec3 c in patch) map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                check(!MushroomHabitatRules.MeetsHabitat(dryRot, cell, map), "completely roofed wood has no rain exposure");
                map.roofGrid.SetRoof(cell + IntVec3.East, null);
                check(MushroomHabitatRules.MeetsHabitat(dryRot, cell, map), "adjacent native roof opening restores rain exposure");
                map.terrainGrid.SetTerrain(cell, TerrainDef.Named("Concrete"));
                check(!MushroomHabitatRules.AllowsFloor(dryRot, cell, map), "ordinary constructed stone floor is not a wood substrate");
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
                check(MushroomHabitatRules.MeetsHabitat(Crop("YellowDapperling"), cell, map), "roofed dim native soil satisfies indoor-soil habitat");
                map.terrainGrid.SetTerrain(cell, TerrainDef.Named("Concrete"));
                check(!MushroomHabitatRules.MeetsHabitat(Crop("YellowDapperling"), cell, map), "constructed floor closes indoor-soil habitat");
            }
            finally
            {
                foreach (Thing thing in made) if (!thing.Destroyed) thing.Destroy(DestroyMode.Vanish);
                foreach (IntVec3 c in patch)
                {
                    map.terrainGrid.SetTerrain(c, terrain[c]);
                    map.roofGrid.SetRoof(c, roofs[c]);
                }
                map.skyManager.ForceSetCurSkyGlow(oldGlow);
                map.Biome.constantOutdoorTemperature = oldBiomeTemperature;
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            }
        }

        private static void VerifyObservedRain(Map map, Action<bool, string> check)
        {
            // Detached components keep this bounded scheduling fixture from
            // replacing the map's real pending regrowth or random/budget state.
            var component = new MapComponent_WildMushrooms(map);
            var settings = component.Settings;
            int oldBudget = settings.dailyExtraBudget;
            WeatherDef oldCurrent = map.weatherManager.curWeather, oldLast = map.weatherManager.lastWeather;
            int oldWeatherAge = map.weatherManager.curWeatherAge;
            try
            {
                settings.dailyExtraBudget = 0;
                WeatherDef rain = DefDatabase<WeatherDef>.AllDefs.First(d => d.rainRate >= 0.1f);
                map.weatherManager.curWeather = map.weatherManager.lastWeather = rain;
                map.weatherManager.curWeatherAge = 10000;
                check(map.weatherManager.RainRate >= 0.1f, "native 1.7 weather supplies a measurable rain rate");
                int now = Find.TickManager.TicksGame;
                int interval = Mathf.Clamp(settings.checkIntervalTicks, 250, GenDate.TicksPerDay);
                int minimum = Mathf.RoundToInt(settings.rainMinHours * GenDate.TicksPerHour);
                component.TickEcology(now);
                check(component.RainWindowUntil == -1, "loading into rain never infers an unobserved prior storm");
                int stop = now;
                for (int elapsed = interval; elapsed <= minimum + interval; elapsed += interval)
                {
                    stop = now + elapsed;
                    component.TickEcology(stop);
                }
                check(component.RainWindowUntil > stop && !component.RainEpisodeUsed,
                    "continuous observed native rain opens one unused post-rain episode");
                map.weatherManager.curWeather = map.weatherManager.lastWeather = WeatherDefOf.Clear;
                stop += interval;
                component.TickEcology(stop);
                int expectedUntil = stop + Mathf.RoundToInt(settings.rainWindowHours * GenDate.TicksPerHour);
                check(component.RainWindowUntil == expectedUntil && component.ExtraSpawnedToday == 0,
                    "native clear weather starts the configured 48-hour window without spawning when budget is zero");
                map.weatherManager.curWeather = map.weatherManager.lastWeather = rain;
                var gap = new MapComponent_WildMushrooms(map);
                gap.TickEcology(now);
                gap.TickEcology(now + minimum + interval * 3);
                check(gap.RainWindowUntil == -1, "a long unobserved sampling gap does not qualify as continuous rain");
            }
            finally
            {
                settings.dailyExtraBudget = oldBudget;
                map.weatherManager.curWeather = oldCurrent;
                map.weatherManager.lastWeather = oldLast;
                map.weatherManager.curWeatherAge = oldWeatherAge;
            }
        }
    }
}
#endif
