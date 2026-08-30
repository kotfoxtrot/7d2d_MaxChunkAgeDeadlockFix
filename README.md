# MaxChunkAgeDeadlockFix

Server-side Harmony mod for 7 Days to Die dedicated server 3.x.
Breaks a lock-order deadlock that freezes the whole server when `MaxChunkAge` chunk reset is
enabled. No client download.

## Symptom

With `MaxChunkAge` set (or after a `resetregion` / chunk reset request), the server hangs hard:
the main thread stops ticking, players time out, the console stops responding. No exception, no
stack trace — the process is alive and idle. A `SIGQUIT` stack dump shows the main thread and the
`SaveChunks` thread each parked in `Monitor.Enter`, waiting on each other.

## The locks involved

Three unrelated subsystems each guard their state with their own monitor:

| lock | owner | guards |
|---|---|---|
| `RegionFileManager.chunksInSaveDir` | region file I/O | the `Dictionary<long, uint>` of saved chunk keys → timestamps, and the chunk group table |
| `MultiBlockManager.lockObj` | multiblock tracking | `trackedDataMap`, `oversizedBlocksWithDirtyStability` |
| `ChunkManager.chunksToUnload` | chunk lifecycle | the list of chunks pending unload |

There is no global lock order, and the three subsystems call into each other.

## The cycle

**Edge 1 — `chunksInSaveDir` → `lockObj` (SaveChunks thread).**

`thread_SaveChunks` → `DoSaveChunks()` → `CullExpiredChunks()`:

```csharp
lock (saveLock)
lock (chunksInSaveDir)
{
    ...
    RemoveChunks(expiredChunks, _resetDecos: true, _optimizeFileLayouts: false);
}
```

and `RemoveChunks` ends with:

```csharp
lock (chunksInSaveDir)
{
    RemovePersistentDataForChunks(_chunks);
    resetVolumeDataForChunks(_chunks);      // touches World from the save thread
    foreach (long _chunk in _chunks) RemoveChunk(_chunk, _resetDecos);
    MultiBlockManager.Instance.CullChunklessData();   // -> lock (lockObj)
    ...
}
```

So the save thread holds `chunksInSaveDir` and then asks for `lockObj`.

**Edge 2 — `chunksInSaveDir` → `chunksToUnload` (SaveChunks thread).**

Still inside the same `lock (chunksInSaveDir)`, `CullExpiredChunks` calls
`UpdateChunkProtectionLevels()`, whose `EvaluateSingleChunkProtectionLevels` does:

```csharp
GameManager.Instance.World.m_ChunkManager.ProcessChunksPendingUnload(chunk => { ... });
// -> lock (chunksToUnload)
```

**Edge 3 — `chunksToUnload` → `lockObj` (main thread).**

`ChunkManager.removeChunksToUnload()` holds `lock (chunksToUnload)` for its whole body and calls
`chunkCache.UnloadChunk(chunk)` inside it. Sooner or later that chunk goes back to the memory
pool, and `MemoryPooledObject<Chunk>.Free` calls `Chunk.Reset()`, which does:

```csharp
StopStabilityCalculation = true;
```

That property setter fires `ChunkManager.OnChunkStabilityCalculationEnabled`, which
`MultiBlockManager` subscribes to:

```csharp
public void OnChunkStabilityCalculationEnabled(Chunk chunk)
{
    if (!CheckFeatures(FeatureFlags.OversizedStability)) return;
    Vector2i chunkPos = new Vector2i(chunk.X, chunk.Z);
    lock (lockObj)   // <-- here
    {
        AddChunkOverlappingBlocksToSet(chunkPos, trackedDataMap.OversizedBlocks, oversizedBlocksWithDirtyStability);
    }
}
```

On a dedicated server the free is not always immediate. `ChunkProviderGenerateWorld.UnloadChunk`
hands the chunk to `RegionFileManager.AddChunkSync`, which frees it right there on the main
thread when saving is off or the chunk still needs decoration, and otherwise queues it — the
`SaveChunks` thread then frees it at the top of `DoSaveChunks` under `lock (chunksToUnloadLater)`.
Both routes end in `Chunk.Reset()` and therefore in `lockObj`, so this edge exists on the main
thread and on the save thread.

**Edge 4 — `lockObj` → `chunksInSaveDir` (main thread). This is the edge that closes the cycle.**

`MultiBlockManager.TryRegisterCrossChunkMultiBlock` (and `ProcessDeregistrationCleanup`) call
into the region file manager *while holding their own lock*:

```csharp
lock (lockObj)
{
    ...
    regionFileManager.AddGroupedChunks(tempChunksToGroup);   // -> lock (chunksInSaveDir)
}
```

Put together:

```
SaveChunks thread:   chunksInSaveDir  ──wants──>  lockObj
main thread:         lockObj          ──wants──>  chunksInSaveDir
```

Classic ABBA. `chunksToUnload` is the third leg: once the main thread parks inside
`removeChunksToUnload` waiting for `lockObj`, it is still holding `chunksToUnload`, so the save
thread's `ProcessChunksPendingUnload` blocks too, and every chunk load/unload in the game stops
behind it.

Without `MaxChunkAge` the cycle is mostly unreachable, because `CullExpiredChunks` returns
immediately (`maxChunkAge < 0 && resetRequestedChunks.Count == 0`) and edges 1 and 2 are never
taken. Turning `MaxChunkAge` on makes the save thread walk that path once per `DoSaveChunks`,
which is what makes the race fire in practice.

## The fix

The rule the mod enforces: **nobody blocks on a lock that is part of the cycle.** Every
cycle-forming acquisition becomes `Monitor.TryEnter(..., 0)`. On contention the work is queued
and replayed on the main thread, so behaviour is preserved rather than dropped.

### `MultiBlockManager.CullChunklessData` — edge 1

Prefix takes `lockObj` with `TryEnter(0)`. If it fails, the original method is skipped and the
save thread walks on without ever blocking. This is the one place where work is genuinely
skipped, and it is safe: `CullChunklessData` is a garbage-collection pass over tracked data whose
chunks are gone, and `DoSaveChunks` runs it again on the next cycle. Skips are counted and logged
at most once per 30s.

### `MultiBlockManager.OnChunkStabilityCalculationEnabled` — edge 3

Prefix takes `lockObj` with `TryEnter(0)`. On contention the chunk position is pushed into a
pending `HashSet<Vector2i>` and the original is skipped, so the chunk unload path never parks
while holding `chunksToUnload`.

### `RegionFileManager.AddGroupedChunks` — edge 4

Prefix takes `chunksInSaveDir` with `TryEnter(0)`. On contention the key set is copied and queued
together with its `RegionFileManager`, and the original is skipped. If the queue is at its hard
cap (65536 entries) the prefix falls through to the original blocking call instead of losing the
grouping — a bounded queue must never silently drop chunk group data.

### Replay — `MultiBlockManager.MainThreadUpdate`

A prefix on `MainThreadUpdate` drains both queues on the main thread:

* stability: one `TryEnter(lockObj, 20ms)` for the whole batch, replaying
  `AddChunkOverlappingBlocksToSet` per queued chunk; on timeout the batch is re-queued;
* grouping: `TryEnter(chunksInSaveDir, 20ms)` per entry, calling the real `AddGroupedChunks`
  behind a `[ThreadStatic] BypassGrouping` flag so the prefix does not re-defer it. Entries that
  could not run are re-inserted at the head of the queue in order.

Backlogs of 4096+ are warned about at most once per 30s.

### `RegionFileManager.resetVolumeDataForChunks`

Not part of the lock cycle, but the same call chain has the save thread mutating main-thread
world state:

```csharp
World world = GameManager.Instance.World;
foreach (long _chunk in _chunks)
{
    world.ResetTriggerVolumes(_chunk);
    world.ResetSleeperVolumes(_chunk);
}
```

The prefix passes through untouched when already on the main thread. Off the main thread it
copies the keys, schedules the work via `ThreadManager.AddSingleTaskMainThread` and skips the
original.

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
  chunks reset, cull skips, queued/run/pending defers, time since last drain);
* at 90s — the same at `Log.Error`, then `kill(getpid(), SIGQUIT)` via `libc`, which makes Mono
  print a managed stack dump of every thread into the log. Unix only; on other platforms it logs
  that it skipped the dump.

The watchdog only reports. It never touches locks or game state.

## Behaviour notes

* Only `CullChunklessData` can be skipped; stability updates and chunk grouping are always
  replayed.
* All patches are guarded by `AccessTools` lookups in `InitMod`. If a game update renames a
  member, that patch group is skipped with a log line instead of throwing, and the rest still
  apply.
* Logging and watchdog installation are wrapped in their own `try/catch` — if either fails the
  deadlock patches are unaffected.

## Build

```bash
dotnet build -c Release
```

`GameRoot` defaults to `/home/sdtdtest/serverfiles`; override with
`dotnet build -c Release -p:GameRoot=/path/to/server`.

Deploy `bin/MaxChunkAgeDeadlockFix.dll` plus `ModInfo.xml` into
`<server>/Mods/1_MaxChunkAgeDeadlockFix/`. The `1_` prefix keeps it loading after
`0_TFP_Harmony`.
