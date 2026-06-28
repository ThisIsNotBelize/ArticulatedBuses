using System;
using System.Reflection;
using Colossal.Serialization.Entities;
using Game;
using UnityEngine;

namespace TINB.ArticulatedBuses
{
    /* Owns the per-session lifecycle of the user SessionLog: on every game/save load it truncates the file and
       writes a fresh header (mod version, game version, load purpose/mode), satisfying "cleared at the start of
       each game session / save load". It has no per-frame work — OnUpdate is empty; all it reacts to is the load
       lifecycle callback the framework already raises on every GameSystemBase. */
    public sealed partial class ArticulatedBusSessionLogSystem : GameSystemBase
    {
        protected override void OnCreate()
        {
            base.OnCreate();
        }

        /* No periodic work. */
        protected override void OnUpdate()
        {
        }

        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            string modVersion = TryGetModVersion();
            string gameVersion = TryGetGameVersion();

            SessionLog.Begin(
                "=== TINB.ArticulatedBuses — user session log ===\n" +
                $"started     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"mod version : {modVersion}\n" +
                $"game version: {gameVersion}\n" +
                $"load        : purpose={purpose}, mode={mode}\n" +
                $"diagnostics : {Mod.IsDiagnosticLoggingEnabled()} (developer log is separate: TINB.ArticulatedBuses.log)\n" +
                "note        : low-volume log; the LAST line survives a hard crash. Please attach this file to bug reports.\n" +
                "------------------------------------------------");

            ArticulatedBusSessionStats.Reset();
            SessionLog.Event($"load complete (purpose={purpose}, mode={mode})");
        }

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

        private static string TryGetGameVersion()
        {
            try
            {
                // UnityEngine bundleVersion — CS2 sets this to the game build string.
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
