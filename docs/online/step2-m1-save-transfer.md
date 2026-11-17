# Step 2 / M1: save transfer — findings

Branch `online-step2` (from `online-ghosts`). Code: `Assembly-CSharp/Memoria/Online/Session/`.

**Status: M1 works (single-PC tests, 2026-11-16).** A save slot loaded from memory gives exactly the same game state as
the game's own Continue; mid-game snapshots round-trip exactly; guest save files aren't written. Blob: **83 KB**.
Two-PC transfer is open. New Game: every client runs it, then one resync (decided).

## Summary

- **The serializer is the game's own `JsonParser`**, and the whole state of a save is one JSON tree. Making an
  in-memory blob is just "build the tree, write it as bytes"; loading is "read the bytes, hand the tree to the game's
  parser, enter the game the way the title screen does". **No save file is read or written on the guest.**
- **Encryption is only on disk.** The storage layer encrypts the tree when writing a slot and decrypts it when reading;
  the tree itself isn't encrypted, so the blob never touches the game's encryption.
- **Memoria's extra data is in the same tree** (`MemoriaExtraData`), so the blob carries it too.
- **Guests keep their own options and achievements** (removed from the tree before loading), and **their autosave is
  blocked** (it would otherwise write the host's game to the guest's save file on the first field load).
- **New Game: recommendation below** (every client runs the game's own New Game, plus a full resync once the host has
  control); it's a design decision for you.

## How the game saves and loads

| Layer | Class | What it does |
|---|---|---|
| Serializer | `WinIosAndrSharedDataSerializer` (`FF9StateSystem.Serializer`) | `Save(slot, save)`: `Parser.ParseFromFF9StateSystem()` then `Storage.Save(...)`. `Load(slot, save)`: `Storage.Load(...)` then `Parser.ParseToFF9StateSystem(tree)`. |
| Parser | `JsonParser` (`Serializer.Parser`, a component) | `ParseFromFF9StateSystem()` builds the tree from `FF9StateSystem`: `Data/10000_State` (mode, `fldMapNo`, `fldLocNo`, world map), `20000_Event` (`gEventGlobal` flags, scenario counter...), `30000_MiniGame`, `40000_Common` (party, players, items, gil...), `50000_Setting`, `60000_Sound`, `70000_World`, `80000_Achievement`, `9x000_*` extras, and `MemoriaExtraData` (Memoria's extended event/common/minigame data). `ParseToFF9StateSystem(tree)` applies it, then re-runs `ff9play.FF9Play_Update` on every player. |
| Storage | `SharedDataBytesStorage` (`Serializer.Storage`) | Disk only. `SavedData_ww.dat` holds every slot: a metadata header and per-slot encrypted, schema-typed binary of the vanilla part. The `MemoriaExtraData` subtree is removed from the tree and written to its own file (`..._Memoria_...dat`, plain SimpleJSON binary); on load it's merged back into one tree, after checking both parts were saved at the same play time. |
| Entering the game | `TitleUI.LoadAutosave`, `SaveLoadUI.OnFinishedLoadFile_delay` | After the parse: hide the title, `EventEngine.ReplaceLoadMap()` (field or world map from `mode` / `fldMapNo` / `fldLocNo`), `Settings.SetSound/SetSoundEffect/UpdateSetting`, `AchievementManager.ResyncNormalAchievements()`, `ff9.w_frameNewSession()`. |
| New Game | `TitleUI.OnNewGameButtonClick` | `FF9StateSystem.ReInitStateSystem()` then `SceneDirector.Replace("FieldMap")`. |
| Autosave (Memoria) | `EventEngine` field init (`EventEngine.cs`, ~line 620) | Every field load (except a few story spots) writes the autosave slot and `SavedData_ww_Memoria_Autosave.dat`. |

A save stores the field and its **entrance** (`fldLocNo`), not an exact position: loading puts the player where the
field's script places you for that entrance. Fine for a session start; positions are per player anyway.

## The blob (`GameSnapshot`)

- **Capture the running game:** `GameSnapshot.CaptureCurrent` = `JsonParser.ParseFromFF9StateSystem()` →
  `JSONNode.SaveToStream` (SimpleJSON binary). Exactly what a save would contain at that moment. It's serialized at
  once because `SharedDataBytesStorage.Save` removes `MemoriaExtraData` from the tree it's handed.
- **Capture a slot without loading it (host "Continue"):** `GameSnapshot.CaptureSlot(slot, save)` =
  `Storage.Load(slot, save, callback)`: the decrypted, merged tree the game would load. No game state changes.
- **Size:** **83 KB** (85,218 bytes for a disc 1 save; 6 chunks of 16 KB). No compression: SimpleJSON's compressed functions are stubbed out in this build
  (they need SharpZipLib, not referenced), and `System.IO.Compression` needs `MonoPosixHelper`, which the game's
  Mono doesn't ship. If the size matters, the game folder does ship `ICSharpCode.SharpZipLib.dll`, so that's the
  option to look at.
- **Load from memory:** `GameSnapshot.Load(blob, asGuest)` = `JSONNode.Deserialize` → (guest:) remove
  `Data/50000_Setting`, `Data/80000_Achievement`, `Data/98000_Achievement`, block autosave → `ParseToFF9StateSystem`
  → the title screen's enter-game sequence (without `ResyncNormalAchievements` on a guest). Works from the title
  screen and in game (field with control, world map).
- **Check:** once the loaded game is playable, its state is captured again and compared with the tree that was loaded
  (`GameSnapshot.Diff`, play time ignored); differences are logged. A second check runs right after the parse, before
  any field script, and at the title `T` also compares the memory load with the game's own load of the same slot.

### What the guest keeps of its own

| Subtree | Why |
|---|---|
| `50000_Setting` | The player's options (sound, ATB, cursor, language...). The play time lives in `95000_Setting` and comes from the host. |
| `80000_Achievement`, `98000_Achievement` | Steam achievement progress counters: loading the host's would unlock / resync achievements on the guest's Steam account (`ResyncNormalAchievements` is skipped too). |

### Guest save files

- Nothing in the load path writes a file.
- **Autosave is blocked on a guest** (`GuestGuard`, one marked `[Online]` line in `EventEngine`'s autosave). Without
  it, the guest's first field load would write the host's game to the guest's autosave slot.
- Still to block in M4: the save menu and Moogle saves.

## Transfer (`BlobTransfer`)

- Channel 2 (state), reliable, **16 KB chunks**. Header (19 bytes): type `0x20`, transfer id, total size, CRC32 of
  the whole blob, chunk index, chunk count, chunk length.
- The sender queues 4 chunks per frame per peer (spike A: reliable sends are buffered by Steam and can stall behind
  each other). The receiver reassembles by index (any order), checks the CRC, and drops a corrupt or malformed blob.
- Each capture also runs a local loopback through the codec (chunks encoded in reverse order and decoded).

## New Game — decided (2026-11-16): every client runs New Game, then one resync

The brief prefers "host starts New Game, sends the state once on the first field". The problem: New Game starts with
the opening (FMV, then the Prima Vista cutscene) with no player control for a long time, and a snapshot taken
mid-cutscene loads like a save at a place the game never lets you save: the field script would see a scenario
counter it normally only meets during that cutscene, entered through the "load" path. That's the riskiest kind of load.

Recommended instead:
1. **Every client runs the game's own New Game** (`FF9StateSystem.ReInitStateSystem()` is a constant initial state:
   same party, items, gil, flags on every PC; random numbers only matter in battles and encounters, which are
   host-only in this step).
2. **Then one full resync** with the host's blob the first time the host has control on a field (same path as
   Continue), and a state-hash check from then on (M4's periodic hash).

For the sequel this is the same: its New Game is its own constant initial state.

## Mid-game snapshots (resync after battles, M4/M6)

A snapshot taken anywhere loads like a save made there. That's normal for places with a Moogle or the world map;
elsewhere it relies on the field script handling a load at that point. The single-PC test checks a few ordinary
fields; for M4/M6 the preferred resync after a battle is an incremental party update, with the full blob as fallback.

## How to test

Single PC, `[Debug] Enabled = 1` and `OnlineDebug = 1` in `Memoria.ini` (debug hotkeys, Ctrl+Shift + key):

1. Note the save files' timestamps (`%USERPROFILE%\AppData\LocalLow\SquareEnix\FINAL FANTASY IX\Steam\EncryptedSavedData`).
2. **Title screen → T**: snapshot of the latest save slot. Log: size, chunks, CRC, codec loopback OK.
3. **R**: loads it from memory into the game. Once you can move, the log reports the check
   (`matches the snapshot exactly`, or the differing paths).
4. Walk around, change field twice: log shows `Autosave blocked`.
5. **S** in a field: snapshot of the running game. Walk elsewhere, change something visible (e.g. open a chest, use an
   item), then **R**: you're back in the captured field with the captured state; check result in the log.
6. Quit; the save files' timestamps are unchanged.

Two PCs (after M1 is accepted): host **S** or **T**, both in a lobby, host **B** sends it; guest log shows the
transfer time and CRC; guest **R** loads it.

## Results

Single PC, three runs (logs in `Output/logs/m1-*.log`, not committed; no exceptions in `output_log.txt`):

| Check | Result |
|---|---|
| Blob size | slot 0/0: 85,218 bytes (83.2 KB); mid-game (cargo ship): 85,119-85,122 bytes; 6 chunks |
| Capture time | slot read from disk: 117-123 ms; running game: 5 ms |
| Parse time (load) | 2-7 ms, then the normal field load |
| Chunk codec loopback (reverse order) | OK every time |
| **Memory load vs the game's own load of the same slot** | **identical game state** |
| Mid-game snapshot → load in game (S → R) | identical right after parsing **and** once playable |
| Slot snapshot loaded from the title / in game (T → R) | loads and plays; the state check shows the same differences as the game's own load (below) |
| Autosave blocked, save files unchanged | 10 autosaves blocked; no save file written after the guest load (timestamps checked) |
| Two-PC transfer time | *pending (after M1 is accepted)* |

**Found:** a save made while a temporary character is in the party (Cinna in old slot 5, disc 1) doesn't come back
identical even through the game's own Continue: right after loading, a fresh capture shows Quina in `players[5]`.
That's how the game maps temporary characters between its old-format save slots and its player list; the memory load
does exactly the same. It isn't an M1 problem, but it means **the state hash in M4 must be computed from a capture
taken after loading on both sides**, never compared against the raw slot tree.

The first run also loaded a cargo-ship snapshot taken mid-scene: within 3 s the field script moved on (504 → 507,
scenario flags changed). That's the "mid-cutscene load" risk from the New Game section, seen for real.
