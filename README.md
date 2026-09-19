# MaxChunkAgeDeadlockFix

Breaks the lock-order deadlock that freezes a 7 Days to Die dedicated server when `MaxChunkAge`
chunk reset is enabled, and repairs two pieces of missing synchronization on the chunk save path.

> Server-side Harmony mod · 7 Days to Die dedicated server 2.6 · no client download

## What it does

Three things, all on the chunk save path:

1. **breaks the lock-order inversion** that freezes the whole server when `MaxChunkAge` chunk reset
   is enabled. The main thread stops ticking, players time out, the process stays alive and idle,
   and a `SIGQUIT` dump shows the main thread and the `SaveChunks` thread each parked in
   `Monitor.Enter`, waiting on each other;
2. **restores the synchronization 2.6 omits** around region file layout optimization, which
   otherwise lets a chunk write destroy the region file header and leaves the file unopenable with
   `Incorrect region file header!`;
3. **serializes the world's single shared chunk read and write buffers**, which the engine leaves
   unsynchronized in both 2.6 and 3.2, and which silently delete player chunks when two threads load
   at once.

Contended locks are taken non-blocking and the work is replayed on the main thread instead of being
dropped, so chunk reset keeps its full behaviour. Every patch on the save path falls back to vanilla
instead of propagating, so a mod fault can never abort `DoSaveChunks`.

This is the 2.6 port of the 3.x mod of the same name; see
[What differs from the 3.x version](#what-differs-from-the-3x-version). It carries two fixes the 3.x
build does not need.

## Requirements

| | |
|---|---|
| game | 7 Days to Die dedicated server **2.6** |
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

1. Download the **2.6** archive from
   [Releases](https://github.com/kotfoxtrot/7d2d_MaxChunkAgeDeadlockFix/releases).
2. Unpack it into `<server>/Mods/` so that you end up with `<server>/Mods/1_MaxChunkAgeDeadlockFix/`
   containing `MaxChunkAgeDeadlockFix.dll`, `ModInfo.xml` and `Config.xml`.
3. Restart the server.

### From source

```bash
git clone -b 2.6 https://github.com/kotfoxtrot/7d2d_MaxChunkAgeDeadlockFix.git
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

### Verifying before deploy

The incident this port fixes was a 3.x-only API call that compiled fine and threw at runtime. Two
checks catch that class of bug without starting the server:

```bash
dotnet apicheck.dll \
  bin/MaxChunkAgeDeadlockFix.dll \
  <server>/7DaysToDieServer_Data/Managed \
  <server>/Mods/0_TFP_Harmony

dotnet patchcheck.dll \
  bin/MaxChunkAgeDeadlockFix.dll \
  <server>/7DaysToDieServer_Data/Managed/Assembly-CSharp.dll
```

The first resolves every member reference against the real 2.6 assemblies; the second resolves every
`[HarmonyPatch]` target and every injected parameter name.

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
| `Logging` | `true` | silences the routine diagnostic output: chunk reset/removal batches, the "CullChunklessData skipped" notice, the deferred backlog warning and the per-patch "applied" lines at startup. Counters keep running, so a watchdog dump still reports real numbers. |
| `Watchdog` | `true` | the watchdog is not installed at all: no `ThreadManager.UpdateEv` subscription, no background thread, no stall warnings and no `SIGQUIT` stack dump. |

`Logging=false` never silences the data-integrity reports, because those describe something that
actually happened to chunk data on disk: the chunk recovered on read retry, the refused
delete-on-read-failure, the dropped stale sector entry, and every `Failsafe` hit. Those stay at
`Log.Warning`/`Log.Error` regardless.

The file is hot-reloaded: a `FileSystemWatcher` (with a 1s poll fallback) picks up edits while the
server runs, and each successful reload is logged. `Logging` takes effect immediately; `Watchdog`
starts or stops the watchdog thread on the spot. A malformed file is rejected with a warning and
the current values are kept.

Both switches affect only diagnostics. The deadlock, region file and chunk stream patches are
always applied.

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

The region file and shared chunk stream protection below have no counterpart in the 3.x build:
3.2 has the `lock (this)` that 2.6 omits, and the shared-buffer defect is reachable there only
under the same second-reader condition.

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

## Region file protection

Vanilla 2.6 omits the `lock (this)` that 3.2 has on `RegionFileV2.OptimizeLayout`. `RemoveChunk`
poisons `usedSectors` with key 0 through `SetLocationInfo(cX, cZ, 0, 0)`, `findFreeSectorOfSize`
then hands back sector 0, 1 or 2, and `WriteData` logs `Sector offset < 3` but writes anyway,
destroying the magic bytes and the location header. The file no longer opens:
`Incorrect region file header!`.

Three patches close it:

* **`OptimizeLayout`** runs under the region file lock, plus a process-wide gate — see below.
* **`findFreeSectorOfSize`** skips `usedSectors` entries below sector 3 or with a non-positive
  length, never lets the running candidate move backwards, and a postfix clamps any result below
  3 up to 3.
* **`WriteData`** drops a location entry that points into the reserved sectors before the write
  and lets the chunk be reallocated, instead of overwriting the header.

`OptimizeLayout` also takes a **static** gate, not just the instance lock. `optimizerMemoryStream`
is `static` on `RegionFileV2` and shared by every region file in the world, so `lock (this)` — the
form 3.2 uses — does not protect it. Two threads optimizing different regions would trample a
shared 16 MB buffer. Only the save thread calls it today, so this has never fired; the guard is
there because the lock was on the wrong object.

Region file protection is a precondition. If any of those members is missing, no patch in the mod
is applied at all, because deferral without it is the combination that corrupts.

## Shared chunk stream protection

The engine keeps **one** chunk read path and **one** chunk write path for the whole world, with no
synchronization:

* `RegionFileAccessMultipleChunks` holds a single `readStream` and a single `writeStream`.
* `RegionFileChunkReader` holds a single `zipLoadStream` (a `DeflateInputStream` created once and
  reused forever, because `innerLoadStream != inputStream` never becomes true), plus
  `loadChunkMemoryStream`, `loadBuffer` and `magicBytes`.
* `RegionFileChunkWriter` holds a single `zipSaveStream` and `saveBuffer`.

Two threads loading chunks at once therefore tear each other's stream. The victim thread sees
`Wrong chunk header!` or `Attempted to read past the end of the stream`, or an inflate error such
as `invalid block type`, and `ChunkSnapshotUtil.LoadChunk` then **deletes the chunk** in its catch
block via `regionFileAccess.Remove` — without consulting land claim protection. Player builds
disappear and regenerate from seed.

This is identical in 2.6 and 3.2; those three files do not differ by a byte. A vanilla server has
a single chunk loader and never trips it. Undead Legacy adds a second one:
`H_ChunkResetPatch.RegionFileManager_RemoveChunks_PrefixPatch` calls `RegionFileManager.GetChunkSync`
for every chunk being reset, on `thread_SaveChunks`, while `ChunkProviderGenerateWorld.GenerateChunksThread`
is loading chunks for players.

Measured on a live server with `ChunkIoProbe`: 194 079 loads without concurrent access produced
**zero** failures; 27 loads with concurrent access produced **four**. Two `error_backup` dumps
written by the two different threads for two different chunks were byte-identical, and their
content was offset by one byte from the chunk header — the shared stream, caught mid-read.

Each shared buffer has exactly one entry point, so one lock per path covers all of it:

```
readIntoLoadStream    <- only ChunkSnapshotUtil.LoadChunk
WriteBackup           <- only ChunkSnapshotUtil.LoadChunk
GetInputStream        <- only readIntoLoadStream
WriteStreamCompressed <- only RegionFileChunkSnapshot.Write
```

* **`ChunkSnapshotUtil.LoadChunk`** runs under a process-wide reader gate. This covers
  `readStream`, `zipLoadStream`, `loadChunkMemoryStream`, `loadBuffer`, `magicBytes` and
  `WriteBackup` in one place, for any number of readers — not just the two that exist today.
* **`RegionFileChunkWriter.WriteStreamCompressed`** runs under a writer gate. Only the save thread
  writes today and no contention has ever been observed, but it is the same class of defect.
* **`readIntoLoadStream`** retries once on failure. Because the retry runs with the reader gate
  held, a read that failed from interference succeeds the second time and the chunk is recovered
  instead of destroyed. A genuinely corrupt chunk fails twice and falls through to vanilla
  handling. A `[ThreadStatic]` flag stops the retry recursing.
* **`RegionFileAccessMultipleChunks.Remove`** is refused while the calling thread is inside
  `LoadChunk`. `Remove` is reachable there only after the catch block, so this blocks exactly the
  delete-on-read-failure path and nothing else. Note what this does and does not buy: the chunk
  survives on disk, but the caller still regenerates it in memory and may save the regenerated
  copy over it. The real protection is the gate and the retry; this is a last net that keeps the
  bytes recoverable and makes the event loud.

Lock order is `saveLock` → `chunksInSaveDir` → reader gate on the save thread, and reader gate →
`regionTable` → region file instance on the loader. No path takes them the other way round, so
there is no inversion. Contention is rare by measurement — 27 in 194 079 calls — so serializing
costs effectively nothing.

Like region file protection, these members are a precondition: if `LoadChunk`,
`readIntoLoadStream`, `WriteStreamCompressed` or `Remove` cannot be found, the mod applies nothing.
The retry is the one exception, applied in its own `try/catch` because its signature is the most
fragile; if it fails the gates still stand.

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

**Counters.** The watchdog dump also carries the region and stream counters:

```
reservedSectorRejections=0 staleReservedEntries=0
chunkReadRetries recovered=0 failed=0 deletionsBlocked=0
```

`reservedSectorRejections` and `staleReservedEntries` must stay at zero — a non-zero value means a
region file was about to be written into its own header. `chunkReadRetries recovered` counts reads
that failed and succeeded on the second attempt under the gate; each one is a chunk that vanilla
would have deleted. `failed` counts reads that failed twice, which points at genuine on-disk
corruption rather than interference. `deletionsBlocked` counts refused delete-on-read-failure
calls.

With the gates in place all three should sit at zero. `ChunkIoProbe`, if installed, is the
independent check: its `overlapped` count for the reader resource must go to zero.

## Failure behaviour

Every patch on the chunk save path is wrapped so it can never propagate an exception into the
game. This is not theoretical: `DoSaveChunks` calls `CullExpiredChunks` **before** it writes
anything, catches everything, and returns `false`. One throwing prefix therefore stops chunk
saving for the whole process — and because `CullExpiredChunks` clears `expiredChunks` only after
`RemoveChunks` returns, the expired list also stops being cleared and grows to its 10000 cap. The
server then never saves another chunk and hangs forever in `WaitSaveDone()` on shutdown, which has
no timeout.

On any exception a patch logs once per site per 60s through `Failsafe.Report` and returns `true`,
letting the vanilla method run. `failsafeHits` is included in the watchdog dump.

Beyond that:

* Only `CullChunklessData` can be skipped; stability updates and chunk grouping are replayed.
  Both queues are capped at 65536 entries. The grouping queue falls through to the original
  blocking call at the cap so chunk group data is never lost; the stability queue drops further
  positions at the cap, which at worst delays an oversized-block stability refresh.
* All patches are guarded by `AccessTools` lookups in `InitMod`. If a game update renames a
  member, that patch group is skipped with a log line instead of throwing, and the rest still
  apply — except for the two preconditions above, where nothing at all is applied.
* Logging and watchdog installation are wrapped in their own `try/catch` — if either fails the
  deadlock patches are unaffected.
* The reader and writer gates are process-wide and held only for the duration of one chunk load or
  one chunk write, so a long cull pass does not lock the loader out — the two interleave between
  individual chunks.
* Fixing the shared buffers here rather than in a mod that patches Undead Legacy is deliberate:
  the defect is in the engine, and the gate covers any second, third or fourth reader regardless
  of which mod introduces it. Undead Legacy's own cost — one disk read and 65 536 block lookups
  per reset chunk, where its `ULM_PowerManager` already indexes every node by `Vector3i` — is a
  performance problem for its author, not a correctness one for this mod.

## Related mods

Three server-side mods for the same dedicated server, same build layout, same `[Name]` log prefix.
All three have a `2.6` branch:

| mod | what it is for |
|---|---|
| [MaxChunkAgeDeadlockFix](https://github.com/kotfoxtrot/7d2d_MaxChunkAgeDeadlockFix) | this mod — makes `MaxChunkAge` chunk reset safe by breaking the lock-order deadlock |
| [CullExpiredFix](https://github.com/kotfoxtrot/7d2d_CullExpiredFix) | makes that same reset cheap: `CullExpiredChunks` drops from 47% of one core to 0.05% |
| [ProfLog](https://github.com/kotfoxtrot/7d2d_ProfLog) | read-only profiler — the tool those numbers were measured with |

**CullExpiredFix — optional, recommended.** This mod makes `MaxChunkAge` safe to enable; it does
nothing about what that reset costs. With `MaxChunkAge` on, the save thread runs a full O(N) sweep
of the save directory once per saved chunk — measured at 17.7 sweeps/s, 26 ms each, 47% of one core
on a 105k-chunk world. `CullExpiredChunks` and its call site are identical in 2.6, so the same fix
applies. Not required: this mod is complete on its own.

**ProfLog — optional, for admins.** A read-only measuring mod. It decomposes `CullExpiredChunks`
into lock wait, protection rebuild, scan and removal, and its `MultiBlockMain` probe covers this
mod's main-thread drain and its `TryEnter(chunksInSaveDir, 20)`. Useful for diagnosis, not needed to
run this mod.
