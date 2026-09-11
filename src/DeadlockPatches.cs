using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace MaxChunkAgeDeadlockFix
{
    [HarmonyPatch(typeof(MultiBlockManager), "CullChunklessData")]
    internal static class CullChunklessDataPatch
    {
        private static int skipCount;

        private static long lastLogTicks;

        private static bool Prefix(object ___lockObj, ref bool __state)
        {
            __state = false;

            try
            {
                if (___lockObj == null)
                {
                    return true;
                }

                __state = Monitor.TryEnter(___lockObj, 0);
                if (!__state)
                {
                    Interlocked.Increment(ref Diagnostics.CullSkips);
                    int total = Interlocked.Increment(ref skipCount);
                    long now = DateTime.UtcNow.Ticks;
                    long last = Interlocked.Read(ref lastLogTicks);
                    if (now - last > 30 * TimeSpan.TicksPerSecond
                        && Interlocked.CompareExchange(ref lastLogTicks, now, last) == last)
                    {
                        Debug.Log("[MaxChunkAgeDeadlockFix] CullChunklessData skipped under lock contention, deadlock avoided. Total skips: " + total);
                    }
                }

                return __state;
            }
            catch (Exception e)
            {
                Failsafe.Report("CullChunklessDataPatch.Prefix", e);
                __state = false;
                return true;
            }
        }

        private static void Finalizer(object ___lockObj, bool __state)
        {
            if (!__state || ___lockObj == null)
            {
                return;
            }

            try
            {
                Monitor.Exit(___lockObj);
            }
            catch (Exception e)
            {
                Failsafe.Report("CullChunklessDataPatch.Finalizer", e);
            }
        }
    }

    internal static class Deferred
    {
        private struct GroupingEntry
        {
            public RegionFileManager Manager;

            public long[] Keys;
        }

        private const int WarnThreshold = 4096;

        private const int HardCap = 65536;

        private const int DrainLockTimeoutMs = 20;

        private const int WarnIntervalSeconds = 30;

        private static readonly object gate = new object();

        private static readonly HashSet<Vector2i> pendingStability = new HashSet<Vector2i>();

        private static readonly List<GroupingEntry> pendingGrouping = new List<GroupingEntry>();

        private static readonly List<Vector2i> stabilityScratch = new List<Vector2i>();

        private static readonly List<GroupingEntry> groupingScratch = new List<GroupingEntry>();

        private static long lastWarnTicks;

        [ThreadStatic]
        internal static bool BypassGrouping;

        internal static void QueueStability(Vector2i chunkPos)
        {
            lock (gate)
            {
                if (pendingStability.Count >= HardCap)
                {
                    return;
                }

                if (!pendingStability.Add(chunkPos))
                {
                    return;
                }
            }

            Interlocked.Increment(ref Diagnostics.StabilityDefersQueued);
        }

        internal static bool TryQueueGrouping(RegionFileManager manager, ICollection<long> keys)
        {
            if (manager == null || keys == null || keys.Count == 0)
            {
                return true;
            }

            long[] copy = new long[keys.Count];
            keys.CopyTo(copy, 0);

            lock (gate)
            {
                if (pendingGrouping.Count >= HardCap)
                {
                    return false;
                }

                pendingGrouping.Add(new GroupingEntry
                {
                    Manager = manager,
                    Keys = copy
                });
            }

            Interlocked.Increment(ref Diagnostics.GroupingDefersQueued);
            return true;
        }

        internal static void DrainMainThread()
        {
            try
            {
                DrainStability();
                DrainGrouping();
                WarnIfBacklogged();
            }
            catch (Exception e)
            {
                Failsafe.Report("Deferred.DrainMainThread", e);
            }
        }

        private static void DrainStability()
        {
            lock (gate)
            {
                if (pendingStability.Count == 0)
                {
                    return;
                }

                stabilityScratch.Clear();
                stabilityScratch.AddRange(pendingStability);
                pendingStability.Clear();
            }

            MultiBlockManager instance = MultiBlockManager.Instance;
            if (instance == null
                || !instance.CheckFeatures(MultiBlockManager.FeatureFlags.OversizedStability | MultiBlockManager.FeatureFlags.TerrainAlignment, MultiBlockManager.FeatureRequirement.OneOrMoreEnabled))
            {
                stabilityScratch.Clear();
                return;
            }

            if (instance.lockObj == null || !Monitor.TryEnter(instance.lockObj, DrainLockTimeoutMs))
            {
                RequeueStability();
                return;
            }

            try
            {
                bool oversized = instance.CheckFeatures(MultiBlockManager.FeatureFlags.OversizedStability);
                bool alignment = instance.CheckFeatures(MultiBlockManager.FeatureFlags.TerrainAlignment);

                for (int i = 0; i < stabilityScratch.Count; i++)
                {
                    if (oversized)
                    {
                        MultiBlockManager.AddChunkOverlappingBlocksToSet(stabilityScratch[i], instance.trackedDataMap.OversizedBlocks, instance.oversizedBlocksWithDirtyStability);
                    }

                    if (alignment)
                    {
                        MultiBlockManager.AddChunkOverlappingBlocksToSet(stabilityScratch[i], instance.trackedDataMap.TerrainAlignedBlocks, instance.blocksWithDirtyAlignment);
                    }
                }

                Interlocked.Add(ref Diagnostics.StabilityDefersRun, stabilityScratch.Count);
                Interlocked.Exchange(ref Diagnostics.LastDeferDrainTicks, DateTime.UtcNow.Ticks);
            }
            finally
            {
                Monitor.Exit(instance.lockObj);
                stabilityScratch.Clear();
            }
        }

        private static void RequeueStability()
        {
            lock (gate)
            {
                for (int i = 0; i < stabilityScratch.Count; i++)
                {
                    pendingStability.Add(stabilityScratch[i]);
                }
            }

            stabilityScratch.Clear();
        }

        private static void DrainGrouping()
        {
            lock (gate)
            {
                if (pendingGrouping.Count == 0)
                {
                    return;
                }

                groupingScratch.Clear();
                groupingScratch.AddRange(pendingGrouping);
                pendingGrouping.Clear();
            }

            int ran = 0;
            int i = 0;
            try
            {
                for (; i < groupingScratch.Count; i++)
                {
                    GroupingEntry entry = groupingScratch[i];
                    RegionFileManager manager = entry.Manager;
                    object saveLock = manager != null ? manager.saveLock : null;
                    object chunksInSaveDir = manager != null ? manager.chunksInSaveDir : null;
                    if (saveLock == null || chunksInSaveDir == null)
                    {
                        break;
                    }

                    if (!Monitor.TryEnter(saveLock, DrainLockTimeoutMs))
                    {
                        break;
                    }

                    try
                    {
                        if (!Monitor.TryEnter(chunksInSaveDir, DrainLockTimeoutMs))
                        {
                            break;
                        }

                        try
                        {
                            BypassGrouping = true;
                            manager.AddGroupedChunks(entry.Keys);
                            ran++;
                        }
                        finally
                        {
                            BypassGrouping = false;
                            Monitor.Exit(chunksInSaveDir);
                        }
                    }
                    finally
                    {
                        Monitor.Exit(saveLock);
                    }
                }
            }
            finally
            {
                if (i < groupingScratch.Count)
                {
                    lock (gate)
                    {
                        for (int j = groupingScratch.Count - 1; j >= i; j--)
                        {
                            pendingGrouping.Insert(0, groupingScratch[j]);
                        }
                    }
                }

                groupingScratch.Clear();

                if (ran > 0)
                {
                    Interlocked.Add(ref Diagnostics.GroupingDefersRun, ran);
                    Interlocked.Exchange(ref Diagnostics.LastDeferDrainTicks, DateTime.UtcNow.Ticks);
                }
            }
        }

        private static void WarnIfBacklogged()
        {
            int stability;
            int grouping;
            lock (gate)
            {
                stability = pendingStability.Count;
                grouping = pendingGrouping.Count;
            }

            if (stability < WarnThreshold && grouping < WarnThreshold)
            {
                return;
            }

            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref lastWarnTicks);
            if (now - last < WarnIntervalSeconds * TimeSpan.TicksPerSecond)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref lastWarnTicks, now, last) != last)
            {
                return;
            }

            Log.Warning("[MaxChunkAgeDeadlockFix] Deferred backlog is growing: stability=" + stability + " grouping=" + grouping);
        }

        internal static int PendingStabilityCount
        {
            get
            {
                lock (gate)
                {
                    return pendingStability.Count;
                }
            }
        }

        internal static int PendingGroupingCount
        {
            get
            {
                lock (gate)
                {
                    return pendingGrouping.Count;
                }
            }
        }
    }

    [HarmonyPatch(typeof(RegionFileManager), "AddGroupedChunks")]
    internal static class GroupedChunksDeferPatch
    {
        private static bool Prefix(RegionFileManager __instance, ICollection<long> chunksToGroup, ref bool __state)
        {
            __state = false;

            try
            {
                if (Deferred.BypassGrouping)
                {
                    return true;
                }

                if (chunksToGroup == null || chunksToGroup.Count == 0)
                {
                    return true;
                }

                object chunksInSaveDir = __instance.chunksInSaveDir;
                if (chunksInSaveDir == null)
                {
                    return true;
                }

                __state = Monitor.TryEnter(chunksInSaveDir, 0);
                if (__state)
                {
                    return true;
                }

                return !Deferred.TryQueueGrouping(__instance, chunksToGroup);
            }
            catch (Exception e)
            {
                Failsafe.Report("GroupedChunksDeferPatch.Prefix", e);
                __state = false;
                return true;
            }
        }

        private static void Finalizer(RegionFileManager __instance, bool __state)
        {
            if (!__state)
            {
                return;
            }

            try
            {
                object chunksInSaveDir = __instance.chunksInSaveDir;
                if (chunksInSaveDir != null)
                {
                    Monitor.Exit(chunksInSaveDir);
                }
            }
            catch (Exception e)
            {
                Failsafe.Report("GroupedChunksDeferPatch.Finalizer", e);
            }
        }
    }

    [HarmonyPatch(typeof(RegionFileManager), "resetVolumeDataForChunks")]
    internal static class ResetVolumeDataPatch
    {
        private static bool Prefix(ICollection<long> _chunks)
        {
            try
            {
                if (ThreadManager.IsMainThread())
                {
                    return true;
                }

                if (_chunks == null || _chunks.Count == 0)
                {
                    return false;
                }

                long[] copy = new long[_chunks.Count];
                _chunks.CopyTo(copy, 0);

                Interlocked.Increment(ref Diagnostics.VolumeDefersQueued);

                ThreadManager.AddSingleTaskMainThread("MaxChunkAgeDeadlockFix.ResetVolumes", RunVolumeReset, copy);

                return false;
            }
            catch (Exception e)
            {
                Failsafe.Report("ResetVolumeDataPatch.Prefix", e);
                return true;
            }
        }

        private static void RunVolumeReset(object _parameter)
        {
            long[] keys = _parameter as long[];
            if (keys == null)
            {
                return;
            }

            Interlocked.Exchange(ref Diagnostics.VolumeResetInProgress, 1);
            try
            {
                World world = GameManager.Instance != null ? GameManager.Instance.World : null;
                if (world == null)
                {
                    return;
                }

                for (int i = 0; i < keys.Length; i++)
                {
                    world.ResetTriggerVolumes(keys[i]);
                    world.ResetSleeperVolumes(keys[i]);
                }
            }
            catch (Exception e)
            {
                Failsafe.Report("ResetVolumeDataPatch.RunVolumeReset", e);
            }
            finally
            {
                Interlocked.Exchange(ref Diagnostics.VolumeResetInProgress, 0);
                Interlocked.Increment(ref Diagnostics.VolumeDefersRun);
                Interlocked.Exchange(ref Diagnostics.LastVolumeDeferRunTicks, DateTime.UtcNow.Ticks);
            }
        }
    }

    [HarmonyPatch(typeof(MultiBlockManager), "OnChunkInitialized")]
    internal static class StabilityDeferPatch
    {
        private static bool Prefix(MultiBlockManager __instance, Chunk chunk, ref bool __state)
        {
            __state = false;

            try
            {
                if (chunk == null)
                {
                    return true;
                }

                if (!__instance.CheckFeatures(MultiBlockManager.FeatureFlags.OversizedStability | MultiBlockManager.FeatureFlags.TerrainAlignment, MultiBlockManager.FeatureRequirement.OneOrMoreEnabled))
                {
                    return true;
                }

                if (__instance.lockObj == null)
                {
                    return true;
                }

                __state = Monitor.TryEnter(__instance.lockObj, 0);
                if (__state)
                {
                    return true;
                }

                Deferred.QueueStability(new Vector2i(chunk.X, chunk.Z));
                return false;
            }
            catch (Exception e)
            {
                Failsafe.Report("StabilityDeferPatch.Prefix", e);
                __state = false;
                return true;
            }
        }

        private static void Finalizer(MultiBlockManager __instance, bool __state)
        {
            if (!__state || __instance.lockObj == null)
            {
                return;
            }

            try
            {
                Monitor.Exit(__instance.lockObj);
            }
            catch (Exception e)
            {
                Failsafe.Report("StabilityDeferPatch.Finalizer", e);
            }
        }
    }

    [HarmonyPatch(typeof(MultiBlockManager), "MainThreadUpdate")]
    internal static class DeferredDrainPatch
    {
        private static void Prefix()
        {
            Deferred.DrainMainThread();
        }
    }
}
