# MaxChunkAgeDeadlockFix (2.6)

Server-side Harmony mod for 7 Days to Die dedicated server **2.6**. Port of the 3.x mod of the
same name. No client download.

It does three things, all on the chunk save path:

1. breaks the lock-order inversion that freezes the whole server when `MaxChunkAge` chunk reset is
   enabled;
2. restores the synchronization 2.6 omits around region file layout optimization, which otherwise
   lets a chunk write destroy the region file header;
3. serializes the world's single shared chunk read and write buffers, which the engine leaves
   unsynchronized in both 2.6 and 3.2, and which silently delete player chunks when two threads
   load at once.

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
* The reader and writer gates are process-wide and held only for the duration of one chunk load or
  one chunk write, so a long cull pass does not lock the loader out — the two interleave between
  individual chunks.
* Fixing the shared buffers here rather than in a mod that patches Undead Legacy is deliberate:
  the defect is in the engine, and the gate covers any second, third or fourth reader regardless
  of which mod introduces it. Undead Legacy's own cost — one disk read and 65 536 block lookups
  per reset chunk, where its `ULM_PowerManager` already indexes every node by `Vector3i` — is a
  performance problem for its author, not a correctness one for this mod.

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
