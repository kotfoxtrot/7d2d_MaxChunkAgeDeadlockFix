using System;
using HarmonyLib;
using UnityEngine;

namespace MaxChunkAgeDeadlockFix
{
    public class Api : IModApi
    {
        public void InitMod(Mod modInstance)
        {
            try
            {
                if (AccessTools.Field(typeof(MultiBlockManager), "lockObj") == null)
                {
                    Debug.LogError("[MaxChunkAgeDeadlockFix] MultiBlockManager.lockObj not found, game code changed, patch skipped.");
                    return;
                }

                if (AccessTools.Method(typeof(MultiBlockManager), "CullChunklessData") == null)
                {
                    Debug.LogError("[MaxChunkAgeDeadlockFix] MultiBlockManager.CullChunklessData not found, game code changed, patch skipped.");
                    return;
                }

                if (AccessTools.Method(typeof(RegionFileManager), "resetVolumeDataForChunks") == null)
                {
                    Debug.LogError("[MaxChunkAgeDeadlockFix] RegionFileManager.resetVolumeDataForChunks not found, game code changed, patch skipped.");
                    return;
                }

                Harmony harmony = new Harmony("com.sdtdtest.maxchunkagedeadlockfix");

                if (AccessTools.Method(typeof(RegionFileV2), "OptimizeLayout") == null
                    || AccessTools.Method(typeof(RegionFileV2), "findFreeSectorOfSize") == null
                    || AccessTools.Method(typeof(RegionFileV2), "WriteData") == null
                    || AccessTools.Method(typeof(RegionFileSectorBased), "GetLocationInfo") == null
                    || AccessTools.Method(typeof(RegionFileV2), "SetLocationInfo") == null
                    || AccessTools.Field(typeof(RegionFileV2), "usedSectors") == null)
                {
                    Debug.LogError("[MaxChunkAgeDeadlockFix] RegionFileV2 members not found, region file protection unavailable, all patches skipped.");
                    return;
                }

                if (AccessTools.Method(typeof(ChunkSnapshotUtil), "LoadChunk") == null
                    || AccessTools.Method(typeof(RegionFileChunkReader), "readIntoLoadStream") == null
                    || AccessTools.Method(typeof(RegionFileChunkWriter), "WriteStreamCompressed") == null
                    || AccessTools.Method(typeof(RegionFileAccessMultipleChunks), "Remove") == null)
                {
                    Debug.LogError("[MaxChunkAgeDeadlockFix] chunk stream members not found, shared buffer protection unavailable, all patches skipped.");
                    return;
                }

                harmony.CreateClassProcessor(typeof(OptimizeLayoutLockPatch)).Patch();
                harmony.CreateClassProcessor(typeof(FindFreeSectorPatch)).Patch();
                harmony.CreateClassProcessor(typeof(WriteDataSectorGuardPatch)).Patch();
                Debug.Log("[MaxChunkAgeDeadlockFix] Region file protection applied: layout serialization, sector allocation guard, stale sector guard.");

                harmony.CreateClassProcessor(typeof(LoadChunkLockPatch)).Patch();
                harmony.CreateClassProcessor(typeof(WriteStreamLockPatch)).Patch();
                harmony.CreateClassProcessor(typeof(FailedLoadRemoveGuardPatch)).Patch();
                Debug.Log("[MaxChunkAgeDeadlockFix] Chunk stream protection applied: read serialization, write serialization, deletion guard.");

                try
                {
                    harmony.CreateClassProcessor(typeof(ChunkReadRetryPatch)).Patch();
                    Debug.Log("[MaxChunkAgeDeadlockFix] Chunk read retry applied.");
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[MaxChunkAgeDeadlockFix] Chunk read retry failed to apply, stream protection unaffected: " + e);
                }

                harmony.CreateClassProcessor(typeof(CullChunklessDataPatch)).Patch();
                Debug.Log("[MaxChunkAgeDeadlockFix] Deadlock patch applied.");

                if (AccessTools.Method(typeof(ThreadManager), "AddSingleTaskMainThread", new[] { typeof(string), typeof(ThreadManager.MainThreadTaskFunctionDelegate), typeof(object) }) == null)
                {
                    Debug.LogError("[MaxChunkAgeDeadlockFix] ThreadManager.AddSingleTaskMainThread(string, MainThreadTaskFunctionDelegate, object) not found, volume reset deferral skipped.");
                }
                else
                {
                    harmony.CreateClassProcessor(typeof(ResetVolumeDataPatch)).Patch();
                    Debug.Log("[MaxChunkAgeDeadlockFix] Volume reset main-thread deferral applied.");
                }

                bool drainAvailable = AccessTools.Method(typeof(MultiBlockManager), "MainThreadUpdate") != null;
                if (!drainAvailable)
                {
                    Debug.LogError("[MaxChunkAgeDeadlockFix] MultiBlockManager.MainThreadUpdate not found, no main-thread drain point, all deferrals skipped.");
                }

                bool stabilityDeferred = false;
                if (drainAvailable)
                {
                    if (AccessTools.Method(typeof(MultiBlockManager), "OnChunkInitialized") == null
                        || AccessTools.Method(typeof(MultiBlockManager), "AddChunkOverlappingBlocksToSet") == null
                        || AccessTools.Field(typeof(MultiBlockManager), "trackedDataMap") == null
                        || AccessTools.Field(typeof(MultiBlockManager), "oversizedBlocksWithDirtyStability") == null
                        || AccessTools.Field(typeof(MultiBlockManager), "blocksWithDirtyAlignment") == null)
                    {
                        Debug.LogError("[MaxChunkAgeDeadlockFix] MultiBlockManager stability members not found, chunk init deferral skipped.");
                    }
                    else
                    {
                        harmony.CreateClassProcessor(typeof(StabilityDeferPatch)).Patch();
                        stabilityDeferred = true;
                        Debug.Log("[MaxChunkAgeDeadlockFix] Chunk init stability deferral applied.");
                    }
                }

                bool groupingDeferred = false;
                if (drainAvailable)
                {
                    if (AccessTools.Method(typeof(RegionFileManager), "AddGroupedChunks") == null
                        || AccessTools.Field(typeof(RegionFileManager), "chunksInSaveDir") == null)
                    {
                        Debug.LogError("[MaxChunkAgeDeadlockFix] RegionFileManager.AddGroupedChunks not found, grouping deferral skipped.");
                    }
                    else
                    {
                        harmony.CreateClassProcessor(typeof(GroupedChunksDeferPatch)).Patch();
                        groupingDeferred = true;
                        Debug.Log("[MaxChunkAgeDeadlockFix] Chunk grouping deferral applied.");
                    }
                }

                if (stabilityDeferred || groupingDeferred)
                {
                    harmony.CreateClassProcessor(typeof(DeferredDrainPatch)).Patch();
                    Debug.Log("[MaxChunkAgeDeadlockFix] Deferred work drain applied on MultiBlockManager.MainThreadUpdate.");
                }

                try
                {
                    harmony.CreateClassProcessor(typeof(CullExpiredChunksMarker)).Patch();
                    harmony.CreateClassProcessor(typeof(RemoveChunksLogPatch)).Patch();
                    Debug.Log("[MaxChunkAgeDeadlockFix] Chunk reset logging applied.");
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[MaxChunkAgeDeadlockFix] Chunk reset logging failed, deadlock patch unaffected: " + e);
                }

                try
                {
                    MainThreadWatchdog.Install();
                    Debug.Log("[MaxChunkAgeDeadlockFix] Main thread watchdog started (warn 45s, stack dump 90s).");
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[MaxChunkAgeDeadlockFix] Watchdog failed to start, patches unaffected: " + e);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[MaxChunkAgeDeadlockFix] Init failed: " + e);
            }
        }
    }
}
