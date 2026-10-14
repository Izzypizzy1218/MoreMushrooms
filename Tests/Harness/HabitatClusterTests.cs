using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimMushrooms;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimMushroomsTests
{
    // Opt-in, isolated native QA. Never distributed with the playable mod.
    public sealed class HabitatClusterTests : GameComponent
    {
        private static HabitatClusterTests active;
        private static bool hooksInstalled;
        private readonly List<string> errors = new List<string>();
        private readonly Dictionary<int, List<PlantState>> generationBefore = new Dictionary<int, List<PlantState>>();
        private readonly Dictionary<int, List<PlantState>> generationAfter = new Dictionary<int, List<PlantState>>();
        private int phase, mapId = -1, checks, nativeSpawnCount, redirectedCount, frames;
        private bool started, finished, captureRequested;
        private float captureAfter, captureDeadline;
        private string output, pendingCapture;
        private Map map;
        private List<string> savedPlants = new List<string>();
        private List<string> savedTickets = new List<string>();
        private int savedRandom, savedRing, savedBudget;
        private readonly List<KeyValuePair<IntVec3, string>> labels = new List<KeyValuePair<IntVec3, string>>();
        private const string SaveName = "MoreMushrooms-HabitatCluster-080";
        private const string Seed = "MoreMushrooms-Habitat-080-A";
        private bool Enabled => GenCommandLine.CommandLineArgPassed("mushroomHabitatCluster");
        private string Report => Path.Combine(output ?? Path.Combine(GenFilePaths.SaveDataFolderPath, "HabitatCluster"), "habitat-cluster-report.txt");

        public HabitatClusterTests(Game game)
        {
            if (!Enabled) return;
            active = this;
            Application.logMessageReceived += NativeLog;
            if (hooksInstalled) return;
            // Force the production static initializer to install its native hooks first.
            var unused = WildMushroomEcology.Settings;
            var harmony = new Harmony("izzypizzy.rimmushrooms.tests.habitat-cluster");
            harmony.Patch(AccessTools.Method(typeof(MapComponent_WildMushrooms), nameof(MapComponent_WildMushrooms.MapGenerated)),
                new HarmonyMethod(typeof(HabitatClusterTests), nameof(BeforeGenerated)) { priority = Priority.First },
                new HarmonyMethod(typeof(HabitatClusterTests), nameof(AfterGenerated)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(WildPlantSpawner), nameof(WildPlantSpawner.SpawnPlant)),
                new HarmonyMethod(typeof(HabitatClusterTests), nameof(BeforeNativeSpawn)) { priority = Priority.First },
                new HarmonyMethod(typeof(HabitatClusterTests), nameof(AfterNativeSpawn)) { priority = Priority.Last });
            hooksInstalled = true;
        }

        private void NativeLog(string message, string trace, LogType type)
        {
            if (finished || (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
            lock (errors) errors.Add(type + ": " + message + "\n" + trace);
        }

        public static void BeforeGenerated(Map ___map)
        {
            if (active?.Enabled == true) active.generationBefore[___map.uniqueID] = Snapshot(___map, false);
        }
        public static void AfterGenerated(Map ___map)
        {
            if (active?.Enabled == true) active.generationAfter[___map.uniqueID] = Snapshot(___map, false);
        }
        public static void BeforeNativeSpawn(ThingDef plant, Map map, IntVec3 cell, out SpawnAudit __state)
        {
            __state = active?.Enabled == true && WildMushroomEcology.IsMushroom(plant)
                ? new SpawnAudit { map = map, requested = cell, previous = Snapshot(map, true) } : null;
        }
        public static void AfterNativeSpawn(Plant __result, SpawnAudit __state)
        {
            if (__state == null || active == null) return;
            var after = Snapshot(__state.map, true);
            if (__result == null || !__result.Spawned || after.Count != __state.previous.Count + 1
                || !SameOldPlants(__state.previous, after, true))
                active.errors.Add("Native SpawnPlant did not add exactly one mushroom while preserving all existing IDs/positions/state.");
            active.nativeSpawnCount++;
            if (__result != null && __result.Position != __state.requested) active.redirectedCount++;
        }
        public sealed class SpawnAudit
        {
            public Map map;
            public IntVec3 requested;
            public List<PlantState> previous;
        }

        public override void GameComponentUpdate()
        {
            if (!Enabled || finished || Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting || Find.CurrentMap == null) return;
            try
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                if (!started) Initialize();
                if (output == null) output = Path.Combine(GenFilePaths.SaveDataFolderPath, "HabitatCluster");
                if (map == null) map = Find.Maps.Single(m => m.uniqueID == mapId);
                CheckNativeErrors();
                if (phase == 1)
                {
                    if (!FinishCapture()) return;
                    RunFixtures();
                    PrepareCapture("02-native-spawner-host-fixture.png", map.Center, 20f);
                    phase = 2;
                }
                else if (phase == 2)
                {
                    if (!FinishCapture()) return;
                    PrepareSave();
                    phase = 90;
                    GameDataSaveLoader.SaveGame(SaveName);
                    string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "Saves", SaveName + ".rws");
                    Check(File.Exists(path) && new FileInfo(path).Length > 1024, "native isolated save exists");
                    phase = 91;
                }
                else if (phase == 91)
                {
                    phase = 92;
                    GameDataSaveLoader.LoadGame(SaveName);
                }
                else if (phase == 90)
                {
                    VerifyLoaded();
                    CheckNativeErrors();
                    Append("RESULT PASS checks=" + checks + " nativeMushroomSpawnCalls=" + nativeSpawnCount + " redirectedCalls=" + redirectedCount);
                    File.WriteAllText(Path.Combine(output, "ready.flag"), "PASS\n");
                    finished = true;
                    Application.logMessageReceived -= NativeLog;
                    Application.Quit(0);
                }
            }
            catch (Exception error)
            {
                Directory.CreateDirectory(output ?? Path.Combine(GenFilePaths.SaveDataFolderPath, "HabitatCluster"));
                Append("RESULT FAIL " + error);
                finished = true;
                Application.logMessageReceived -= NativeLog;
                Log.Error("[HabitatClusterTests] " + error);
                Application.Quit(1);
            }
        }

        private void Initialize()
        {
            string specified;
            if (!GenCommandLine.TryGetCommandLineArg("savedatafolder", out specified)) throw new Exception("Explicit isolated savedatafolder required.");
            string profile = Path.GetFullPath(GenFilePaths.SaveDataFolderPath).TrimEnd('\\', '/');
            if (!string.Equals(profile, Path.GetFullPath(specified).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                || profile.Replace('/', '\\').IndexOf("\\Tests\\Runtime\\", StringComparison.OrdinalIgnoreCase) < 0
                || Path.GetFileName(profile).IndexOf("HabitatCluster", StringComparison.OrdinalIgnoreCase) < 0)
                throw new Exception("Only Tests/Runtime/*HabitatCluster* profiles are allowed.");
            if (GenCommandLine.CommandLineArgPassed("mushroomSmoke")) throw new Exception("Do not combine smoke and habitat flags.");
            output = Path.Combine(profile, "HabitatCluster");
            Directory.CreateDirectory(output);
            File.WriteAllText(Report, "v0.8.0 native habitat and count-preserving small-clump QA\n"
                + "One untouched native TemperateForest 250x250 map, followed by explicitly controlled spawner fixtures.\n"
                + "No past saves/logs, release archives, or player profile are used. No natural count is forced.\n"
                + "Whole-map before/after is measured immediately around the production MapGenerated clumping pass.\n"
                + "Native CheckSpawnWildPlantAt fixtures temporarily use a single selectable species; they test execution, not natural frequency.\n"
                + "Native SpawnPlant calls must add exactly one plant, preserving every previous mushroom's ID/state/position.\n", Encoding.UTF8);
            var language = LanguageDatabase.activeLanguage;
            var translationErrors = language.loadErrors.Concat(language.defInjections.SelectMany(package => package.loadErrors)).ToArray();
            File.WriteAllText(Path.Combine(output, "translation-diagnostics.txt"), "Active language=" + language.FriendlyNameEnglish
                + " errors=" + translationErrors.Length + "\n" + string.Join("\n", translationErrors) + "\n", Encoding.UTF8);
            Append("TRANSLATION active=" + language.FriendlyNameEnglish + " errors=" + translationErrors.Length
                + " RMushReferences=" + translationErrors.Count(e => e.IndexOf("RMush", StringComparison.OrdinalIgnoreCase) >= 0)
                + "; exact native messages are in translation-diagnostics.txt; attribution is not assumed.");
            started = true;
            DebugSettings.enableStoryteller = false;
            DebugSettings.fastEcology = false;
            var candidates = Enumerable.Range(0, Find.WorldGrid.TilesCount).Select(i => new PlanetTile(i))
                .Where(t => Find.WorldGrid[t].PrimaryBiome == BiomeDefOf.TemperateForest && !Find.WorldGrid[t].WaterCovered
                    && Find.WorldGrid[t].Mutators.Count == 0 && !Find.WorldObjects.AnyMapParentAt(t)).ToArray();
            Check(candidates.Length > 0, "natural single-biome TemperateForest tile available");
            PlanetTile tile = candidates.OrderBy(t => Find.WorldGrid[t].hilliness == Hilliness.Flat ? 0 : 1).ThenBy(t => t.tileId).First();
            var parent = (MapParent)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
            parent.Tile = tile;
            parent.SetFaction(Faction.OfPlayer);
            Find.WorldObjects.Add(parent);
            string worldSeed = Find.World.info.seedString;
            try
            {
                Find.World.info.seedString = Seed;
                map = MapGenerator.GenerateMap(new IntVec3(250, 1, 250), parent, MapGeneratorDefOf.Base_Player);
            }
            finally { Find.World.info.seedString = worldSeed; }
            mapId = map.uniqueID;
            Current.Game.CurrentMap = map;
            Check(map.Biome == BiomeDefOf.TemperateForest && map.Biomes.Count() == 1 && map.TileInfo.Mutators.Count == 0,
                "natural map has one supported biome and no mutators");
            VerifyNaturalGeneration(worldSeed, tile);
            // Visual preparation happens only after the untouched before/after census.
            foreach (var pawn in map.listerThings.AllThings.OfType<Pawn>().ToArray()) pawn.DeSpawn();
            foreach (var c in map.AllCells) map.fogGrid.Unfog(c);
            foreach (var pawn in map.listerThings.AllThings.OfType<Pawn>().ToArray()) pawn.DeSpawn();
            int delta = (int)(((0.5f - GenLocalDate.DayPercent(map) + 1f) % 1f) * GenDate.TicksPerDay);
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + delta);
            map.weatherManager.TransitionTo(WeatherDefOf.Clear);
            map.weatherManager.curWeatherAge = 100000;
            map.skyManager.SkyManagerUpdate();
            PrepareCapture("01-native-whole-map-clumps.png", map.Center, 140f);
            phase = 1;
        }

        private void VerifyNaturalGeneration(string worldSeed, PlanetTile tile)
        {
            List<PlantState> before, after;
            Check(generationBefore.TryGetValue(mapId, out before) && generationAfter.TryGetValue(mapId, out after),
                "test hooks observed actual native MapGenerated pre/post");
            before = generationBefore[mapId]; after = generationAfter[mapId];
            Check(before.Count == after.Count && SameOldPlants(before, after, false),
                "initial clumping preserves all map plant IDs/species/growth/age/health/sown/regrowth flags and total count");
            var beforeMushrooms = before.Where(p => p.mushroom).ToList();
            var afterMushrooms = after.Where(p => p.mushroom).ToList();
            var afterById = after.ToDictionary(p => p.id);
            Check(beforeMushrooms.Count > 0, "untouched native TemperateForest has mushrooms (no expected count forced)");
            Check(before.Where(p => !p.mushroom || p.sown).All(p => afterById[p.id].position == p.position),
                "native generation clumping leaves all non-mushrooms and cultivated plants in place");
            var ecology = map.GetComponent<MapComponent_WildMushrooms>();
            int distinctMoved = beforeMushrooms.Count(p => afterById[p.id].position != p.position);
            Check(ecology.InitialClusterCandidates <= beforeMushrooms.Count && distinctMoved <= ecology.InitialClusterCandidates
                && ecology.InitialClusterMoved >= distinctMoved,
                "initial candidate count and movement events reconcile with distinct moved native IDs");
            Append("NATIVE MAP geographySeed=" + worldSeed + " mapSeed=" + Seed + " tile=" + tile + " mapId=" + mapId
                + " gameTick=" + Find.TickManager.TicksGame + " absTick=" + Find.TickManager.TicksAbs
                + " season=" + GenLocalDate.Season(map) + " before=" + beforeMushrooms.Count + " after=" + afterMushrooms.Count
                + " allPlants=" + before.Count + " candidates=" + ecology.InitialClusterCandidates + " moveEvents=" + ecology.InitialClusterMoved + " distinctMoved=" + distinctMoved
                + " groups=" + ecology.InitialClusterGroups + " annualTemp=" + map.TileInfo.temperature + " outdoorTemp=" + map.mapTemperature.OutdoorTemp);
            var csv = new StringBuilder("phase,thingId,plantDef,x,z,growth,age,health,sown\n");
            foreach (var pair in new[] { new KeyValuePair<string, List<PlantState>>("before", beforeMushrooms), new KeyValuePair<string, List<PlantState>>("after", afterMushrooms) })
                foreach (var p in pair.Value) csv.AppendLine(Csv(pair.Key, p.id, p.def, p.position.x, p.position.z, p.growth, p.age, p.health, p.sown));
            File.WriteAllText(Path.Combine(output, "initial-plants.csv"), csv.ToString(), Encoding.UTF8);
            File.WriteAllText(Path.Combine(output, "initial-species.csv"), "plantDef,before,after\n" + string.Join("\n", beforeMushrooms.Select(p => p.def).Union(afterMushrooms.Select(p => p.def)).OrderBy(d => d)
                .Select(d => Csv(d, beforeMushrooms.Count(p => p.def == d), afterMushrooms.Count(p => p.def == d)))) + "\n", Encoding.UTF8);
            foreach (var plant in Mushrooms(map)) Check(WildMushroomEcology.MeetsTreeHabitat(plant.def, plant.Position, map), "initial actual native tree habitat " + plant.ThingID);
            WriteClusters("initial", false);
        }

        private void RunFixtures()
        {
            labels.Clear();
            ClearPatch(map.Center, 75);
            map.Biome.constantOutdoorTemperature = 21f;
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            foreach (var room in CellRect.CenteredOn(map.Center, 75, 75).Cells.Select(c => c.GetRoom(map)).Where(r => r != null).Distinct()) room.Temperature = 21f;
            Check(Mathf.Abs(map.Center.GetTemperature(map) - 21f) < .01f, "controlled fixture actual room temperature is 21C");
            string[] ids = { "Matsutake", "Chaga", "Porcini", "BlackTrumpet", "FlyAgaric", "PantherCap" };
            string[] hosts = { "pine", "birch", "any", "any", "any", "any" };
            for (int i = 0; i < ids.Length; i++) VerifyHost(Def(ids[i]), hosts[i]);
            VerifyRejectedCells();
            VerifyClumpJoins();
            VerifyInitialArrangement();
            VerifyRegrowthAnchor();
            BuildHostVisual();
            WriteClusters("fixture", true);
            Append("FIXTURE nativeSpawnCalls=" + nativeSpawnCount + " redirectedCalls=" + redirectedCount
                + "; no manual mushroom GenSpawn; direct fixture spawning is restricted to host trees, obstructions and one marked cultivated control.");
        }

        private void VerifyHost(ThingDef def, string expected)
        {
            ClearLocal();
            IntVec3 cell = map.Center;
            var profile = def.GetModExtension<MushroomEcologyExtension>();
            Check(profile.habitatTree == expected && profile.habitatTreeMinGrowth == .5f && profile.habitatTreeRadius == 2,
                "declared tree rule " + def.defName);
            using (new SingleSpeciesScope(map, def))
            {
                Check(!WildMushroomEcology.MeetsTreeHabitat(def, cell, map) && Weight(def, cell) == 0f && !NativeTry(cell), "native choice rejects missing host " + def.defName);
                ThingDef hostDef = ThingDef.Named(expected == "birch" ? "Plant_TreeBirch" : "Plant_TreePine");
                Plant tree = Host(hostDef, cell + IntVec3.East * 2, .49f);
                Check(!WildMushroomEcology.MeetsTreeHabitat(def, cell, map) && Weight(def, cell) == 0f && !NativeTry(cell), "native choice rejects growth .49 host " + def.defName);
                tree.Growth = .5f;
                Check(WildMushroomEcology.MeetsTreeHabitat(def, cell, map) && Weight(def, cell) > 0f, "native choice accepts growth .50 at distance2 " + def.defName);
                Plant_Mushroom plant = SpawnNative(def, cell);
                Check(WildMushroomEcology.MeetsTreeHabitat(def, plant.Position, map), "actual selected mushroom respects host after relocation " + def.defName);
                plant.Destroy(); tree.Destroy();
                tree = Host(hostDef, cell + new IntVec3(2, 0, 2), 1f);
                Check(WildMushroomEcology.MeetsTreeHabitat(def, cell, map) && Weight(def, cell) > 0f, "diagonal Chebyshev distance2 accepted " + def.defName);
                tree.Destroy();
                tree = Host(hostDef, cell + IntVec3.East * 3, 1f);
                Check(!WildMushroomEcology.MeetsTreeHabitat(def, cell, map) && Weight(def, cell) == 0f && !NativeTry(cell), "distance3 rejected " + def.defName);
                tree.Destroy();
                ThingDef wrongDef = ThingDef.Named(expected == "any" ? "Plant_Grass" : expected == "pine" ? "Plant_TreeBirch" : "Plant_TreePine");
                tree = Host(wrongDef, cell + IntVec3.East, 1f);
                Check(!WildMushroomEcology.MeetsTreeHabitat(def, cell, map) && Weight(def, cell) == 0f && !NativeTry(cell), "wrong species/non-tree rejected " + def.defName);
                tree.Destroy();
                if (expected == "any")
                {
                    tree = Host(ThingDef.Named("Plant_TreeBirch"), cell + IntVec3.West, .5f);
                    Check(WildMushroomEcology.MeetsTreeHabitat(def, cell, map) && Weight(def, cell) > 0f, "any-tree rule accepts second native tree species " + def.defName);
                    tree.Destroy();
                }
            }
        }

        private void VerifyRejectedCells()
        {
            ClearLocal();
            ThingDef def = Def("Matsutake"); IntVec3 cell = map.Center;
            Host(ThingDef.Named("Plant_TreePine"), cell + IntVec3.East * 2, 1f);
            var ecology = map.GetComponent<MapComponent_WildMushrooms>();
            using (new SingleSpeciesScope(map, def))
            {
                map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                Check(!ecology.IsEligibleCell(cell, def) && Weight(def, cell) == 0f && !NativeTry(cell), "native selection rejects roof despite correct host");
                map.roofGrid.SetRoof(cell, null);
                map.terrainGrid.SetTerrain(cell, TerrainDef.Named("Concrete"));
                Check(!ecology.IsEligibleCell(cell, def) && !NativeTry(cell), "native selection rejects zero fertility/floor despite host");
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
                var zone = new Zone_Growing(map.zoneManager); map.zoneManager.RegisterZone(zone); zone.AddCell(cell);
                Check(!ecology.IsEligibleCell(cell, def) && Weight(def, cell) == 0f && !NativeTry(cell), "native selection rejects player growing zone");
                zone.Delete(false);
                Thing basin = GenSpawn.Spawn(ThingDef.Named("HydroponicsBasin"), cell, map);
                Check(!ecology.IsEligibleCell(cell, def) && !NativeTry(cell), "native selection rejects hydroponics footprint");
                basin.Destroy();
                Check(ecology.IsEligibleCell(cell, def) && Weight(def, cell) > 0f, "correct host and legal outdoor soil restored");
            }
        }

        private void VerifyClumpJoins()
        {
            ClearLocal();
            ThingDef def = Def("Button"); var ecology = map.GetComponent<MapComponent_WildMushrooms>();
            Check(ecology.Settings.smallClusterMinCount == 2 && ecology.Settings.smallClusterMaxCount == 5, "small-clump configured bounds2..5");
            var sown = (Plant_Mushroom)GenSpawn.Spawn(def, map.Center + new IntVec3(-10, 0, -10), map);
            sown.sown = true; sown.Growth = .65f;
            IntVec3 sownCell = sown.Position;
            using (new SingleSpeciesScope(map, def))
            {
                for (int i = 0; i < 10; i++)
                {
                    // A previous valid native relocation can occupy any old
                    // request/fallback cell. Start every positive draw on a
                    // currently legal empty cell; negative gate tests stay exact.
                    IntVec3 requestCenter = map.Center + new IntVec3(-3, 0, -7);
                    IntVec3 requested = CellRect.CenteredOn(requestCenter, 17, 13).Cells
                        .OrderBy(c => c.DistanceToSquared(requestCenter))
                        .First(c => ecology.IsEligibleCell(c, def));
                    var before = Snapshot(map, true);
                    var plant = SpawnNative(def, requested);
                    Check(SameOldPlants(before, Snapshot(map, true), true), "ongoing native spawn moves only its new plant index=" + i);
                    var group = ecology.ConnectedWildCluster(def, plant.Position, 1000);
                    Check(group.Count >= 1 && group.Count <= 5 && group.All(p => !p.sown && p.def == def), "ongoing same-species connected component is bounded index=" + i);
                    if (group.Count >= 3) Check(!Collinear(group.Select(p => p.Position).ToList()), "ongoing 3..5 cluster is irregular/non-collinear index=" + i);
                    Check(group.All(p => Chebyshev(p.Position, plant.Position) <= ecology.Settings.smallClusterCellRadius * 2), "small cluster remains compact index=" + i);
                    Check(sown.Spawned && sown.Position == sownCell && sown.Growth == .65f && sown.sown, "cultivated control is never moved index=" + i);
                }
            }
            var groups = Components(Mushrooms(map).Where(p => p.def == def && !p.sown).ToList());
            Check(groups.Any(g => g.Count >= 3 && g.Count <= 5), "actual native ongoing path formed at least one irregular3..5 clump");
            sown.Destroy();
        }

        private void VerifyRegrowthAnchor()
        {
            ClearLocal();
            ThingDef def = Def("Matsutake"); var profile = def.GetModExtension<MushroomEcologyExtension>();
            var ecology = map.GetComponent<MapComponent_WildMushrooms>();
            float oldChance = profile.regrowthChance;
            try
            {
                // Species has no production regrowth. This scoped fixture isolates
                // habitat validation of the existing ticket path, not its balance.
                profile.regrowthChance = 1f;
                using (new SingleSpeciesScope(map, def))
                {
                    Plant host = Host(ThingDef.Named("Plant_TreePine"), map.Center + IntVec3.East * 2, 1f);
                    Plant_Mushroom source = SpawnNative(def, map.Center); source.Growth = 1f;
                    IntVec3 position = source.Position;
                    Check(ecology.TryQueueRegrowth(source), "explicit temporary Matsutake regrowth fixture queued through normal chance path");
                    var ticket = ecology.PendingRegrowth.Single(t => t.sourceId == source.ThingID);
                    source.Destroy(); host.Destroy();
                    Check(!ecology.IsPreservedRegrowthHabitat(position, def), "removing host invalidates saved mycelium habitat");
                    ecology.ProcessRegrowth(ticket.dueTick);
                    Check(!ecology.PendingRegrowth.Any(t => t == ticket) && position.GetPlant(map) == null,
                        "host-removed ticket is discarded without spawning");
                }
            }
            finally { profile.regrowthChance = oldChance; }
            Check(profile.regrowthChance == oldChance, "temporary regrowth balance restored");
        }

        private void VerifyInitialArrangement()
        {
            ClearLocal();
            ThingDef def = Def("Button"); var ecology = map.GetComponent<MapComponent_WildMushrooms>();
            int oldRadius = ecology.Settings.smallClusterSearchRadius;
            var candidates = new List<Plant_Mushroom>();
            try
            {
                // Keep these native-selected fixture candidates separate initially.
                // This is an explicit initial-pass fixture, not natural abundance.
                ecology.Settings.smallClusterSearchRadius = 1;
                using (new SingleSpeciesScope(map, def))
                    foreach (var offset in new[] { new IntVec3(-10, 0, -10), new IntVec3(10, 0, -10), new IntVec3(-10, 0, 10), new IntVec3(10, 0, 10) })
                        candidates.Add(SpawnNative(def, map.Center + offset));
            }
            finally { ecology.Settings.smallClusterSearchRadius = oldRadius; }
            Check(candidates.All(p => ecology.ConnectedWildCluster(def, p.Position, 6).Count == 1), "controlled initial-pass candidates start as four separated native singletons");
            var before = Snapshot(map, true);
            foreach (var plant in candidates) ecology.RecordInitialNativePlant(plant);
            ecology.ArrangeInitialNativeClusters();
            var after = Snapshot(map, true);
            var candidateIds = new HashSet<string>(candidates.Select(p => p.ThingID));
            Check(before.Count == after.Count && SameOldPlants(before, after, false), "controlled initial pass preserves native IDs/count/growth/age/flags");
            Check(SameOldPlants(before.Where(p => !candidateIds.Contains(p.id)).ToList(), after, true), "controlled initial pass never moves old noncandidate plants");
            Check(ecology.InitialClusterCandidates == 4 && ecology.InitialClusterMoved > 0 && ecology.InitialClusterGroups > 0,
                "actual initial arrangement moves recorded native candidates and forms groups; complete no-op would fail");
            foreach (var plant in candidates)
            {
                var group = ecology.ConnectedWildCluster(def, plant.Position, 6);
                Check(group.Count >= 2 && group.Count <= 5 && (group.Count < 3 || !Collinear(group.Select(p => p.Position).ToList())),
                    "controlled initial candidate ends in irregular bounded2..5 clump " + plant.ThingID);
            }
            Append("INITIAL-PASS FIXTURE temporary searchRadius1 creates separated native candidates, restored before arrangement. Explicit RecordInitialNativePlant is test setup; no extra plants added by Arrange.");
        }

        private void BuildHostVisual()
        {
            ClearLocal(); labels.Clear();
            foreach (var pair in new[] { new KeyValuePair<string, string>("Matsutake", "Plant_TreePine"), new KeyValuePair<string, string>("Chaga", "Plant_TreeBirch") })
            {
                IntVec3 treeCell = map.Center + new IntVec3(pair.Key == "Matsutake" ? -9 : 9, 0, 3);
                Host(ThingDef.Named(pair.Value), treeCell, 1f);
                ThingDef def = Def(pair.Key);
                using (new SingleSpeciesScope(map, def))
                    for (int i = 0; i < 5; i++)
                    {
                        IntVec3 c = CellRect.CenteredOn(treeCell, 5, 5).Cells.First(p => p != treeCell && p.GetPlant(map) == null
                            && map.GetComponent<MapComponent_WildMushrooms>().IsEligibleCell(p, def));
                        SpawnNative(def, c);
                    }
                labels.Add(new KeyValuePair<IntVec3, string>(treeCell + new IntVec3(0, 0, 5), pair.Key == "Matsutake" ? "Pine + Matsutake (native spawner)" : "Birch + Chaga (native spawner)"));
            }
            ThingDef button = Def("Button");
            using (new SingleSpeciesScope(map, button))
                for (int i = 0; i < 5; i++)
                {
                    IntVec3 requested = CellRect.CenteredOn(map.Center + new IntVec3(0, 0, -8), 13, 7).Cells
                        .OrderBy(c => c.DistanceToSquared(map.Center + new IntVec3(0, 0, -8)))
                        .First(c => map.GetComponent<MapComponent_WildMushrooms>().IsEligibleCell(c, button));
                    SpawnNative(button, requested);
                }
            labels.Add(new KeyValuePair<IntVec3, string>(map.Center + new IntVec3(0, 0, -12), "Button: 5 new native wild spawns; no added supply"));
        }

        private void PrepareSave()
        {
            var ecology = map.GetComponent<MapComponent_WildMushrooms>();
            var button = Def("Button");
            using (new SingleSpeciesScope(map, button))
            {
                var source = SpawnNative(button, map.Center + new IntVec3(16, 0, 16)); source.Growth = 1f;
                Check(ecology.TryQueueRegrowth(source, bypassChance: true), "controlled save fixture queues actual native-selected Button source (chance bypass explicitly scoped to test)");
                var ticket = ecology.PendingRegrowth.Single(t => t.sourceId == source.ThingID);
                IntVec3 position = source.Position;
                source.Destroy();
                // Advance the paused fixture clock to the real due time. Only the
                // ecology call is exercised; no full-world longitudinal claim.
                Find.TickManager.DebugSetTicksGame(ticket.dueTick);
                ecology.ProcessRegrowth(Find.TickManager.TicksGame);
                var regenerated = position.GetPlant(map) as Plant_Mushroom;
                Check(regenerated != null && regenerated.def == button && regenerated.WildRegrowthGeneration == 1 && ecology.ExtraSpawnedToday > 0,
                    "save fixture contains actual generation1 plant and nonzero same-day extra budget");
                var pendingSource = SpawnNative(button, map.Center + new IntVec3(16, 0, 12)); pendingSource.Growth = 1f;
                Check(ecology.TryQueueRegrowth(pendingSource, bypassChance: true) && pendingSource.WildRegrowthSpent,
                    "save fixture retains actual spent source and nonempty future mycelium ticket");
            }
            Check(ecology.PendingRegrowthCount > 0 && Mushrooms(map).Any(p => p.WildRegrowthGeneration == 1)
                && Mushrooms(map).Any(p => p.WildRegrowthSpent) && ecology.ExtraSpawnedToday > 0,
                "native save/load persistence fixture has all nondefault ecology values");
            Append("SAVE FIXTURE uses actual native selection, normal queue validation with explicit chance bypass, coherent paused clock advance, and ProcessRegrowth. No full-world time simulation or natural regrowth probability is claimed.");
            savedPlants = Snapshot(map, true).Select(p => p.Encode()).OrderBy(s => s, StringComparer.Ordinal).ToList();
            savedTickets = ecology.PendingRegrowth.Select(Ticket).OrderBy(s => s, StringComparer.Ordinal).ToList();
            savedRandom = ecology.RandomState; savedRing = ecology.NextRingTick; savedBudget = ecology.ExtraSpawnedToday;
            Append("SAVE snapshot mushrooms=" + savedPlants.Count + " pending=" + savedTickets.Count + " random=" + savedRandom + " ring=" + savedRing + " budget=" + savedBudget);
        }

        private void VerifyLoaded()
        {
            var now = Snapshot(map, true).Select(p => p.Encode()).OrderBy(s => s, StringComparer.Ordinal).ToList();
            Check(savedPlants.SequenceEqual(now), "native save/load preserves every mushroom ID/position/growth/age/health/sown/regrowth flag exactly");
            var ecology = map.GetComponent<MapComponent_WildMushrooms>();
            Check(ecology.PendingRegrowth.Select(Ticket).OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(savedTickets)
                && ecology.RandomState == savedRandom && ecology.NextRingTick == savedRing && ecology.ExtraSpawnedToday == savedBudget,
                "native save/load preserves ecology tickets/random/schedule/budget");
            Check(ecology.InitialClusterCandidates == 0 && ecology.InitialClusterMoved == 0 && ecology.InitialClusterGroups == 0,
                "load does not rerun initial generation clumping; unsaved diagnostic counters reset");
            var before = Snapshot(map, true);
            ecology.ArrangeInitialNativeClusters();
            Check(SameOldPlants(before, Snapshot(map, true), true), "empty initial candidate set cannot rearrange loaded existing plants");
            WriteClusters("loaded", true);
        }

        private Plant_Mushroom SpawnNative(ThingDef def, IntVec3 cell)
        {
            var before = new HashSet<Plant_Mushroom>(Mushrooms(map));
            for (int attempt = 0; attempt < 128; attempt++)
            {
                if (!NativeTry(cell)) continue;
                var added = Mushrooms(map).Where(p => !before.Contains(p)).ToArray();
                Check(added.Length == 1 && added[0].def == def && !added[0].sown,
                    "actual CheckSpawnWildPlantAt -> SpawnPlant adds exactly one " + def.defName + " attempts=" + (attempt + 1));
                return added[0];
            }
            throw new Exception("No native spawn in128 attempts: " + def.defName + " cell=" + cell + " weight=" + Weight(def, cell)
                + " eligible=" + map.GetComponent<MapComponent_WildMushrooms>().IsEligibleCell(cell, def) + " temp=" + cell.GetTemperature(map));
        }
        private bool NativeTry(IntVec3 cell) => map.wildPlantSpawner.CheckSpawnWildPlantAt(cell, 1f, 100000f, true);
        private float Weight(ThingDef def, IntVec3 cell) => (float)AccessTools.Method(typeof(WildPlantSpawner), "PlantChoiceWeight")
            .Invoke(map.wildPlantSpawner, new object[] { def, cell, new Dictionary<ThingDef, float>(), 100000f, 1f });
        private Plant Host(ThingDef def, IntVec3 cell, float growth) { var p = (Plant)GenSpawn.Spawn(def, cell, map); p.Growth = growth; return p; }
        private static ThingDef Def(string id) => ThingDef.Named("RMush_Plant" + id);
        private static List<Plant_Mushroom> Mushrooms(Map m) => m.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant_Mushroom>().ToList();
        private void ClearLocal() => ClearPatch(map.Center, 49);
        private void ClearPatch(IntVec3 center, int width)
        {
            foreach (var cell in CellRect.CenteredOn(center, width, width).ClipInsideMap(map))
            {
                foreach (var thing in cell.GetThingList(map).ToArray())
                {
                    if (thing.Spawned) { if (thing.def.destroyable) thing.Destroy(); else thing.DeSpawn(); }
                }
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil); map.roofGrid.SetRoof(cell, null); map.fogGrid.Unfog(cell);
                map.snowGrid.SetDepth(cell, 0f);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
        }

        private void PrepareCapture(string name, IntVec3 center, float size)
        {
            foreach (var window in Find.WindowStack.Windows.ToList()) window.Close(false);
            Current.Game.CurrentMap = map;
            // Native camera height is derived from this range. A root size140
            // with the normal max60 puts the camera beyond its far clip plane.
            Find.CameraDriver.config.sizeRange = new FloatRange(11f, size > 60f ? 160f : 60f);
            Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), size);
            map.mapDrawer.RegenerateEverythingNow();
            Append("CAPTURE PREP " + name + " cameraPosition=" + Find.Camera.transform.position
                + " farClip=" + Find.Camera.farClipPlane + " orthographicSize=" + Find.Camera.orthographicSize
                + " configMax=" + Find.CameraDriver.config.sizeRange.max + " drawingMap=" + WorldRendererUtility.DrawingMap);
            pendingCapture = Path.Combine(output, name); frames = 0; captureRequested = false; captureAfter = Time.realtimeSinceStartup + 2.5f;
        }
        private bool FinishCapture()
        {
            // Capturing is the only phase that closes transient test popups.
            foreach (var window in Find.WindowStack.Windows.ToList()) window.Close(false);
            if (phase == 1) Find.CameraDriver.SetRootPosAndSize(map.Center.ToVector3Shifted(), 140f);
            if (++frames < 40 || Time.realtimeSinceStartup < captureAfter) return false;
            if (!captureRequested)
            {
                ScreenCapture.CaptureScreenshot(pendingCapture); captureRequested = true; frames = 0;
                captureAfter = Time.realtimeSinceStartup + 2.5f; captureDeadline = Time.realtimeSinceStartup + 20f; return false;
            }
            if (!File.Exists(pendingCapture) || new FileInfo(pendingCapture).Length < 1024)
            {
                if (Time.realtimeSinceStartup < captureDeadline) return false;
                throw new Exception("Native screenshot was not written: " + pendingCapture);
            }
            if (!ImageHasScene(pendingCapture))
            {
                Append("UNUSABLE SCREENSHOT " + Path.GetFileName(pendingCapture) + ": pixel quality check found no rendered scene. Excluded as visual evidence.");
                string cameraPath = Path.Combine(output, Path.GetFileNameWithoutExtension(pendingCapture) + "-native-camera.png");
                CaptureNativeCamera(cameraPath);
                Check(ImageHasScene(cameraPath), "explicit native map camera rendering contains a non-black scene");
                pendingCapture = cameraPath;
                Append("CAMERA FALLBACK uses the actual native map Camera.Render to RenderTexture; no UI, labels or screen capture are claimed, no image editing.");
            }
            Append("FRAME " + Path.GetFileName(pendingCapture) + " bytes=" + new FileInfo(pendingCapture).Length + " camera=" + Find.CameraDriver.RootSize
                + " cameraPosition=" + Find.Camera.transform.position + " farClip=" + Find.Camera.farClipPlane
                + " orthographicSize=" + Find.Camera.orthographicSize + " drawingMap=" + WorldRendererUtility.DrawingMap
                + "; pixel quality is a preliminary check; scene suitability also requires human review.");
            return true;
        }
        private static bool ImageHasScene(string path)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
            try
            {
                if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path))) return false;
                var pixels = texture.GetPixels32();
                int nonBlack = 0, minimum = 255, maximum = 0;
                var colors = new HashSet<uint>();
                foreach (var pixel in pixels)
                {
                    int brightness = Math.Max(pixel.r, Math.Max(pixel.g, pixel.b));
                    if (brightness > 20) nonBlack++;
                    minimum = Math.Min(minimum, brightness); maximum = Math.Max(maximum, brightness);
                    if (colors.Count < 64) colors.Add((uint)(pixel.r << 16 | pixel.g << 8 | pixel.b));
                }
                return nonBlack > pixels.Length / 200 && maximum - minimum > 16 && colors.Count >= 32;
            }
            finally { UnityEngine.Object.Destroy(texture); }
        }
        private static void CaptureNativeCamera(string path)
        {
            var camera = Find.Camera;
            if (camera == null) throw new Exception("Native map camera is unavailable.");
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var render = new RenderTexture(1600, 1000, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(1600, 1000, TextureFormat.RGB24, false);
            try
            {
                render.Create(); camera.targetTexture = render; camera.Render();
                RenderTexture.active = render;
                texture.ReadPixels(new Rect(0, 0, 1600, 1000), 0, 0); texture.Apply();
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(texture));
            }
            finally
            {
                camera.targetTexture = previousTarget; RenderTexture.active = previousActive;
                render.Release(); UnityEngine.Object.Destroy(render); UnityEngine.Object.Destroy(texture);
            }
        }
        public override void GameComponentOnGUI()
        {
            if (!Enabled || !started || finished || map != Find.CurrentMap || phase < 1 || phase > 2) return;
            string title = phase == 1 ? "NATIVE 250x250 MAP: yellow dots = existing wild mushrooms; initial rearrangement adds ZERO plants"
                : "CONTROLLED NATIVE SPAWNER FIXTURE: matching trees + compact same-species clumps (not natural abundance)";
            GenMapUI.DrawThingLabel(new Vector2(UI.screenWidth / 2f, 40f), title, Color.white);
            if (phase == 1)
            {
                var before = generationBefore[mapId].Count(p => p.mushroom);
                GenMapUI.DrawThingLabel(new Vector2(UI.screenWidth / 2f, 65f), "Mushrooms before=" + before + " after=" + Mushrooms(map).Count + "  |  seed=" + Seed + " tile=" + map.Tile, Color.white);
                foreach (var p in Mushrooms(map))
                {
                    Vector2 pos = GenMapUI.LabelDrawPosFor(p.Position);
                    Widgets.DrawBoxSolid(new Rect(pos.x - 2f, pos.y - 2f, 4f, 4f), Color.yellow);
                }
            }
            foreach (var label in labels) GenMapUI.DrawThingLabel(GenMapUI.LabelDrawPosFor(label.Key), label.Value, Color.white);
        }

        private void WriteClusters(string stage, bool fixture)
        {
            var clusters = Components(Mushrooms(map).Where(p => !p.sown).ToList());
            var rows = new StringBuilder("stage,plantDef,count,collinear,x,z,thingIds\n");
            foreach (var group in clusters) rows.AppendLine(Csv(stage, group[0].def.defName, group.Count, Collinear(group.Select(p => p.Position).ToList()),
                group[0].Position.x, group[0].Position.z, string.Join(";", group.Select(p => p.ThingID))));
            File.WriteAllText(Path.Combine(output, stage + "-clusters.csv"), rows.ToString(), Encoding.UTF8);
            Append("CLUSTERS " + stage + " singletons=" + clusters.Count(g => g.Count == 1) + " pairs=" + clusters.Count(g => g.Count == 2)
                + " compact3to5=" + clusters.Count(g => g.Count >= 3 && g.Count <= 5 && !Collinear(g.Select(p => p.Position).ToList()))
                + " nativeExcessOver5=" + clusters.Count(g => g.Count > 5) + " nativeLines3plus=" + clusters.Count(g => g.Count >= 3 && Collinear(g.Select(p => p.Position).ToList()))
                + (fixture ? "; fixture cases are checked individually; natural pre-existing/blocked exceptional shapes are retained" : "; unavoidable singletons and pre-existing/blocked native shapes are diagnostics, not extra spawning"));
        }
        private static List<List<Plant_Mushroom>> Components(List<Plant_Mushroom> plants)
        {
            var remaining = new HashSet<Plant_Mushroom>(plants); var result = new List<List<Plant_Mushroom>>();
            while (remaining.Count > 0)
            {
                var first = remaining.First(); remaining.Remove(first); var group = new List<Plant_Mushroom> { first };
                for (int i = 0; i < group.Count; i++) foreach (var p in remaining.Where(p => p.def == first.def && Chebyshev(p.Position, group[i].Position) == 1).ToArray()) { group.Add(p); remaining.Remove(p); }
                result.Add(group);
            }
            return result;
        }
        private static int Chebyshev(IntVec3 a, IntVec3 b) => Math.Max(Math.Abs(a.x - b.x), Math.Abs(a.z - b.z));
        private static bool Collinear(List<IntVec3> cells) => cells.Count <= 2 || cells.Skip(2).All(c => (c.x - cells[0].x) * (cells[1].z - cells[0].z) == (c.z - cells[0].z) * (cells[1].x - cells[0].x));

        public sealed class PlantState
        {
            public string id, def; public IntVec3 position; public float growth; public int age, health, generation; public bool sown, spent, mushroom;
            public string Encode() => string.Join("|", id, def, position.x, position.z, growth.ToString("R", CultureInfo.InvariantCulture), age, health, sown, generation, spent);
        }
        private static List<PlantState> Snapshot(Map m, bool mushroomsOnly)
        {
            return m.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant>().Where(p => !mushroomsOnly || p is Plant_Mushroom)
                .Select(p => new PlantState { id = p.ThingID, def = p.def.defName, position = p.Position, growth = p.Growth, age = p.Age, health = p.HitPoints,
                    sown = p.sown, mushroom = p is Plant_Mushroom, generation = (p as Plant_Mushroom)?.WildRegrowthGeneration ?? 0,
                    spent = (p as Plant_Mushroom)?.WildRegrowthSpent ?? false }).ToList();
        }
        private static bool SameOldPlants(List<PlantState> before, List<PlantState> after, bool positions)
        {
            var lookup = after.ToDictionary(p => p.id);
            return before.All(p => { PlantState q; return lookup.TryGetValue(p.id, out q) && p.def == q.def && p.growth == q.growth && p.age == q.age && p.health == q.health
                && p.sown == q.sown && p.generation == q.generation && p.spent == q.spent && (!positions || p.position == q.position); });
        }
        private static string Ticket(MushroomMycelium t) => string.Join("|", t.plantDef.defName, t.cell.x, t.cell.z, t.sourceId, t.dueTick, t.expiryTick);
        private void Check(bool pass, string message) { if (!pass) throw new Exception(message); checks++; Append("PASS " + message); }
        private void CheckNativeErrors() { lock (errors) if (errors.Count > 0) throw new Exception("Native errors:\n" + string.Join("\n", errors)); }
        private void Append(string text) { Directory.CreateDirectory(Path.GetDirectoryName(Report)); File.AppendAllText(Report, text + "\n", Encoding.UTF8); }
        private static string Csv(params object[] values) => string.Join(",", values.Select(v => "\"" + Convert.ToString(v, CultureInfo.InvariantCulture).Replace("\"", "\"\"") + "\""));

        // An explicit native selection fixture. Exact original biome cache objects
        // and the map's native spawner are restored; no XML/production weight changes.
        private sealed class SingleSpeciesScope : IDisposable
        {
            private readonly Map map; private readonly WildPlantSpawner oldSpawner; private readonly BiomeDef biome;
            private readonly FieldInfo[] fields; private readonly object[] originals;
            public SingleSpeciesScope(Map map, ThingDef def)
            {
                this.map = map; biome = map.Biome; oldSpawner = map.wildPlantSpawner;
                fields = new[] { "cachedPlantCommonalities", "cachedWildPlants", "cachedLowestWildPlantOrder", "cachedMaxWildPlantsClusterRadius" }
                    .Select(n => AccessTools.Field(typeof(BiomeDef), n)).ToArray();
                if (fields.Any(f => f == null)) throw new Exception("Unknown native biome cache layout.");
                originals = fields.Select(f => f.GetValue(biome)).ToArray();
                fields[0].SetValue(biome, new Dictionary<ThingDef, float> { { def, 1f } });
                fields[1].SetValue(biome, new List<ThingDef> { def });
                fields[2].SetValue(biome, (float?)def.plant.wildOrder);
                fields[3].SetValue(biome, (int?)def.plant.wildClusterRadius);
                map.wildPlantSpawner = new WildPlantSpawner(map);
            }
            public void Dispose()
            {
                for (int i = 0; i < fields.Length; i++) fields[i].SetValue(biome, originals[i]);
                map.wildPlantSpawner = oldSpawner;
                if (!ReferenceEquals(map.wildPlantSpawner, oldSpawner) || fields.Select((f, i) => Equals(f.GetValue(biome), originals[i])).Any(v => !v))
                    throw new Exception("Native single-species fixture did not restore its exact references.");
            }
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref phase, "habitatTestPhase"); Scribe_Values.Look(ref started, "habitatTestStarted");
            Scribe_Values.Look(ref mapId, "habitatTestMap", -1); Scribe_Values.Look(ref checks, "habitatTestChecks");
            Scribe_Values.Look(ref nativeSpawnCount, "habitatTestNativeSpawns"); Scribe_Values.Look(ref redirectedCount, "habitatTestRedirects");
            Scribe_Collections.Look(ref savedPlants, "habitatTestSavedPlants", LookMode.Value);
            Scribe_Collections.Look(ref savedTickets, "habitatTestSavedTickets", LookMode.Value);
            Scribe_Values.Look(ref savedRandom, "habitatTestRandom"); Scribe_Values.Look(ref savedRing, "habitatTestRing"); Scribe_Values.Look(ref savedBudget, "habitatTestBudget");
        }
    }
}
