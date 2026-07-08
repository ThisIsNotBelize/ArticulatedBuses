using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TINB.ArticulatedBuses
{
    /// <summary>
    /// Unified logging entry point for the mod
    /// </summary>
    /// <remarks>
    /// Owns the always-on, low-volume user log (this file) and also routes developer diagnostic lines to Mod.Log via
    /// Diagnostic/DiagnosticWarn, which are gated by the diagnostics toggle and absent from Release builds. Gives a
    /// reporting player a small file we can read after a CTD
    /// - Every line is opened, appended, flushed and closed immediately, so the LAST line survives a hard native
    ///   crash (a data-race or ECB-playback CTD never throws a managed exception, but a flushed line persists through
    ///   a process kill). That breadcrumb tells us which system or phase was active
    /// - The file is truncated on each game/save load (see ArticulatedBusSessionLogSystem), so it only ever holds the
    ///   current session, with no manual clearing and no unbounded growth
    /// - Hard contract, no per-frame logging. Only lifecycle events, structural actions (spawn, restore, delete,
    ///   strip), about-to breadcrumbs around risky main-thread reads, and invariant violations
    /// </remarks>
    public static class SessionLog
    {
        private const string FileName = "TINB.ArticulatedBuses.session.log";

        private static readonly object s_Lock = new object();

        /// <summary>
        /// Error contexts already logged this session
        /// </summary>
        /// <remarks>
        /// A system that throws every tick would spam the file, so we log each distinct error context once per session
        /// </remarks>
        private static readonly HashSet<string> s_SeenErrors = new HashSet<string>();

        private static string? s_Path;
        private static bool s_Failed;
        private static bool s_Started;

        /// <summary>
        /// Resolve the log file path in the game's Logs folder
        /// </summary>
        private static string ResolvePath()
        {
            // same folder as the game's own logs
            string dir = Path.Combine(Application.persistentDataPath, "Logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, FileName);
        }

        /// <summary>
        /// Start the log once per app launch: truncate the file and write a fresh header
        /// </summary>
        /// <remarks>
        /// Only the first call per process truncates. Later game/save loads (menu, game, editor) are no-ops here and keep appending to the same file, so the log spans the whole launch and is wiped only when the game is restarted
        /// </remarks>
        public static void Begin(string header)
        {
            lock (s_Lock)
            {
                if (s_Started)
                {
                    return;
                }
                s_Started = true;

                s_Failed = false;
                s_SeenErrors.Clear();
                try
                {
                    s_Path = ResolvePath();
                    File.WriteAllText(s_Path, header + Environment.NewLine);
                }
                catch (Exception e)
                {
                    s_Failed = true;
                    Mod.Log.Warn($"SessionLog.Begin failed: {e}");
                }
            }
        }

        /// <summary>
        /// Log an infrequent noteworthy event
        /// </summary>
        /// <remarks>
        /// For example a trailer spawned or the depot fix ran
        /// </remarks>
        public static void Event(string message) => Write("EVENT", message);

        /// <summary>
        /// Log a breadcrumb before a risky main-thread call
        /// </summary>
        /// <remarks>
        /// Flushed first so it survives a CTD
        /// </remarks>
        public static void Breadcrumb(string message) => Write("TRAIL", message);

        /// <summary>
        /// Log a violated invariant
        /// </summary>
        /// <remarks>
        /// For example the depot-upgrade CTD precondition is present
        /// </remarks>
        public static void Warn(string message) => Write("WARN", message);

        /// <summary>
        /// Write a developer diagnostic line to the dev log
        /// </summary>
        /// <remarks>
        /// Only when diagnostic logging is on
        /// </remarks>
        public static void Diagnostic(string message)
        {
            if (Mod.IsDiagnosticLoggingEnabled())
            {
                Mod.Log.Info(message);
            }
        }

        /// <summary>
        /// Write a developer diagnostic warning to the dev log
        /// </summary>
        /// <remarks>
        /// Only when diagnostic logging is on
        /// </remarks>
        public static void DiagnosticWarn(string message)
        {
            if (Mod.IsDiagnosticLoggingEnabled())
            {
                Mod.Log.Warn(message);
            }
        }

        /// <summary>
        /// Log a caught exception
        /// </summary>
        /// <remarks>
        /// De-duplicated by context so a per-tick thrower cannot flood the file
        /// </remarks>
        public static void Exception(string context, Exception ex)
        {
            lock (s_Lock)
            {
                // first occurrence of this context only
                if (!s_SeenErrors.Add(context))
                {
                    return;
                }
            }

            Write("ERROR", $"{context} :: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        /// <summary>
        /// Write one line to the log, then flush and close
        /// </summary>
        private static void Write(string level, string message)
        {
            lock (s_Lock)
            {
                if (s_Failed)
                {
                    return;
                }

                try
                {
                    s_Path ??= ResolvePath();
                    // open, append, flush and close per line so it reaches the OS file cache before any later risky call can crash
                    File.AppendAllText(s_Path, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
                }
                catch (Exception e)
                {
                    // stop trying after the first IO failure
                    s_Failed = true;
                    Mod.Log.Warn($"SessionLog write failed: {e}");
                }
            }
        }
    }
}
