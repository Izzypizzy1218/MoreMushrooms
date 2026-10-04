using System.Xml;
using Verse;

namespace RimMushrooms
{
    // Game loads/creates its small components before deserializing world/map things.
    // Upgrade only the in-memory class tag of our legacy vanilla Plants. All saved
    // fields and IDs stay intact; the user's save file is never edited on disk.
    public sealed class MushroomSaveCompatibility : GameComponent
    {
        public MushroomSaveCompatibility(Game game)
        {
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                UpgradeLegacyPlants(Scribe.loader.curXmlParent?.OwnerDocument);
        }

        private static void UpgradeLegacyPlants(XmlDocument document)
        {
            if (document == null) return;
            int count = 0;
            foreach (XmlNode node in document.SelectNodes("//*[@Class='Plant' or @Class='RimWorld.Plant']"))
            {
                string defName = node["def"]?.InnerText;
                if (defName == null) continue;
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (def?.thingClass != typeof(Plant_Mushroom)) continue;
                node.Attributes["Class"].Value = typeof(Plant_Mushroom).FullName;
                count++;
            }
            if (count > 0) Log.Message("[More Mushrooms] Updated " + count + " legacy mushroom plants while loading; saved IDs and growth preserved.");
        }
    }
}
