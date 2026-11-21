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

## Parts 2 and 3, as built (2026-11-18): finds, flags, the host's scenes

### "Acted on this field"
`GuestGuard.ActedOnThisField` is false from a field (or world map) load until the PC first has player control there.
A field's opening scripts run on every PC that loads it (and again on a meeting reload), so what they give or set is
never forwarded. When a field is left, its last changes (an exit scene) are flushed first, so they still count.

### Tier 1: finds (`Party/GuestFinds.cs`)
- `[Online]` hooks on the script opcodes AddItem / RemoveItem (`ff9item.FF9Item_*_Generic`: items, key items and
  cards) and AddGil / RemoveGil record what a guest's scripts give or take, once the guest has acted on the field.
- The next party request carries them as `found`. The item, gil, key item and card differences they caused are left
  out of the request; any other story change in the same diff isn't sent (logged), so the host's game decides.
- The host replays the finds with the same game functions, then broadcasts as usual.
- Log: guest `Finds: a script gave item N xK (sent to the host)`; host `Party: request N ... (found: item N +K)
  applied`.
- Guest shop purchases: allowed since `online-battle-join` (step2-m4-party-state.md, "Guest shopping").

### Tier 2: flags (`EventWatch.cs`, `SessionLobby.Meet.cs`)
- Every 250 ms, a guest that has acted on its field sends its changed event bytes to the host (`MsgFlags`: index and
  value). Bytes 2-63 (field bookkeeping) are never sent; bytes 0-1 (scenario counter) and 64+ are.
- The host applies them to its live game. Log: `Event bytes: N applied from #tag on field F: [i] a->b`. Other
  guests get them at their next meeting resync.
- Not covered: `gScriptVector` / `gScriptDictionary` changes (logged nowhere yet).

### Part 3: the host's scenes and battles
- The host sends its state (`MsgHostState`: Playing / Scene / Menu / Battle / WorldMap, and its field) on each change
  and every second. "Scene" = no control on a field for 1 s or more (so a door or a quick sign doesn't count, but a
  long dialog does).
- A guest on the host's field freezes while the host is in a Scene or a Battle: it can't move or interact, and sees
  "The host is in a scene..." / "...in a battle...". Guests elsewhere aren't affected. The freeze lifts if the host's
  state goes 3.5 s without an update.
- The freeze notice and the toasts now show without the status box too.

## R2 test steps (two PCs; same save as R1)

1. **Chest alone:** guest walks to a field without the host and opens a chest (or takes an NPC gift). Guest log:
   `Finds: a script gave item ...`. Host log: `request ... (found: item ...) applied`. The host's inventory has it
   right away (open the host's Item menu).
2. **Chest flag:** the host walks to that field afterwards: the chest is already open on the host.
3. **NPC flag alone:** guest talks to an NPC whose dialog changes after the first talk. Host log:
   `Event bytes: N applied from #tag`. When the host talks to that NPC, it gets the "second time" line.
4. **Gil:** if any script gives gil (a reward), the host's gil goes up by the same amount.
5. **Scene freeze:** both on one field; the host starts a scene or a long dialog. Guest: "The host is in a
   scene...", can't move or talk; free again when the host is. A guest on another field keeps moving.
6. **Field openings don't count:** walk into fields with intro scenes (e.g. the ship) on the guest. The guest log says
   `field opening: not sent` for those changes, and the host log shows nothing applied from them.
7. Send both logs (Memoria.log and output_log.txt).

## R2 test results (2026-11-18)

Two PCs, cargo ship fields (502/504/505/507).
- **Find:** guest opened a chest alone (item 1001). Guest `Finds: a script gave item 1001 x1`; host `request 2
  (found: item 1001 +1) applied` 5 s later (incl. the guest's ~5 s clock offset: same moment).
- **Flags:** host applied the guest's bytes 1063 (0->4, with the chest), 220, 243-249 and 1000. Bytes 243-249
  changed together (0->195/1/75/239/191): a multi-byte value (a timer or counter?), worth a look if it causes
  trouble.
- **Freeze:** the host was in a Scene 10 times; the guest froze and was released 7 times (only while on the host's
  field), every time within about a second.
- **Meeting resyncs:** still fine (3 more), kept the guest's spot.
- No exceptions on either PC. Host Unity log: one `Can't add component 'Animation' to 9004` (Clove's hood model,
  harmless warning, M3 body code or the mod's model swap).
- **At session start** the guest's first party request was rejected (`character 1: info, character 3: info, item
  238 +8, other: slot`: the field opening's party setup) and showed a "didn't accept that" toast. Harmless, listed as
  a known bug.
