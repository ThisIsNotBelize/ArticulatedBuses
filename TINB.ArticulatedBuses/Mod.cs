using System.IO;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace TINB.ArticulatedBuses
{
    /* Mod entry point: schedules the systems and exposes the shared run-gate + diagnostics helpers */
    public sealed class Mod : IMod
    {
        public static readonly ILog Log = LogManager.GetLogger(nameof(TINB) + "." + nameof(ArticulatedBuses)).SetShowsErrorsInUI(false);

        /* The options page (registered in all builds; null only before OnLoad has run). In Release its
           DiagnosticLogging toggle drives IsDiagnosticLoggingEnabled; in Debug that is forced true regardless. */
        public static ArticulatedBusSettings? Settings;

        /* Run-gate for every gameplay system: true ONLY in an actual game (GameMode.Game). Must be positive (IsGame),
           NOT !IsEditor: during an editor LOAD the mode is still MainMenu (it flips to Editor only at
           OnGameLoadingComplete), so the old !IsEditor() form passed and let prefab setup/inflation mutate assets in
           the editor. Editor-time work is handled solely by the IsEditor()-gated ArticulatedBusIconCreatorCompatSystem. */
        public static bool ShouldRunRuntimeSystems()
        {
            return GameManager.instance != null && GameManager.instance.gameMode.IsGame();
        }

        /* Whether the developer diagnostic log is on. Two tiers: a Debug build ALWAYS logs (it exists for debugging);
           a Release build logs only when the user turns on the options toggle (off by default). */
        public static bool IsDiagnosticLoggingEnabled()
        {
            #if ARTICULATEDBUSES_DIAGNOSTICS
            return true;
            #else
            return Settings != null && Settings.DiagnosticLogging;
            #endif
        }

        /* Schedules each system into its update phase */
        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info("Loading ArticulatedBuses");

            /* Options page (all builds): the Diagnostic logging toggle. In Release it drives diagnostic logging (off
               by default); in Debug logging is always on and the toggle is shown but inert. */
            Settings = new ArticulatedBusSettings(this);
            Settings.RegisterInOptionsUI();
            /* Load translations from the deployed lang/ folder (one JSON per locale). No mod dependency. */
            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out ExecutableAsset modAsset))
            {
                string langDir = Path.Combine(Path.GetDirectoryName(modAsset.path), "lang");
                SettingsLocaleLoader.LoadFromFolder(Settings, langDir);
            }
            AssetDatabase.global.LoadSettings("TINB.ArticulatedBuses", Settings, new ArticulatedBusSettings(this));

            /* Reset the session log each load */
            updateSystem.UpdateAt<ArticulatedBusSessionLogSystem>(SystemUpdatePhase.Modification1);

            /* Prep applicable bus prefabs: trailer link, livery, doors, parking length */
            updateSystem.UpdateAfter<ArticulatedBusPrefabSetupSystem, Game.Prefabs.VehicleInitializeSystem>(SystemUpdatePhase.PrefabUpdate);

            /* Attach a trailers */
            updateSystem.UpdateBefore<ArticulatedBusTrailerSpawnSystem, Game.Vehicles.InitializeSystem>(SystemUpdatePhase.Modification5);

            /* Delete orphaned trailers (no front) */
            updateSystem.UpdateAfter<ArticulatedBusOrphanTrailerCleanupSystem, ArticulatedBusTrailerSpawnSystem>(SystemUpdatePhase.Modification5);

            /* Remove ALL articulated buses on request (options-page button; e.g. before mod removal) */
            updateSystem.UpdateAfter<ArticulatedBusCleanupSystem, ArticulatedBusTrailerSpawnSystem>(SystemUpdatePhase.Modification5);

            /* Fix for 1.0.1 parked fronts 
               @TODO: Remove in new version after 31 July 2026 */
            updateSystem.UpdateAfter<ArticulatedBusParkedFrontFixSystem, ArticulatedBusTrailerSpawnSystem>(SystemUpdatePhase.Modification5);

            /* Re-attach a missing trailer to a live front */
            updateSystem.UpdateAfter<ArticulatedBusTrailerRestoreSystem, ArticulatedBusParkedFrontFixSystem>(SystemUpdatePhase.Modification5);

            /* Periodically log the states to monitor past sources of CTDs (up to 1.0.2): a parked front still carrying a trailer layout
               (depot-upgrade CTD), a trailer whose front is gone, or a front with more than one trailer */
            updateSystem.UpdateAfter<ArticulatedBusSelfCheckSystem, ArticulatedBusTrailerRestoreSystem>(SystemUpdatePhase.Modification5);

            /* Asset Icon Creator support (editor) */
            updateSystem.UpdateAt<ArticulatedBusIconCreatorCompatSystem>(SystemUpdatePhase.Modification1);

            /* Framewise bone-rendering */
            updateSystem.UpdateAfter<ArticulatedBusConnectionBoneSystem, Game.Rendering.ObjectInterpolateSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateBefore<ArticulatedBusConnectionBoneSystem, Game.Rendering.ProceduralSkeletonSystem>(SystemUpdatePhase.Rendering);

            /* Sync trailer colour to the front */
            updateSystem.UpdateAfter<ArticulatedBusColorSyncSystem, Game.Rendering.ObjectInterpolateSystem>(SystemUpdatePhase.Rendering);
        }

        /* Unload hook: take the options page down so a mod reload can't register it twice */
        public void OnDispose()
        {
            Log.Info("Disposing ArticulatedBuses");
            Settings?.UnregisterInOptionsUI();
            Settings = null;
        }
    }
}
