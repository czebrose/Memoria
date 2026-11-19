# Step 2 / M5: free roaming (part 1, as built 2026-11-18)

Plan: [step2-m5-m7-plan.md](step2-m5-m7-plan.md#m5-free-roaming-meeting-resyncs-and-guest-changes). Part 1 covers
plan steps 1-2 (the meeting resync and Join host), plus the logging that starts step 4 (event bytes). Finds and flags
aren't forwarded to the host yet (steps 3-4), and there's no cutscene freeze yet (step 5).

## What it does

- **Guests roam freely.** Nothing ever blocked exits (the old plan's blocks were never built), so a guest walks to
  any field and runs it with the host's world as of their last meeting.
- **Meeting resync** (`SessionLobby.Meet.cs`):
  - Each guest compares its own field with the host's field. The host's field comes from the host's ghost states
    (`GhostManager.TryGetLastField`). A menu, battle or world map on the host's side changes nothing.
  - When both are the same field, the guest sends `MsgMeet` (kind meet, field id) once per meeting. It sends nothing
    again until the two are apart. A guest that just loaded the host's world counts as met.
  - The host answers once it has control (not during a cutscene, a menu or a fade). It sends its whole game (the M1
    blob) and a 12-byte `MsgResync` with mode "keep spot" and the field id. If it has left that field meanwhile, it
    drops the request; the guest asks again at the next meeting.
  - The guest loads it only if it is still on that field (otherwise "not loaded" in the log). It saves its position
    and facing before the load and puts the player back there once the reloaded field is playable.
- **Join host** (Ctrl+Shift+J in game, or the panel's button, guests only): the host sends the normal resync. The
  guest lands where the host is, on a field or the world map. In game, Ctrl+Shift+J no longer means "join a lobby".
- **Panel** (status box on): guests see "On the host's field" / "Exploring on your own" and the Join host button.
- **Event bytes log** (`EventWatch.cs`): on every PC while playing, each change to `gEventGlobal` is logged every
  250 ms, with the field and who: `host`, `guest, with the host` or `guest, alone`. It also notes changes made
  "during a scene" (no player control). A whole-game load only takes a new baseline.

## Known limits of part 1

- The world map never triggers a meeting (only fields do). Join host works from and to the world map.
- Walking into the host's field loads it twice (once with the guest's flags, then with the host's), so there's a
  blink.
- A guest's chests and flags made alone are still lost at the next meeting (steps 3-4 are next).
- Guests still get random encounters and battles while roaming (M6).

## R1 test steps (two PCs; Continue on the cargo ship, guest plays any character)

1. Both on the starting field. Each log has one `Meet: ... both on field N` (guest) / `sent the host's world`
   (host). The guest doesn't jump to the host's spot.
2. **Guest walks off** to the next field alone. Guest panel says "Exploring on your own"; guest log
   `Meet: apart now`. The host's game doesn't change.
3. **Host follows** onto the guest's field. Guest: one reload (a blink), still at its own spot; log
   `Meet: back at your spot`.
4. **Guest walks back** into a field where the host is. One blink on arrival, guest at its entrance (its own spot).
5. **Host in a cutscene or menu** when the guest arrives: the host's answer waits until the host has control.
6. **Join host:** guest goes 2+ fields away and presses Ctrl+Shift+J (or the button). It appears at the host's spot.
7. **Open a chest or talk to NPCs** on both PCs (alone and together). Afterwards, send both logs: the
   `Event bytes` lines are the material for steps 3-4.

## R1 test results (2026-11-18)

Two PCs, about 5 minutes: cargo ship (505/504/507/502), then the town fields 100-107, both ways.
- **17 meeting resyncs, all clean.** The host answered every request; each answer took 0-1 s, except one that waited
  4 s for the host to regain control (step 5 works). The guest was put back at its own spot every time
  (`Meet: back at your spot`), with no reload loops and no "not loaded" cases.
- **Join host** worked (507 → the guest arrived next to the host).
- **"Error Env Play() Slot=0" box on field 507:** it's a mod script bug, not a multiplayer one. The host got it too
  (its event bytes 13-14 went 2->9 on 507 at the same step). Talking to the Pilot on field 100 (`Pilot_SpeakBTN`,
  mod-written) jumps to 507 without the vanilla exit's ambient-sound shutdown: field 100 leaves
  `Field_AmbientSoundState1/2` (bytes 13/14) at 2 ("playing"). 507 has no ambient sound, so its `Main_Init` turns 2
  into 9, the error. Fix (in the mod's `field_100.txt`): before `Field( 507 )`, do what vanilla exits do (e.g.
  field_103's exit to 104): set each state to 3 if < 9, and `RunSoundCode1( 20864, 355/415, 0 )` (Stop Sound Res).
- Guest output_log: one `WalkMesh.BGI_findAccessibleTriangle` NRE (the walkmesh NRE already listed as an open item).

What the event-byte log already shows (material for steps 3-4):
- **Bytes 0-1: the scenario counter** (little-endian; 132 + 4×256 = 1156, matching "Scenario counter: 1156").
- **Byte 2: changes on every field change** (previous field/entrance bookkeeping). Bytes 9-14: ambient sound IDs
  and states. These are field-local and must **not** be forwarded.
- **Byte 464:** a real flag (100 on 502, then 101 on 106 after an interaction). This is the kind of byte tier 2
  forwards.
