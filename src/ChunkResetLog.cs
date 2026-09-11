using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using HarmonyLib;

namespace MaxChunkAgeDeadlockFix
{
    [HarmonyPatch(typeof(RegionFileManager), "CullExpiredChunks")]
    internal static class CullExpiredChunksMarker
    {
        [ThreadStatic]
        internal static bool Active;

        private static void Prefix()
        {
            Active = true;
        }

        private static void Finalizer()
        {
            Active = false;
        }
    }

    [HarmonyPatch(typeof(RegionFileManager), "RemoveChunks")]
    internal static class RemoveChunksLogPatch
    {
        private const int MaxListed = 256;

        private static void Postfix(ICollection<long> _chunks)
        {
            try
            {
                int count = _chunks?.Count ?? 0;
                if (count == 0)
                {
                    return;
                }

                Interlocked.Increment(ref Diagnostics.ResetBatches);
                Interlocked.Add(ref Diagnostics.ChunksReset, count);
                Interlocked.Exchange(ref Diagnostics.LastResetTicks, DateTime.UtcNow.Ticks);

                StringBuilder sb = new StringBuilder(64 + Math.Min(count, MaxListed) * 14);
                sb.Append("[MaxChunkAgeDeadlockFix] ");
                sb.Append(CullExpiredChunksMarker.Active
                    ? "Chunk reset (MaxChunkAge/requested): "
                    : "Chunk removal (manual/space): ");
                sb.Append(count).Append(" chunk(s), world XZ:");

                int listed = 0;
                foreach (long key in _chunks)
                {
                    if (listed >= MaxListed)
                    {
                        sb.Append(" +").Append(count - listed).Append(" more");
                        break;
                    }

                    sb.Append(" (").Append(WorldChunkCache.extractX(key) << 4).Append(',')
                        .Append(WorldChunkCache.extractZ(key) << 4)
                        .Append(')');
                    listed++;
                }

                Log.Out(sb.ToString());
            }
            catch (Exception e)
            {
                Failsafe.Report("RemoveChunksLogPatch.Postfix", e);
            }
        }
    }
}
