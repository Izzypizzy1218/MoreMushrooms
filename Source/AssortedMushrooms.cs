using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMushrooms
{
    // The menu entry is a sowing recipe. Jobs create ordinary species plants,
    // so their growth, rendering, harvest, thoughts and save data remain native.
    public sealed class AssortedMushroomSettings : DefModExtension
    {
        public List<ThingDef> varieties;

        public override IEnumerable<string> ConfigErrors()
        {
            if (varieties == null || varieties.Count != 9 || varieties.Distinct().Count() != 9)
                yield return "Assorted mushrooms require nine distinct varieties.";
            else if (varieties.Any(d => d == null || d.plant == null || !d.plant.Sowable || d.GetModExtension<AssortedMushroomSettings>() != null))
                yield return "Assorted mushroom varieties must be individual sowable plants.";
        }
    }

    public static class AssortedMushrooms
    {
        public static ThingDef SelectionAt(IntVec3 cell, Map map)
        {
            var selected = cell.GetPlantToGrowSettable(map)?.GetPlantDefToGrow();
            return selected?.GetModExtension<AssortedMushroomSettings>() == null ? null : selected;
        }

        public static bool CanSow(Pawn pawn, ThingDef selection)
        {
            int required = selection.plant.sowMinSkill;
            if (pawn.skills != null && pawn.skills.GetSkill(SkillDefOf.Plants).Level < required) return false;
            return !pawn.IsColonyMech || pawn.RaceProps.mechFixedSkillLevel >= required;
        }

        public static ThingDef WantedAt(IntVec3 cell, Map map, ThingDef selection)
        {
            var varieties = selection.GetModExtension<AssortedMushroomSettings>().varieties;
            var planted = cell.GetPlant(map);
            // Existing members are the wanted crop, never a weed to cut/replant.
            return planted != null && varieties.Contains(planted.def) ? planted.def : varieties.RandomElement();
        }
    }

    public sealed class WorkGiver_AssortedMushroomSow : WorkGiver_GrowerSow
    {
        protected override bool ExtraRequirements(IPlantToGrowSettable settable, Pawn pawn)
        {
            if (!base.ExtraRequirements(settable, pawn)) return false;
            var selected = settable.GetPlantDefToGrow();
            return selected.GetModExtension<AssortedMushroomSettings>() == null || AssortedMushrooms.CanSow(pawn, selected);
        }

        public override Job JobOnCell(Pawn pawn, IntVec3 cell, bool forced = false)
        {
            var selection = AssortedMushrooms.SelectionAt(cell, pawn.Map);
            if (selection == null) return base.JobOnCell(pawn, cell, forced);
            var settable = cell.GetPlantToGrowSettable(pawn.Map);
            if (!AssortedMushrooms.CanSow(pawn, selection) || !settable.CanAcceptSowNow()
                || (settable is Zone_Growing zone && !zone.allowSow)) return null;
            var previous = wantedPlantDef;
            try
            {
                wantedPlantDef = AssortedMushrooms.WantedAt(cell, pawn.Map, selection);
                return base.JobOnCell(pawn, cell, forced);
            }
            finally { wantedPlantDef = previous; }
        }
    }

    public sealed class WorkGiver_AssortedMushroomHarvest : WorkGiver_GrowerHarvest
    {
        public override bool HasJobOnCell(Pawn pawn, IntVec3 cell, bool forced = false)
        {
            var selection = AssortedMushrooms.SelectionAt(cell, pawn.Map);
            if (selection == null) return base.HasJobOnCell(pawn, cell, forced);
            var plant = cell.GetPlant(pawn.Map);
            if (plant == null) return false;
            var previous = wantedPlantDef;
            try
            {
                if (selection.GetModExtension<AssortedMushroomSettings>().varieties.Contains(plant.def))
                    wantedPlantDef = plant.def;
                else wantedPlantDef = selection;
                return base.HasJobOnCell(pawn, cell, forced);
            }
            finally { wantedPlantDef = previous; }
        }
    }
}
