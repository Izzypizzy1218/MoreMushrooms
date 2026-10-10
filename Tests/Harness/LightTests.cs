using System;
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
        public static void Run(Map map, IntVec3 cell, Action<bool, string> check)
        {
            int oldTicks = Find.TickManager.TicksGame;
            float oldGlow = map.skyManager.CurSkyGlow;
            var oldRoof = map.roofGrid.RoofAt(cell);
            Plant plant = null;
            try
            {
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
            }
        }

        public static void VerifyLoaded(Map map, bool legacy, Action<bool, string> check)
        {
            var plants = map.listerThings.AllThings.OfType<Plant>().Where(p => p.def.defName.StartsWith("RMush_Plant")).ToArray();
            check(plants.Length >= 33 && plants.All(p => p is Plant_Mushroom), (legacy ? "legacy" : "new") + " save restores shade-aware plants");
            if (!legacy) return;
            var doc = new XmlDocument();
            doc.Load(Path.Combine(GenFilePaths.SaveDataFolderPath, "Saves", "MoreMushrooms-LegacyFixture.rws"));
            int compared = 0;
            foreach (XmlNode node in doc.SelectNodes("//thing[@Class='Plant' or @Class='RimWorld.Plant' or @Class='RimMushrooms.Plant_Mushroom']"))
            {
                string defName = node["def"]?.InnerText;
                if (defName == null || !defName.StartsWith("RMush_Plant")) continue;
                var plant = plants.Single(p => p.ThingID == node["id"].InnerText);
                check(plant.def.defName == defName && plant.Position == IntVec3.FromString(node["pos"].InnerText), "legacy identity and position preserved " + plant.ThingID);
                check(Mathf.Abs(plant.Growth - float.Parse(node["growth"].InnerText, CultureInfo.InvariantCulture)) < 0.000001f, "legacy growth preserved " + plant.ThingID);
                check(plant.Age == (node["age"] == null ? 0 : int.Parse(node["age"].InnerText)), "legacy age preserved " + plant.ThingID);
                if (node["health"] != null) check(plant.HitPoints == int.Parse(node["health"].InnerText), "legacy health preserved " + plant.ThingID);
                compared++;
            }
            check(compared >= 33, "old save plants restored without replacing IDs or growth");
            check(map.listerThings.AllThings.OfType<Plant>().Where(p => !p.def.defName.StartsWith("RMush_Plant")).All(p => !(p is Plant_Mushroom)), "legacy vanilla plants not converted");
            // Check that loading did not write the class migration back into the source file.
            using (var stream = File.OpenRead(Path.Combine(GenFilePaths.SaveDataFolderPath, "Saves", "MoreMushrooms-LegacyFixture.rws")))
            using (var hash = SHA256.Create())
                check(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "") == File.ReadAllText(Path.Combine(GenFilePaths.SaveDataFolderPath, "legacy-input.sha256")).Trim(), "legacy input save remains unchanged on disk");
        }
    }
}
