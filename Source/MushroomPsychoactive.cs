using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMushrooms
{
    // Food provenance supplies actual ingredient doses for both raw food and meals.
    // These are normal foods; no chemical, drug policy or extraction recipe is needed.
    public static class MushroomPsychoactive
    {
        // A mixed meal is one exposure: half portions of two psychoactive foods
        // count as one full dose, rather than each being penalized as a repeat.
        // Mood uses the strongest source. Timing uses the longest species profile,
        // so adding a pleasant short-lived ingredient cannot shorten a longer one.
        public static void ApplyMixture(Pawn pawn, IEnumerable<KeyValuePair<ThingDef, float>> ingredients)
        {
            if (ingredients == null) return;
            ThingDef dominant = null;
            MushroomExposureProperties timingProfile = null;
            float strongestMood = float.MinValue;
            float totalDoses = 0f;
            foreach (var ingredient in ingredients)
            {
                var settings = ingredient.Key?.GetModExtension<MushroomExposureProperties>();
                if (settings == null || !settings.psychoactive || ingredient.Value <= 0f
                    || float.IsNaN(ingredient.Value) || float.IsInfinity(ingredient.Value)) continue;
                totalDoses = Mathf.Min(4f, totalDoses + ingredient.Value);
                if (timingProfile == null || settings.hallucinationHoursMax > timingProfile.hallucinationHoursMax
                    || (settings.hallucinationHoursMax == timingProfile.hallucinationHoursMax
                        && settings.hallucinationHoursMin > timingProfile.hallucinationHoursMin)) timingProfile = settings;
                if (settings.moodBonus > strongestMood)
                {
                    dominant = ingredient.Key;
                    strongestMood = settings.moodBonus;
                }
            }
            if (dominant != null) Apply(pawn, dominant, totalDoses, timingProfile);
        }

        public static void Apply(Pawn pawn, ThingDef source, float doses)
        {
            Apply(pawn, source, doses, null);
        }

        private static void Apply(Pawn pawn, ThingDef source, float doses, MushroomExposureProperties timingProfile)
        {
            var settings = source?.GetModExtension<MushroomExposureProperties>();
            if (settings == null || !settings.psychoactive || doses <= 0f || float.IsNaN(doses)
                || float.IsInfinity(doses) || pawn == null || pawn.Dead || !pawn.RaceProps.Humanlike
                || pawn.needs?.mood == null || pawn.health == null) return;
            doses = Mathf.Clamp(doses, 0.01f, 4f);
            var toleranceDef = HediffDef.Named("RMush_PsychedelicTolerance");
            var tolerance = pawn.health.hediffSet.GetFirstHediffOfDef(toleranceDef) as Hediff_MushroomTolerance;
            float previousTolerance = tolerance?.Severity ?? 0f;
            if (tolerance == null)
            {
                tolerance = (Hediff_MushroomTolerance)HediffMaker.MakeHediff(toleranceDef, pawn);
                pawn.health.AddHediff(tolerance);
            }
            tolerance.RegisterDose(doses);

            var hallDef = HediffDef.Named("RMush_Hallucination");
            var hallucination = pawn.health.hediffSet.GetFirstHediffOfDef(hallDef) as Hediff_MushroomHallucination;
            if (hallucination == null || hallucination.ShouldRemove)
            {
                if (hallucination != null) pawn.health.RemoveHediff(hallucination);
                hallucination = (Hediff_MushroomHallucination)HediffMaker.MakeHediff(hallDef, pawn);
                hallucination.sourceDef = source;
                hallucination.sourceLabel = source.label;
                pawn.health.AddHediff(hallucination);
            }
            hallucination.RegisterDose(timingProfile ?? settings, doses, previousTolerance);

            int bonus = Mathf.Max(1, Mathf.RoundToInt(settings.moodBonus * Mathf.Clamp01(doses)
                * Mathf.Lerp(1f, 0.4f, previousTolerance)));
            var memory = (Thought_MushroomHallucination)ThoughtMaker.MakeThought(
                ThoughtDef.Named("RMush_PsychedelicExperience"), hallucination.Panic ? 2 : bonus > 10 ? 0 : 1);
            memory.moodOffset = hallucination.Panic ? -8 : bonus;
            memory.durationTicksOverride = Mathf.RoundToInt(Mathf.Clamp(settings.moodDurationHours, 0.1f, 6f) * 2500f);
            pawn.needs.mood.thoughts.memories.TryGainMemory(memory);
            hallucination.TryStartWandering();
        }
    }

    public sealed class Thought_MushroomHallucination : Thought_Memory
    {
        public override bool ShouldDiscard => !permanent && age >= DurationTicks;

        public override bool TryMergeWithExistingMemory(out bool showBubble)
        {
            var memories = pawn.needs.mood.thoughts.memories;
            Thought_MushroomHallucination strongest = null;
            foreach (var memory in memories.Memories)
            {
                var experience = memory as Thought_MushroomHallucination;
                if (experience == null || experience.ShouldDiscard) continue;
                if (strongest == null || experience.moodOffset < 0
                    || (strongest.moodOffset >= 0 && experience.moodOffset > strongest.moodOffset)) strongest = experience;
            }
            // A lower dose must not refresh a stronger experience, nor erase a panic.
            if (strongest != null && ((strongest.moodOffset < 0 && moodOffset >= 0)
                || (strongest.moodOffset >= 0 && moodOffset >= 0 && strongest.moodOffset > moodOffset)))
            {
                showBubble = false;
                return true;
            }
            showBubble = strongest == null || strongest.CurStageIndex != CurStageIndex;
            for (int i = memories.Memories.Count - 1; i >= 0; i--)
                if (memories.Memories[i] is Thought_MushroomHallucination) memories.RemoveMemory(memories.Memories[i]);
            return false;
        }
    }

    public sealed class Hediff_MushroomTolerance : HediffWithComps
    {
        private int lastUpdateTick = -1;
        public void RegisterDose(float doses)
        {
            UpdateDecay();
            Severity = Mathf.Clamp01(Severity + Mathf.Clamp(doses, 0f, 4f) * 0.25f);
        }
        private void UpdateDecay()
        {
            int now = Find.TickManager.TicksGame;
            if (lastUpdateTick >= 0 && now > lastUpdateTick) Severity -= (now - lastUpdateTick) / 120000f;
            lastUpdateTick = now;
        }
        public override void PostTickInterval(int delta)
        {
            base.PostTickInterval(delta);
            UpdateDecay();
        }
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref lastUpdateTick, "lastUpdateTick", -1);
        }
    }

    public sealed class Hediff_MushroomHallucination : HediffWithComps
    {
        private const float MinimumControlHours = 6f;
        private const float MaximumControlHours = 24f;
        private const int MaximumEpisodeTicks = 60000;
        private int episodeStartTick = -1;
        private int endTick = -1;
        private float totalDoses;
        private bool startAttempted;
        private bool panic;
        private IntVec3 anchor = IntVec3.Invalid;
        public int EpisodeStartTick => episodeStartTick;
        public int EndTick => endTick;
        public float TotalDoses => totalDoses;
        public bool Panic => panic;
        public bool StartAttempted => startAttempted;
        public override bool ShouldRemove => endTick >= 0 && Find.TickManager.TicksGame >= endTick;
        public override string TipStringExtra => base.TipStringExtra + "\n" + "RMush_HallucinationTime".Translate(
            Mathf.Max(0, endTick - Find.TickManager.TicksGame).ToStringTicksToPeriod());

        public void RegisterDose(MushroomExposureProperties settings, float doses, float tolerance)
        {
            int now = Find.TickManager.TicksGame;
            if (episodeStartTick < 0) episodeStartTick = now;
            totalDoses = Mathf.Min(4f, totalDoses + doses);
            float minHours = Mathf.Clamp(settings.hallucinationHoursMin, MinimumControlHours, MaximumControlHours);
            float maxHours = Mathf.Clamp(settings.hallucinationHoursMax, minHours, MaximumControlHours);
            // Ingredient quantities still scale mood, tolerance and the sampled
            // duration. Even a fractional food dose has a six-hour scheduled
            // episode; repeats cannot extend it past 24 hours from first exposure.
            float hours = Mathf.Clamp(Rand.Range(minHours, maxHours) * Mathf.Clamp(doses, 0.05f, 2f),
                MinimumControlHours, MaximumControlHours);
            int duration = Mathf.RoundToInt(hours * 2500f);
            endTick = Math.Min(episodeStartTick + MaximumEpisodeTicks, Math.Max(endTick, now + duration));
            // First ordinary exposure is not a random punishment. Repeated/high doses
            // can become unpleasant, and switching species cannot bypass tolerance.
            if (!panic && (tolerance >= 0.25f || totalDoses > 1.5f))
                panic = Rand.Chance(Mathf.Clamp(0.10f + tolerance * 0.35f + Mathf.Max(0f, totalDoses - 1.5f) * 0.10f, 0f, 0.65f));
            var state = pawn.MentalState as MentalState_MushroomWander;
            if (state != null) state.Configure(endTick, panic, anchor);
        }

        public void TryStartWandering()
        {
            if (startAttempted || ShouldRemove || pawn == null || pawn.Dead) return;
            // Never replace an unrelated mental state. Nor restart after rescue,
            // sedation, sleep, or ordinary recovery during the same exposure episode.
            if (pawn.Downed || !pawn.Awake() || pawn.InMentalState)
            {
                startAttempted = true;
                return;
            }
            if (!pawn.Spawned || pawn.mindState?.mentalStateHandler == null) return;
            startAttempted = true;
            anchor = pawn.Position;
            if (pawn.mindState.mentalStateHandler.TryStartMentalState(DefDatabase<MentalStateDef>.GetNamed("RMush_HallucinatoryWander"),
                "RMush_HallucinationReason".Translate(sourceLabel), forced: false, forceWake: false, causedByMood: false))
                (pawn.MentalState as MentalState_MushroomWander)?.Configure(endTick, panic, anchor);
        }

        public override void PostTickInterval(int delta)
        {
            base.PostTickInterval(delta);
            if (!startAttempted && !ShouldRemove) TryStartWandering();
        }
        public override void PostRemoved()
        {
            base.PostRemoved();
            var state = pawn.MentalState as MentalState_MushroomWander;
            if (state != null) state.RecoverFromState();
        }
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref episodeStartTick, "episodeStartTick", -1);
            Scribe_Values.Look(ref endTick, "endTick", -1);
            Scribe_Values.Look(ref totalDoses, "totalDoses", 0f);
            Scribe_Values.Look(ref startAttempted, "startAttempted", false);
            Scribe_Values.Look(ref panic, "panic", false);
            Scribe_Values.Look(ref anchor, "anchor", IntVec3.Invalid);
        }
    }

    public sealed class MentalState_MushroomWander : MentalState
    {
        private int endTick = -1;
        private bool panic;
        private IntVec3 anchor = IntVec3.Invalid;
        public int EndTick => endTick;
        public IntVec3 Anchor => anchor.IsValid ? anchor : pawn.Position;
        public bool Panic => panic;
        public override string InspectLine => (panic ? "RMush_HallucinationPanic" : "RMush_HallucinationWander").Translate()
            + " (" + Mathf.Max(0, endTick - Find.TickManager.TicksGame).ToStringTicksToPeriod() + ")";
        public void Configure(int until, bool unpleasant, IntVec3 origin)
        {
            endTick = until;
            panic = unpleasant;
            if (origin.IsValid) anchor = origin;
        }
        public override void PostStart(string reason)
        {
            base.PostStart(reason);
            var exposure = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDef.Named("RMush_Hallucination")) as Hediff_MushroomHallucination;
            Configure(exposure?.EndTick ?? Find.TickManager.TicksGame + 2500, exposure?.Panic ?? false, pawn.Position);
        }
        public override void MentalStateTick(int delta)
        {
            age += delta;
            if (pawn.Dead || pawn.Downed || !pawn.Awake() || endTick < 0 || Find.TickManager.TicksGame >= endTick)
                RecoverFromState();
        }
        public override RandomSocialMode SocialModeMax() => RandomSocialMode.Off;
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref endTick, "mushroomEndTick", -1);
            Scribe_Values.Look(ref panic, "mushroomPanic", false);
            Scribe_Values.Look(ref anchor, "mushroomAnchor", IntVec3.Invalid);
        }
    }

    public sealed class JobGiver_MushroomWander : JobGiver_Wander
    {
        public JobGiver_MushroomWander()
        {
            wanderRadius = 6f;
            maxDanger = Danger.None;
            ticksBetweenWandersRange = new IntRange(120, 300);
            expiryInterval = 300;
            canBashDoors = false;
            locomotionUrgency = LocomotionUrgency.Walk;
            wanderDestValidator = (pawn, root, cell) => cell.InBounds(pawn.Map) && !cell.OnEdge(pawn.Map)
                && !cell.IsForbidden(pawn) && cell.Standable(pawn.Map) && !pawn.Map.exitMapGrid.IsExitCell(cell)
                && cell.GetDangerFor(pawn, pawn.Map) == Danger.None;
        }
        protected override IntVec3 GetWanderRoot(Pawn pawn) => (pawn.MentalState as MentalState_MushroomWander)?.Anchor ?? pawn.Position;
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!(pawn.MentalState is MentalState_MushroomWander) || pawn.Downed) return null;
            var job = base.TryGiveJob(pawn);
            if (job != null) return job;
            job = JobMaker.MakeJob(JobDefOf.Wait_Wander);
            job.expiryInterval = 200;
            return job;
        }
    }
}
