using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;

namespace MaxChunkAgeDeadlockFix
{
    internal static class RegionFileLayout
    {
        internal const int ReservedSectors = 3;
    }

    [HarmonyPatch(typeof(RegionFileV2), "OptimizeLayout")]
    internal static class OptimizeLayoutLockPatch
    {
        private static void Prefix(RegionFileV2 __instance, ref int __state)
        {
            __state = 0;

            try
            {
                Monitor.Enter(ChunkIoGates.Optimizer);
                __state = 1;
                Monitor.Enter(__instance);
                __state = 2;
            }
            catch (Exception e)
            {
                Failsafe.Report("OptimizeLayoutLockPatch.Prefix", e);
            }
        }

        private static void Finalizer(RegionFileV2 __instance, int __state)
        {
            try
            {
                if (__state >= 2)
                {
                    Monitor.Exit(__instance);
                }

                if (__state >= 1)
                {
                    Monitor.Exit(ChunkIoGates.Optimizer);
                }
            }
            catch (Exception e)
            {
                Failsafe.Report("OptimizeLayoutLockPatch.Finalizer", e);
            }
        }
    }

    [HarmonyPatch(typeof(RegionFileV2), "findFreeSectorOfSize")]
    internal static class FindFreeSectorPatch
    {
        private static bool Prefix(RegionFileV2 __instance, int _sectorLength, ref int __result)
        {
            try
            {
                SortedDictionary<int, int> usedSectors = __instance.usedSectors;
                if (usedSectors == null)
                {
                    return true;
                }

                lock (__instance)
                {
                    int candidate = RegionFileLayout.ReservedSectors;

                    foreach (KeyValuePair<int, int> usedSector in usedSectors)
                    {
                        if (usedSector.Key < RegionFileLayout.ReservedSectors || usedSector.Value <= 0)
                        {
                            continue;
                        }

                        if (usedSector.Key - candidate >= _sectorLength)
                        {
                            __result = candidate;
                            return false;
                        }

                        int end = usedSector.Key + usedSector.Value;
                        if (end > candidate)
                        {
                            candidate = end;
                        }
                    }

                    __result = candidate;
                    return false;
                }
            }
            catch (Exception e)
            {
                Failsafe.Report("FindFreeSectorPatch.Prefix", e);
                return true;
            }
        }

        private static void Postfix(ref int __result)
        {
            if (__result < RegionFileLayout.ReservedSectors)
            {
                Interlocked.Increment(ref Diagnostics.ReservedSectorRejections);
                __result = RegionFileLayout.ReservedSectors;
            }
        }
    }

    [HarmonyPatch(typeof(RegionFileV2), "WriteData")]
    internal static class WriteDataSectorGuardPatch
    {
        private static void Prefix(RegionFileV2 __instance, int _cX, int _cZ)
        {
            try
            {
                short sectorOffset;
                byte sectorLength;

                lock (__instance)
                {
                    __instance.GetLocationInfo(_cX, _cZ, out sectorOffset, out sectorLength);
                    if (sectorOffset <= 0 || sectorOffset >= RegionFileLayout.ReservedSectors)
                    {
                        return;
                    }

                    SortedDictionary<int, int> usedSectors = __instance.usedSectors;
                    if (usedSectors != null)
                    {
                        usedSectors.Remove(sectorOffset);
                    }

                    __instance.SetLocationInfo(_cX, _cZ, 0, 0);
                }

                Interlocked.Increment(ref Diagnostics.StaleReservedSectorEntries);
                __instance.GetPositionAndPath(out int regionX, out int regionZ, out string fullFilePath);
                Log.Warning("[MaxChunkAgeDeadlockFix] Chunk " + _cX + "/" + _cZ + " in region " + regionX + "/" + regionZ
                    + " pointed at reserved sector " + sectorOffset + " (len " + sectorLength + "), entry dropped and reallocated: " + fullFilePath);
            }
            catch (Exception e)
            {
                Failsafe.Report("WriteDataSectorGuardPatch.Prefix", e);
            }
        }
    }
}
