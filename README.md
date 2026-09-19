# MaxChunkAgeDeadlockFix

Breaks the lock-order deadlock that freezes a 7 Days to Die dedicated server when `MaxChunkAge`
chunk reset is enabled.

> Server-side Harmony mod · 7 Days to Die dedicated server 3.x · no client download

## What it does

With `MaxChunkAge` set (or after a `resetregion` / chunk reset request), the server hangs hard:
the main thread stops ticking, players time out, the console stops responding. No exception, no
stack trace — the process is alive and idle. A `SIGQUIT` stack dump shows the main thread and the
`SaveChunks` thread each parked in `Monitor.Enter`, waiting on each other.

Three unrelated subsystems — region file I/O, multiblock tracking and the chunk unload path — take
each other's locks in opposite orders, and turning `MaxChunkAge` on is what makes the save thread
walk that path often enough for the race to fire.

The mod makes every acquisition that can close the cycle non-blocking. On contention the work is
queued and replayed on the main thread instead of being dropped, so chunk reset keeps its full
behaviour. `MaxChunkAge` becomes usable.

Two diagnostics come with it — chunk reset logging and a main thread watchdog — both switchable in
`Config.xml` with hot reload.

The full mechanism is in [The locks involved](#the-locks-involved) and everything below it.

## Requirements

| | |
|---|---|
| game | 7 Days to Die dedicated server **3.x** |
| dependency | `0_TFP_Harmony` (ships with the server) |
| clients | nothing to download, server-side only |

Branches of this repository:

| branch | game version |
|---|---|
| [`3.x`](https://github.com/kotfoxtrot/7d2d_MaxChunkAgeDeadlockFix/tree/3.x) | 3.0, 3.1, 3.2 |
| [`2.6`](https://github.com/kotfoxtrot/7d2d_MaxChunkAgeDeadlockFix/tree/2.6) | 2.6 |

**Recommended companion: [CullExpiredFix](https://github.com/kotfoxtrot/7d2d_CullExpiredFix).**
This mod makes `MaxChunkAge` reset *safe*, it does not make it *cheap*. See
[Related mods](#related-mods).

## Install

### From a release

1. Download the archive for your game version from
   [Releases](https://github.com/kotfoxtrot/7d2d_MaxChunkAgeDeadlockFix/releases).
2. Unpack it into `<server>/Mods/` so that you end up with `<server>/Mods/1_MaxChunkAgeDeadlockFix/`
   containing `MaxChunkAgeDeadlockFix.dll`, `ModInfo.xml` and `Config.xml`.
3. Restart the server.

### From source

```bash
git clone -b 3.x https://github.com/kotfoxtrot/7d2d_MaxChunkAgeDeadlockFix.git
cd 7d2d_MaxChunkAgeDeadlockFix
dotnet build -c Release -p:GameRoot=/path/to/server
```

`GameRoot` is the dedicated server root — the folder holding `7DaysToDieServer_Data/Managed` and
`Mods/0_TFP_Harmony`. Omit `-p:GameRoot` and the path baked into the `.csproj` is used. The build
references the game assemblies in place and never copies them.

Copy `bin/MaxChunkAgeDeadlockFix.dll`, `ModInfo.xml` and `Config.xml` into
`<server>/Mods/1_MaxChunkAgeDeadlockFix/`.

### Folder name

The folder must start with `1_`. Mods are loaded in alphabetical order: `0_TFP_Harmony` provides
Harmony and has to come first, and this mod patches engine call sites that other mods also patch,
so its patches should be applied ahead of theirs. `1_` puts it directly after Harmony and before
everything else.

## Configuration

`Config.xml` sits next to the DLL in the mod folder and is created with defaults on first start if
missing. Both switches are on by default:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<MaxChunkAgeDeadlockFix>
  <property name="Logging" value="true" />
  <property name="Watchdog" value="true" />
</MaxChunkAgeDeadlockFix>
```

| property | default | effect when `false` |
|---|---|---|
| `Logging` | `true` | silences the diagnostic log output: chunk reset/removal batches, the "CullChunklessData skipped" notice, the deferred backlog warning and the per-patch "applied" lines at startup. Counters keep running, so a watchdog dump still reports real numbers. Errors and warnings from failure paths are never silenced. |
| `Watchdog` | `true` | the watchdog is not installed at all: no `ThreadManager.UpdateEv` subscription, no background thread, no stall warnings and no `SIGQUIT` stack dump. |

The file is hot-reloaded: a `FileSystemWatcher` (with a 1s poll fallback) picks up edits while the
server runs, and each successful reload is logged. `Logging` takes effect immediately; `Watchdog`
starts or stops the watchdog thread on the spot. A malformed file is rejected with a warning and
the current values are kept.

Both switches affect only diagnostics. The deadlock patches themselves are always applied — there
is no reason to run this mod with them off.

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

## Failure behaviour

* Only `CullChunklessData` can be skipped; stability updates and chunk grouping are always
  replayed.
* All patches are guarded by `AccessTools` lookups in `InitMod`. If a game update renames a
  member, that patch group is skipped with a log line instead of throwing, and the rest still
  apply.
* Logging and watchdog installation are wrapped in their own `try/catch` — if either fails the
  deadlock patches are unaffected.
* Both diagnostics are switchable at runtime via `Config.xml`, see [Configuration](#configuration).

## Related mods

Three server-side mods for the same dedicated server, same build layout, same `[Name]` log prefix:

| mod | what it is for |
|---|---|
| [MaxChunkAgeDeadlockFix](https://github.com/kotfoxtrot/7d2d_MaxChunkAgeDeadlockFix) | this mod — makes `MaxChunkAge` chunk reset safe by breaking the lock-order deadlock |
| [CullExpiredFix](https://github.com/kotfoxtrot/7d2d_CullExpiredFix) | makes that same reset cheap: `CullExpiredChunks` drops from 47% of one core to 0.05% |
| [ProfLog](https://github.com/kotfoxtrot/7d2d_ProfLog) | read-only profiler — the tool those numbers were measured with |

**CullExpiredFix — optional, recommended.** This mod makes `MaxChunkAge` safe to enable; it does
nothing about what that reset costs. With `MaxChunkAge` on, the save thread runs a full O(N) sweep
of the save directory once per saved chunk — measured at 17.7 sweeps/s, 26 ms each, 47% of one core
on a 105k-chunk world. CullExpiredFix removes that cost and changes nothing about the semantics.
Not required: this mod is complete on its own.

**ProfLog — optional, for admins.** A read-only measuring mod. It is how the figures above were
produced, and it is how you check the same figures on your own server: it decomposes
`CullExpiredChunks` into lock wait, protection rebuild, scan and removal, and its `MultiBlockMain`
probe covers this mod's main-thread drain and its `TryEnter(chunksInSaveDir, 20)`. Useful for
diagnosis, not needed to run this mod.
