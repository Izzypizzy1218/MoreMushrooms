using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMushrooms
{
    public sealed class MushroomLightSettings : DefModExtension
    {
        public float shadeMaxGlow = 0.5f;
        public float fullLightGrowthFactor = 0.5f;

        public float FactorAt(float glow) => Mathf.Lerp(1f, fullLightGrowthFactor,
            Mathf.InverseLerp(shadeMaxGlow, 1f, glow));

        public override IEnumerable<string> ConfigErrors()
        {
            if (shadeMaxGlow < 0f || shadeMaxGlow >= 1f)
                yield return "Mushroom shadeMaxGlow must be between 0 (inclusive) and 1 (exclusive).";
            if (fullLightGrowthFactor <= 0f || fullLightGrowthFactor > 1f)
                yield return "Mushroom fullLightGrowthFactor must be above 0 and at most 1.";
        }
    }

    public sealed class Plant_Mushroom : Plant
    {
        private int wildRegrowthGeneration;
        private bool wildRegrowthSpent;
        private int successfulHarvestTick = -1;

        public int WildRegrowthGeneration => wildRegrowthGeneration;
        public bool WildRegrowthSpent => wildRegrowthSpent;

        public void SetWildRegrowthGeneration(int generation) { wildRegrowthGeneration = generation <= 0 ? 0 : 1; }
        public void MarkWildRegrowthSpent() { wildRegrowthSpent = true; }
        public void NotifySuccessfulHarvest()
        {
            if (Spawned && HarvestableNow && CanYieldNow()) successfulHarvestTick = Find.TickManager.TicksGame;
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            map.GetComponent<MapComponent_WildMushrooms>()?.Register(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            map?.GetComponent<MapComponent_WildMushrooms>()?.Unregister(this);
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref wildRegrowthGeneration, "mushroomWildRegrowthGeneration");
            Scribe_Values.Look(ref wildRegrowthSpent, "mushroomWildRegrowthSpent");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                wildRegrowthGeneration = wildRegrowthGeneration <= 0 ? 0 : 1;
                successfulHarvestTick = -1;
            }
        }

        public override void PlantCollected(Pawn by, PlantDestructionMode plantDestructionMode)
        {
            bool success = successfulHarvestTick == Find.TickManager.TicksGame;
            successfulHarvestTick = -1;
            if (success && plantDestructionMode == PlantDestructionMode.Chop && by != null && Spawned)
                Map.GetComponent<MapComponent_WildMushrooms>()?.TryQueueRegrowth(this);
            base.PlantCollected(by, plantDestructionMode);
        }

        public float BrightLightGrowthFactor
        {
            get
            {
                var settings = def.GetModExtension<MushroomLightSettings>();
                return !Spawned || settings == null ? 1f : settings.FactorAt(Map.glowGrid.GroundGlowAt(Position));
            }
        }

        public override float GrowthRate => base.GrowthRate * BrightLightGrowthFactor;

        public override string GrowthRateCalcDesc
        {
            get
            {
                string result = base.GrowthRateCalcDesc;
                float factor = BrightLightGrowthFactor;
                if (factor < 1f)
                    result += (result.NullOrEmpty() ? "" : "\n") + "MM_BrightLightGrowthFactor".Translate(factor.ToStringPercent());
                return result;
            }
        }
    }
}
