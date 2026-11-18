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
