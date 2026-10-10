using System;
using System.Collections.Generic;
using System.Linq;
using RimMushrooms;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMushroomsTests
{
    internal static class PsychoactiveTests
    {
        private static string savedPawn;
        private static int savedEnd, savedStateEnd;
        private static float savedDose, savedTolerance;
        private static Thought_MushroomHallucination[] Memories(Pawn pawn) => pawn.needs.mood.thoughts.memories.Memories.OfType<Thought_MushroomHallucination>().ToArray();
        private static Hediff_MushroomHallucination Effect(Pawn pawn) => pawn.health.hediffSet.GetFirstHediffOfDef(HediffDef.Named("RMush_Hallucination")) as Hediff_MushroomHallucination;
        private static Hediff_MushroomTolerance Tolerance(Pawn pawn) => pawn.health.hediffSet.GetFirstHediffOfDef(HediffDef.Named("RMush_PsychedelicTolerance")) as Hediff_MushroomTolerance;
        private static void Check(Action<bool, string> check, bool valid, string text) => check(valid, "psychoactive: " + text);
        private static Pawn Healthy(Map map, int offset)
        {
            var pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            foreach (var effect in pawn.health.hediffSet.hediffs.ToList()) pawn.health.RemoveHediff(effect);
            pawn.needs.food.CurLevelPercentage = 1f;
            pawn.needs.rest.CurLevelPercentage = 1f;
            GenSpawn.Spawn(pawn, map.Center + new IntVec3(15 + offset, 0, -16), map);
            return pawn;
        }

        public static void Run(Pawn farmer, Map map, Action<bool, string> check)
        {
            int clock = Find.TickManager.TicksGame;
            var letters = Find.LetterStack.LettersListForReading.ToArray();
            try
            {
                string[] ids = { "LibertyCap", "Cubensis", "FlyAgaric", "PantherCap" };
                for (int i = 0; i < ids.Length; i++)
                {
                    var pawn = Healthy(map, i);
                    try
                    {
                        var raw = ThingDef.Named("RMush_Raw" + ids[i]);
                        var settings = raw.GetModExtension<MushroomExposureProperties>();
                        Check(check, settings != null && settings.psychoactive && !raw.IsDrug && raw.IsNutritionGivingIngestible,
                            "normal edible ingredient, no chemical drug " + ids[i]);
                        Check(check, new[] { "CookMealSimple", "CookMealFine", "CookMealLavish" }.All(name =>
                            DefDatabase<RecipeDef>.GetNamed(name).ingredients.Any(ingredient => ingredient.filter.Allows(raw))),
                            "native ordinary cooking filters accept " + ids[i]);
                        var stack = ThingMaker.MakeThing(raw);
                        stack.stackCount = 10;
                        float nutrition = stack.Ingested(pawn, 0.5f);
                        var exposure = Effect(pawn);
                        var state = pawn.MentalState as MentalState_MushroomWander;
                        var memory = Memories(pawn).SingleOrDefault();
                        Check(check, nutrition > 0f && stack.Destroyed && exposure != null && Tolerance(pawn) != null,
                            "native raw ingestion creates one exposure and shared tolerance " + ids[i]);
                        Check(check, state != null && !state.causedByMood && !state.def.blockNormalThoughts
                            && state.def.moodRecoveryThought == null && !state.def.IsAggro, "nonviolent control loss keeps mood thoughts " + ids[i]);
                        Check(check, memory != null && memory.moodOffset == (int)settings.moodBonus && memory.DurationTicks == 15000,
                            "first ordinary dose has species mood and six-hour duration " + ids[i]);
                        Check(check, exposure.EndTick >= clock + (int)(settings.hallucinationHoursMin * 2500f)
                            && exposure.EndTick <= clock + (int)(settings.hallucinationHoursMax * 2500f), "species control-loss time range " + ids[i]);
                        if (state != null)
                        {
                            var tree = DefDatabase<ThinkTreeDef>.GetNamed("RMush_HallucinatoryWanderBehavior");
                            for (int n = 0; n < 12; n++)
                            {
                                var result = tree.thinkRoot.TryIssueJobPackage(pawn, default(JobIssueParams));
                                Check(check, result.IsValid && (result.Job.def == JobDefOf.GotoWander || result.Job.def == JobDefOf.Wait_Wander),
                                    "actual inserted AI returns nearby wander/wait, no berserk or map exit " + ids[i]);
                                if (result.IsValid && result.Job.def == JobDefOf.GotoWander)
                                {
                                    var cell = result.Job.targetA.Cell;
                                    Check(check, cell.InHorDistOf(state.Anchor, 6.1f) && !cell.OnEdge(map)
                                        && !map.exitMapGrid.IsExitCell(cell) && !cell.IsForbidden(pawn), "wander destination is bounded away from exits " + ids[i]);
                                }
                            }
                            Find.TickManager.DebugSetTicksGame(exposure.EndTick);
                            state.MentalStateTick(1);
                            Check(check, !pawn.InMentalState && !pawn.needs.mood.thoughts.memories.Memories.Any(m => m.def == ThoughtDefOf.Catharsis),
                                "fixed deadline restores control without catharsis " + ids[i]);
                            exposure.PostTickInterval(250);
                            Check(check, !pawn.InMentalState, "expired/resolved episode never restarts " + ids[i]);
                        }
                    }
                    finally { pawn.Destroy(DestroyMode.Vanish); Find.TickManager.DebugSetTicksGame(clock); }
                }
                ValidateRepeatDose(map, check);
                ValidateMixedDose(map, check);
                ValidateOtherMentalState(map, check);
                ValidateDowned(map, check);
                ValidateDecay(map, check);
            }
            finally
            {
                Find.TickManager.DebugSetTicksGame(clock);
                foreach (var letter in Find.LetterStack.LettersListForReading.Where(l => !letters.Contains(l)).ToList()) Find.LetterStack.RemoveLetter(letter);
            }
            PrepareSaved(map, check);
        }

        private static void ValidateMixedDose(Map map, Action<bool, string> check)
        {
            var pawn = Healthy(map, 0);
            try
            {
                MushroomPsychoactive.ApplyMixture(pawn, new[] {
                    new KeyValuePair<ThingDef, float>(ThingDef.Named("RMush_RawLibertyCap"), 0.5f),
                    new KeyValuePair<ThingDef, float>(ThingDef.Named("RMush_RawCubensis"), 0.5f) });
                Check(check, Math.Abs(Effect(pawn).TotalDoses - 1f) < 0.0001f && Math.Abs(Tolerance(pawn).Severity - 0.251f) < 0.0001f,
                    "two half-dose ingredients count as one exposure and one tolerance increment");
                Check(check, Memories(pawn).Length == 1 && Memories(pawn)[0].moodOffset == 15 && !Effect(pawn).Panic,
                    "mixed first dose gives one full strongest mood, without sequential-repeat penalty");
            }
            finally { pawn.Destroy(DestroyMode.Vanish); }
        }

        private static void ValidateRepeatDose(Map map, Action<bool, string> check)
        {
            var pawn = Healthy(map, 0);
            try
            {
                var first = ThingDef.Named("RMush_RawLibertyCap");
                MushroomPsychoactive.Apply(pawn, first, 1f);
                var exposure = Effect(pawn);
                var state = pawn.MentalState;
                float initialTolerance = Tolerance(pawn).Severity;
                foreach (var memory in Memories(pawn)) pawn.needs.mood.thoughts.memories.RemoveMemory(memory);
                Rand.PushState(97531);
                try { MushroomPsychoactive.Apply(pawn, ThingDef.Named("RMush_RawCubensis"), 1f); }
                finally { Rand.PopState(); }
                Check(check, Tolerance(pawn).Severity > initialTolerance && pawn.MentalState == state,
                    "switching species adds shared tolerance without replacing active state");
                var afterRepeat = Memories(pawn).Single();
                Check(check, afterRepeat.moodOffset < 15, "repeated dose lowers mood benefit or causes panic");
                for (int i = 0; i < 20; i++) MushroomPsychoactive.Apply(pawn, first, 4f);
                Check(check, Effect(pawn) == exposure && exposure.TotalDoses == 4f && exposure.EndTick <= Find.TickManager.TicksGame + 20000
                    && Tolerance(pawn).Severity <= 1f && Memories(pawn).Length == 1, "repeat dose bounds episode, dose, tolerance and memory stacking");
                Check(check, exposure.Panic && Memories(pawn)[0].moodOffset == -8
                    && pawn.MentalState is MentalState_MushroomWander && !pawn.MentalStateDef.IsAggro,
                    "high/repeated doses can cause negative-mood panic with safe wandering");
                ((MentalState_MushroomWander)pawn.MentalState).RecoverFromState();
                exposure.PostTickInterval(250);
                Check(check, !pawn.InMentalState, "manual rescue/recovery cannot re-trigger same hallucination episode");
            }
            finally { pawn.Destroy(DestroyMode.Vanish); }
        }

        private static void ValidateOtherMentalState(Map map, Action<bool, string> check)
        {
            var pawn = Healthy(map, 0);
            try
            {
                pawn.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Wander_Sad, forced: true, causedByMood: false);
                var existing = pawn.MentalState;
                Check(check, existing != null, "unrelated mental state fixture starts");
                MushroomPsychoactive.Apply(pawn, ThingDef.Named("RMush_RawCubensis"), 1f);
                Check(check, pawn.MentalState == existing, "ingestion never overwrites unrelated mental state");
                existing.RecoverFromState();
                Effect(pawn).PostTickInterval(250);
                Check(check, !pawn.InMentalState, "no delayed replacement after unrelated mental state ends");
            }
            finally { pawn.Destroy(DestroyMode.Vanish); }
        }

        private static void ValidateDowned(Map map, Action<bool, string> check)
        {
            var pawn = Healthy(map, 0);
            try
            {
                pawn.health.AddHediff(HediffDefOf.Anesthetic);
                Check(check, pawn.Downed, "native anesthetic incapacitates fixture");
                MushroomPsychoactive.Apply(pawn, ThingDef.Named("RMush_RawCubensis"), 1f);
                Check(check, !pawn.InMentalState && Effect(pawn).StartAttempted, "incapacitated pawn is not forced awake or put into wandering");
                foreach (var anesthetic in pawn.health.hediffSet.hediffs.Where(h => h.def == HediffDefOf.Anesthetic).ToList()) pawn.health.RemoveHediff(anesthetic);
                Effect(pawn).PostTickInterval(250);
                Check(check, !pawn.InMentalState, "recovery from incapacitation cannot repeatedly restart wandering");
            }
            finally { pawn.Destroy(DestroyMode.Vanish); }
        }

        private static void ValidateDecay(Map map, Action<bool, string> check)
        {
            var pawn = Healthy(map, 0);
            int clock = Find.TickManager.TicksGame;
            try
            {
                MushroomPsychoactive.Apply(pawn, ThingDef.Named("RMush_RawLibertyCap"), 1f);
                var tolerance = Tolerance(pawn);
                float before = tolerance.Severity;
                Find.TickManager.DebugSetTicksGame(clock + 30000);
                tolerance.PostTickInterval(30000);
                Check(check, Math.Abs((before - tolerance.Severity) - 0.25f) < 0.0001f, "shared tolerance decays with actual game time");
            }
            finally { Find.TickManager.DebugSetTicksGame(clock); pawn.Destroy(DestroyMode.Vanish); }
        }

        private static void PrepareSaved(Map map, Action<bool, string> check)
        {
            var pawn = Healthy(map, 0);
            MushroomPsychoactive.Apply(pawn, ThingDef.Named("RMush_RawLibertyCap"), 1f);
            savedPawn = pawn.GetUniqueLoadID();
            savedEnd = Effect(pawn).EndTick;
            savedDose = Effect(pawn).TotalDoses;
            savedTolerance = Tolerance(pawn).Severity;
            savedStateEnd = ((MentalState_MushroomWander)pawn.MentalState).EndTick;
            Check(check, savedEnd == savedStateEnd, "save fixture has matching absolute effect/state deadlines");
        }

        public static void VerifyLoaded(Map map, Action<bool, string> check)
        {
            var pawn = map.mapPawns.AllPawnsSpawned.Single(p => p.GetUniqueLoadID() == savedPawn);
            var exposure = Effect(pawn);
            var state = pawn.MentalState as MentalState_MushroomWander;
            Check(check, exposure != null && exposure.EndTick == savedEnd && exposure.TotalDoses == savedDose && exposure.StartAttempted
                && Tolerance(pawn).Severity == savedTolerance, "native save/load preserves exposure dose, tolerance and deadline");
            Check(check, state != null && state.EndTick == savedStateEnd && !state.causedByMood && Memories(pawn).Length == 1,
                "native save/load preserves custom mental state and unique mood memory");
            int clock = Find.TickManager.TicksGame;
            try
            {
                Find.TickManager.DebugSetTicksGame(savedEnd);
                state.MentalStateTick(1);
                exposure.PostTickInterval(250);
                Check(check, !pawn.InMentalState, "loaded episode restores control at original deadline without resetting duration");
            }
            finally { Find.TickManager.DebugSetTicksGame(clock); }
        }
    }
}
