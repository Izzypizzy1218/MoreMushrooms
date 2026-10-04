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
