using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimMushrooms
{
    // Only this mod's item graphics use this class. No Harmony/global patches.
    public sealed class MushroomStackThresholds : DefModExtension
    {
        public int lowMaxCount = 25;
        public int mediumMaxCount = 50;

        public override IEnumerable<string> ConfigErrors()
        {
            if (lowMaxCount < 1 || mediumMaxCount <= lowMaxCount)
                yield return "Mushroom stack thresholds must satisfy 1 <= lowMaxCount < mediumMaxCount.";
        }
    }

    public sealed class Graphic_MushroomStack : Graphic_Collection
    {
        public override void Init(GraphicRequest req)
        {
            base.Init(req);
            if (subGraphics.Length != 3)
                Log.Error("[Rim Mushrooms] Expected exactly 3 sorted textures (01Low, 02Medium, 03Full) at " + req.path);
        }

        public override Material MatSingle => subGraphics[subGraphics.Length - 1].MatSingle;

        public Graphic SubGraphicFor(Thing thing)
        {
            if (thing == null) return subGraphics[subGraphics.Length - 1];
            var settings = thing.def.GetModExtension<MushroomStackThresholds>();
            int low = settings != null ? settings.lowMaxCount : 25;
            int medium = settings != null ? settings.mediumMaxCount : 50;
            int index = thing.stackCount <= low ? 0 : (thing.stackCount <= medium ? 1 : 2);
            return subGraphics[System.Math.Min(index, subGraphics.Length - 1)];
        }

        // Ground meshes, carried items, and material-based UI all read the live count.
        public override Material MatSingleFor(Thing thing) => SubGraphicFor(thing).MatSingle;
        public override Material MatAt(Rot4 rot, Thing thing = null) => MatSingleFor(thing);

        public override void DrawWorker(Vector3 loc, Rot4 rot, ThingDef thingDef, Thing thing, float extraRotation)
        {
            SubGraphicFor(thing).DrawWorker(loc, rot, thingDef, thing, extraRotation);
        }

        public override Graphic GetColoredVersion(Shader newShader, Color newColor, Color newColorTwo)
        {
            return GraphicDatabase.Get<Graphic_MushroomStack>(path, newShader, drawSize, newColor, newColorTwo, data);
        }
    }
}
