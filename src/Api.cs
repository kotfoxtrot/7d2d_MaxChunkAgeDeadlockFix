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

                harmony.CreateClassProcessor(typeof(CullChunklessDataPatch)).Patch();
                Debug.Log("[MaxChunkAgeDeadlockFix] Deadlock patch applied.");

                harmony.CreateClassProcessor(typeof(ResetVolumeDataPatch)).Patch();
                Debug.Log("[MaxChunkAgeDeadlockFix] Volume reset main-thread deferral applied.");

                if (AccessTools.Method(typeof(MultiBlockManager), "OnChunkStabilityCalculationEnabled") == null
                    || AccessTools.Method(typeof(MultiBlockManager), "AddChunkOverlappingBlocksToSet") == null
                    || AccessTools.Method(typeof(MultiBlockManager), "MainThreadUpdate") == null
                    || AccessTools.Field(typeof(MultiBlockManager), "trackedDataMap") == null
                    || AccessTools.Field(typeof(MultiBlockManager), "oversizedBlocksWithDirtyStability") == null)
                {
                    Debug.LogError("[MaxChunkAgeDeadlockFix] MultiBlockManager stability members not found, unload deferral skipped.");
                }
                else
                {
                    harmony.CreateClassProcessor(typeof(StabilityDeferPatch)).Patch();
                    harmony.CreateClassProcessor(typeof(DeferredDrainPatch)).Patch();
                    Debug.Log("[MaxChunkAgeDeadlockFix] Chunk unload stability deferral applied.");
                }

                if (AccessTools.Method(typeof(RegionFileManager), "AddGroupedChunks") == null
                    || AccessTools.Field(typeof(RegionFileManager), "chunksInSaveDir") == null)
                {
                    Debug.LogError("[MaxChunkAgeDeadlockFix] RegionFileManager.AddGroupedChunks not found, grouping deferral skipped.");
                }
                else
                {
                    harmony.CreateClassProcessor(typeof(GroupedChunksDeferPatch)).Patch();
                    Debug.Log("[MaxChunkAgeDeadlockFix] Chunk grouping deferral applied.");
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
