using System;
using Colossal.Serialization.Entities;
using Game;
using UnityEngine;

namespace TINB.ArticulatedBuses
{
    /// <summary>
    /// Manage the user SessionLog lifecycle across an app launch
    /// </summary>
    /// <remarks>
    /// Truncates the file and writes a fresh header on the FIRST game/save load of the launch, then appends a
    /// load-complete marker on every load (menu, game, editor). The log therefore spans the whole launch and is wiped
    /// only when the game is restarted. No per-frame work: the system reacts only to the load lifecycle callback the
    /// framework raises on every GameSystemBase
    /// </remarks>
    public sealed partial class ArticulatedBusSessionLogSystem : GameSystemBase
    {
        /// <summary>
        /// No periodic work
        /// </summary>
        /// <remarks>
        /// The system exists only to receive OnGameLoadingComplete, but GameSystemBase requires an OnUpdate, so
        /// this stays empty
        /// </remarks>
        protected override void OnUpdate()
        {
        }

        /// <summary>
        /// Start the session log on the first load of the launch, then mark every load
        /// </summary>
        /// <remarks>
        /// SessionLog.Begin truncates and writes the header only once per app launch; every load (including the first) appends a load-complete event, so the file accumulates across menu, game and editor
        /// </remarks>
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            string modVersion = TryGetModVersion();
            string gameVersion = TryGetGameVersion();

            SessionLog.Begin(
                "=== TINB.ArticulatedBuses - user session log ===\n" +
                $"started     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"mod version : {modVersion}\n" +
                $"game version: {gameVersion}\n" +
                $"first load  : purpose={purpose}, mode={mode}\n" +
                $"diagnostics : {Mod.IsDiagnosticLoggingEnabled()} (developer log is separate: TINB.ArticulatedBuses.log)\n" +
                "note        : low-volume log; the LAST line survives a hard crash. Please attach this file to bug reports.\n" +
                "------------------------------------------------");

            SessionLog.Event($"load complete (purpose={purpose}, mode={mode})");
        }

        /// <summary>
        /// Read the mod assembly version for the log header
        /// </summary>
        /// <returns>The version string, or "unknown" if it can't be read</returns>
        private static string TryGetModVersion()
        {
            try
            {
                System.Version? v = typeof(Mod).Assembly.GetName().Version;
                return v?.ToString() ?? "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        /// <summary>
        /// Read the game build version for the log header
        /// </summary>
        /// <returns>The version string, or "unknown" if it can't be read</returns>
        private static string TryGetGameVersion()
        {
            try
            {
                // UnityEngine bundleVersion, CS2 sets this to the game build string
                string v = Application.version;
                return string.IsNullOrEmpty(v) ? "unknown" : v;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
