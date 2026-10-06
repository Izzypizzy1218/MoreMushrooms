using System;
using System.Collections.Generic;
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
            if (fatal && (lethalSeverity <= 0f || lethalSeverity > maximumSeverity || responseGraceHours < 24f))
                yield return "Fatal mushroom poison must allow at least 24 hours after its first symptom warning.";
        }
    }

    public sealed class IngestionOutcomeDoer_MushroomPoison : IngestionOutcomeDoer
    {
        public HediffDef hediff;
        public int doseUnitCount = 1;

        protected override void DoIngestionOutcomeSpecial(Pawn pawn, Thing ingested, int ingestedCount)
        {
            if (hediff == null || pawn == null || pawn.Dead || !pawn.RaceProps.IsFlesh || ingestedCount <= 0)
                return;
            var poisoning = pawn.health.hediffSet.GetFirstHediffOfDef(hediff) as Hediff_MushroomPoisoning;
            int doses = Mathf.Clamp(Mathf.CeilToInt((float)ingestedCount / Mathf.Max(1, doseUnitCount)), 1, 4);
            if (poisoning == null)
            {
                poisoning = HediffMaker.MakeHediff(hediff, pawn) as Hediff_MushroomPoisoning;
                if (poisoning == null) return;
                poisoning.sourceDef = ingested.def;
                poisoning.sourceLabel = ingested.def.label;
                poisoning.RegisterExposure(doses);
                pawn.health.AddHediff(poisoning);
            }
            else
            {
                poisoning.RegisterExposure(doses);
            }
        }
    }

    public sealed class Hediff_MushroomPoisoning : HediffWithComps
    {
        private const int TicksPerHour = 2500;
        private const int TicksPerDay = 60000;
        private const int UpdateInterval = 250;
        private const int MaximumExposures = 4;
        private const float ExposureSeverity = 0.15f;
        private static readonly MushroomPoisonSettings FallbackSettings = new MushroomPoisonSettings();

        private int onsetTick = -1;
        private int firstSymptomWarningTick = -1;
        private int lastProgressTick = -1;
        private int stabilizedTick = -1;
        private int recoveryEndTick = -1;
        private int treatmentUntilTick = -1;
        private int exposureCount;
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

        public MushroomPoisonSettings Settings => def.GetModExtension<MushroomPoisonSettings>() ?? FallbackSettings;
        private int Now => Find.TickManager.TicksGame;
        public bool IsLatent => onsetTick < 0 || Now < onsetTick;
        public bool IsStabilized => stabilized;
        public int OnsetTick => onsetTick;
        public int FirstSymptomWarningTick => firstSymptomWarningTick;
        public int RecoveryEndTick => recoveryEndTick;
        public float TreatmentReserve => treatmentReserve;
        public int ExposureCount => exposureCount;
        public float PeakSeverity => peakSeverity;

        public MushroomPoisonPhase Phase
        {
            get
            {
                if (IsLatent) return MushroomPoisonPhase.Latent;
                if (stabilized)
                    return recoveryProgressTicks < recoveryDurationTicks * 0.08f
                        ? MushroomPoisonPhase.Stabilized : MushroomPoisonPhase.Recovering;
                if (!Settings.fatal && recoveryProgressTicks > recoveryDurationTicks * 0.25f)
                    return MushroomPoisonPhase.Recovering;
                return MushroomPoisonPhase.Worsening;
            }
        }

        public override bool Visible => true;
        public override float TendPriority => Settings.fatal && !stabilized ? 1f : 0.5f;
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
                if (recoveryEndTick >= 0 && (!Settings.fatal || stabilized))
                    text.AppendLine("RMush_PoisonTipRecovery".Translate(
                        Mathf.Max(0, recoveryEndTick - Now).ToStringTicksToPeriod()));
                if (Settings.fatal && !stabilized)
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

        internal void RegisterExposure(int doses)
        {
            if (onsetTick < 0)
            {
                onsetTick = Now + Mathf.RoundToInt(Rand.Range(Settings.latencyHoursMin, Settings.latencyHoursMax) * TicksPerHour);
                lastProgressTick = Now;
                exposureCount = Mathf.Clamp(doses, 1, MaximumExposures);
                peakSeverity = Mathf.Min(Settings.maximumSeverity,
                    Settings.peakSeverity + (exposureCount - 1) * ExposureSeverity);
                recoveryDurationTicks = RandomRecoveryDuration();
                recoveryEndTick = Settings.fatal ? -1 : onsetTick + Mathf.CeilToInt(recoveryDurationTicks);
                Severity = 0.01f;
            }
            else
            {
                AdvancePoison();
                int oldExposureCount = exposureCount;
                exposureCount = Mathf.Clamp(exposureCount + doses, 1, MaximumExposures);
                peakSeverity = Mathf.Min(Settings.maximumSeverity,
                    Settings.peakSeverity + (exposureCount - 1) * ExposureSeverity);
                // Repeated doses remain one condition with a bounded severity and
                // at most one extra day of recovery; they do not stack new hediffs.
                int addedDoses = exposureCount - oldExposureCount;
                int extra = Mathf.Min(TicksPerDay - extraRecoveryTicks, addedDoses * TicksPerDay / 4);
                extraRecoveryTicks += extra;
                recoveryDurationTicks += extra;
                if (!IsLatent) Severity = Mathf.Min(Settings.maximumSeverity, Severity + doses * ExposureSeverity);
                treatmentReserve = Mathf.Min(treatmentReserve * 0.7f, Settings.stabilizationTreatment * 0.6f);
                if (stabilized && Settings.fatal)
                {
                    stabilized = false;
                    stabilizationAnnounced = false;
                    stabilizedTick = -1;
                    recoveryEndTick = -1;
                    recoveryProgressTicks = 0f;
                }
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
                Severity = Mathf.Max(0.15f, Severity);
                SendNotice("RMush_PoisonOnsetLabel", "RMush_PoisonOnsetText", def.label, Settings.fatal);
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
            else if (!Settings.fatal)
            {
                recoveryProgressTicks += delta * recoverySpeed;
                float fraction = Mathf.Clamp01(recoveryProgressTicks / recoveryDurationTicks);
                Severity = fraction <= 0.25f
                    ? Mathf.Lerp(0.15f, peakSeverity, fraction / 0.25f)
                    : Mathf.Max(0.01f, peakSeverity * (1f - (fraction - 0.25f) / 0.75f));
                if (fraction >= 1f && now - onsetTick >= MinimumRecoveryTicks) Severity = 0f;
                recoveryEndTick = now + Mathf.CeilToInt(Mathf.Max(
                    (recoveryDurationTicks - recoveryProgressTicks) / recoverySpeed, MinimumRecoveryTicks - (now - onsetTick)));
            }
            else
            {
                float suppression = tended ? Mathf.Lerp(0.5f, 0.1f, Mathf.Clamp01(lastTendQuality)) : 1f;
                float increase = Settings.untreatedSeverityPerDay * delta / TicksPerDay * suppression;
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
            if (!Settings.fatal) return;
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
            return Settings.fatal && !IsLatent && !stabilized && firstSymptomWarningTick >= 0
                && Now - firstSymptomWarningTick >= GraceTicks && Severity >= Settings.lethalSeverity;
        }

        public override bool TryMergeWith(Hediff other)
        {
            if (other == null || other.def != def || other.Part != Part) return false;
            var poison = other as Hediff_MushroomPoisoning;
            RegisterExposure(Mathf.Max(1, poison != null ? poison.exposureCount : 1));
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
        }
    }
}
