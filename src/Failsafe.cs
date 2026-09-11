using System;
using System.Collections.Generic;
using System.Threading;

namespace MaxChunkAgeDeadlockFix
{
    internal static class Failsafe
    {
        private const int RepeatIntervalSeconds = 60;

        private static readonly object gate = new object();

        private static readonly Dictionary<string, long> lastLogTicks = new Dictionary<string, long>();

        public static long Total;

        internal static void Report(string site, Exception e)
        {
            Interlocked.Increment(ref Total);

            try
            {
                long now = DateTime.UtcNow.Ticks;
                lock (gate)
                {
                    if (lastLogTicks.TryGetValue(site, out long last)
                        && now - last < RepeatIntervalSeconds * TimeSpan.TicksPerSecond)
                    {
                        return;
                    }

                    lastLogTicks[site] = now;
                }

                Log.Error("[MaxChunkAgeDeadlockFix] " + site + " failed, falling back to vanilla behaviour: " + e);
            }
            catch
            {
            }
        }
    }
}
