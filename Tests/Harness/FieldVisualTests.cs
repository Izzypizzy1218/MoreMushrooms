using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimMushrooms;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMushroomsTests
{
    // Native rendered frames; the harness is never part of the released mod.
    public sealed class FieldVisualTests : GameComponent
    {
        private Map map;
        private ThingDef[] species;
        private readonly List<Thing> fixtures = new List<Thing>();
        private readonly List<KeyValuePair<IntVec3, string>> labels = new List<KeyValuePair<IntVec3, string>>();
        private readonly StringBuilder report = new StringBuilder();
        private int shot, frames;
        private bool initialized, requested, finished;
        private float readyAt, captureDeadline;
        private string pending;
        private IntVec3 center;
        private string Output => Path.Combine(GenFilePaths.SaveDataFolderPath, "Visual");
        private bool Enabled => GenCommandLine.CommandLineArgPassed("mushroomVisual");
        public FieldVisualTests(Game game) { }

        public override void GameComponentUpdate()
        {
            if (!Enabled || finished || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting) return;
            try
            {
                // Error logs remain on disk; keep automatic log windows out of frames.
                foreach (var window in Find.WindowStack.Windows.ToList()) window.Close(false);
                if (!initialized)
                {
                    map = Find.CurrentMap;
                    if (map == null) return;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    DebugSettings.enableStoryteller = false;
                    foreach (var window in Find.WindowStack.Windows.ToList()) window.Close(false);
                    foreach (var pawn in map.mapPawns.AllPawnsSpawned.ToArray()) pawn.Destroy();
                    center = map.Center;
                    foreach (IntVec3 c in CellRect.CenteredOn(center, 50).ClipInsideMap(map))
                    {
                        foreach (var t in c.GetThingList(map).ToArray())
                            if (t.def.destroyable) t.Destroy(); else if (t.Spawned) t.DeSpawn();
                        map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                        map.roofGrid.SetRoof(c, null);
                        map.fogGrid.Unfog(c);
                    }
                    species = DefDatabase<ThingDef>.AllDefs.Where(d => d.thingClass == typeof(Plant_Mushroom)
                        && d.plant?.harvestedThingDef != null && d.defName != "RMush_PlantEnoki").OrderBy(d => d.defName).ToArray();
                    if (species.Length != 46) throw new Exception("Expected 46 wild species; actual=" + species.Length);
                    Directory.CreateDirectory(Output);
                    report.AppendLine("Native RimWorld render, 1600x1000; isolated Core profile; no postprocessing.");
                    report.AppendLine("Growth 25/65/100 percent (sown=false), raw stack 25/50/75. Enoki uses wild variant.");
                    report.AppendLine("Paused fixed fixtures measure appearance, not natural co-occurrence or growth rate.");
                    initialized = true;
                    PrepareShot();
                }
                if (++frames < 40 || Time.realtimeSinceStartup < readyAt) return;
                if (!requested)
                {
                    ScreenCapture.CaptureScreenshot(pending);
                    requested = true;
                    frames = 0;
                    readyAt = Time.realtimeSinceStartup + 3f;
                    captureDeadline = Time.realtimeSinceStartup + 20f;
                    return;
                }
                if (!File.Exists(pending) || new FileInfo(pending).Length < 1024)
                {
                    if (Time.realtimeSinceStartup < captureDeadline) return;
                    throw new Exception("Native screenshot was not written: " + pending);
                }
                report.AppendLine("FRAME " + Path.GetFileName(pending) + " bytes=" + new FileInfo(pending).Length
                    + " cameraRootSize=" + Find.CameraDriver.RootSize + " cellPixels=" + Find.CameraDriver.CellSizePixels
                    + " localDayFraction=" + GenLocalDate.DayPercent(map) + " skyGlow=" + map.skyManager.CurSkyGlow);
                shot++;
                if (shot >= 10)
                {
                    File.WriteAllText(Path.Combine(Output, "visual-report.txt"), report.ToString());
                    finished = true;
                    Application.Quit(0);
                    return;
                }
                PrepareShot();
            }
            catch (Exception e)
            {
                Directory.CreateDirectory(Output);
                File.WriteAllText(Path.Combine(Output, "visual-failure.txt"), report + "\n" + e);
                Log.Error("[Mushroom visual fixture] " + e);
                finished = true;
                Application.Quit(2);
            }
        }

        private void PrepareShot()
        {
            foreach (var t in fixtures.Where(t => !t.Destroyed)) t.Destroy();
            fixtures.Clear(); labels.Clear();
            bool night = shot >= 8;
            float targetTime = night ? 0.0f : 0.5f;
            int delta = (int)(((targetTime - GenLocalDate.DayPercent(map) + 1f) % 1f) * 60000f);
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + delta);
            map.weatherManager.TransitionTo(WeatherDefOf.Clear);
            map.weatherManager.curWeatherAge = 100000;
            map.skyManager.SkyManagerUpdate();
            if (!night)
            {
                int page = shot / 2;
                for (int i = page * 12; i < Math.Min(species.Length, (page + 1) * 12); i++)
                {
                    int slot = i % 12;
                    IntVec3 start = center + new IntVec3(-20 + slot / 6 * 23, 0, 13 - slot % 6 * 5);
                    float[] growth = { 0.25f, 0.65f, 1f };
                    for (int j = 0; j < 3; j++) SpawnPlant(species[i], start + new IntVec3(j * 3, 0, 0), growth[j]);
                    for (int j = 0; j < 3; j++)
                    {
                        Thing raw = ThingMaker.MakeThing(species[i].plant.harvestedThingDef);
                        raw.stackCount = (j + 1) * 25;
                        fixtures.Add(GenSpawn.Spawn(raw, start + new IntVec3((j + 3) * 3, 0, 0), map));
                    }
                    labels.Add(new KeyValuePair<IntVec3, string>(start + new IntVec3(7, 0, 2), species[i].label));
                    if (shot % 2 == 0) report.AppendLine("PAGE " + (page + 1) + " row=" + slot + " " + species[i].defName);
                }
                pending = Path.Combine(Output, "growth-stack-page-" + (page + 1) + (shot % 2 == 0 ? "-normal.png" : "-small.png"));
            }
            else
            {
                string[] ids = { "RMush_PlantGhostFungus", "RMush_PlantChlorophos", "RMush_PlantButton", "Plant_Glowstool" };
                for (int i = 0; i < ids.Length; i++)
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(ids[i]);
                    if (def == null) { report.AppendLine("Unavailable optional reference " + ids[i]); continue; }
                    IntVec3 pos = center + new IntVec3(-15 + i * 10, 0, 0);
                    SpawnPlant(def, pos, 1f);
                    labels.Add(new KeyValuePair<IntVec3, string>(pos + new IntVec3(0, 0, 4), def.label));
                }
                map.glowGrid.GlowGridUpdate_First();
                foreach (var t in fixtures)
                    report.AppendLine("NIGHT " + t.def.defName + " own=" + map.glowGrid.GroundGlowAt(t.Position)
                        + " adjacent=" + map.glowGrid.GroundGlowAt(t.Position + IntVec3.East)
                        + " distance3=" + map.glowGrid.GroundGlowAt(t.Position + IntVec3.East * 3));
                pending = Path.Combine(Output, shot == 8 ? "night-normal.png" : "night-small.png");
            }
            map.mapDrawer.RegenerateEverythingNow();
            Find.CameraDriver.JumpToCurrentMapLoc(center);
            Find.CameraDriver.SetRootSize(shot % 2 == 0 ? 18f : 32f);
            frames = 0; requested = false; readyAt = Time.realtimeSinceStartup + 2f;
        }

        private void SpawnPlant(ThingDef def, IntVec3 pos, float growth)
        {
            Plant plant = (Plant)GenSpawn.Spawn(def, pos, map);
            plant.sown = false; plant.Growth = growth;
            fixtures.Add(plant);
        }

        public override void GameComponentOnGUI()
        {
            if (!Enabled || !initialized || finished || map != Find.CurrentMap) return;
            GenMapUI.DrawThingLabel(new Vector2(UI.screenWidth / 2f, 40f), shot < 8
                ? "MORE MUSHROOMS — native frame — growth 25% / 65% / 100%   |   boxes 25 / 50 / 75"
                : "MORE MUSHROOMS — midnight — ghost fungus / chlorophos / button control / vanilla glowstool", Color.white);
            foreach (var label in labels) GenMapUI.DrawThingLabel(GenMapUI.LabelDrawPosFor(label.Key), label.Value, Color.white);
        }
    }
}
