# Online co-op: rules for contributors

Code for online play lives under `Assembly-CSharp/Memoria/Online/`. These rules were found the hard way during the two
spikes; most of them are the difference between working code and a game that crashes at startup. The full story is in
[steam-spike-a.md](steam-spike-a.md) (Steam) and [spike-b-ghosts.md](spike-b-ghosts.md) (field actors).

1. **All Steam calls go through `SteamNative`.**
   Many exports of the game's `steam_api64.dll` share one address (identical stubs folded by the linker), and Unity
   5.2's Mono caches `Marshal.GetDelegateForFunctionPointer` wrappers *by address*, so binding them directly makes one
   function run with another's marshalling and crashes. `SteamNative` gives every binding its own 16-byte jump stub.
   → [steam-spike-a.md, A2](steam-spike-a.md#a2-interface-pointers-packets)

2. **Never pump Steam callbacks before the title screen.**
   Square Enix's `steamwrapper.dll` calls the game's C# stats delegate without a null check, and the game registers it
   at the title screen, so an earlier `SteamAPI_RunCallbacks` crashes. Poll call results, lobby membership and packets;
   rely on callbacks only once `SiliconStudio.Social.IsCreated` (the game then pumps them every frame).
   → [steam-spike-a.md, A1](steam-spike-a.md#a1-how-ff9-already-talks-to-steam)

3. **Reuse the game's Steam init; never ship or replace `steam_api64.dll`.**
   The game calls `SteamAPI_Init` itself (through `steamwrapper.dll`); a second init or a different DLL breaks
   achievements, cloud saves and the App ID. The game folder has two identical copies of the DLL; `SteamNative` binds
   to the one already loaded (`GetModuleHandle`), never through `[DllImport("steam_api64")]`, which could load the
   other copy uninitialised. → [steam-spike-a.md, A1](steam-spike-a.md#a1-how-ff9-already-talks-to-steam)

4. **.NET 3.5 and Unity 5.2.3p2 only.**
   That's what the game runs: no `async`/`await`, no `Task`, no .NET Standard libraries, old Unity APIs (no
   `DefaultExecutionOrder`, for example). → [steam-spike-a.md](steam-spike-a.md)

5. **Act on a peer's first packet, not on "peer joined".**
   Legacy Steam P2P takes ~4 s after a lobby member appears before packets flow; anything spawned or sent on the join
   event works with nothing. Ghosts spawn on their first state. → [steam-spike-a.md, two-PC results](steam-spike-a.md#two-pc-results-run-1-72-s-two-steam-accounts-on-two-pcs)

6. **Remove field objects with `UnregisterHonoBehavior(true)`, never a plain `Destroy`.**
   A destroyed behaviour left in `HonoBehaviorSystem`'s list makes its `Update` throw every frame, which skips every
   object updated after it (characters glitch). Do what `EventEngine.DisposeObj` does; see `GhostActor.Despawn`.
   → [spike-b-ghosts.md, B2](spike-b-ghosts.md#b2-spawning-outside-the-script--yes)
   Two traps found in M3 (both freeze every character): don't destroy the object's **shadow** before the system has
   disposed of the object (`FieldMapActor.HonoLateUpdate` throws on it), and don't `SetActive(false)` it either (the
   disposal only finds behaviours on active objects). Hide it by its renderers; destroy the shadow once the object is
   gone (`GhostManager.ProcessPendingRemovals`, `BodySwap.CheckReplaced`). Unity's own log
   (`x64/FF9_Data/output_log.txt`) shows these exceptions; Memoria.log doesn't.

7. **Mark engine edits; keep new code under `Memoria/Online/`.**
   Every change to existing game code carries a tag comment (`[Online]` from now on; `[Ghosts]` from spike B) so it
   can be found, reviewed and kept through upstream Memoria merges. New files go in `Memoria/Online/`.

8. **UIDs 244-247 are ghost slots.**
   Ghosts register under these object UIDs (custom range 128-249, below the 250+ special references). The sequel's
   field scripts must not use them; if one does, that field shows fewer ghosts (logged).
   → [spike-b-ghosts.md, B2](spike-b-ghosts.md#b2-spawning-outside-the-script--yes)

9. **Change a live object's body only through `BodyRebuild`.**
   A field object's state is spread over the `Actor`, its `FieldMapActorController` (pathing, speed, floor, walk in
   progress, attach position) and `FieldMapActor` (attachment), plus the per-UID `charArray` entry and the object's
   name ("Player" is looked up by name). `BodyRebuild.Rebuild` carries all of it over and translates animations; mesh
   numbers are per model, so mesh visibility is kept per body (`BodySwap.OnMeshVisibility`).
   → [step2-m3-own-leader.md, section 10](step2-m3-own-leader.md#10-implementation-2026-11-17)

Also:
- **Logs:** use `SteamNative.Tag(steamId)` (`#1234`) in log lines; Steam names (`GetName`) are for the on-screen status
  only. Logs get attached to issues and quoted in docs.
- **Two logs to check:** Memoria's own `Memoria.log`, and Unity's `x64\FF9_Data\output_log.txt`, where Unity's own
  exceptions go.
- **Build and install:** `tools/multiplayer/build.ps1`, then `install.ps1` (backs up the game's DLLs once, outside
  the game folder; `restore.ps1` puts them back).
