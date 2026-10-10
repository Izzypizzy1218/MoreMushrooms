using System;
using System.Reflection;
using Verse;

namespace RimMushroomsTests
{
    // Keep full exception stacks in this isolated test process. Harmony's normal
    // cache can consume the first rendering when Unity reads Exception.StackTrace
    // repeatedly before it writes the message to Player.log.
    [StaticConstructorOnStartup]
    internal static class RuntimeDiagnostics
    {
        internal static bool FullExceptionStacksEnabled { get; private set; }

        static RuntimeDiagnostics()
        {
            try
            {
                var settings = Type.GetType("HarmonyMod.Settings, HarmonyMod", throwOnError: false);
                var field = settings?.GetField("noStacktraceCaching", BindingFlags.Public | BindingFlags.Static);
                if (field == null || field.FieldType != typeof(bool))
                    throw new MissingFieldException("HarmonyMod.Settings", "noStacktraceCaching");
                field.SetValue(null, true);
                FullExceptionStacksEnabled = (bool)field.GetValue(null);
                if (!FullExceptionStacksEnabled)
                    throw new InvalidOperationException("Harmony stacktrace caching remained enabled.");
                // Do not call ModSettings.Write: only this test process changes.
                Log.Message("[Rim Mushrooms Tests] Diagnostics: full exception stacks enabled; Harmony stacktrace caching disabled for this process only.");
            }
            catch (Exception exception)
            {
                Log.Error("[Rim Mushrooms Tests] DIAGNOSTIC FAILURE: could not enable full exception stacks: "
                    + exception.GetType().Name + ": " + exception.Message);
            }
        }
    }
}
