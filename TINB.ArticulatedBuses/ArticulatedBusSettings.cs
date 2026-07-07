using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace TINB.ArticulatedBuses
{
    /* The mod's options page (registered in all builds). Holds the Diagnostic logging toggle. In a Release build the
       toggle drives diagnostic logging (off by default). In a Debug build diagnostic logging is always on (compile
       symbol), so the toggle is shown but inert there — it just lets us see exactly what Release users see. */
    [FileLocation("ModsSettings/TINB.ArticulatedBuses/settings")]
    [SettingsUIGroupOrder(DiagnosticsGroup, PreRemovalGroup)]
    [SettingsUIShowGroupName(DiagnosticsGroup, PreRemovalGroup)]
    public sealed class ArticulatedBusSettings : ModSetting
    {
        public const string MainTab = "Main";
        public const string DiagnosticsGroup = "Diagnostics";
        public const string PreRemovalGroup = "PreRemoval";

        public ArticulatedBusSettings(IMod mod) : base(mod)
        {
        }

        /* Runtime override of the compile-time diagnostics default; read by Mod.IsDiagnosticLoggingEnabled */
        [SettingsUISection(MainTab, DiagnosticsGroup)]
        public bool DiagnosticLogging { get; set; }

        /* Mod pre-removal cleanup (one-shot): delete every articulated bus in the city (lines/depots stay; they
           dispatch new buses). Precautionary, for users who plan to remove the mod and want a 100%-clean save first;
           doubles as a save repair. Confirmed via the vanilla dialog; only accepted while in a game. */
        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(MainTab, PreRemovalGroup)]
        public bool RemoveAllArticulatedBuses
        {
            set { ArticulatedBusCleanupSystem.RequestCleanup(); }
        }

        public override void SetDefaults()
        {
            // Off by default: the Release default, and what a Debug build shows too (Debug logging is always on anyway)
            DiagnosticLogging = false;
        }
    }
}
