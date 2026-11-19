# Step 2 close-out (2026-11-18)

Step 2 ("online co-op on top of the ghosts": save transfer, lobby, own leader, shared party, roaming) is closed
after M5. **M6 (battles) moves into Step 3**, which redesigns battles anyway. **M7 (disconnects) moves into the
bug-fix pass planned after Step 3.** Branch `online-step2` (czebrose/Memoria), all pushed at close-out.

## What works (tested on two PCs)

| Milestone | What | Doc |
|---|---|---|
| M1 | The host's whole game as an in-memory save (~85 KB), sent and loaded in ~10 ms; guests never write saves | [step2-m1-save-transfer.md](step2-m1-save-transfer.md) |
| M2 | Lobby at the title (Ctrl+Shift+H/I/J), host picks New Game / a save, guests pick a character, Start | [step2-results.md](step2-results.md) |
| M3 | Each player sees their own character as the field leader; the others as ghosts | [step2-m3-own-leader.md](step2-m3-own-leader.md) |
| M4 | Host-authoritative party state (equip, items, gil, cards); guests' menu changes as requests; in-game Take / Swap / Release / watching | [step2-m4-party-state.md](step2-m4-party-state.md) |
| M5 | Free roaming; a resync when a guest and the host meet (keeps the guest's spot); Join host; guests' finds and flags go to the host; guests on the host's field wait during its scenes | [step2-m5-roaming.md](step2-m5-roaming.md) |

Hotkeys and the status box: [step2-results.md](step2-results.md). Planning history: [step2-m5-m7-plan.md](step2-m5-m7-plan.md).

## What Step 3 inherits (battles)

Today a battle is a single-player battle on whichever PC starts it:
- **Guests still get random encounters and can start scripted battles** (talk, regions) on their own, both alone
  and on the host's field. A guest's battle runs on the guest's PC alone; its results are the guest's until the next
  meeting resync replaces them (EXP, items and gil won are lost; a find from a battle isn't forwarded).
- **A guest's game over isn't handled** (it goes to the game's own game over / title; the session isn't reset).
- **The host in battle:** guests on its field freeze ("The host is in a battle..."), others keep roaming. The host
  fights with the whole party; the guests' characters are driven by the host's game.
- **The party sync pauses in battle** (`PartySync.CanTouchPartyNow`): the host's post-battle state reaches guests
  with the next update after the battle, and in full with the next meeting resync.
- **Reusable pieces:** the host status message (`MsgHostState`: Playing / Scene / Menu / Battle / WorldMap + field),
  the freeze (`GuestGuard.Frozen`), the full resync and the meeting resync, PartySync's request path (a guest's
  battle actions could ride on it), the script-opcode hooks pattern (`GuestFinds`).

The battle design has to decide what happens when a **guest roaming alone** meets an encounter (block it, fight it
alone, pull the host in...).

## Deferred to the bug-fix pass (M7 and known bugs)

Disconnects (old M7 plan in [step2-m5-m7-plan.md](step2-m5-m7-plan.md#m7-disconnects)):
- **Host leaves:** nothing handles it. Steam makes a guest the lobby owner; that guest's game carries on as if it
  were the host. Wanted: "Host left", everyone back to the title.
- **A guest drops without leaving the lobby:** no silence timeout yet (the character stays theirs).
- Joining mid-game isn't supported (the lobby says "already started").

Known bugs and gaps:
- Guests can't buy in shops: the host rejects the gil change ("only the host can change gil").
- The guest's first party request at a session start is rejected (party setup by the field opening) and shows a
  "didn't accept that" toast.
- Walkmesh NRE (`WalkMesh.BGI_findAccessibleTriangle` from the player's `ServiceChar`), seen a few times on guests.
- Story progress isn't guarded: a guest alone can trigger story events, and the scenario counter they set reaches
  the host (trusted for now).
- `gScriptVector` / `gScriptDictionary` changes on guests aren't forwarded (only `gEventGlobal` is).
- Event bytes 243-249 changed together on a guest (a multi-byte value): forwarded byte by byte, watch for tearing.
- Walking into the host's field reloads it twice (a blink); the world map never triggers a meeting.
- Vayle and character 10 have no field body (a guest playing them shows the stand-in Clove body).
- Ghosts only play stand / walk / run.

Fixed after close-out:
- Snapshot loads replayed the field's entrance (found in spike C): `GameSnapshot.Load` re-entered the field with the
  entrance it was originally entered by, so the field's `Main_Init` replayed it; the mod's field 507 re-ran its New
  Game party setup and dropped Duskly and Vayle. This hit the debug Ctrl+Shift+R restore and guests' resync loads
  alike. Fix: on a field, `Load` sets `FieldEntrance = 10000` like a moogle-save load (cherry-picked from b9529e02).

Mod bugs (in the mod's scripts, not here): "Error Env Play() Slot=0" after the Pilot (field 100) and likely Vivi
(field 300) send you to the ship: brief in `handoff-mod-bug-pilot-exit-ambient-sound.md` (Memoria-Multiplayer
folder).

## Housekeeping
- The test PCs run with `Memoria.ini` `[Debug] Enabled = 1`, `OnlineDebug = 1` (the status box's debug lines and
  keys). Revert at the end of all testing.
- Test package: `Output/OnlineStep2-PC2.zip` (DLL hash `6B45721CF507` at close-out), with `INSTALL-FOR-AGENT.md`.
