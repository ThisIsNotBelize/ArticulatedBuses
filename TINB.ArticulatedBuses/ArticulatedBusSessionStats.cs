using System.Threading;

namespace TINB.ArticulatedBuses
{
    /* Lightweight per-session counters surfaced in the user session log. Reset on each game/save load. Incremented
       from the structural systems (all main-thread, but Interlocked keeps it trivially safe regardless). */
    internal static class ArticulatedBusSessionStats
    {
        private static int s_TrailersSpawned;
        private static int s_TrailersRestored;
        private static int s_OrphansDeleted;
        private static int s_FrontsFixed;
        private static int s_SelfCheckViolations;

        internal static void Reset()
        {
            Interlocked.Exchange(ref s_TrailersSpawned, 0);
            Interlocked.Exchange(ref s_TrailersRestored, 0);
            Interlocked.Exchange(ref s_OrphansDeleted, 0);
            Interlocked.Exchange(ref s_FrontsFixed, 0);
            Interlocked.Exchange(ref s_SelfCheckViolations, 0);
        }

        internal static int TrailersSpawned => s_TrailersSpawned;
        internal static int TrailersRestored => s_TrailersRestored;
        internal static int OrphansDeleted => s_OrphansDeleted;
        internal static int FrontsFixed => s_FrontsFixed;
        internal static int SelfCheckViolations => s_SelfCheckViolations;

        internal static void TrailerSpawned() => Interlocked.Increment(ref s_TrailersSpawned);
        internal static void TrailerRestored() => Interlocked.Increment(ref s_TrailersRestored);
        internal static void OrphanDeleted() => Interlocked.Increment(ref s_OrphansDeleted);
        internal static void FrontFixed() => Interlocked.Increment(ref s_FrontsFixed);
        internal static void SelfCheckViolation() => Interlocked.Increment(ref s_SelfCheckViolations);

        internal static string Summary() =>
            $"spawned={s_TrailersSpawned} restored={s_TrailersRestored} orphansDeleted={s_OrphansDeleted} " +
            $"frontsFixed={s_FrontsFixed} selfCheckViolations={s_SelfCheckViolations}";
    }
}
