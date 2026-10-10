using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMushrooms
{
    public enum MushroomPoisonPhase
    {
        Latent,
        Worsening,
        Stabilized,
        Recovering
    }

    // All durations are game time. No immunity, antidote, organ damage, or custom
    // medical jobs are required: ordinary tending drives the treatment reserve.
    public sealed class MushroomPoisonSettings : DefModExtension
    {
        public float latencyHoursMin = 1f;
        public float latencyHoursMax = 1f;
        public float recoveryDaysMin = 1f;
        public float recoveryDaysMax = 2f;
        public float untreatedSeverityPerDay = 0.2f;
        public float maximumSeverity = 1f;
        public bool fatal;
        public float minimumFatalExposureUnits = 0.25f;
        public float conditionalFatalThreshold;
        public float lethalSeverity = 0.95f;
        public float responseGraceHours = 24f;
        public float stabilizationTreatment = 1f;
        public float peakSeverity = 0.6f;

        public override IEnumerable<string> ConfigErrors()
        {
            if (latencyHoursMin < 0f || latencyHoursMax < latencyHoursMin)
                yield return "Mushroom poison latency must be a non-negative, ordered range.";
            if (recoveryDaysMin <= 0f || recoveryDaysMax < recoveryDaysMin)
                yield return "Mushroom poison recovery must be a positive, ordered range.";
            if (maximumSeverity <= 0f || untreatedSeverityPerDay < 0f || stabilizationTreatment <= 0f)
                yield return "Mushroom poison severity and treatment settings are invalid.";
            if (minimumFatalExposureUnits <= 0f || minimumFatalExposureUnits > 4f || conditionalFatalThreshold < 0f || conditionalFatalThreshold > 4f)
                yield return "Mushroom poison dose thresholds must be within the supported exposure range.";
            if ((fatal || conditionalFatalThreshold > 0f) && (lethalSeverity <= 0f || lethalSeverity > maximumSeverity || responseGraceHours < 24f))
                yield return "Fatal mushroom poison must allow at least 24 hours after its first symptom warning.";
        }
    }

    public static class MushroomPoisoning
    {
        // Raw ingestion and ingredient-provenance meals use the same fractional
        // dose entry point. Nutrition from the finished meal never creates a dose.
        public static Hediff_MushroomPoisoning ApplyExposure(Pawn pawn, ThingDef source, HediffDef hediff, float doseUnits)
        {
            if (hediff == null || pawn == null || pawn.Dead || !pawn.RaceProps.IsFlesh
                || float.IsNaN(doseUnits) || float.IsInfinity(doseUnits) || doseUnits <= 0f)
                return null;
            var poisoning = pawn.health.hediffSet.GetFirstHediffOfDef(hediff) as Hediff_MushroomPoisoning;
            if (poisoning != null && poisoning.ShouldRemove)
            {
                pawn.health.RemoveHediff(poisoning);
                poisoning = null;
            }
            if (poisoning == null)
            {
                poisoning = HediffMaker.MakeHediff(hediff, pawn) as Hediff_MushroomPoisoning;
                if (poisoning == null) return null;
                poisoning.sourceDef = source;
                poisoning.sourceLabel = source != null ? source.label : hediff.label;
                poisoning.RegisterExposure(doseUnits);
                pawn.health.AddHediff(poisoning);
            }
            else poisoning.RegisterExposure(doseUnits);
            return poisoning;
        }
    }

    public sealed class IngestionOutcomeDoer_MushroomPoison : IngestionOutcomeDoer
    {
        public HediffDef hediff;
        public int doseUnitCount = 1;

        protected override void DoIngestionOutcomeSpecial(Pawn pawn, Thing ingested, int ingestedCount)
        {
            if (ingested == null || ingestedCount <= 0) return;
            MushroomPoisoning.ApplyExposure(pawn, ingested.def, hediff,
                (float)ingestedCount / Mathf.Max(1, doseUnitCount));
        }
    }

    public sealed class Hediff_MushroomPoisoning : HediffWithComps
    {
        private const int TicksPerHour = 2500;
        private const int TicksPerDay = 60000;
        private const int UpdateInterval = 250;
        private const int MaximumExposures = 4;
        private const float ExposureSeverity = 0.15f;
        private const float MaximumCapacityPenalty = 0.8f;
        private static readonly MushroomPoisonSettings FallbackSettings = new MushroomPoisonSettings();
        private static readonly FieldInfo[] StageFields = typeof(HediffStage).GetFields(BindingFlags.Public | BindingFlags.Instance);

        private int onsetTick = -1;
        private int firstSymptomWarningTick = -1;
        private int lastProgressTick = -1;
        private int stabilizedTick = -1;
        private int recoveryEndTick = -1;
        private int treatmentUntilTick = -1;
        private int exposureCount;
        private float exposureUnits = -1f;
        private int treatmentCount;
        private int extraRecoveryTicks;
        private float recoveryDurationTicks;
        private float recoveryProgressTicks;
        private float treatmentReserve;
        private float lastTendQuality;
        private float peakSeverity;
        private float stabilizedSeverity;
        private bool stabilized;
        private bool onsetAnnounced;
        private bool stabilizationAnnounced;
        private bool exposureAnnounced;
        private HediffStage copiedStageSource;
        private HediffStage copiedStage;

        public MushroomPoisonSettings Settings => def.GetModExtension<MushroomPoisonSettings>() ?? FallbackSettings;
        private int Now => Find.TickManager.TicksGame;
        public bool IsLatent => onsetTick < 0 || Now < onsetTick;
        public bool IsStabilized => stabilized;
        public int OnsetTick => onsetTick;
        public int FirstSymptomWarningTick => firstSymptomWarningTick;
        public int RecoveryEndTick => recoveryEndTick;
        public float TreatmentReserve => treatmentReserve;
        public int ExposureCount => exposureCount;
        public float ExposureUnits => exposureUnits < 0f ? exposureCount : exposureUnits;
        public float PeakSeverity => peakSeverity;
        public bool IsLifeThreatening => (Settings.fatal && ExposureUnits >= Settings.minimumFatalExposureUnits)
            || (Settings.conditionalFatalThreshold > 0f && ExposureUnits >= Settings.conditionalFatalThreshold);
        private float InitialSymptomSeverity => 0.15f * Mathf.Min(1f, ExposureUnits);
        private HediffStage UnsharedStage => base.CurStage;

        public override HediffStage CurStage
        {
            get
            {
                var stage = base.CurStage;
                if (stage == null || stage.capMods == null || pawn == null) return stage;
                if (copiedStageSource != stage)
                {
                    copiedStageSource = stage;
                    copiedStage = new HediffStage();
                    foreach (var field in StageFields) field.SetValue(copiedStage, field.GetValue(stage));
                    copiedStage.capMods = new List<PawnCapacityModifier>();
                    foreach (var modifier in stage.capMods)
                        copiedStage.capMods.Add(new PawnCapacityModifier {
                            capacity = modifier.capacity, offset = modifier.offset, postFactor = modifier.postFactor,
                            setMax = modifier.setMax, statFactorMod = modifier.statFactorMod,
                            setMaxCurveOverride = modifier.setMaxCurveOverride, setMaxCurveEvaluateStat = modifier.setMaxCurveEvaluateStat
                        });
                }
                // Mixed species remain separate diagnosable conditions, but their
                // additive offsets cannot kill a healthy pawn ahead of warning
                // grace. Other illness, injury and modifier types are unchanged.
                for (int index = 0; index < stage.capMods.Count; index++)
                {
                    var modifier = stage.capMods[index];
                    float totalPenalty = 0f;
                    if (modifier.offset < 0f)
                        foreach (var existing in pawn.health.hediffSet.hediffs)
                        {
                            var poison = existing as Hediff_MushroomPoisoning;
                            var poisonStage = poison != null ? poison.UnsharedStage : null;
                            if (poisonStage == null || poisonStage.capMods == null) continue;
                            foreach (var other in poisonStage.capMods)
                                if (other.capacity == modifier.capacity && other.offset < 0f) totalPenalty -= other.offset;
                        }
                    float maximumPenalty = modifier.capacity == PawnCapacityDefOf.Consciousness
                        ? ConsciousnessPenaltyBudget() : MaximumCapacityPenalty;
                    copiedStage.capMods[index].offset = modifier.offset * (totalPenalty > maximumPenalty
                        ? maximumPenalty / totalPenalty : 1f);
                }
                return copiedStage;
            }
        }

        private float ConsciousnessPenaltyBudget()
        {
            // Native consciousness subtracts pain and applies vital-capacity
            // cascades before direct offsets. Capping offsets alone at -0.8
            // can therefore kill a healthy pawn when two poisons combine.
            // Reproduce only poison-origin contributions, without increasing
            // body-part health or suppressing other illness/injury effects.
            float pain = 0f, painFactor = 1f, filtration = 0f, pumping = 0f, breathing = 0f;
            foreach (var existing in pawn.health.hediffSet.hediffs)
            {
                var poison = existing as Hediff_MushroomPoisoning;
                var stage = poison != null ? poison.UnsharedStage : null;
                if (stage == null) continue;
                pain += Mathf.Max(0f, stage.painOffset);
                painFactor *= stage.painFactor;
                if (stage.capMods == null) continue;
                foreach (var modifier in stage.capMods)
                {
                    if (modifier.offset >= 0f) continue;
                    if (modifier.capacity == PawnCapacityDefOf.BloodFiltration) filtration -= modifier.offset;
                    else if (modifier.capacity == PawnCapacityDefOf.BloodPumping) pumping -= modifier.offset;
                    else if (modifier.capacity == PawnCapacityDefOf.Breathing) breathing -= modifier.offset;
                }
            }
            if (pawn.genes != null) painFactor *= pawn.genes.PainFactor;
            if (pawn.story != null && pawn.story.traits != null)
                foreach (var trait in pawn.story.traits.allTraits) painFactor *= trait.CurrentData.painFactor;
            float painPenalty = Mathf.Clamp((pain * painFactor - 0.1f) / 0.9f * 0.4f, 0f, 0.4f);
            float healthyBase = (1f - painPenalty)
                * (1f - 0.2f * Mathf.Min(MaximumCapacityPenalty, pumping))
                * (1f - 0.2f * Mathf.Min(MaximumCapacityPenalty, breathing))
                * (1f - 0.1f * Mathf.Min(MaximumCapacityPenalty, filtration));
            // Reserve 0.25 before the shared hallucination offset (-0.1), so
            // otherwise healthy mixed-poison patients remain alive and downed.
            return Mathf.Clamp(healthyBase - 0.25f, 0.05f, MaximumCapacityPenalty);
        }

        public MushroomPoisonPhase Phase
        {
            get
            {
                if (IsLatent) return MushroomPoisonPhase.Latent;
                if (stabilized)
                    return recoveryProgressTicks < recoveryDurationTicks * 0.08f
                        ? MushroomPoisonPhase.Stabilized : MushroomPoisonPhase.Recovering;
                if (!IsLifeThreatening && recoveryProgressTicks > recoveryDurationTicks * 0.25f)
                    return MushroomPoisonPhase.Recovering;
                return MushroomPoisonPhase.Worsening;
            }
        }

        public override bool Visible => true;
        public override float TendPriority => IsLifeThreatening && !stabilized ? 1f : 0.5f;
        public override string SeverityLabel => Severity.ToStringPercent();
        public override string LabelInBrackets => base.LabelInBrackets + ", " +
            ("RMush_PoisonPhase" + Phase).Translate();

        public override string TipStringExtra
        {
            get
            {
                var text = new StringBuilder(base.TipStringExtra);
                text.AppendLine("RMush_PoisonTipPhase".Translate(("RMush_PoisonPhase" + Phase).Translate()));
                if (IsLatent && onsetTick >= 0)
                    text.AppendLine("RMush_PoisonTipOnset".Translate((onsetTick - Now).ToStringTicksToPeriod()));
                text.AppendLine("RMush_PoisonTipTreatment".Translate(
                    Mathf.Clamp01(treatmentReserve / Settings.stabilizationTreatment).ToStringPercent()));
                if (recoveryEndTick >= 0 && (!IsLifeThreatening || stabilized))
                    text.AppendLine("RMush_PoisonTipRecovery".Translate(
                        Mathf.Max(0, recoveryEndTick - Now).ToStringTicksToPeriod()));
                if (IsLifeThreatening && !stabilized)
                {
                    text.AppendLine("RMush_PoisonDangerNote".Translate());
                    if (firstSymptomWarningTick >= 0)
                    {
                        int safeTicks = firstSymptomWarningTick + GraceTicks - Now;
                        if (safeTicks > 0)
                            text.AppendLine("RMush_PoisonTipGrace".Translate(safeTicks.ToStringTicksToPeriod()));
                    }
                }
                return text.ToString().TrimEnd();
            }
        }

        private int GraceTicks => Mathf.RoundToInt(Mathf.Max(24f, Settings.responseGraceHours) * TicksPerHour);
        private int MinimumRecoveryTicks => Mathf.RoundToInt(Settings.recoveryDaysMin * TicksPerDay);

        public void RegisterExposure(int doses) => RegisterExposure((float)doses);

        public void RegisterExposure(float doses)
        {
            if (float.IsNaN(doses) || float.IsInfinity(doses) || doses <= 0f) return;
            doses = Mathf.Min(MaximumExposures, doses);
            if (onsetTick < 0)
            {
                onsetTick = Now + Mathf.RoundToInt(Rand.Range(Settings.latencyHoursMin, Settings.latencyHoursMax) * TicksPerHour);
                lastProgressTick = Now;
                exposureUnits = Mathf.Min(MaximumExposures, doses);
                exposureCount = Mathf.CeilToInt(exposureUnits);
                peakSeverity = Mathf.Min(Settings.maximumSeverity,
                    Settings.peakSeverity * Mathf.Min(1f, exposureUnits) + Mathf.Max(0f, exposureUnits - 1f) * ExposureSeverity);
                recoveryDurationTicks = RandomRecoveryDuration();
                recoveryEndTick = IsLifeThreatening ? -1 : onsetTick + Mathf.CeilToInt(recoveryDurationTicks);
                Severity = 0.01f * Mathf.Min(1f, exposureUnits);
            }
            else
            {
                AdvancePoison();
                if (pawn != null && pawn.Dead) return;
                float oldExposureUnits = ExposureUnits;
                bool wasLifeThreatening = IsLifeThreatening;
                exposureUnits = Mathf.Min(MaximumExposures, oldExposureUnits + doses);
                exposureCount = Mathf.CeilToInt(exposureUnits);
                peakSeverity = Mathf.Min(Settings.maximumSeverity,
                    Settings.peakSeverity * Mathf.Min(1f, exposureUnits) + Mathf.Max(0f, exposureUnits - 1f) * ExposureSeverity);
                // Repeated doses remain one condition with a bounded severity and
                // at most one extra day of recovery; they do not stack new hediffs.
                float addedDoses = exposureUnits - oldExposureUnits;
                int extra = Mathf.Min(TicksPerDay - extraRecoveryTicks, Mathf.RoundToInt(addedDoses * TicksPerDay / 4f));
                extraRecoveryTicks += extra;
                recoveryDurationTicks += extra;
                if (!IsLatent) Severity = Mathf.Min(Settings.maximumSeverity, Severity + addedDoses * ExposureSeverity);
                if (addedDoses > 0f)
                    treatmentReserve = Mathf.Min(treatmentReserve * Mathf.Pow(0.7f, addedDoses), Settings.stabilizationTreatment * 0.6f);
                if ((!wasLifeThreatening && IsLifeThreatening) || (stabilized && IsLifeThreatening && addedDoses > 0f))
                {
                    stabilized = false;
                    stabilizationAnnounced = false;
                    stabilizedTick = -1;
                    recoveryEndTick = -1;
                    recoveryProgressTicks = 0f;
                }
                if (!wasLifeThreatening && IsLifeThreatening && onsetAnnounced)
                    SendNotice("RMush_PoisonOnsetLabel", "RMush_PoisonOnsetText", def.label, true);
                if (IsLifeThreatening) recoveryEndTick = -1;
            }
            if (!exposureAnnounced)
            {
                exposureAnnounced = true;
                SendNotice("RMush_PoisonExposureLabel", "RMush_PoisonExposureText", sourceLabel ?? def.label, false);
            }
        }

        public override void PostAdd(DamageInfo? dinfo)
        {
            base.PostAdd(dinfo);
            if (onsetTick < 0) RegisterExposure(1);
        }

        public override void PostTick()
        {
            base.PostTick();
            AdvancePoison();
        }

        public override void PostTickInterval(int delta)
        {
            base.PostTickInterval(delta);
            AdvancePoison();
        }

        public override bool TendableNow(bool ignoreTimer = false)
        {
            SynchronizeTendExpiry();
            return base.TendableNow(ignoreTimer);
        }

        private void SynchronizeTendExpiry()
        {
            // The vanilla comp ticks every game tick. The absolute expiry also
            // keeps elapsed-time state correct after load and interval updates.
            if (treatmentUntilTick >= 0 && Now >= treatmentUntilTick)
            {
                var tend = this.TryGetComp<HediffComp_TendDuration>();
                // AllowTend compares overlap > ticksLeft strictly. With zero
                // overlap, zero would block every future treatment; restore the
                // vanilla untended sentinel instead.
                if (tend != null) tend.tendTicksLeft = -1;
            }
        }

        private void AdvancePoison()
        {
            if (pawn == null || pawn.Dead || onsetTick < 0) return;
            int now = Now;
            if (lastProgressTick < 0) lastProgressTick = now;
            if (now < lastProgressTick) lastProgressTick = now;
            if (now - lastProgressTick < UpdateInterval && now < onsetTick) return;
            if (now - lastProgressTick < UpdateInterval && onsetAnnounced) return;
            int previousTick = lastProgressTick;
            lastProgressTick = now;
            SynchronizeTendExpiry();
            if (now < onsetTick) return;

            if (!onsetAnnounced)
            {
                onsetAnnounced = true;
                // Grace begins when the first symptom warning is actually issued,
                // never at ingestion or at an unseen, scheduled latency deadline.
                firstSymptomWarningTick = now;
                Severity = Mathf.Max(InitialSymptomSeverity, Severity);
                SendNotice("RMush_PoisonOnsetLabel", "RMush_PoisonOnsetText", def.label, IsLifeThreatening);
            }
            int delta = Mathf.Max(0, now - Mathf.Max(previousTick, onsetTick));
            bool tended = treatmentUntilTick > now;
            if (!stabilized && treatmentCount >= 2 && treatmentReserve >= Settings.stabilizationTreatment)
            {
                Stabilize();
                if (stabilized) delta = 0;
            }

            float recoverySpeed = 1f + (tended ? Mathf.Clamp01(lastTendQuality) * 0.15f : 0f)
                + (pawn.InBed() ? 0.1f : 0f);
            if (stabilized)
            {
                recoveryProgressTicks += delta * recoverySpeed;
                int elapsed = now - stabilizedTick;
                float fraction = Mathf.Clamp01(recoveryProgressTicks / recoveryDurationTicks);
                Severity = Mathf.Max(0.01f, stabilizedSeverity * (1f - fraction));
                if (fraction >= 1f && elapsed >= MinimumRecoveryTicks) Severity = 0f;
                recoveryEndTick = now + Mathf.CeilToInt(Mathf.Max(
                    (recoveryDurationTicks - recoveryProgressTicks) / recoverySpeed, MinimumRecoveryTicks - elapsed));
            }
            else if (!IsLifeThreatening)
            {
                recoveryProgressTicks += delta * recoverySpeed;
                float fraction = Mathf.Clamp01(recoveryProgressTicks / recoveryDurationTicks);
                Severity = fraction <= 0.25f
                    ? Mathf.Lerp(InitialSymptomSeverity, peakSeverity, fraction / 0.25f)
                    : Mathf.Max(0.01f, peakSeverity * (1f - (fraction - 0.25f) / 0.75f));
                if (fraction >= 1f && now - onsetTick >= MinimumRecoveryTicks) Severity = 0f;
                recoveryEndTick = now + Mathf.CeilToInt(Mathf.Max(
                    (recoveryDurationTicks - recoveryProgressTicks) / recoverySpeed, MinimumRecoveryTicks - (now - onsetTick)));
            }
            else
            {
                float suppression = tended ? Mathf.Lerp(0.5f, 0.1f, Mathf.Clamp01(lastTendQuality)) : 1f;
                float increase = Settings.untreatedSeverityPerDay * delta / TicksPerDay * suppression
                    * Mathf.Clamp(ExposureUnits, 0.25f, 2f);
                Severity = Mathf.Min(Settings.maximumSeverity, Severity + increase);
                if (!tended) treatmentReserve = Mathf.Max(0f, treatmentReserve - delta / (float)TicksPerDay * 0.04f);
            }
            if (Severity > 0f && CauseDeathNow()) pawn.Kill(null, this);
        }

        public override void Tended(float quality, float maxQuality, int batchPosition = 0)
        {
            AdvancePoison();
            if (pawn.Dead) return;
            base.Tended(quality, maxQuality, batchPosition);
            var tend = this.TryGetComp<HediffComp_TendDuration>();
            lastTendQuality = tend != null ? tend.tendQuality : Mathf.Clamp(quality, 0f, maxQuality);
            treatmentUntilTick = Now + (tend != null ? tend.tendTicksLeft : 6 * TicksPerHour);
            treatmentCount++;
            // Even glitterworld-quality tending needs another treatment. A good
            // doctor still suppresses deterioration and stabilizes sooner.
            treatmentReserve = Mathf.Min(Settings.stabilizationTreatment * 2f,
                treatmentReserve + Mathf.Min(0.65f, 0.18f + 0.55f * Mathf.Clamp01(lastTendQuality)));
            if (!IsLatent && !stabilized && treatmentCount >= 2 && treatmentReserve >= Settings.stabilizationTreatment)
                Stabilize();
        }

        private void Stabilize()
        {
            // Mild poisons recover on their onset schedule instead of restarting
            // a multi-day recovery clock merely because a doctor treated them.
            if (!IsLifeThreatening) return;
            stabilized = true;
            stabilizedTick = Now;
            stabilizedSeverity = Severity;
            recoveryProgressTicks = 0f;
            recoveryDurationTicks = RandomRecoveryDuration() + extraRecoveryTicks;
            // Very late rescue can take longer than the normal target range.
            if (stabilizedSeverity >= 0.7f) recoveryDurationTicks *= 1.25f;
            recoveryEndTick = Now + Mathf.CeilToInt(recoveryDurationTicks);
            if (!stabilizationAnnounced)
            {
                stabilizationAnnounced = true;
                SendNotice("RMush_PoisonStabilizedLabel", "RMush_PoisonStabilizedText", def.label, false);
            }
        }

        private float RandomRecoveryDuration()
        {
            return Mathf.Max(1f, Rand.Range(Settings.recoveryDaysMin, Settings.recoveryDaysMax) * TicksPerDay);
        }

        public override bool CauseDeathNow()
        {
            return IsLifeThreatening && !IsLatent && !stabilized && firstSymptomWarningTick >= 0
                && Now - firstSymptomWarningTick >= GraceTicks && Severity >= Settings.lethalSeverity;
        }

        public override bool TryMergeWith(Hediff other)
        {
            if (other == null || other.def != def || other.Part != Part) return false;
            var poison = other as Hediff_MushroomPoisoning;
            RegisterExposure(poison != null && poison.ExposureUnits > 0f ? poison.ExposureUnits : 1f);
            return true;
        }

        private void SendNotice(string labelKey, string textKey, string mushroomLabel, bool urgent)
        {
            if (!PawnUtility.ShouldSendNotificationAbout(pawn) || Find.LetterStack == null) return;
            var text = textKey.Translate(pawn.LabelShortCap, mushroomLabel);
            if (urgent) text += "\n\n" + "RMush_PoisonDangerNote".Translate();
            Find.LetterStack.ReceiveLetter(labelKey.Translate(), text,
                urgent ? LetterDefOf.ThreatBig : LetterDefOf.NegativeEvent, pawn);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref onsetTick, "mushroomOnsetTick", -1);
            Scribe_Values.Look(ref firstSymptomWarningTick, "mushroomFirstSymptomWarningTick", -1);
            Scribe_Values.Look(ref lastProgressTick, "mushroomLastProgressTick", -1);
            Scribe_Values.Look(ref stabilizedTick, "mushroomStabilizedTick", -1);
            Scribe_Values.Look(ref recoveryEndTick, "mushroomRecoveryEndTick", -1);
            Scribe_Values.Look(ref treatmentUntilTick, "mushroomTreatmentUntilTick", -1);
            Scribe_Values.Look(ref exposureCount, "mushroomExposureCount", 0);
            // A missing float is an old v0.4.0 save. Its integer doses retain the
            // same course; new fractional meal doses survive save/load exactly.
            Scribe_Values.Look(ref exposureUnits, "mushroomExposureUnits", -1f);
            Scribe_Values.Look(ref treatmentCount, "mushroomTreatmentCount", 0);
            Scribe_Values.Look(ref extraRecoveryTicks, "mushroomExtraRecoveryTicks", 0);
            Scribe_Values.Look(ref recoveryDurationTicks, "mushroomRecoveryDurationTicks", 0f);
            Scribe_Values.Look(ref recoveryProgressTicks, "mushroomRecoveryProgressTicks", 0f);
            Scribe_Values.Look(ref treatmentReserve, "mushroomTreatmentReserve", 0f);
            Scribe_Values.Look(ref lastTendQuality, "mushroomLastTendQuality", 0f);
            Scribe_Values.Look(ref peakSeverity, "mushroomPeakSeverity", 0f);
            Scribe_Values.Look(ref stabilizedSeverity, "mushroomStabilizedSeverity", 0f);
            Scribe_Values.Look(ref stabilized, "mushroomStabilized", false);
            Scribe_Values.Look(ref onsetAnnounced, "mushroomOnsetAnnounced", false);
            Scribe_Values.Look(ref stabilizationAnnounced, "mushroomStabilizationAnnounced", false);
            Scribe_Values.Look(ref exposureAnnounced, "mushroomExposureAnnounced", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && exposureUnits < 0f)
            {
                exposureUnits = Mathf.Clamp(exposureCount, 1, MaximumExposures);
                // Old high-dose fly agaric/sulfur tuft saves were nonfatal. The
                // expanded danger must warn again rather than kill on loading.
                if (Settings.conditionalFatalThreshold > 0f && IsLifeThreatening && onsetAnnounced && !stabilized)
                {
                    firstSymptomWarningTick = Now;
                    recoveryEndTick = -1;
                    recoveryProgressTicks = 0f;
                    SendNotice("RMush_PoisonOnsetLabel", "RMush_PoisonOnsetText", def.label, true);
                }
            }
        }
    }
}
