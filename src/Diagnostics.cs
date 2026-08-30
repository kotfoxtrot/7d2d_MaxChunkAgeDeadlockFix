using System;
using System.Threading;

namespace MaxChunkAgeDeadlockFix
{
    internal static class Diagnostics
    {
        public static long ResetBatches;

        public static long ChunksReset;

        public static long CullSkips;

        public static long VolumeDefersQueued;

        public static long VolumeDefersRun;

        public static long StabilityDefersQueued;

        public static long StabilityDefersRun;

        public static long GroupingDefersQueued;

        public static long GroupingDefersRun;

        public static long LastResetTicks;

        public static long LastVolumeDeferRunTicks;

        public static long LastDeferDrainTicks;

        public static int VolumeResetInProgress;

        public static string Describe(double stalledSeconds, long mainTicks)
        {
            long now = DateTime.UtcNow.Ticks;
            return "[MaxChunkAgeDeadlockFix][Watchdog] main thread has not ticked for " + stalledSeconds.ToString("0.0")
                + "s | mainTicks=" + mainTicks
                + " | resetBatches=" + Interlocked.Read(ref ResetBatches)
                + " chunksReset=" + Interlocked.Read(ref ChunksReset)
                + " | cullSkips=" + Interlocked.Read(ref CullSkips)
                + " | volumeDefers queued=" + Interlocked.Read(ref VolumeDefersQueued)
                + " run=" + Interlocked.Read(ref VolumeDefersRun)
                + " inProgress=" + (Interlocked.CompareExchange(ref VolumeResetInProgress, 0, 0) != 0)
                + " | stabilityDefers queued=" + Interlocked.Read(ref StabilityDefersQueued)
                + " run=" + Interlocked.Read(ref StabilityDefersRun)
                + " pending=" + Deferred.PendingStabilityCount
                + " | groupingDefers queued=" + Interlocked.Read(ref GroupingDefersQueued)
                + " run=" + Interlocked.Read(ref GroupingDefersRun)
                + " pending=" + Deferred.PendingGroupingCount
                + " | sinceLastReset=" + Age(now, Interlocked.Read(ref LastResetTicks))
                + " sinceLastVolumeDeferRun=" + Age(now, Interlocked.Read(ref LastVolumeDeferRunTicks))
                + " sinceLastDeferDrain=" + Age(now, Interlocked.Read(ref LastDeferDrainTicks));
        }

        private static string Age(long now, long then)
        {
            if (then == 0L)
            {
                return "never";
            }

            return ((double)(now - then) / TimeSpan.TicksPerSecond).ToString("0.0") + "s";
        }
    }
}
