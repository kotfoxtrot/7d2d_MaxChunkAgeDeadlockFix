using System;
using System.Threading;
using HarmonyLib;

namespace MaxChunkAgeDeadlockFix
{
    internal static class ChunkIoGates
    {
        internal static readonly object Reader = new object();

        internal static readonly object Writer = new object();

        internal static readonly object Optimizer = new object();
    }

    internal static class LoadChunkState
    {
        [ThreadStatic]
        internal static int Depth;

        [ThreadStatic]
        internal static bool Retrying;
    }

    [HarmonyPatch(typeof(ChunkSnapshotUtil), "LoadChunk")]
    internal static class LoadChunkLockPatch
    {
        private static void Prefix(ref bool __state)
        {
            __state = false;

            try
            {
                Monitor.Enter(ChunkIoGates.Reader);
                __state = true;
                LoadChunkState.Depth++;
            }
            catch (Exception e)
            {
                Failsafe.Report("LoadChunkLockPatch.Prefix", e);
            }
        }

        private static void Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            try
            {
                LoadChunkState.Depth--;
                Monitor.Exit(ChunkIoGates.Reader);
            }
            catch (Exception e)
            {
                Failsafe.Report("LoadChunkLockPatch.Finalizer", e);
            }
        }
    }

    [HarmonyPatch(typeof(RegionFileChunkReader), "readIntoLoadStream")]
    internal static class ChunkReadRetryPatch
    {
        private static Exception Finalizer(Exception __exception, RegionFileChunkReader __instance,
            string _dir, int chunkX, int chunkZ, string ext, ref uint version, ref PooledBinaryReader __result)
        {
            if (__exception == null)
            {
                return null;
            }

            if (LoadChunkState.Retrying)
            {
                return __exception;
            }

            LoadChunkState.Retrying = true;
            try
            {
                __result = __instance.readIntoLoadStream(_dir, chunkX, chunkZ, ext, out uint retryVersion);
                version = retryVersion;
                Interlocked.Increment(ref Diagnostics.ChunkReadRetriesRecovered);
                Log.Warning("[MaxChunkAgeDeadlockFix] chunk " + chunkX + "/" + chunkZ + " failed to read and was recovered on retry: "
                    + __exception.GetType().Name);
                return null;
            }
            catch (Exception)
            {
                Interlocked.Increment(ref Diagnostics.ChunkReadRetriesFailed);
                return __exception;
            }
            finally
            {
                LoadChunkState.Retrying = false;
            }
        }
    }

    [HarmonyPatch(typeof(RegionFileAccessMultipleChunks), "Remove")]
    internal static class FailedLoadRemoveGuardPatch
    {
        private const long LogLimit = 50L;

        private static long logged;

        private static bool Prefix(int _chunkX, int _chunkZ)
        {
            try
            {
                if (LoadChunkState.Depth <= 0)
                {
                    return true;
                }

                Interlocked.Increment(ref Diagnostics.FailedLoadRemovalsBlocked);

                if (Interlocked.Increment(ref logged) <= LogLimit)
                {
                    Log.Warning("[MaxChunkAgeDeadlockFix] refused to delete chunk " + _chunkX + "/" + _chunkZ
                        + " in region r." + (_chunkX >> 5) + "." + (_chunkZ >> 5)
                        + " after a failed read, chunk data kept on disk");
                }

                return false;
            }
            catch (Exception e)
            {
                Failsafe.Report("FailedLoadRemoveGuardPatch.Prefix", e);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(RegionFileChunkWriter), "WriteStreamCompressed")]
    internal static class WriteStreamLockPatch
    {
        private static void Prefix(ref bool __state)
        {
            __state = false;

            try
            {
                Monitor.Enter(ChunkIoGates.Writer);
                __state = true;
            }
            catch (Exception e)
            {
                Failsafe.Report("WriteStreamLockPatch.Prefix", e);
            }
        }

        private static void Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            try
            {
                Monitor.Exit(ChunkIoGates.Writer);
            }
            catch (Exception e)
            {
                Failsafe.Report("WriteStreamLockPatch.Finalizer", e);
            }
        }
    }
}
