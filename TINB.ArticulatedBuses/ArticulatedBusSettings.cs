using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace TINB.ArticulatedBuses
{
    /// <summary>
    /// Mod settings
    /// </summary>
    [FileLocation("ModsSettings/TINB.ArticulatedBuses/settings")] // game's default mod settings path
    [SettingsUIGroupOrder(DiagnosticsGroup, PreRemovalGroup)]
    [SettingsUIShowGroupName(DiagnosticsGroup, PreRemovalGroup)]
    public sealed class ArticulatedBusSettings : ModSetting
    {
        public const string MainTab = "Main";
        public const string DiagnosticsGroup = "Diagnostics";
        public const string PreRemovalGroup = "PreRemoval";

        /// <summary>
        /// Create the settings, bound to the mod
        /// </summary>
        public ArticulatedBusSettings(IMod mod) : base(mod)
        {
        }

        /// <summary>
        /// Developer diagnostic logging toggle
        /// </summary>
        [SettingsUISection(MainTab, DiagnosticsGroup)]
        public bool DiagnosticLogging { get; set; }

        /// <summary>
        /// Mod pre-removal cleanup button
        /// </summary>
        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(MainTab, PreRemovalGroup)]
        public bool RemoveAllArticulatedBuses
        {
            set { ArticulatedBusCleanupSystem.RequestCleanup(); }
        }

        /// <summary>
        /// Apply the default setting values
        /// </summary>
        public override void SetDefaults()
        {
            // Off by default (Debug logging is always on)
            DiagnosticLogging = false;
        }
    }
}
