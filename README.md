# MaxChunkAgeDeadlockFix (2.6)

Server-side Harmony mod for 7 Days to Die dedicated server **2.6**. Port of the 3.x mod of the
same name. Breaks the lock-order inversion that freezes the whole server when `MaxChunkAge` chunk
reset is enabled. No client download.

## Symptom

With `MaxChunkAge` set (or after a `resetregion` / chunk reset request), the server hangs hard:
the main thread stops ticking, players time out, the process stays alive and idle. No exception,
no stack trace. A `SIGQUIT` stack dump shows the main thread and the `SaveChunks` thread each
parked in `Monitor.Enter`, waiting on each other.

## The locks involved

| lock | owner | guards |
|---|---|---|
| `RegionFileManager.chunksInSaveDir` | region file I/O | `Dictionary<long, uint>` of saved chunk keys → timestamps, and the chunk group table |
| `MultiBlockManager.lockObj` | multiblock tracking | `trackedDataMap`, `oversizedBlocksWithDirtyStability`, `blocksWithDirtyAlignment` |
| `ChunkManager.chunksToUnload` | chunk lifecycle | list of chunks pending unload |

## The cycle in 2.6

**Edge 1 — `chunksInSaveDir` → `lockObj` (SaveChunks thread).**

`thread_SaveChunks` → `DoSaveChunks()` → `CullExpiredChunks()` (`RegionFileManager.cs:574`):

```csharp
lock (saveLock)
lock (chunksInSaveDir)
{
    ...
    RemoveChunks(expiredChunks);
    expiredChunks.Clear();
}
```

and `RemoveChunks` (`:1547`) runs, still under both locks:

```csharp
RemovePersistentDataForChunks(_chunks);
resetVolumeDataForChunks(_chunks);
foreach (long _chunk in _chunks) RemoveChunk(_chunk, _resetDecos);
MultiBlockManager.Instance.CullChunklessData();   // -> lock (lockObj)
```

So the save thread holds `chunksInSaveDir` and then asks for `lockObj`.

**Edge 2 — `chunksInSaveDir` → `chunksToUnload` (SaveChunks thread).**

Inside the same `lock (chunksInSaveDir)`, `CullExpiredChunks` calls `UpdateChunkProtectionLevels()`,
whose `EvaluateSingleChunkProtectionLevels` (`:997`) does:

```csharp
GameManager.Instance.World.m_ChunkManager.ProcessChunksPendingUnload(chunk => { ... });
// -> lock (chunksToUnload)
```

**Edge 3 — `lockObj` → `chunksInSaveDir` (main thread). This closes the cycle.**

`MultiBlockManager.TryRegisterCrossChunkMultiBlock` (`MultiBlockManager.cs:1099`) and
`ProcessDeregistrationCleanup` call into the region file manager *while holding their own lock*:

```csharp
lock (lockObj)
{
    ...
    regionFileManager.AddGroupedChunks(tempChunksToGroup);   // -> MergeOrCreateChunkGroup -> lock (chunksInSaveDir)
}
```

Put together:

```
SaveChunks thread:   chunksInSaveDir  ──wants──>  lockObj
main thread:         lockObj          ──wants──>  chunksInSaveDir
```

Classic ABBA. `chunksToUnload` is dragged in through edge 2: once the save thread parks, every
chunk load/unload queues behind it.

Without `MaxChunkAge` the cycle is mostly unreachable, because `CullExpiredChunks` returns
immediately (`maxChunkAge < 0 && resetRequestedChunks.Count == 0`) and edges 1 and 2 are never
taken. Turning `MaxChunkAge` on makes the save thread walk that path once per `DoSaveChunks`,
which is what makes the race fire in practice.

## What differs from the 3.x version

| 3.x | 2.6 |
|---|---|
| `ThreadManager.AddSingleTaskMainThread(string, Action)` | overload does not exist — only `(string, MainThreadTaskFunctionDelegate, object)`; the reset keys are passed as `_parameter` instead of captured |
| `MultiBlockManager.OnChunkStabilityCalculationEnabled(Chunk)` | does not exist — the equivalent hook is `MultiBlockManager.OnChunkInitialized(Chunk)`, subscribed to `ChunkManager.OnChunkInitialized` and fired from `task_Lighting` on a worker thread |
| stability hook touches `OversizedBlocks` only | `OnChunkInitialized` handles `OversizedBlocks` **and** `TerrainAlignedBlocks`, each behind its own feature flag; the replay mirrors both |
| `Chunk.StopStabilityCalculation` setter fires a stability event, giving a `chunksToUnload` → `lockObj` edge | plain `public bool` field (`Chunk.cs:217`), no event — that edge does not exist here |
| `RemoveChunks(_chunks, _resetDecos, _optimizeFileLayouts)` | `RemoveChunks(_chunks, _resetDecos)` |

Because the `chunksToUnload` → `lockObj` edge is absent in 2.6, deferring `OnChunkInitialized` is
not required to break the cycle — either `CullChunklessDataPatch` or `GroupedChunksDeferPatch`
alone is sufficient. It is kept because it still keeps the lighting thread from piling up behind
`lockObj` and costs a `TryEnter(0)` on an uncontended lock.

## The fix

The rule: **nobody blocks on a lock that is part of the cycle.** Every cycle-forming acquisition
becomes `Monitor.TryEnter(..., 0)`. On contention the work is queued and replayed on the main
thread, so behaviour is preserved rather than dropped.

### `MultiBlockManager.CullChunklessData` — edge 1

Prefix takes `lockObj` with `TryEnter(0)`. If it fails, the original is skipped and the save
thread walks on without ever blocking. This is the one place where work is genuinely skipped, and
it is safe: `CullChunklessData` is a garbage-collection pass over tracked data whose chunks are
gone, and `DoSaveChunks` runs it again next cycle. Skips are counted and logged at most once per
30s.

### `RegionFileManager.AddGroupedChunks` — edge 3

Prefix takes `chunksInSaveDir` with `TryEnter(0)`. On contention the key set is copied and queued
together with its `RegionFileManager`, and the original is skipped. If the queue is at its hard
cap (65536 entries) the prefix falls through to the original blocking call instead of losing the
grouping — a bounded queue must never silently drop chunk group data.

### `MultiBlockManager.OnChunkInitialized` — contention relief

Prefix takes `lockObj` with `TryEnter(0)`. On contention the chunk position is queued and the
original skipped, so the lighting thread never parks on `lockObj`.

### Replay — `MultiBlockManager.MainThreadUpdate`

A prefix on `MainThreadUpdate` (reached from `World.OnUpdateTick`, `World.cs:1858`) drains both
queues on the main thread:

* stability: one `TryEnter(lockObj, 20ms)` for the whole batch, replaying
  `AddChunkOverlappingBlocksToSet` per queued chunk for whichever of `OversizedStability` /
  `TerrainAlignment` is enabled; on timeout the batch is re-queued;
* grouping: `TryEnter(chunksInSaveDir, 20ms)` per entry, calling the real `AddGroupedChunks`
  behind a `[ThreadStatic] BypassGrouping` flag so the prefix does not re-defer it. Entries that
  could not run are re-inserted at the head of the queue in order.

Backlogs of 4096+ are warned about at most once per 30s.

`InitMod` checks for `MainThreadUpdate` first. If the drain point is missing, **neither** deferral
is installed — queueing work that can never be replayed would silently lose it.

### `RegionFileManager.resetVolumeDataForChunks`

Not part of the lock cycle, but the same call chain has the save thread mutating main-thread world
state:

```csharp
World world = GameManager.Instance.World;
foreach (long _chunk in _chunks)
{
    world.ResetTriggerVolumes(_chunk);
    world.ResetSleeperVolumes(_chunk);
}
```

The prefix passes through untouched when already on the main thread. Off the main thread it copies
the keys and schedules the work with the 2.6 signature:

```csharp
ThreadManager.AddSingleTaskMainThread("MaxChunkAgeDeadlockFix.ResetVolumes", RunVolumeReset, copy);
```

`InitMod` verifies that exact overload exists before installing the patch.

## Failsafe

Every patch on the chunk save path is wrapped so it can never propagate an exception into the
game. This is not theoretical: `DoSaveChunks` calls `CullExpiredChunks` **before** it writes
anything, catches everything, and returns `false`. One throwing prefix therefore stops chunk
saving for the whole process — and because `CullExpiredChunks` clears `expiredChunks` only after
`RemoveChunks` returns, the expired list also stops being cleared and grows to its 10000 cap. The
server then never saves another chunk and hangs forever in `WaitSaveDone()` on shutdown, which has
no timeout.

On any exception a patch logs once per site per 60s through `Failsafe.Report` and returns `true`,
letting the vanilla method run. `failsafeHits` is included in the watchdog dump.

## Diagnostics

**Chunk reset logging.** A `[ThreadStatic]` marker around `CullExpiredChunks` lets a postfix on
`RemoveChunks` tell the two cases apart, and logs the batch with world coordinates (first 256
chunks, then `+N more`):

```
[MaxChunkAgeDeadlockFix] Chunk reset (MaxChunkAge/requested): 12 chunk(s), world XZ: (1024,-512) (1040,-512) ...
[MaxChunkAgeDeadlockFix] Chunk removal (manual/space): 3 chunk(s), world XZ: ...
```

**Main thread watchdog.** Subscribes to `ThreadManager.UpdateEv` and polls from a background
thread every 5s:

* at 45s without a main thread tick — `Log.Warning` with the full counter dump (reset batches,
  chunks reset, cull skips, queued/run/pending defers, failsafe hits, time since last drain);
* at 90s — the same at `Log.Error`, then `kill(getpid(), SIGQUIT)` via `libc`, which makes Mono
  print a managed stack dump of every thread into the log. Unix only.

The watchdog only reports. It never touches locks or game state.

## Behaviour notes

* Only `CullChunklessData` can be skipped; stability updates and chunk grouping are replayed.
  Both queues are capped at 65536 entries. The grouping queue falls through to the original
  blocking call at the cap so chunk group data is never lost; the stability queue drops further
  positions at the cap, which at worst delays an oversized-block stability refresh.
* All patches are guarded by `AccessTools` lookups in `InitMod`. If a game update renames a
  member, that patch group is skipped with a log line instead of throwing, and the rest still
  apply.
* Logging and watchdog installation are wrapped in their own `try/catch` — if either fails the
  deadlock patches are unaffected.

## Build

```bash
dotnet build -c Release
```

`GameRoot` defaults to `/home/sdtdtest/scripting_2.6/gamefiles`; override with
`dotnet build -c Release -p:GameRoot=/path/to/server`.

Deploy `bin/MaxChunkAgeDeadlockFix.dll` plus `ModInfo.xml` into
`<server>/Mods/1_MaxChunkAgeDeadlockFix/`. The `1_` prefix keeps it loading after
`0_TFP_Harmony`.

## Verifying before deploy

The incident this port fixes was a 3.x-only API call that compiled fine and threw at runtime.
Two checks catch that class of bug without starting the server:

```bash
dotnet /home/sdtdtest/crashdig/apicheck/bin/Release/net8.0/apicheck.dll \
  bin/MaxChunkAgeDeadlockFix.dll \
  /home/sdtdtest/scripting_2.6/gamefiles/7DaysToDieServer_Data/Managed \
  /home/sdtdtest/scripting_2.6/gamefiles/Mods/0_TFP_Harmony

dotnet /home/sdtdtest/crashdig/patchcheck/bin/Release/net8.0/patchcheck.dll \
  bin/MaxChunkAgeDeadlockFix.dll \
  /home/sdtdtest/scripting_2.6/gamefiles/7DaysToDieServer_Data/Managed/Assembly-CSharp.dll
```

The first resolves every member reference against the real 2.6 assemblies; the second resolves
every `[HarmonyPatch]` target and every injected parameter name.
