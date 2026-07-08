using System.IO;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace TINB.ArticulatedBuses
{
    /// <summary>
    /// The mod entry point
    /// </summary>
    /// <remarks>
    /// Registers the settings and schedules the mod's systems into the game's update phases
    /// </remarks>
    public sealed class Mod : IMod
    {
        /// <summary>
        /// Developer log channel
        /// </summary>
        /// <remarks>
        /// Writes to TINB.ArticulatedBuses.log, separate from the user-facing SessionLog
        /// </remarks>
        public static readonly ILog Log = LogManager.GetLogger(nameof(TINB) + "." + nameof(ArticulatedBuses)).SetShowsErrorsInUI(false);

        /// <summary>
        /// The mod's options page
        /// </summary>
        public static ArticulatedBusSettings? Settings;

        /// <summary>
        /// Whether the game is in an active city
        /// </summary>
        /// <returns>True when a city simulation is running (not editor, menu, or loading)</returns>
        public static bool IsInGame()
        {
            return GameManager.instance != null && GameManager.instance.gameMode.IsGame();
        }

        /// <summary>
        /// Whether the game is in the asset or map editor
        /// </summary>
        /// <returns>True when the editor is active</returns>
        public static bool IsInEditor()
        {
            return GameManager.instance != null && GameManager.instance.gameMode.IsEditor();
        }

        /// <summary>
        /// Whether developer diagnostic logging is enabled
        /// </summary>
        /// <remarks>
        /// In a debug build always on; in a release build the user switches it on in settings
        /// </remarks>
        public static bool IsDiagnosticLoggingEnabled()
        {
            #if ARTICULATEDBUSES_DIAGNOSTICS
            return true;
            #else
            return Settings != null && Settings.DiagnosticLogging;
            #endif
        }

        /// <summary>
        /// Register the settings and schedule the mod's systems
        /// </summary>
        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info("Loading ArticulatedBuses Mod");

            // Options page
            Settings = new ArticulatedBusSettings(this);
            Settings.RegisterInOptionsUI();

            // Load translations from the deployed lang/ folder (one JSON per locale)
            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out ExecutableAsset modAsset))
            {
                string langDir = Path.Combine(Path.GetDirectoryName(modAsset.path), "lang");
                SettingsLocaleLoader.LoadFromFolder(Settings, langDir);
            }
            AssetDatabase.global.LoadSettings("TINB.ArticulatedBuses", Settings, new ArticulatedBusSettings(this));

            // Reset the session log (each load)
            updateSystem.UpdateAt<ArticulatedBusSessionLogSystem>(SystemUpdatePhase.Modification1);

            // Prefab handling

            // Identify and set up bus prefabs
            updateSystem.UpdateAfter<ArticulatedBusPrefabSetupSystem, Game.Prefabs.VehicleInitializeSystem>(SystemUpdatePhase.PrefabUpdate);

            // Spawn trailer prefabs
            updateSystem.UpdateBefore<ArticulatedBusTrailerSpawnSystem, Game.Vehicles.InitializeSystem>(SystemUpdatePhase.Modification5); // starting off right before vehicle initialisation in the last modification block of the simulation phase

            // Re-attach a missing trailer to a live front
            updateSystem.UpdateAfter<ArticulatedBusTrailerRestoreSystem, ArticulatedBusTrailerSpawnSystem>(SystemUpdatePhase.Modification5); // continuing at the end of the modification stage of the simulation phase

            // Delete orphaned trailers
            updateSystem.UpdateAfter<ArticulatedBusOrphanTrailerCleanupSystem, ArticulatedBusTrailerRestoreSystem>(SystemUpdatePhase.Modification5);

            // Remove all articulated buses (triggered manually by user via options-page button; e.g. before mod removal)
            updateSystem.UpdateAfter<ArticulatedBusCleanupSystem, ArticulatedBusOrphanTrailerCleanupSystem>(SystemUpdatePhase.Modification5);

            // Legacy Fix for 1.0.1 parked fronts
            // @TODO: Remove in any new version released after 31 July 2026
            updateSystem.UpdateAfter<ArticulatedBusParkedFrontFixSystem, ArticulatedBusCleanupSystem>(SystemUpdatePhase.Modification5);


            // Rendering

            // Framewise bone-rendering
            updateSystem.UpdateAfter<ArticulatedBusConnectionBoneSystem, Game.Rendering.ObjectInterpolateSystem>(SystemUpdatePhase.Rendering); // start after vanilla's vehicle transforms applied to solve the bend angle
            updateSystem.UpdateBefore<ArticulatedBusConnectionBoneSystem, Game.Rendering.ProceduralSkeletonSystem>(SystemUpdatePhase.Rendering); // update skeleton/bone buffers before processed in frame

            // Sync trailer color to the front
            updateSystem.UpdateAfter<ArticulatedBusColorSyncSystem, Game.Rendering.ObjectInterpolateSystem>(SystemUpdatePhase.Rendering);

            // Misc

            // Asset Icon Creator support (editor only)
            updateSystem.UpdateAt<ArticulatedBusIconCreatorCompatSystem>(SystemUpdatePhase.Modification1);
        }

        /// <summary>
        /// Unregister the options page on unload
        /// </summary>
        /// <remarks>
        /// So a mod reload can't register it twice
        /// </remarks>
        public void OnDispose()
        {
            Log.Info("Disposing ArticulatedBuses Mod");
            Settings?.UnregisterInOptionsUI();
            Settings = null;
        }
    }
}
