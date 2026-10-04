using System;
using System.Collections.Generic;
using System.Linq;
using RimMushrooms;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMushroomsTests
{
    // Only used in the isolated smoke-test map. Actual vanilla buildings, power
    // networks and ordered native sow/harvest jobs exercise the production defs.
    internal sealed class HydroponicsTests
    {
        private readonly Map map;
        private readonly Pawn farmer;
        private readonly Action<bool, string> check;
        private readonly ThingDef[] varieties;
        private readonly float originalSkyGlow;
        private readonly int originalGrowingPriority;
        private readonly Building_PlantGrower basin;
        private readonly Building battery;
        private readonly CompPowerTrader basinPower;
        private readonly IntVec3 cell;
        private int phase, cycle, baseline;
        private float deadline;
        private Plant plant;
        private bool originalHarvestFailable, completed;

        private static ThingDef Selection => ThingDef.Named("RMush_PlantAssorted");
        private static WorkGiver_GrowerSow Sower => AssortedTests.Sower;
        private static WorkGiver_GrowerHarvest Harvester => AssortedTests.Harvester;
        private static IntVec3 BasinPosition(Map map) => map.Center + new IntVec3(16, 0, -10);

        public HydroponicsTests(Map map, Pawn farmer, Action<bool, string> check)
        {
            this.map = map;
            this.farmer = farmer;
            this.check = check;
            originalSkyGlow = map.skyManager.CurSkyGlow;
            // Keep the fixture on one basin cell while ordered vanilla jobs run.
            // Automatic worker scans are still queried and tested explicitly.
            originalGrowingPriority = farmer.workSettings.GetPriority(WorkTypeDefOf.Growing);
            farmer.workSettings.SetPriority(WorkTypeDefOf.Growing, 0);
            varieties = Selection.GetModExtension<AssortedMushroomSettings>().varieties.OrderBy(d => d.defName).ToArray();
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            farmer.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            farmer.pather.StopDead();

            var fixtureCells = CellRect.CenteredOn(map.Center + new IntVec3(18, 0, -10), 11, 9).ToArray();
            check(fixtureCells.All(c => c.InBounds(map)), "hydroponics fixture remains inside isolated map");
            foreach (var c in fixtureCells)
            {
                foreach (var thing in c.GetThingList(map).ToList())
                    if (!(thing is Pawn)) thing.Destroy(DestroyMode.Vanish);
                map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                map.roofGrid.SetRoof(c, null);
                map.snowGrid.SetDepth(c, 0f);
            }

            // Supplying an initially charged vanilla battery is fixture control,
            // not a production power override. The native grid switches the basin on.
            basin = (Building_PlantGrower)SpawnBuilding("HydroponicsBasin", BasinPosition(map), Rot4.North);
            battery = SpawnBuilding("Battery", map.Center + new IntVec3(20, 0, -10), Rot4.North);
            battery.GetComp<CompPowerBattery>().SetStoredEnergyPct(1f);
            foreach (var c in basin.OccupiedRect()) SpawnBuilding("PowerConduit", c, Rot4.North);
            // The battery itself transmits power; do not overlap a conduit with
            // its transmitter cells or the native manager creates duplicate nets.
            for (int x = 17; x <= 19; x++) SpawnBuilding("PowerConduit", map.Center + new IntVec3(x, 0, -10), Rot4.North);
            foreach (var c in basin.OccupiedRect().Concat(battery.OccupiedRect()))
                map.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
            basinPower = basin.GetComp<CompPowerTrader>();
            cell = basin.Position;
            basin.SetPlantDefToGrow(Selection);
            farmer.Position = cell + IntVec3.West;
            farmer.Notify_Teleported();
            int toNoon = (int)((0.5f - GenLocalDate.DayPercent(map) + 1f) % 1f * 60000f);
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + toNoon);
            deadline = Time.realtimeSinceStartup + 40f;
        }

        private Building SpawnBuilding(string defName, IntVec3 position, Rot4 rotation)
        {
            var building = (Building)ThingMaker.MakeThing(ThingDef.Named(defName));
            building.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(building, position, map, rotation);
            return building;
        }

        public bool Update()
        {
            if (completed) return true;
            foreach (var window in Find.WindowStack.Windows.ToList()) window.Close(false);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
            farmer.needs.food.CurLevelPercentage = 1f;
            farmer.needs.rest.CurLevelPercentage = 1f;
            if (farmer.needs.mood != null) farmer.needs.mood.CurLevelPercentage = 1f;
            if (Time.realtimeSinceStartup > deadline)
                throw new Exception("Hydroponics timeout phase=" + phase + " cycle=" + cycle + " job=" + farmer.CurJob + " basinPower=" + basinPower.PowerOn);

            if (phase == 0)
            {
                if (basinPower.PowerNet == null || !basinPower.PowerOn) return false;
                check(basinPower.PowerNet == battery.GetComp<CompPowerBattery>().PowerNet && basinPower.PowerNet.HasActivePowerSource,
                    "actual hydroponics basin is powered through native battery/conduit network");
                ValidateMenuAndWorkers();
                phase = 1;
                deadline = Time.realtimeSinceStartup + 40f;
            }
            if (phase == 1)
            {
                farmer.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                farmer.pather.StopDead();
                bool rice = cycle == varieties.Length;
                bool assorted = cycle > varieties.Length;
                ThingDef selected = assorted ? Selection : rice ? ThingDef.Named("Plant_Rice") : varieties[cycle];
                basin.SetPlantDefToGrow(selected);
                foreach (var c in basin.OccupiedRect()) map.roofGrid.SetRoof(c, rice ? null : RoofDefOf.RoofRockThick);
                map.skyManager.ForceSetCurSkyGlow(rice ? 1f : 0f);
                check(Sower.PotentialWorkCellsGlobal(farmer).Contains(cell), "native automatic sow scan includes selected powered basin " + selected.defName + " cycle=" + cycle);
                // Exhausting the native scan resets its shared wantedPlantDef cache.
                Sower.PotentialWorkCellsGlobal(farmer).ToList();
                var job = Sower.JobOnCell(farmer, cell);
                if (job != null && job.def == JobDefOf.HaulToCell)
                {
                    job.playerForced = true;
                    farmer.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    phase = 4;
                    deadline = Time.realtimeSinceStartup + 40f;
                    return false;
                }
                check(job != null && job.def == JobDefOf.Sow && (assorted ? varieties.Contains(job.plantDefToSow) : job.plantDefToSow == selected),
                    "native hydroponic sow job chooses real species " + selected.defName + " cycle=" + cycle);
                check(job.plantDefToSow.CanNowPlantAt(cell, map), "selected actual crop accepts basin fertility and planting cell " + job.plantDefToSow.defName);
                job.playerForced = true;
                farmer.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                phase = 2;
                deadline = Time.realtimeSinceStartup + 40f;
                return false;
            }
            if (phase == 2)
            {
                plant = cell.GetPlant(map);
                if (plant == null || plant.Growth <= 0f) return false;
                if (farmer.CurJobDef == JobDefOf.Sow && (farmer.CurJob.targetA.Thing == plant || farmer.CurJob.targetA.Cell == cell)) return false;
                farmer.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                bool rice = cycle == varieties.Length;
                bool assorted = cycle > varieties.Length;
                check(plant.sown && (assorted ? varieties.Contains(plant.def) : plant.def == basin.GetPlantDefToGrow()),
                    "pawn actually sowed hydroponic " + plant.def.defName + " cycle=" + cycle);
                check(plant.def != Selection, "hydroponics contains an ordinary crop rather than assorted selector plant");
                Sower.PotentialWorkCellsGlobal(farmer).ToList();
                check(Sower.JobOnCell(farmer, cell) == null, "hydroponic crop is kept without cutting or replanting " + plant.def.defName);
                ValidateGrowth(plant, rice);

                // Nine species, vanilla rice and three assorted sow/harvest
                // cycles finish first. A fourth assorted sow remains for loading.
                if (cycle == varieties.Length + 4)
                {
                    plant.Growth = 0.37f;
                    foreach (var extra in basin.PlantsOnMe.Where(p => p != plant).ToList()) extra.Destroy();
                    farmer.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    map.skyManager.ForceSetCurSkyGlow(originalSkyGlow);
                    farmer.workSettings.SetPriority(WorkTypeDefOf.Growing, originalGrowingPriority);
                    check(basin.GetPlantDefToGrow() == Selection && varieties.Contains(plant.def), "assorted hydroponics save fixture retains chosen variety and basin selection");
                    completed = true;
                    return true;
                }
                plant.Growth = 1f;
                Harvester.PotentialWorkCellsGlobal(farmer).ToList();
                check(Harvester.HasJobOnCell(farmer, cell), "native automatic harvester recognizes mature hydroponic " + plant.def.defName);
                var harvest = Harvester.JobOnCell(farmer, cell);
                check(harvest != null && harvest.def == JobDefOf.Harvest && harvest.targetQueueA.Any(t => t.Thing == plant),
                    "native hydroponic harvest queue contains actual planted species " + plant.def.defName);
                baseline = CountRaw(plant.def.plant.harvestedThingDef);
                // Only the fixture guarantees a successful harvest. Restore the
                // original species definition immediately after the native job.
                originalHarvestFailable = plant.def.plant.harvestFailable;
                plant.def.plant.harvestFailable = false;
                harvest.playerForced = true;
                farmer.jobs.TryTakeOrderedJob(harvest, JobTag.Misc);
                deadline = Time.realtimeSinceStartup + 40f;
                phase = 3;
                return false;
            }
            if (phase == 3)
            {
                if (!plant.Destroyed) return false;
                farmer.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                plant.def.plant.harvestFailable = originalHarvestFailable;
                check(CountRaw(plant.def.plant.harvestedThingDef) > baseline,
                    "actual hydroponic pawn harvest yields selected ingredient " + plant.def.plant.harvestedThingDef.defName + " cycle=" + cycle);
                cycle++;
                phase = 1;
                deadline = Time.realtimeSinceStartup + 40f;
                return false;
            }
            if (phase == 4)
            {
                if (farmer.CurJobDef == JobDefOf.HaulToCell) return false;
                check(true, "native haul-aside job clears harvested hydroponic pile for replanting");
                phase = 1;
                deadline = Time.realtimeSinceStartup + 40f;
            }
            return false;
        }

        private int CountRaw(ThingDef def)
        {
            int count = map.listerThings.ThingsOfDef(def).Sum(t => t.stackCount);
            var carried = farmer.carryTracker.CarriedThing;
            if (carried != null && carried.def == def) count += carried.stackCount;
            return count;
        }

        private void ValidateMenuAndWorkers()
        {
            var menu = PlantUtility.ValidPlantTypesForGrowers(new List<IPlantToGrowSettable> { basin }).ToArray();
            // Native command construction reads the current UI selection. Give
            // it the actual basin; command/menu data are checked, not rendering.
            var previousSelection = Find.Selector.SelectedObjects.ToList();
            Find.Selector.ClearSelection();
            Find.Selector.Select(basin, playSound: false, forceDesignatorDeselect: false);
            try
            {
                check(basin.GetGizmos().Any(g => g is Command_SetPlantToGrow), "selected vanilla hydroponics basin creates native crop-selection command");
            }
            finally
            {
                Find.Selector.ClearSelection();
                foreach (var selected in previousSelection)
                    if (!(selected is Thing thing) || !thing.Destroyed)
                        Find.Selector.Select(selected, playSound: false, forceDesignatorDeselect: false);
            }
            check(varieties.Length == 9, "hydroponics assorted pool still contains exactly nine real species");
            foreach (var def in varieties.Concat(new[] { Selection }))
                check(menu.Contains(def) && Command_SetPlantToGrow.IsPlantAvailable(def, map), "native hydroponics crop menu includes " + def.defName);
            foreach (string name in new[] { "RMush_PlantMatsutake", "RMush_PlantEnokiWild" })
                check(!menu.Contains(ThingDef.Named(name)), "wild-only mushroom is excluded from hydroponics menu " + name);
            check(menu.Contains(ThingDef.Named("Plant_Rice")), "vanilla rice remains selectable in hydroponics menu");
            check(Mathf.Abs(map.fertilityGrid.FertilityAt(cell) - 2.8f) < 0.00001f, "actual hydroponics building overrides soil with 280 percent fertility");
            check(AssortedMushrooms.SelectionAt(cell, map) == Selection, "assorted selection resolves from actual Building_PlantGrower");

            int oldSkill = farmer.skills.GetSkill(SkillDefOf.Plants).Level;
            try
            {
                farmer.skills.GetSkill(SkillDefOf.Plants).Level = 5;
                check(Sower.JobOnCell(farmer, cell) == null && !Sower.PotentialWorkCellsGlobal(farmer).Contains(cell), "hydroponic assorted rejects skill 5 in jobs and automatic scan");
                farmer.skills.GetSkill(SkillDefOf.Plants).Level = 6;
                check(Sower.PotentialWorkCellsGlobal(farmer).Contains(cell), "hydroponic assorted accepts skill 6 in automatic scan");
            }
            finally { farmer.skills.GetSkill(SkillDefOf.Plants).Level = oldSkill; }

            var temporary = (Plant)GenSpawn.Spawn(varieties[0], cell, map);
            temporary.sown = true;
            temporary.Growth = 0.25f;
            try
            {
                // A synchronous controlled blackout toggles the already powered
                // native comp; normal runs use battery/network power throughout.
                int hp = temporary.HitPoints;
                bool wasPowered = basinPower.PowerOn;
                basinPower.PowerOn = false;
                try
                {
                    check(!basin.CanAcceptSowNow() && Sower.JobOnCell(farmer, cell) == null, "hydroponic assorted respects basin blackout sow gate");
                    check(!Sower.PotentialWorkCellsGlobal(farmer).Contains(cell), "native automatic scan excludes unpowered hydroponics basin");
                    basin.TickRare();
                    check(temporary.HitPoints == hp - 1, "mushrooms retain vanilla hydroponics blackout damage");
                }
                finally { basinPower.PowerOn = wasPowered; }
            }
            finally { temporary.Destroy(); }

            foreach (var def in varieties)
            {
                var member = (Plant)GenSpawn.Spawn(def, cell, map);
                try
                {
                    member.sown = true; member.Growth = 0.25f;
                    Sower.PotentialWorkCellsGlobal(farmer).ToList();
                    check(Sower.JobOnCell(farmer, cell) == null, "assorted basin never cuts growing member " + def.defName);
                    member.Growth = 1f;
                    Harvester.PotentialWorkCellsGlobal(farmer).ToList();
                    check(Harvester.HasJobOnCell(farmer, cell), "automatic assorted basin harvest accepts every real member " + def.defName);
                }
                finally { member.Destroy(); }
            }
        }

        private void ValidateGrowth(Plant crop, bool rice)
        {
            // Native daytime rest must not make ticker assertions depend on how
            // long previous ordered jobs took in this isolated fixture.
            int toNoon = (int)((0.5f - GenLocalDate.DayPercent(map) + 1f) % 1f * 60000f);
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + toNoon);
            check(Mathf.Abs(map.fertilityGrid.FertilityAt(cell) - 2.8f) < 0.00001f, "native 280 percent basin fertility reaches planted " + crop.def.defName);
            float fertilityFactor = rice ? 2.8f : 1.54f;
            check(Mathf.Abs(crop.GrowthRateFactor_Fertility - fertilityFactor) < 0.00001f,
                "hydroponic fertility multiplier " + crop.def.defName + "=" + fertilityFactor);
            float native = crop.GrowthRateFactor_Fertility * crop.GrowthRateFactor_Temperature * crop.GrowthRateFactor_Light * crop.GrowthRateFactor_NoxiousHaze * crop.GrowthRateFactor_Drought;
            check(native > 0f, "native basin light and temperature support growth " + crop.def.defName);
            if (rice)
                check(crop.GetType() == typeof(Plant) && crop.GrowthRateFactor_Light == 1f && Mathf.Abs(crop.GrowthRate - native) < 0.00001f,
                    "vanilla hydroponic rice retains native class and full-light growth");
            else
            {
                var mushroom = crop as Plant_Mushroom;
                check(mushroom != null && Mathf.Abs(crop.def.plant.fertilitySensitivity - 0.3f) < 0.00001f,
                    "hydroponic real mushroom retains shade-aware class and fertility sensitivity 0.3 " + crop.def.defName);
                check(mushroom.BrightLightGrowthFactor == 1f && Mathf.Abs(crop.GrowthRate - native) < 0.00001f,
                    "shaded hydroponic mushroom uses full 1.54 fertility multiplier " + crop.def.defName);
                map.roofGrid.SetRoof(cell, null);
                map.skyManager.ForceSetCurSkyGlow(1f);
                check(Mathf.Abs(map.glowGrid.GroundGlowAt(cell) - 1f) < 0.00001f && Mathf.Abs(mushroom.BrightLightGrowthFactor - 0.5f) < 0.00001f,
                    "full-light hydroponic mushroom retains 50 percent shade penalty " + crop.def.defName);
                check(Mathf.Abs(crop.GrowthRate - native * 0.5f) < 0.00001f,
                    "hydroponic fertility and sunlight penalties multiply in actual growth " + crop.def.defName);
                map.roofGrid.SetRoof(cell, RoofDefOf.RoofRockThick);
                map.skyManager.ForceSetCurSkyGlow(0f);
            }
            crop.Growth = 0.25f;
            float expected = native * GenTicks.TickLongInterval / (60000f * crop.def.plant.growDays);
            crop.TickLong();
            check(Mathf.Abs(crop.Growth - 0.25f - expected) < 0.000002f,
                "actual hydroponic growth tick follows selected species growDays " + crop.def.defName);
        }

        public static void VerifyLoaded(Map map, Action<bool, string> check)
        {
            var basin = map.listerThings.ThingsOfDef(ThingDef.Named("HydroponicsBasin")).OfType<Building_PlantGrower>().Single(b => b.Position == BasinPosition(map));
            var members = Selection.GetModExtension<AssortedMushroomSettings>().varieties;
            var plant = basin.Position.GetPlant(map);
            check(basin.GetPlantDefToGrow() == Selection, "hydroponics basin assorted selection persists after save/load");
            check(plant is Plant_Mushroom && members.Contains(plant.def) && plant.sown, "chosen real hydroponic species and sown state persist after save/load");
            check(Mathf.Abs(plant.Growth - 0.37f) < 0.00001f, "hydroponic species growth persists after save/load");
            check(Mathf.Abs(map.fertilityGrid.FertilityAt(plant.Position) - 2.8f) < 0.00001f, "loaded hydroponic crop keeps native 280 percent fertility");
            check(basin.CanAcceptSowNow() && basin.GetComp<CompPowerTrader>().PowerNet != null, "native hydroponics power network and powered state restore after save/load");
            check(AssortedMushrooms.SelectionAt(plant.Position, map) == Selection, "loaded assorted basin selection resolves from growing building");
            var farmer = map.mapPawns.FreeColonistsSpawned.First(p => !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Growing));
            Sower.PotentialWorkCellsGlobal(farmer).ToList();
            check(Sower.JobOnCell(farmer, plant.Position) == null, "loaded assorted hydroponic member is kept without cutting or replanting");
        }
    }
}
