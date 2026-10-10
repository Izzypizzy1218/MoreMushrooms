using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using RimMushrooms;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMushroomsTests
{
    internal static class LightTests
    {
        private static readonly HashSet<string> ProvenLegacyStaticOverlaps = new HashSet<string>();
        internal static bool WasValidatedLegacyStaticOverlap(string id) => ProvenLegacyStaticOverlaps.Contains(id);

        public static void Run(Map map, IntVec3 cell, Action<bool, string> check)
        {
            int oldTicks = Find.TickManager.TicksGame;
            float oldGlow = map.skyManager.CurSkyGlow;
            var oldRoof = map.roofGrid.RoofAt(cell);
            float? oldBiomeTemperature = map.Biome.constantOutdoorTemperature;
            float oldCellTemperature = cell.GetTemperature(map);
            Plant plant = null;
            try
            {
                // The biome target alone does not update RoomTempTracker until
                // native equalization ticks. This synchronous light fixture must
                // actually be at its declared 21C before measuring growth.
                map.Biome.constantOutdoorTemperature = 21f;
                cell.GetRoom(map).Temperature = 21f;
                check(Mathf.Abs(cell.GetTemperature(map) - 21f) < 0.001f,
                    "light fixture actual room temperature is 21C; previous=" + oldCellTemperature + " actual=" + cell.GetTemperature(map));
                int toNoon = (int)((0.5f - GenLocalDate.DayPercent(map) + 1f) % 1f * 60000f);
                Find.TickManager.DebugSetTicksGame(oldTicks + toNoon);
                foreach (var def in DefDatabase<ThingDef>.AllDefs.Where(d => d.defName.StartsWith("RMush_Plant")))
                {
                    plant = (Plant)GenSpawn.Spawn(def, cell, map);
                    var mushroom = plant as Plant_Mushroom;
                    check(mushroom != null, "shade-aware spawned class " + def.defName);
                    plant.Growth = 0.25f;
                    map.roofGrid.SetRoof(cell, null);
                    float nativeFactors = plant.GrowthRateFactor_Fertility * plant.GrowthRateFactor_Temperature * plant.GrowthRateFactor_Light * plant.GrowthRateFactor_NoxiousHaze * plant.GrowthRateFactor_Drought;
                    check(nativeFactors > 0f, "fixture supports growth " + def.defName + " temp=" + cell.GetTemperature(map)
                        + " fertility=" + plant.GrowthRateFactor_Fertility + " light=" + plant.GrowthRateFactor_Light
                        + " temperature=" + plant.GrowthRateFactor_Temperature + " haze=" + plant.GrowthRateFactor_NoxiousHaze + " drought=" + plant.GrowthRateFactor_Drought);
                    foreach (float glow in new[] {0f, 0.49f, 0.5f, 0.51f, 0.75f, 1f})
                    {
                        map.skyManager.ForceSetCurSkyGlow(glow);
                        float actualGlow = map.glowGrid.GroundGlowAt(cell);
                        float expected = actualGlow <= 0.5f ? 1f : 1.5f - actualGlow;
                        check(Mathf.Abs(mushroom.BrightLightGrowthFactor - expected) < 0.0001f, "light curve " + def.defName + " glow=" + actualGlow + " multiplier=" + expected);
                        check(Mathf.Abs(plant.GrowthRate - nativeFactors * expected) < 0.0001f, "total growth multiplier " + def.defName + " glow=" + actualGlow);
                        plant.Growth = 0.25f;
                        int hp = plant.HitPoints;
                        plant.TickLong();
                        float expectedDelta = nativeFactors * expected * GenTicks.TickLongInterval / (60000f * def.plant.growDays);
                        check(Mathf.Abs((plant.Growth - 0.25f) - expectedDelta) < 0.000002f, "actual growth tick follows light multiplier " + def.defName + " glow=" + actualGlow);
                        check(!plant.DyingBecauseExposedToLight && plant.HitPoints == hp, "light causes no direct damage " + def.defName);
                    }
                    check(plant.GrowthRateCalcDesc.Contains("MM_BrightLightGrowthFactor".Translate("50%")), "translated 50 percent growth tooltip " + def.defName);
                    map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    map.skyManager.ForceSetCurSkyGlow(1f);
                    check(map.glowGrid.GroundGlowAt(cell) <= 0.5f && mushroom.BrightLightGrowthFactor == 1f, "roof shade restores full multiplier " + def.defName);
                    check(Mathf.Abs(plant.GrowthRate - nativeFactors) < 0.0001f, "roof shade restores actual growth rate " + def.defName);
                    check(!plant.GrowthRateCalcDesc.Contains("MM_BrightLightGrowthFactor".Translate("50%")), "shade removes bright-light penalty from tooltip " + def.defName);
                    plant.Growth = 0.25f;
                    Find.TickManager.DebugSetTicksGame(oldTicks + toNoon + 30000);
                    plant.TickLong();
                    check(plant.Growth == 0.25f, "night rest still applies " + def.defName);
                    Find.TickManager.DebugSetTicksGame(oldTicks + toNoon);
                    plant.Destroy(); plant = null;
                    map.roofGrid.SetRoof(cell, null);
                }
                var rice = (Plant)GenSpawn.Spawn(ThingDef.Named("Plant_Rice"), cell, map);
                plant = rice;
                map.skyManager.ForceSetCurSkyGlow(1f);
                check(rice.GetType() == typeof(Plant) && rice.GrowthRateFactor_Light == 1f, "vanilla rice class and sunlight growth unaffected");
                rice.Destroy(); plant = null;
                if (ModsConfig.IdeologyActive)
                    check(ThingDef.Named("Plant_Nutrifungus").plant.diesToLight, "vanilla nutrifungus retains its own light rules");
            }
            finally
            {
                if (plant != null && !plant.Destroyed) plant.Destroy();
                map.roofGrid.SetRoof(cell, oldRoof);
                map.skyManager.ForceSetCurSkyGlow(oldGlow);
                Find.TickManager.DebugSetTicksGame(oldTicks);
                map.Biome.constantOutdoorTemperature = oldBiomeTemperature;
                cell.GetRoom(map).Temperature = oldCellTemperature;
            }
        }

        public static void VerifyLoaded(Map map, bool legacy, Action<bool, string> check)
        {
            var plants = map.listerThings.AllThings.OfType<Plant>().Where(p => p.def.defName.StartsWith("RMush_Plant")).ToArray();
            check(plants.Length >= 33 && plants.All(p => p is Plant_Mushroom), (legacy ? "legacy" : "new") + " save restores shade-aware plants");
            if (!legacy) return;
            ProvenLegacyStaticOverlaps.Clear();
            var doc = new XmlDocument { XmlResolver = null };
            doc.Load(Path.Combine(GenFilePaths.SaveDataFolderPath, "Saves", "MoreMushrooms-LegacyFixture.rws"));
            var savedMap = doc.SelectSingleNode("/savegame/game/maps/li[uniqueID='" + map.uniqueID + "']");
            check(savedMap != null, "legacy plant comparison uses the same saved map ID " + map.uniqueID);
            var compressedStatics = ReadSavedCompressedStatics(savedMap, map, check);
            int compared = 0;
            foreach (XmlNode node in savedMap.SelectNodes("things/thing[@Class='Plant' or @Class='RimWorld.Plant' or @Class='RimMushrooms.Plant_Mushroom']"))
            {
                string defName = node["def"]?.InnerText;
                if (defName == null || !defName.StartsWith("RMush_Plant")) continue;
                string id = node["id"].InnerText;
                var cell = IntVec3.FromString(node["pos"].InnerText);
                var plant = plants.SingleOrDefault(p => p.ThingID == id);
                if (plant == null)
                {
                    ushort hash;
                    compressedStatics.TryGetValue(cell, out hash);
                    var staticDef = hash == 0 ? null : DefDatabase<ThingDef>.AllDefs.SingleOrDefault(d => d.shortHash == hash);
                    var blocker = cell.GetThingList(map).FirstOrDefault(t => t is Building && t.def == staticDef && t.def.saveCompressible);
                    // The v0.6 DLC artwork fixture extended below its cleared soil
                    // rectangle. Native plant spawn permits rock overlap, while
                    // loading the saved compressed rock correctly wipes that plant.
                    // Accept only this fully proven native case, never an unexplained
                    // missing plant, changed ID, other map or held-object mismatch.
                    check(hash != 0 && staticDef != null && blocker != null
                        && GenSpawn.SpawningWipes(blocker.def, ThingDef.Named(defName)),
                        "missing legacy plant must be explained by its original compressed static building and native wipe rule "
                        + id + " cell=" + cell + " savedHash=" + hash + " resolvedDef=" + staticDef?.defName
                        + " loadedCell=" + string.Join(",", cell.GetThingList(map).Select(t => t.ThingID + ":" + t.def.defName)));
                    ProvenLegacyStaticOverlaps.Add(id);
                    check(true, "preexisting v0.6 invalid artwork overlap receives native load wipe " + id
                        + " cell=" + cell + " compressedHash=" + hash + " building=" + blocker.def.defName);
                    continue;
                }
                check(plant.def.defName == defName && plant.Position == cell, "legacy identity and position preserved " + plant.ThingID);
                check(Mathf.Abs(plant.Growth - float.Parse(node["growth"].InnerText, CultureInfo.InvariantCulture)) < 0.000001f, "legacy growth preserved " + plant.ThingID);
                check(plant.Age == (node["age"] == null ? 0 : int.Parse(node["age"].InnerText)), "legacy age preserved " + plant.ThingID);
                if (node["health"] != null) check(plant.HitPoints == int.Parse(node["health"].InnerText), "legacy health preserved " + plant.ThingID);
                compared++;
            }
            check(compared >= 33, "old save valid plants restored without replacing IDs or growth; compared=" + compared
                + " provenPreexistingStaticOverlaps=" + ProvenLegacyStaticOverlaps.Count);
            check(compared + ProvenLegacyStaticOverlaps.Count == savedMap.SelectNodes("things/thing[starts-with(def,'RMush_Plant')]").Count,
                "every saved legacy mushroom is compared or has an independently proven original static overlap");
            check(map.listerThings.AllThings.OfType<Plant>().Where(p => !p.def.defName.StartsWith("RMush_Plant")).All(p => !(p is Plant_Mushroom)), "legacy vanilla plants not converted");
            // Check that loading did not write the class migration back into the source file.
            using (var stream = File.OpenRead(Path.Combine(GenFilePaths.SaveDataFolderPath, "Saves", "MoreMushrooms-LegacyFixture.rws")))
            using (var hash = SHA256.Create())
                check(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "") == File.ReadAllText(Path.Combine(GenFilePaths.SaveDataFolderPath, "legacy-input.sha256")).Trim(), "legacy input save remains unchanged on disk");
        }

        private static Dictionary<IntVec3, ushort> ReadSavedCompressedStatics(XmlNode savedMap, Map map, Action<bool, string> check)
        {
            var result = new Dictionary<IntVec3, ushort>();
            string compressed = savedMap.SelectSingleNode("compressedThingMapDeflate")?.InnerText;
            string uncompressed = savedMap.SelectSingleNode("compressedThingMap")?.InnerText;
            if (string.IsNullOrWhiteSpace(compressed) && string.IsNullOrWhiteSpace(uncompressed)) return result;
            byte[] bytes = string.IsNullOrWhiteSpace(compressed) ? Convert.FromBase64String(uncompressed)
                : CompressUtility.Decompress(Convert.FromBase64String(compressed));
            check(bytes.Length == map.cellIndices.NumGridCells * 2, "legacy compressed static grid has the expected native ushort dimensions");
            MapSerializeUtility.LoadUshort(bytes, map, (cell, hash) => { if (hash != 0) result.Add(cell, hash); });
            return result;
        }
    }
}
