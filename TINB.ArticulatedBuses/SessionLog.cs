using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TINB.ArticulatedBuses
{
    /* Always-on, low-volume, crash-resilient USER log — distinct from the developer Mod.Log (which is gated by the
       ARTICULATEDBUSES_DIAGNOSTICS compile symbol and so is absent from published Release builds). Its job is to
       give a reporting player a small file we can read after a CTD.

       Two design points that make it useful for the random/un-pause CTDs:
       1. Every line is opened-appended-flushed-closed immediately, so the LAST line written survives a hard native
          crash (a data-race / ECB-playback CTD never throws a managed exception, but a line already flushed to the
          OS file cache persists through a process kill). That last breadcrumb tells us which system/phase was active.
       2. The file is TRUNCATED on each game/save load (see ArticulatedBusSessionLogSystem), so it only ever holds
          the current session — no manual clearing, no unbounded growth.

       HARD CONTRACT: no per-frame logging. Only infrequent lifecycle events, structural actions (spawn/restore/
       delete/strip), "about-to" breadcrumbs around the few risky main-thread reads, and invariant violations. */
    public static class SessionLog
    {
        private const string FileName = "TINB.ArticulatedBuses.session.log";

        private static readonly object s_Lock = new object();

        /* Error de-dup: a system that throws every tick would otherwise spam the file. We log each distinct error
           context once per session. (Breadcrumbs and events are already naturally low-volume.) */
        private static readonly HashSet<string> s_SeenErrors = new HashSet<string>();

        private static string? s_Path;
        private static bool s_Failed;

        private static string ResolvePath()
        {
            // …/AppData/LocalLow/Colossal Order/Cities Skylines II/Logs/ — same folder as the game's own logs.
            string dir = Path.Combine(Application.persistentDataPath, "Logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, FileName);
        }

        /* Truncates the file and writes a fresh multi-line session header. Called once per game/save load. */
        public static void Begin(string header)
        {
            lock (s_Lock)
            {
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

        /* An infrequent, noteworthy thing happened (a trailer was spawned, the depot fix ran, …). */
        public static void Event(string message) => Write("EVENT", message);

        /* About to do something risky on the main thread; flushed BEFORE the risky call so it survives a CTD. */
        public static void Breadcrumb(string message) => Write("TRAIL", message);

        /* An invariant the self-check watches was violated (e.g. the depot-upgrade CTD precondition is present). */
        public static void Warn(string message) => Write("WARN", message);

        /* A managed exception was caught. De-duplicated by context so a per-tick thrower can't flood the file. */
        public static void Exception(string context, Exception ex)
        {
            lock (s_Lock)
            {
                if (!s_SeenErrors.Add(context))
                {
                    return;
                }
            }

            Write("ERROR", $"{context} :: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

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
                    // Open/append/flush/close per line: guarantees the line reaches the OS file cache (and so
                    // survives a process crash) before control returns and any subsequent risky call can crash.
                    File.AppendAllText(s_Path, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
                }
                catch (Exception e)
                {
                    s_Failed = true; // stop trying after the first IO failure
                    Mod.Log.Warn($"SessionLog write failed: {e}");
                }
            }
        }
    }
}
