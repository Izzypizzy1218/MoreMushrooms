using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using RimMushrooms;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMushroomsTests
{
    // This suite owns an outdoor patch away from the crop, health, and artwork
    // fixtures. Only the small fixtures made by PrepareSave remain for reload.
    public static class EcologyTests
    {
        private static string savedGlowId;
        private static IntVec3 savedRegrowthCell;
        private static string savedRegrowthDef;
        private static int savedDueTick;
        private static int savedPendingCount;
        private static int savedExpiryTick;
        private static string savedSourceId;
        private static string savedGenerationId;
        private static string savedSpentId;
        private static int savedRandomState;
        private static int savedNextRingTick;
        private static int savedExtraToday;

        private static void Check(Action<bool, string> check, bool valid, string message)
            => check(valid, "ecology: " + message);

        private static IntVec3 Center(Map map) => map.Center + new IntVec3(-55, 0, -55);
        private static ThingDef Crop(string id) => ThingDef.Named("RMush_Plant" + id);
        private static ThingDef NativeRegrowthCrop(Map map) => map.wildPlantSpawner.AllWildPlants.First(d =>
            d.GetModExtension<MushroomEcologyExtension>()?.regrowthChance > 0f && WildMushroomEcology.NativeCommonality(d, map) > 0f);
        private static ThingDef NativeRingCrop(Map map) => map.wildPlantSpawner.AllWildPlants.FirstOrDefault(d =>
            d.GetModExtension<MushroomEcologyExtension>()?.ringEligible == true && WildMushroomEcology.NativeCommonality(d, map) > 0f
            && d.plant.harvestedThingDef.GetModExtension<MushroomExposureProperties>()?.poisonHediff == null
            && d.plant.harvestedThingDef.GetModExtension<MushroomExposureProperties>()?.psychoactive != true);

        private static void ClearPatch(Map map)
        {
            foreach (var cell in CellRect.CenteredOn(Center(map), 35, 35))
            {
                if (!cell.InBounds(map)) continue;
                foreach (var thing in cell.GetThingList(map).ToArray())
                    if (!(thing is Pawn)) thing.Destroy(DestroyMode.Vanish);
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
                map.roofGrid.SetRoof(cell, null);
                map.snowGrid.SetDepth(cell, 0f);
            }
        }

        private static void PrepareNativeBiomeFixture(Map map, Action<bool, string> check)
        {
            var profiles = DefDatabase<ThingDef>.AllDefs.Where(d => d.GetModExtension<MushroomEcologyExtension>() != null).ToArray();
            var biomes = map.Biomes.ToArray();
            int declared = profiles.Count(d => d.plant.wildBiomes != null
                && d.plant.wildBiomes.Any(record => biomes.Contains(record.biome) && record.commonality > 0f));
            int native = map.wildPlantSpawner.AllWildPlants.Count(d => profiles.Contains(d));
            Check(check, true, "native biome diagnosis biome=" + map.Biome.defName + " biomes=" + string.Join(",", biomes.Select(b => b.defName))
                + " declaredMushrooms=" + declared + " cachedNativeMushrooms=" + native
                + " allWildPlants=" + map.wildPlantSpawner.AllWildPlants.Count
                + " buttonCommonality=" + WildMushroomEcology.NativeCommonality(Crop("Button"), map));
            if (declared == 0 && native == 0)
            {
                // Quicktest chooses a random world tile, including tundra and
                // deserts which deliberately have no mushroom definitions.
                // The isolated game's tile is made explicit for ecological
                // behavior tests, and its native cache is rebuilt accordingly.
                map.TileInfo.PrimaryBiome = BiomeDefOf.TemperateForest;
                map.Biome.constantOutdoorTemperature = 21f;
                map.wildPlantSpawner = new WildPlantSpawner(map);
                Check(check, map.Biome == BiomeDefOf.TemperateForest
                    && map.wildPlantSpawner.AllWildPlants.Any(d => d.GetModExtension<MushroomEcologyExtension>()?.ringEligible == true),
                    "unsupported random quicktest tile replaced by explicit isolated temperate-forest fixture");
            }
            Check(check, map.wildPlantSpawner.AllWildPlants.Any(d => d.GetModExtension<MushroomEcologyExtension>() != null
                && WildMushroomEcology.NativeCommonality(d, map) > 0f),
                "declared mushroom biome and native commonality cache agree biome=" + map.Biome.defName);
        }

        private static void VerifySeasons(Map map, Action<bool, string> check)
        {
            var seasonal = DefDatabase<ThingDef>.AllDefs.Where(d => d.GetModExtension<MushroomEcologyExtension>() != null).ToArray();
            Check(check, seasonal.Length > 0, "season profiles are resolved on real plant definitions");
            int oldTicks = Find.TickManager.TicksGame;
            float oldSky = map.skyManager.CurSkyGlow;
            Plant plant = null;
            try
            {
                // GenDate resolves local seasons from latitude; using actual
                // game dates catches accidental quadrum-only hemisphere logic.
                foreach (var def in seasonal)
                {
                    var extension = def.GetModExtension<MushroomEcologyExtension>();
                    foreach (var season in new[] { Season.Spring, Season.Summer, Season.Fall, Season.Winter })
                    {
                        float expected = season == Season.Spring ? extension.springFactor
                            : season == Season.Summer ? extension.summerFactor
                            : season == Season.Fall ? extension.fallFactor : extension.winterFactor;
                        Check(check, Mathf.Abs(WildMushroomEcology.SeasonMultiplier(def, season) - expected) < 0.00001f,
                            "reported local season weight " + def.defName + " " + season);
                        Check(check, WildMushroomEcology.SeasonMultiplier(def, season, true) == 1f,
                            "tropical biome ignores temperate season weight " + def.defName + " " + season);
                    }
                    Check(check, WildMushroomEcology.SeasonMultiplier(def, Season.PermanentSummer) == extension.summerFactor
                        && WildMushroomEcology.SeasonMultiplier(def, Season.PermanentWinter) == extension.winterFactor,
                        "permanent season follows its native summer or winter climate " + def.defName);
                }
                var sample = seasonal.First(d => d.GetModExtension<MushroomEcologyExtension>().springFactor
                    != d.GetModExtension<MushroomEcologyExtension>().fallFactor);
                for (int day = 0; day < 60; day += 5)
                {
                    long abs = day * 60000L + 30000L;
                    Season north = GenDate.Season(abs, 45f, 0f);
                    Season south = GenDate.Season(abs, -45f, 0f);
                    Check(check, north != south && (int)south - 1 == (((int)north - 1) + 2) % 4,
                        "native north/south local seasons are opposite day=" + day);
                    var profile = sample.GetModExtension<MushroomEcologyExtension>();
                    float expectedSouth = south == Season.Spring ? profile.springFactor : south == Season.Summer ? profile.summerFactor
                        : south == Season.Fall ? profile.fallFactor : profile.winterFactor;
                    Check(check, Mathf.Abs(WildMushroomEcology.SeasonMultiplier(sample, south) - expectedSouth) < 0.00001f,
                        "southern weighting follows native local season day=" + day);
                }

                // Seasonal weighting belongs to wild population selection.
                // Cultivated mushrooms retain the normal temperature/light
                // growth factors and native sow permission all year at 21 C.
                var cell = Center(map);
                map.skyManager.ForceSetCurSkyGlow(0f);
                foreach (string id in new[] { "Button", "Cauliflower", "PurpleBlewit" })
                {
                    var def = Crop(id);
                    plant = (Plant)GenSpawn.Spawn(def, cell, map);
                    plant.sown = true;
                    for (int quarter = 0; quarter < 4; quarter++)
                    {
                        Find.TickManager.DebugSetTicksGame(oldTicks + quarter * 900000);
                        float expected = plant.GrowthRateFactor_Fertility * plant.GrowthRateFactor_Temperature
                            * plant.GrowthRateFactor_Light * plant.GrowthRateFactor_NoxiousHaze * plant.GrowthRateFactor_Drought;
                        Check(check, expected > 0f && Mathf.Abs(plant.GrowthRate - expected) < 0.00001f,
                            "cultivated growth factors ignore wild seasonal weighting " + id + " quarter=" + quarter);
                        plant.Destroy(DestroyMode.Vanish);
                        plant = null;
                        Check(check, def.CanNowPlantAt(cell, map), "native sow permission retained " + id + " quarter=" + quarter);
                        plant = (Plant)GenSpawn.Spawn(def, cell, map);
                        plant.sown = true;
                    }
                    plant.Destroy(DestroyMode.Vanish);
                    plant = null;
                }
            }
            finally
            {
                if (plant != null && !plant.Destroyed) plant.Destroy(DestroyMode.Vanish);
                map.skyManager.ForceSetCurSkyGlow(oldSky);
                Find.TickManager.DebugSetTicksGame(oldTicks);
            }
        }

        private static void VerifyGlow(Map map, Action<bool, string> check)
        {
            var cell = Center(map) + new IntVec3(8, 0, 0);
            float oldSky = map.skyManager.CurSkyGlow;
            Plant plant = null;
            try
            {
                map.skyManager.ForceSetCurSkyGlow(0f);
                foreach (string id in new[] { "GhostFungus", "Chlorophos" })
                {
                    map.glowGrid.GlowGridUpdate_First();
                    float baseline = map.glowGrid.GroundGlowAt(cell, ignoreSky: true);
                    plant = (Plant)GenSpawn.Spawn(Crop(id), cell, map);
                    var glower = plant.TryGetComp<CompGlower>();
                    Check(check, glower != null && glower.Glows, "native glower registers on spawn " + id);
                    map.glowGrid.GlowGridUpdate_First();
                    float lit = map.glowGrid.GroundGlowAt(cell, ignoreSky: true);
                    Check(check, lit > baseline && lit > 0f, "native ground glow is emitted " + id + " glow=" + lit);
                    plant.DeSpawn();
                    map.glowGrid.GlowGridUpdate_First();
                    Check(check, Mathf.Abs(map.glowGrid.GroundGlowAt(cell, ignoreSky: true) - baseline) < 0.0001f,
                        "native ground glow unregisters on despawn " + id);
                    GenSpawn.Spawn(plant, cell, map);
                    map.glowGrid.GlowGridUpdate_First();
                    Check(check, map.glowGrid.GroundGlowAt(cell, ignoreSky: true) > baseline,
                        "native ground glow returns on respawn " + id);
                    plant.Destroy(DestroyMode.Vanish);
                    plant = null;
                    map.glowGrid.GlowGridUpdate_First();
                    Check(check, Mathf.Abs(map.glowGrid.GroundGlowAt(cell, ignoreSky: true) - baseline) < 0.0001f,
                        "native ground glow unregisters on destruction " + id);
                }
                plant = (Plant)GenSpawn.Spawn(Crop("JackOLantern"), cell, map);
                Check(check, plant.TryGetComp<CompGlower>() == null, "jack-o-lantern name alone does not emit light");
            }
            finally
            {
                if (plant != null && !plant.Destroyed) plant.Destroy(DestroyMode.Vanish);
                map.glowGrid.GlowGridUpdate_First();
                map.skyManager.ForceSetCurSkyGlow(oldSky);
            }
        }

        private static void VerifyCellRules(Map map, Action<bool, string> check)
        {
            var ecology = map.GetComponent<MapComponent_WildMushrooms>();
            Check(check, ecology != null, "wild ecology map component is registered automatically");
            var cell = Center(map);
            var def = Crop("Button");
            float? oldTemperature = map.Biome.constantOutdoorTemperature;
            float oldCellTemperature = cell.GetTemperature(map);
            Thing obstruction = null;
            Zone_Growing zone = null;
            try
            {
                map.Biome.constantOutdoorTemperature = 21f;
                Check(check, ecology.IsEligibleCell(cell, def), "empty warm outdoor soil is a wild candidate");
                map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                Check(check, !ecology.IsEligibleCell(cell, def), "roofed cell is excluded from general wild ecology");
                map.roofGrid.SetRoof(cell, null);
                map.terrainGrid.SetTerrain(cell, TerrainDef.Named("Concrete"));
                Check(check, !ecology.IsEligibleCell(cell, def), "zero-fertility constructed floor is excluded");
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
                obstruction = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog), cell, map);
                Check(check, !ecology.IsEligibleCell(cell, def), "building cell is excluded");
                obstruction.Destroy(DestroyMode.Vanish);
                obstruction = null;
                obstruction = GenSpawn.Spawn(ThingDef.Named("Plant_Grass"), cell, map);
                Check(check, !ecology.IsEligibleCell(cell, def), "occupied plant cell is excluded");
                obstruction.Destroy(DestroyMode.Vanish);
                obstruction = null;
                zone = new Zone_Growing(map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                zone.AddCell(cell);
                Check(check, !ecology.IsEligibleCell(cell, def), "player growing zone is excluded even before sowing");
                zone.Delete(false);
                zone = null;
                obstruction = GenSpawn.Spawn(ThingDef.Named("HydroponicsBasin"), cell, map);
                Check(check, !ecology.IsEligibleCell(cell, def), "hydroponics cell is excluded");
                obstruction.Destroy(DestroyMode.Vanish);
                obstruction = null;
                map.Biome.constantOutdoorTemperature = -30f;
                // RoomTempTracker retains air temperature until its equalization
                // tick. Set that native fixture state as well as the biome's
                // target temperature, without advancing unrelated game systems.
                cell.GetRoom(map).Temperature = -30f;
                Check(check, cell.GetTemperature(map) == -30f && !ecology.IsEligibleCell(cell, def),
                    "freezing soil is excluded actual=" + cell.GetTemperature(map) + " configured=" + map.Biome.constantOutdoorTemperature);
                map.Biome.constantOutdoorTemperature = 70f;
                cell.GetRoom(map).Temperature = 70f;
                Check(check, cell.GetTemperature(map) == 70f && !ecology.IsEligibleCell(cell, def),
                    "overheated soil is excluded actual=" + cell.GetTemperature(map) + " configured=" + map.Biome.constantOutdoorTemperature);
                map.Biome.constantOutdoorTemperature = 21f;
                cell.GetRoom(map).Temperature = 21f;
                Check(check, ecology.IsEligibleCell(cell, def), "restored warm empty outdoor soil is eligible again");
            }
            finally
            {
                if (zone != null) zone.Delete(false);
                if (obstruction != null && !obstruction.Destroyed) obstruction.Destroy(DestroyMode.Vanish);
                map.Biome.constantOutdoorTemperature = oldTemperature;
                cell.GetRoom(map).Temperature = oldCellTemperature;
                map.roofGrid.SetRoof(cell, null);
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
            }
        }

        private static void VerifyNativeWildSelection(Map map, Action<bool, string> check)
        {
            var choice = typeof(WildPlantSpawner).GetMethod("PlantChoiceWeight", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(check, choice != null, "installed native wild plant selection method is available");
            var ecology = map.GetComponent<MapComponent_WildMushrooms>();
            var settings = ecology.Settings;
            var def = map.wildPlantSpawner.AllWildPlants.First(d => d.GetModExtension<MushroomEcologyExtension>() != null
                && WildMushroomEcology.NativeCommonality(d, map) > 0f);
            var profile = def.GetModExtension<MushroomEcologyExtension>();
            float oldSpring = profile.springFactor, oldSummer = profile.summerFactor,
                oldFall = profile.fallFactor, oldWinter = profile.winterFactor;
            int oldMin = settings.minWildCap, oldMax = settings.maxWildCap;
            float oldPerArea = settings.wildCapPer10000Cells;
            var cell = Center(map);
            Func<ThingDef, float> weight = plant => (float)choice.Invoke(map.wildPlantSpawner,
                new object[] { plant, cell, new Dictionary<ThingDef, float>(), 10000f, 1f });
            try
            {
                settings.minWildCap = settings.maxWildCap = 2000;
                settings.wildCapPer10000Cells = 100f;
                profile.springFactor = profile.summerFactor = profile.fallFactor = profile.winterFactor = 1f;
                float baseline = weight(def);
                float grass = weight(ThingDef.Named("Plant_Grass"));
                Check(check, baseline > 0f && !float.IsNaN(baseline), "actual native selection assigns positive eligible mushroom weight");
                profile.springFactor = profile.summerFactor = profile.fallFactor = profile.winterFactor = 2f;
                bool tropical = map.Biome.defName == "TropicalRainforest" || map.Biome.defName == "TropicalSwamp";
                Check(check, Mathf.Abs(weight(def) - baseline * (tropical ? 1f : 2f)) < 0.00001f,
                    "Harmony season multiplier executes through the actual native private selection method");
                Check(check, Mathf.Abs(weight(ThingDef.Named("Plant_Grass")) - grass) < 0.00001f,
                    "native non-mushroom wild selection remains unchanged by mushroom season profiles");
                map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                Check(check, weight(def) == 0f, "actual native wild selection excludes roofed mushroom habitat");
                map.roofGrid.SetRoof(cell, null);
                settings.minWildCap = settings.maxWildCap = ecology.WildPopulationCount;
                settings.wildCapPer10000Cells = 0f;
                Check(check, weight(def) == 0f, "actual native wild selection enforces the global wild mushroom population cap");
            }
            finally
            {
                profile.springFactor = oldSpring;
                profile.summerFactor = oldSummer;
                profile.fallFactor = oldFall;
                profile.winterFactor = oldWinter;
                settings.minWildCap = oldMin;
                settings.maxWildCap = oldMax;
                settings.wildCapPer10000Cells = oldPerArea;
                map.roofGrid.SetRoof(cell, null);
            }
        }

        private static void VerifyRingsAndBudgets(Map map, Action<bool, string> check)
        {
            var ecology = map.GetComponent<MapComponent_WildMushrooms>();
            var settings = ecology.Settings;
            int oldDaily = settings.dailyExtraBudget;
            int oldMin = settings.minWildCap, oldMax = settings.maxWildCap;
            float oldPerArea = settings.wildCapPer10000Cells;
            var originalBiome = map.TileInfo.PrimaryBiome;
            var originalSpawner = map.wildPlantSpawner;
            float? originalTemperateTemperature = BiomeDefOf.TemperateForest.constantOutdoorTemperature;
            var ringDef = NativeRingCrop(map);
            bool explicitRingFixture = ringDef == null;
            try
            {
                if (explicitRingFixture)
                {
                    var originalBiomes = map.Biomes.ToArray();
                    int declaredRings = DefDatabase<ThingDef>.AllDefs.Count(d =>
                        d.GetModExtension<MushroomEcologyExtension>()?.ringEligible == true
                        && d.plant.wildBiomes != null && d.plant.wildBiomes.Any(record =>
                            originalBiomes.Contains(record.biome) && record.commonality > 0f));
                    // Boreal and tropical forests legitimately contain mushrooms
                    // but none of the four ring species. Still fail if definitions
                    // promise a local ring species and the native cache lost it.
                    Check(check, declaredRings == 0, "random quicktest biome has no declared ring species biome=" + originalBiome.defName);
                    map.TileInfo.PrimaryBiome = BiomeDefOf.TemperateForest;
                    map.Biome.constantOutdoorTemperature = 21f;
                    map.wildPlantSpawner = new WildPlantSpawner(map);
                    ringDef = NativeRingCrop(map);
                    Check(check, map.Biome == BiomeDefOf.TemperateForest && ringDef != null
                        && WildMushroomEcology.NativeCommonality(ringDef, map) > 0f,
                        "ring-only fixture uses explicit temperate-forest biome and native commonality cache");
                }
                settings.dailyExtraBudget = 100;
                settings.minWildCap = settings.maxWildCap = 10000;
                settings.wildCapPer10000Cells = 10000f;
                foreach (bool half in new[] { false, true })
                {
                    ClearPatch(map);
                    var center = Center(map);
                    var before = new HashSet<Thing>(map.listerThings.AllThings);
                    int result = ecology.TrySpawnRing(center, ringDef, halfRing: half, count: 6);
                    var created = map.listerThings.AllThings.OfType<Plant_Mushroom>().Where(p => !before.Contains(p)).ToArray();
                    Check(check, result >= settings.ringMinCount && created.Length == result && created.Length <= 6,
                        "real " + (half ? "half-ring" : "ring") + " spawns a complete minimum-sized group");
                    Check(check, created.All(p => p.def == ringDef && !p.sown && ecology.IsEligibleCell(p.Position, ringDef) == false),
                        "ring uses one unsown species and fills previously empty cells");
                    var radii = created.Select(p => p.Position.DistanceTo(center)).ToArray();
                    Check(check, radii.All(r => r >= settings.ringMinRadius - 1f && r <= settings.ringMaxRadius + 1f)
                        && radii.Max() - radii.Min() <= 1.5f, "ring positions follow one rounded circle");
                    var angles = created.Select(p => Math.Atan2(p.Position.z - center.z, p.Position.x - center.x)).OrderBy(a => a).ToArray();
                    double largestGap = Enumerable.Range(0, angles.Length).Max(i => i + 1 < angles.Length
                        ? angles[i + 1] - angles[i] : angles[0] + Math.PI * 2 - angles[i]);
                    Check(check, half ? largestGap >= Math.PI - 0.45 : largestGap < Math.PI - 0.45,
                        half ? "half-ring occupies a single half-plane" : "full ring surrounds its center");
                }

                ClearPatch(map);
                var blocked = Center(map);
                foreach (var c in CellRect.CenteredOn(blocked, 13, 13)) map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                int beforeBudget = ecology.ExtraSpawnedToday;
                Check(check, ecology.TrySpawnRing(blocked, ringDef, count: 6) == 0 && ecology.ExtraSpawnedToday == beforeBudget,
                    "invalid roofed ring neither partially spawns nor consumes budget");
                ClearPatch(map);

                // Exactly three ordinary extra spawns use the remaining daily
                // allowance; destroying them does not refund this allowance.
                settings.dailyExtraBudget = ecology.ExtraSpawnedToday + 3;
                for (int i = 0; i < 3; i++)
                    Check(check, ecology.TrySpawnWild(ringDef, Center(map) + new IntVec3(i, 0, 8)) != null,
                        "extra wild spawn consumes daily allowance index=" + i);
                Check(check, ecology.ExtraSpawnedToday == settings.dailyExtraBudget
                    && ecology.TrySpawnWild(ringDef, Center(map) + new IntVec3(4, 0, 8)) == null, "daily extra population allowance is enforced");
                ClearPatch(map);
                Check(check, ecology.TrySpawnWild(ringDef, Center(map)) == null && ecology.TrySpawnRing(Center(map), ringDef, count: 6) == 0,
                    "removing plants cannot bypass an exhausted daily allowance");

                var capEcology = ecology;
                settings.dailyExtraBudget = 100;
                int population = map.listerThings.AllThings.OfType<Plant_Mushroom>().Count(p => !p.sown);
                settings.minWildCap = settings.maxWildCap = population;
                settings.wildCapPer10000Cells = 0f;
                Check(check, capEcology.WildCap == population
                    && capEcology.TrySpawnWild(ringDef, Center(map)) == null, "existing unsown map population counts toward total wild cap");
                Check(check, capEcology.TrySpawnRing(Center(map), ringDef, count: 6) == 0,
                    "ring also respects total wild population cap");
            }
            finally
            {
                settings.dailyExtraBudget = oldDaily;
                settings.minWildCap = oldMin;
                settings.maxWildCap = oldMax;
                settings.wildCapPer10000Cells = oldPerArea;
                ClearPatch(map);
                if (explicitRingFixture)
                {
                    map.TileInfo.PrimaryBiome = originalBiome;
                    map.wildPlantSpawner = originalSpawner;
                    BiomeDefOf.TemperateForest.constantOutdoorTemperature = originalTemperateTemperature;
                    Check(check, map.Biome == originalBiome && ReferenceEquals(map.wildPlantSpawner, originalSpawner),
                        "ring-only fixture restores the original quicktest biome and native spawner");
                }
            }
        }

        private static Plant_Mushroom SpawnMature(Map map, ThingDef def, IntVec3 cell, bool sown = false)
        {
            var plant = (Plant_Mushroom)GenSpawn.Spawn(def, cell, map);
            plant.Growth = 1f;
            plant.sown = sown;
            return plant;
        }

        private static void NativeHarvestSuccess(Pawn farmer, Plant_Mushroom plant)
        {
            farmer.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            farmer.pather.StopDead();
            // This is a synchronous notification integration test. A generated
            // mountain or river between the crop and ecology fixture must not
            // abort the native job before its success signal can be exercised.
            farmer.Position = plant.Position + IntVec3.West;
            farmer.Notify_Teleported();
            var job = JobMaker.MakeJob(JobDefOf.Harvest, plant);
            job.playerForced = true;
            farmer.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            if (!(farmer.jobs.curDriver is JobDriver_PlantHarvest))
                throw new InvalidOperationException("Native harvest fixture did not start the real driver job=" + farmer.CurJob
                    + " downed=" + farmer.Downed + " dead=" + farmer.Dead + " position=" + farmer.Position
                    + " health=" + string.Join(",", farmer.health.hediffSet.hediffs.Select(h => h.def.defName + ":" + h.Severity)));
            var yield = ThingMaker.MakeThing(plant.def.plant.harvestedThingDef);
            yield.stackCount = 1;
            try { Find.QuestManager.Notify_PlantHarvested(farmer, yield); }
            finally { yield.Destroy(DestroyMode.Vanish); }
        }

        private static void VerifyRegrowth(Map map, Pawn farmer, Action<bool, string> check)
        {
            var ecology = map.GetComponent<MapComponent_WildMushrooms>();
            var def = NativeRegrowthCrop(map);
            var extension = def.GetModExtension<MushroomEcologyExtension>();
            var settings = ecology.Settings;
            float oldChance = extension.regrowthChance, oldDelay = extension.regrowthDelayDays;
            int oldDaily = settings.dailyExtraBudget, oldMin = settings.minWildCap, oldMax = settings.maxWildCap;
            float oldPerArea = settings.wildCapPer10000Cells;
            int oldTicks = Find.TickManager.TicksGame;
            IntVec3 originalFarmerPosition = farmer.Position;
            Zone_Growing zone = null;
            Thing temporaryBlocker = null;
            var cell = Center(map);
            try
            {
                extension.regrowthChance = 1f;
                extension.regrowthDelayDays = 1f;
                settings.dailyExtraBudget = 100;
                settings.minWildCap = settings.maxWildCap = 10000;
                settings.wildCapPer10000Cells = 10000f;
                int baseline = ecology.PendingRegrowthCount;
                Check(check, baseline == 0, "fresh map has no unsolicited harvest-site regrowth");

                var cut = SpawnMature(map, def, cell);
                cut.PlantCollected(farmer, PlantDestructionMode.Cut);
                Check(check, cut.Destroyed && ecology.PendingRegrowthCount == baseline, "cutting a wild mushroom does not leave a mycelium ticket");

                var failed = SpawnMature(map, def, cell);
                failed.PlantCollected(farmer, PlantDestructionMode.Chop);
                Check(check, failed.Destroyed && ecology.PendingRegrowthCount == baseline,
                    "failed harvest without native success notification does not leave a mycelium ticket");

                var cultivated = SpawnMature(map, def, cell, sown: true);
                NativeHarvestSuccess(farmer, cultivated);
                cultivated.PlantCollected(farmer, PlantDestructionMode.Chop);
                Check(check, cultivated.Destroyed && ecology.PendingRegrowthCount == baseline,
                    "successful cultivated harvest does not leave wild mycelium");

                zone = new Zone_Growing(map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                zone.AddCell(cell);
                var zoned = SpawnMature(map, def, cell);
                NativeHarvestSuccess(farmer, zoned);
                zoned.PlantCollected(farmer, PlantDestructionMode.Chop);
                Check(check, ecology.PendingRegrowthCount == baseline, "unsown mushroom inside growing zone does not leave wild mycelium");
                zone.Delete(false);
                zone = null;

                var immature = SpawnMature(map, def, cell);
                immature.Growth = 0.01f;
                Check(check, !ecology.TryQueueRegrowth(immature, bypassChance: true) && ecology.PendingRegrowthCount == baseline,
                    "immature wild mushroom cannot queue regrowth");
                immature.Destroy(DestroyMode.Vanish);

                var harvested = SpawnMature(map, def, cell);
                NativeHarvestSuccess(farmer, harvested);
                harvested.PlantCollected(farmer, PlantDestructionMode.Chop);
                Check(check, harvested.Destroyed && ecology.PendingRegrowthCount == baseline + 1,
                    "native successful harvest notification followed by collection creates one mycelium ticket");
                Check(check, harvested.WildRegrowthSpent && !ecology.TryQueueRegrowth(harvested, bypassChance: true)
                    && ecology.PendingRegrowthCount == baseline + 1, "collected original cannot queue a duplicate ticket");

                int due = ecology.PendingRegrowth.Single(m => m.cell == cell).dueTick;
                ecology.ProcessRegrowth(due - 1);
                Check(check, cell.GetPlant(map) == null && ecology.PendingRegrowthCount == baseline + 1,
                    "mycelium stays invisible until its scheduled delay");
                Find.TickManager.DebugSetTicksGame(due + 1);
                ecology.ProcessRegrowth(due + 1);
                var regenerated = cell.GetPlant(map) as Plant_Mushroom;
                Check(check, regenerated != null && regenerated.def == def && !regenerated.sown
                    && regenerated.WildRegrowthGeneration == 1 && ecology.PendingRegrowthCount == baseline,
                    "delayed mycelium regenerates same wild species exactly once");
                Check(check, regenerated.Growth > 0f && regenerated.Growth < 1f, "regenerated plant starts immature rather than producing an instant harvest");
                ecology.ProcessRegrowth(due + 1);
                Check(check, cell.GetThingList(map).OfType<Plant_Mushroom>().Count() == 1,
                    "repeated scheduler call cannot duplicate completed regrowth");
                regenerated.Growth = 1f;
                NativeHarvestSuccess(farmer, regenerated);
                regenerated.PlantCollected(farmer, PlantDestructionMode.Chop);
                Check(check, regenerated.Destroyed && ecology.PendingRegrowthCount == baseline,
                    "harvesting regenerated mushroom cannot begin an endless second generation");

                var oneShot = SpawnMature(map, def, cell);
                Check(check, ecology.TryQueueRegrowth(oneShot, bypassChance: true)
                    && !ecology.TryQueueRegrowth(oneShot, bypassChance: true), "a live original consumes its single regrowth opportunity at queue time");
                oneShot.Destroy(DestroyMode.Vanish);
                temporaryBlocker = GenSpawn.Spawn(def.plant.harvestedThingDef, cell, map);
                int blockedDue = ecology.PendingRegrowth.Single(m => m.cell == cell).dueTick + 1;
                int expiry = ecology.PendingRegrowth.Single(m => m.cell == cell).expiryTick + 1;
                Find.TickManager.DebugSetTicksGame(blockedDue);
                ecology.ProcessRegrowth(blockedDue);
                Check(check, cell.GetPlant(map) == null && ecology.PendingRegrowthCount == baseline + 1,
                    "temporarily occupied harvest site waits without deleting its scheduled mycelium");
                Find.TickManager.DebugSetTicksGame(expiry);
                ecology.ProcessRegrowth(expiry);
                Check(check, ecology.PendingRegrowthCount == baseline, "blocked regrowth ticket is discarded by expiry rather than accumulating forever");
                temporaryBlocker.Destroy(DestroyMode.Vanish);
                temporaryBlocker = null;
                var roofed = SpawnMature(map, def, cell);
                Check(check, ecology.TryQueueRegrowth(roofed, true), "soil conversion fixture begins with a real pending ticket");
                roofed.Destroy(DestroyMode.Vanish);
                map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                ecology.ProcessRegrowth(Find.TickManager.TicksGame);
                Check(check, cell.GetPlant(map) == null && ecology.PendingRegrowthCount == baseline,
                    "roof construction permanently cancels pending wild mycelium");
            }
            finally
            {
                if (zone != null) zone.Delete(false);
                if (temporaryBlocker != null && !temporaryBlocker.Destroyed) temporaryBlocker.Destroy(DestroyMode.Vanish);
                farmer.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                farmer.pather.StopDead();
                farmer.Position = originalFarmerPosition;
                farmer.Notify_Teleported();
                extension.regrowthChance = oldChance;
                extension.regrowthDelayDays = oldDelay;
                settings.dailyExtraBudget = oldDaily;
                settings.minWildCap = oldMin;
                settings.maxWildCap = oldMax;
                settings.wildCapPer10000Cells = oldPerArea;
                Find.TickManager.DebugSetTicksGame(oldTicks);
                ClearPatch(map);
            }
        }

        public static void Verify(Map map, Pawn farmer, Action<bool, string> check)
        {
            Check(check, CellRect.CenteredOn(Center(map), 35, 35).Cells.All(c => c.InBounds(map)),
                "separate outdoor ecology test patch fits map");
            ClearPatch(map);
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            foreach (var room in CellRect.CenteredOn(Center(map), 35, 35).Cells.Select(c => c.GetRoom(map)).Where(r => r != null).Distinct())
                room.Temperature = 21f;
            Check(check, Mathf.Abs(Center(map).GetTemperature(map) - 21f) < 0.001f,
                "ecology fixture native room temperature is 21C actual=" + Center(map).GetTemperature(map));
            try
            {
                PrepareNativeBiomeFixture(map, check);
                VerifySeasons(map, check);
                VerifyGlow(map, check);
                VerifyCellRules(map, check);
                VerifyNativeWildSelection(map, check);
                VerifyRingsAndBudgets(map, check);
                VerifyRegrowth(map, farmer, check);
            }
            finally { ClearPatch(map); }
        }

        public static void PrepareSave(Map map, Action<bool, string> check)
        {
            ClearPatch(map);
            var ecology = map.GetComponent<MapComponent_WildMushrooms>();
            var def = NativeRegrowthCrop(map);
            var original = SpawnMature(map, def, Center(map));
            int before = ecology.PendingRegrowthCount;
            Check(check, ecology.TryQueueRegrowth(original, bypassChance: true), "save fixture queues one deterministic original harvest site");
            savedSourceId = original.ThingID;
            savedRegrowthCell = original.Position;
            savedRegrowthDef = original.def.defName;
            original.Destroy(DestroyMode.Vanish);
            var ticket = ecology.PendingRegrowth.Single(m => m.sourceId == savedSourceId);
            savedDueTick = ticket.dueTick;
            savedExpiryTick = ticket.expiryTick;
            savedPendingCount = ecology.PendingRegrowthCount;
            Check(check, savedPendingCount == before + 1 && savedDueTick > Find.TickManager.TicksGame,
                "save fixture retains a future invisible mycelium ticket");

            var glow = (Plant_Mushroom)GenSpawn.Spawn(Crop("GhostFungus"), Center(map) + new IntVec3(8, 0, 0), map);
            glow.Growth = 0.5f;
            savedGlowId = glow.ThingID;
            var generation = SpawnMature(map, def, Center(map) + new IntVec3(-5, 0, 0));
            generation.SetWildRegrowthGeneration(1);
            savedGenerationId = generation.ThingID;
            var spent = SpawnMature(map, def, Center(map) + new IntVec3(5, 0, 0));
            spent.MarkWildRegrowthSpent();
            savedSpentId = spent.ThingID;
            savedRandomState = ecology.RandomState;
            savedNextRingTick = ecology.NextRingTick;
            savedExtraToday = ecology.ExtraSpawnedToday;
            Check(check, !ecology.TryQueueRegrowth(generation, true) && !ecology.TryQueueRegrowth(spent, true),
                "saved generation and spent originals cannot create extra tickets before save");
        }

        public static void VerifyLoaded(Map map, bool legacyLoaded, Action<bool, string> check)
        {
            var ecology = map.GetComponent<MapComponent_WildMushrooms>();
            Check(check, ecology != null, (legacyLoaded ? "legacy" : "new") + " save restores ecology component");
            if (legacyLoaded)
            {
                VerifyLegacyEcology(map, ecology, check);
                return;
            }

            Check(check, savedGlowId != null && savedSourceId != null, "expected ecology save snapshot was prepared");
            Check(check, ecology.PendingRegrowthCount == savedPendingCount, "pending mycelium count survives native save and reload");
            var ticket = ecology.PendingRegrowth.SingleOrDefault(m => m.sourceId == savedSourceId);
            Check(check, ticket != null && ticket.cell == savedRegrowthCell && ticket.plantDef.defName == savedRegrowthDef
                && ticket.dueTick == savedDueTick && ticket.expiryTick == savedExpiryTick,
                "mycelium species, source, cell, absolute due tick, and expiry survive reload exactly");
            Check(check, ecology.RandomState == savedRandomState && ecology.NextRingTick == savedNextRingTick
                && ecology.ExtraSpawnedToday == savedExtraToday, "ecology random sequence, ring schedule, and daily budget survive reload");
            var glow = map.listerThings.AllThings.OfType<Plant_Mushroom>().Single(p => p.ThingID == savedGlowId);
            Check(check, glow.def == Crop("GhostFungus") && glow.TryGetComp<CompGlower>().Glows,
                "saved glowing mushroom restores native active glower");
            map.glowGrid.GlowGridUpdate_First();
            Check(check, map.glowGrid.GroundGlowAt(glow.Position, ignoreSky: true) > 0f,
                "saved glowing mushroom emits actual ground glow after reload");
            var generation = map.listerThings.AllThings.OfType<Plant_Mushroom>().Single(p => p.ThingID == savedGenerationId);
            var spent = map.listerThings.AllThings.OfType<Plant_Mushroom>().Single(p => p.ThingID == savedSpentId);
            Check(check, generation.WildRegrowthGeneration == 1 && spent.WildRegrowthGeneration == 0 && spent.WildRegrowthSpent,
                "one-generation and spent original flags survive native save and reload");
            Check(check, !ecology.TryQueueRegrowth(generation, true) && !ecology.TryQueueRegrowth(spent, true)
                && ecology.PendingRegrowthCount == savedPendingCount, "save reload cannot reset a plant's consumed regeneration opportunity");

            var settings = ecology.Settings;
            int oldTicks = Find.TickManager.TicksGame;
            int oldMin = settings.minWildCap, oldMax = settings.maxWildCap, oldDaily = settings.dailyExtraBudget;
            float oldPerArea = settings.wildCapPer10000Cells;
            try
            {
                settings.minWildCap = settings.maxWildCap = 10000;
                settings.wildCapPer10000Cells = 10000f;
                settings.dailyExtraBudget = 64;
                ecology.ProcessRegrowth(savedDueTick - 1);
                Check(check, savedRegrowthCell.GetPlant(map) == null
                    && ecology.PendingRegrowth.Any(m => m.sourceId == savedSourceId), "reload does not make pending regrowth occur early");
                Find.TickManager.DebugSetTicksGame(savedDueTick + 1);
                ecology.ProcessRegrowth(savedDueTick + 1);
                var regenerated = savedRegrowthCell.GetPlant(map) as Plant_Mushroom;
                Check(check, regenerated != null && regenerated.def.defName == savedRegrowthDef
                    && regenerated.WildRegrowthGeneration == 1 && !ecology.PendingRegrowth.Any(m => m.sourceId == savedSourceId),
                    "loaded mycelium produces its same species once at the saved scheduled time");
                ecology.ProcessRegrowth(savedDueTick + 1);
                Check(check, savedRegrowthCell.GetThingList(map).OfType<Plant_Mushroom>().Count() == 1
                    && !ecology.TryQueueRegrowth(regenerated, true), "repeated processing after reload cannot duplicate a mushroom or reset its generation");
            }
            finally
            {
                settings.minWildCap = oldMin;
                settings.maxWildCap = oldMax;
                settings.dailyExtraBudget = oldDaily;
                settings.wildCapPer10000Cells = oldPerArea;
                Find.TickManager.DebugSetTicksGame(oldTicks);
            }
        }

        private static void VerifyLegacyEcology(Map map, MapComponent_WildMushrooms ecology, Action<bool, string> check)
        {
            string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "Saves", "MoreMushrooms-LegacyFixture.rws");
            Check(check, File.Exists(path), "actual legacy save XML is available for ecology compatibility comparison");
            var document = new XmlDocument { XmlResolver = null };
            document.Load(path);
            var savedMap = document.SelectSingleNode("/savegame/game/maps/li[uniqueID='" + map.uniqueID + "']");
            Check(check, savedMap != null, "legacy ecology comparison uses the same saved map ID " + map.uniqueID);
            var savedComponent = savedMap.SelectSingleNode("components/li[@Class='RimMushrooms.MapComponent_WildMushrooms']");
            if (savedComponent == null)
            {
                // v0.5 and earlier have no ecology component. Schedule/RNG fields
                // are intentionally initialized, but no tickets or budget use
                // may be invented by that migration.
                Check(check, ecology.PendingRegrowthCount == 0 && ecology.ExtraSpawnedToday == 0,
                    "pre-ecology save starts with no invented tickets or used budget");
            }
            else
            {
                foreach (string name in new[] { "randomState", "nextCheckTick", "nextRingTick", "budgetDay", "extraSpawnedToday" })
                {
                    string tag = "mushroomEcology" + char.ToUpperInvariant(name[0]) + name.Substring(1);
                    int expected = int.Parse(savedComponent.SelectSingleNode(tag)?.InnerText ?? (name == "budgetDay" ? "-1" : "0"), CultureInfo.InvariantCulture);
                    var field = typeof(MapComponent_WildMushrooms).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
                    Check(check, field != null && (int)field.GetValue(ecology) == expected,
                        "actual legacy ecology RNG/schedule/budget field preserved " + name + "=" + expected);
                }
                var savedTickets = savedComponent.SelectNodes("mushroomMycelia/li");
                Check(check, ecology.PendingRegrowthCount == savedTickets.Count, "actual legacy pending mycelium count preserved");
                foreach (XmlNode saved in savedTickets)
                {
                    string source = saved.SelectSingleNode("sourceId")?.InnerText;
                    var ticket = ecology.PendingRegrowth.SingleOrDefault(m => m.sourceId == source);
                    Check(check, ticket != null && ticket.plantDef.defName == saved.SelectSingleNode("plantDef")?.InnerText
                        && ticket.cell == IntVec3.FromString(saved.SelectSingleNode("cell").InnerText)
                        && ticket.dueTick == int.Parse(saved.SelectSingleNode("dueTick").InnerText, CultureInfo.InvariantCulture)
                        && ticket.expiryTick == int.Parse(saved.SelectSingleNode("expiryTick").InnerText, CultureInfo.InvariantCulture),
                        "actual legacy mycelium species/source/cell/absolute deadlines preserved " + source);
                }
            }

            var plants = map.listerThings.AllThings.OfType<Plant_Mushroom>().ToDictionary(p => p.ThingID);
            int compared = 0, generations = 0, spent = 0;
            foreach (XmlNode saved in savedMap.SelectNodes("things/thing[starts-with(def,'RMush_Plant')]"))
            {
                string id = saved.SelectSingleNode("id")?.InnerText;
                int generation = int.Parse(saved.SelectSingleNode("mushroomWildRegrowthGeneration")?.InnerText ?? "0", CultureInfo.InvariantCulture);
                bool used = bool.Parse(saved.SelectSingleNode("mushroomWildRegrowthSpent")?.InnerText ?? "false");
                if (LightTests.WasValidatedLegacyStaticOverlap(id))
                {
                    Check(check, !plants.ContainsKey(id) && generation == 0 && !used,
                        "preexisting invalid static overlap was independently proven by native load rules and held no regeneration state " + id);
                    continue;
                }
                Plant_Mushroom plant;
                Check(check, plants.TryGetValue(id, out plant) && plant.WildRegrowthGeneration == generation && plant.WildRegrowthSpent == used,
                    "actual legacy plant regeneration flags preserved " + id + " generation=" + generation + " spent=" + used);
                if (generation > 0) generations++;
                if (used) spent++;
                compared++;
            }
            Check(check, compared > 0, "legacy ecology plant flags compared against XML for " + compared + " plants; regenerated=" + generations + " spent=" + spent);
        }
    }
}
