using RimWorld;

namespace RimMushrooms
{
    // Native specialThoughtDirect/specialThoughtAsIngredient call this memory.
    // Only this mod's memories compete; meal quality and ideology stay independent.
    public sealed class Thought_MushroomEnjoyment : Thought_Memory
    {
        public override bool ShouldDiscard => !permanent && age >= DurationTicks;

        public override bool TryMergeWithExistingMemory(out bool showBubble)
        {
            var handler = pawn.needs.mood.thoughts.memories;
            Thought_MushroomEnjoyment strongest = null;
            foreach (var memory in handler.Memories)
            {
                var mushroom = memory as Thought_MushroomEnjoyment;
                if (mushroom == null || mushroom.ShouldDiscard) continue;
                if (strongest == null || mushroom.CurStage.baseMoodEffect > strongest.CurStage.baseMoodEffect)
                    strongest = mushroom;
            }

            if (strongest != null && strongest.CurStage.baseMoodEffect > CurStage.baseMoodEffect)
            {
                // A cheap mushroom must not refresh the remaining time of a rare one.
                showBubble = false;
                return true;
            }

            // Equal strength refreshes, stronger replaces. Re-adding preserves the
            // new variety's label without mutating an existing thought's definition.
            showBubble = strongest == null || strongest.def != def || strongest.age > DurationTicks / 2;
            for (int i = handler.Memories.Count - 1; i >= 0; i--)
                if (handler.Memories[i] is Thought_MushroomEnjoyment)
                    handler.RemoveMemory(handler.Memories[i]);
            return false;
        }
    }
}
