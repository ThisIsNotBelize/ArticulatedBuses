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

        /* Run-gate: gameplay only; in the editor only ArticulatedBusIconCreatorCompatSystem runs */
        public static bool ShouldRunRuntimeSystems()
        {
            return GameManager.instance != null && !GameManager.instance.gameMode.IsEditor();
        }

        /* True in diagnostics builds (ARTICULATEDBUSES_DIAGNOSTICS, default in Debug; compiled out of Release) */
        public static bool IsDiagnosticLoggingEnabled()
        {
            #if ARTICULATEDBUSES_DIAGNOSTICS
            return true;
            #else
            return false;
            #endif
        }

        /* Schedules each system into its update phase */
        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info("Loading ArticulatedBuses");

            updateSystem.UpdateAfter<ArticulatedBusPrefabConstraintSystem, Game.Prefabs.VehicleInitializeSystem>(SystemUpdatePhase.PrefabUpdate);
            updateSystem.UpdateBefore<ArticulatedBusTrailerSpawnSystem, Game.Vehicles.InitializeSystem>(SystemUpdatePhase.Modification5);
            updateSystem.UpdateAfter<ArticulatedBusOrphanTrailerCleanupSystem, ArticulatedBusTrailerSpawnSystem>(SystemUpdatePhase.Modification5);
            updateSystem.UpdateAfter<ArticulatedBusParkedTrailerSystem, ArticulatedBusTrailerSpawnSystem>(SystemUpdatePhase.Modification5);
            updateSystem.UpdateAt<ArticulatedBusIconCreatorCompatSystem>(SystemUpdatePhase.Modification1);
            updateSystem.UpdateAfter<ArticulatedBusConnectionBoneSystem, Game.Rendering.ObjectInterpolateSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateBefore<ArticulatedBusConnectionBoneSystem, Game.Rendering.ProceduralSkeletonSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAfter<ArticulatedBusColorSyncSystem, Game.Rendering.ObjectInterpolateSystem>(SystemUpdatePhase.Rendering);
        }

        /* Unload hook (nothing to clean up) */
        public void OnDispose()
        {
            Log.Info("Disposing ArticulatedBuses");
        }
    }
}
