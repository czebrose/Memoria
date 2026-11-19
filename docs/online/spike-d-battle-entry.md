# Spike D: synced battle entry and exit (2026-11-18)

Question: can two PCs **enter the same battle from one description**, with only the characters of the players taking
part, and **leave it together**? Inside the battle the non-owner only waits (spike C covers playback).

Branch `spike-d-battle-entry` (from `online-step2`), code in `Assembly-CSharp/Memoria/Online/Battle/BattleEntry*.cs`.

## Verdict

**Synced entry works, and the subset party needs no big engine change.** In two two-PC runs (both the mod's
scripted battle 37 on field 507, the host owning it), the guest was pulled out of its field into the same battle,
got the same scene, enemy group, start type and units, waited in it and left on the owner's victory. The battle
party held only the characters it should (run 2: party `2 11 10 1`, battle `2 11`, all four back afterwards).

The shape for Step 3 is right: **owner decides, sends one description, participants force it**. Three things are
not settled by the runs:
- **The host's result screen stuck** in both runs (see [Open bugs](#open-bugs)). Not explained: Spike D runs nothing
  on the owner during that screen. A diagnostic build logs the screen's state; no log of a stuck screen yet.
- **Entry takes long**: the participant is held ~4.5 s before the description arrives and the owner waits ~11 s at
  the end of its intro for the participant (battle 37's intro is long). Fine for a spike, worth shortening.
- **Untested**: guest-owned battles (D-T2, D-T3), random encounters, the busy-in-a-menu rule (D-T5), flee/defeat
  (D-T6), a PC quitting mid-battle (D-T7). The code for each exists; see [Tests](#tests).

## D1. What defines a battle

### How a battle starts from a field
- **Random encounter:** `EventEngine.ProcessEvents` → `ProcessEncount` (EventEngine.ProcessEvents.cs): the step
  distance feeds `_encountTimer`; past `EncounterInterval`, `_encountBase += encratio` and `Comn.random8() <
  _encountBase >> 3` rolls the encounter. `SelectScene` (EventEngine.cs:191) picks one of the field's four battles
  (`ENCSCENE` opcode table, `Comn.random8`). Sets `nextMapNo` (via `SetBattleScene`), `btlSubMapNo = -1`,
  `isRandomEncounter`, `isEncount`; `_encountReserved` makes the frame return `FLOW_STATE_JUMP_BATTLE`.
- **Scripted battle:** opcodes `ENCOUNT` (0x2A) / `ENCOUNT2` (0x8C, with a fixed group) in
  EventEngine.DoEventCode.cs set `nextMapNo`, `btlSubMapNo`, `steiner_state` and return `FLOW_STATE_JUMP_BATTLE`.
- **Both** end in `HonoluluFieldMain.FF9FieldMapMain` (HonoluluFieldMain.cs:230): `nextMode = 2`, attr bit 8,
  `ff9fieldInternalBattleEncountStart`, `BackupPosObjData` (positions to return to); the field shuts down,
  `ff9ShutdownStateFieldMap` copies `nextMapNo` to `btlMapNo` / `battleMapIndex`, and the `BattleMap` scene loads
  with the swirl. The field's script state is kept (`sEventContext1`) and resumes after the battle.
- **Battle init:** `HonoluluBattleMain.Start` → `InitBattleScene`: scene data and background from `battleMapIndex`,
  the enemy group (`ChoicePattern`), `battle.InitBattle`, battle scripts, `CreateBattleData` (players from the party
  slots), `btl_init.SetupBattleEnemy` (start type).

### Random at battle start (decided on the owner, sent, never rolled again)

| What | Where | Generator | In Spike D |
|---|---|---|---|
| Encounter roll, which battle | `ProcessEncount`, `SelectScene` | `Comn.random8` | owner only (it's the trigger) |
| Enemy group (pattern) | `HonoluluBattleMain.ChoicePattern` | `UnityEngine.Random.Range` | sent; forced through `btlSubMapNo` (ChoicePattern honours it, no hook) |
| Back attack / preemptive | `btl_sys.StartType` (party support abilities + 2× `Comn.random8`) | | sent; forced in `btl_init.SetupBattleEnemy` |
| Initial ATB, players and enemies | `btl_init` (`Comn.random16() % max.at`; 0 or max on special starts) | | sent with Go; set on the participant (run 2: 4 of 4 differed before) |
| Battle camera | `HonoluluBattleMain.Start` | `UnityEngine.Random.Range` | cosmetic, left alone |

Both PCs also seed `UnityEngine.Random` with the owner's seed before init. Not relied on (run 2 shows the
participant's own ATB rolls still differed), kept as cheap extra alignment.

### Messages (control channel, reliable; type bytes 20-27)

| Message | Content | Size |
|---|---|---|
| Presence | field, state (control / menu / no control / battle / world map) | 4 B, 2/s per peer |
| Pending | seq, field, battle id: participants stop moving | 7 B |
| **BattleStart** | seq, battle id, pattern, start type, flags (random encounter, Steiner, encount), field, seed, per character: id, HP, MP, status (64-bit), trance | 16 B + 17 per character (51-52 B with two) |
| Ready | seq: the participant's intro is done | 3 B |
| Decline | seq, reason (busy / elsewhere / own battle) | 4 B |
| Go | seq, every unit (character id or enemy type) with its initial ATB | 4 B + 3 per unit (16 B with four) |
| End | seq, result (victory / fled / defeat / ..., 0 = left out) | 4 B |
| Alive | seq, 1/s while the owner's battle runs | 3 B |

**Dead end:** the units can't go in BattleStart. Right after `InitBattleScene` the battle list (`btl_list`) is still
empty (run 1 sent `units []`); it's complete at the end of the intro, which is why they ride on Go.

## D2. A battle party that is a subset of the party

**Works with the party slots alone.** FF9 builds the battle party from `FF9.party.member[0..3]` and skips empty
slots (`btl_init.InitPlayerData`, `HonoluluBattleMain.CreateBattleData`; `btl_init.SetupBattlePlayer` centres the row
on the member count). Vanilla parties of 1-3 are normal, and Memoria's own in-battle menu swaps the slots the same way
(`BattleHUD.UpdatePlayersForMainMenu`).

`BattleEntryParty.Apply` keeps the battle's characters in party order, packed into slots 0..n-1, and remembers the
full array; `Restore` puts it back when the result screen closes (`battle.ff9ShutdownStateBattleResult`) or, as a
fallback, when the battle scene is gone. Character records (`FF9.player`) aren't touched, so characters left out keep
their state. PartySync waits while the slots are swapped (`BattleEntry.PartyAltered` in `CanTouchPartyNow`).

- **Who's in the battle:** the characters of the players on the owner's field (Presence) plus the owner's, and the
  party members nobody plays when the host is in the battle (decided 2026-11-18: free characters are the host's;
  Ctrl+Shift+U toggles). A guest fighting without the host fights with only its own character.
- **Rewards:** EXP and AP go to `party.member`, so only the characters in the battle get them (as in vanilla FF9 for
  characters outside the party).
- **Starting HP/MP/status** come from the owner (in BattleStart) and are written into the participant's character
  records before init. Both runs: already equal.
- **Not seen yet:** a battle with three characters, a guest alone with one (D-T3).

**Step 3 design (from the user):** players will assign free characters to **follow** a player; a free character
then joins the battles its leader is in, and its leader gives its orders. On the entry side this is one rule in
`BattleEntry.BeginOwned`; the costs are elsewhere (who-follows-whom data next to the `assign` lobby data, a Follow
control, and a follower's orders coming from its leader's PC in spike C's battle). "Follow the host" is the special
case where everyone follows the host, so today's rule stays the default.

## D3. Entering together

1. **Owner's trigger** (`HonoluluFieldMain` hook after `ServiceEvents`): picks the participants from Presence (same
   field, not in a battle or on the world map, plays a party member) and sends **Pending**. The owner doesn't hold on
   the field (a scripted battle's script has already moved past its `ENCOUNT`), it goes straight into its battle.
2. **Participants** stop moving and interacting (`BattleEntry.HoldPlayer` in `FieldMapActorController` and
   `EventEngine.ProcessEvents`), with "A battle is starting...".
3. **Owner's battle init** (`HonoluluBattleMain.Start`): party slots swapped, seed set; after init it sends
   **BattleStart**. Measured: 4.2 s after the trigger (the owner's own swirl and load).
4. **Each participant** joins at its next field frame where it may (below): it sets the battle id, pattern, flags and
   returns `FLOW_STATE_JUMP_BATTLE` itself, so the field leaves exactly as for its own encounter (positions backed
   up, script state kept). Measured: 14-21 ms after BattleStart arrived.
5. **Both hold at the end of the intro** (`battle.BattleMain`, `PHASE_MENU_ON` → `PHASE_NORMAL` gate): the
   participant sends **Ready** and stays there for the whole battle; the owner goes on once everyone is ready (or 20 s
   pass, dropping whoever isn't: they get End 0) and sends **Go** with the units. Measured: Ready 11.2 s after
   BattleStart (participant's swirl, load and battle 37's long intro).

**The participant's minimal freeze** is that intro gate: in `PHASE_MENU_ON` the ATB doesn't fill and the command
menu isn't on, so there are no player or enemy turns. It shows "*owner* is running this battle...". Spike C's passive
mode replaces it.

**Busy-participant rules** (`BattleEntry.CanJoinNow`):

| Participant is... | Rule |
|---|---|
| in a menu | waits, "A battle is starting: close the menu to join (N s)"; joins when it closes; after 8 s declines and stays out |
| in a scene / dialog (no control) | waits the same way (being pulled out mid-script would lose the scene) |
| fading or changing field | waits |
| on another field by then | declines at once |
| in its own battle (both triggered at once) | declines: each keeps its own battle |
| gone (left the lobby) | the owner stops waiting for it |

Force-closing a menu was not tried (risk of leaving a menu half-open across the scene change, the Step 2 resync bug).
A declined player's character still fights in the owner's battle (the owner's PC drives it) and is left on the field
on the declined PC (frozen by Step 2's "The host is in a battle..." if the host owns it).

## D4. Leaving together

- **Owner:** when its battle's result is set (`btl_result`: victory, fled, defeat...), it sends **End** to the
  participants. In both runs it went out at the victory.
- **Participant:** leaves at once, as an **escape** (`btl_result = ESCAPE`, `PHASE_CLOSE`): no rewards, no victory
  pose, no result screen (the escape's empty result screen closes itself), back to its own spot through the normal
  return to the field. Then `btl_result` is set to the owner's result, so the field sees the real outcome. Measured:
  back on the field ~3 s after End.
- **Party state afterwards:**
  - Host owned the battle: the host's results are authoritative; PartySync broadcasts them after the battle.
  - A guest owned it: its party before and after the battle (`PartyState.Diff`) go to the host as a `BTR1` blob; the
    host applies the battle characters' records, item counts and gil, trusting the battle owner, and broadcasts.
    **Not tested.** Known gap: items or gil the host changed during the battle are fine (deltas), but the guest's
    view of the host's characters isn't used.
  - Every guest re-baselines PartySync after a shared battle, so its battle's own changes (battle counters, HP set
    from the owner) aren't sent as party requests (they'd be rejected with a toast).
- **A PC drops mid-battle:** the owner sends Alive every second; a participant that hears nothing for 6 s, or sees the
  owner leave the lobby, leaves the battle with "The battle's owner left; leaving the battle." A participant leaving
  is just dropped by the owner (its character stays in the owner's battle). **Not tested.**
- **Ending through the normal victory screen** on the participant needs spike C: its passive battle replays the
  owner's deaths and reaches victory by itself. Spike D's escape exit is the stand-in.
- Defeat: the participant leaves the same way; what a guest's game over means is an open design question.

## Tests

| # | Run | Result |
|---|---|---|
| D-T1 / D-T4 | run 1 and 2: host on field 507 triggers scripted battle 37 (the Black Waltz No. 3 fight), guest on the same field | **Pass** on entry and exit: same battle, same units (run 2), guest waited and left on the victory. **Host's result screen stuck** both times |
| subset (D2) | run 2: party Clove, B.Waltz, 10, Duskly; two players | battle had Clove and B.Waltz only (the rule then: free characters sit out); all four back afterwards. Now free characters follow the host |
| D-T2, D-T3 | guest-owned, guest alone | not run |
| D-T5 | guest in a menu | not run |
| D-T6 | flee, defeat | not run |
| D-T7 | a PC quits mid-battle | not run (in run 1 the guest's game quit after the battle: the host dropped it cleanly) |

Unity log: one `NullReferenceException` in `SmoothFrameUpdater_Battle.Apply` when leaving a battle, on the guest in
both runs. Spike C saw the same one without Spike D's code; still unattributed.

## Open bugs

- **The host's result screen stuck after the shared battle** (both runs; "it wouldn't move past"). In run 2 the host
  still reached the field ~4-5 s later according to the guest's freeze log, so the exact symptom is unclear. Spike D
  runs nothing on the owner during that screen except sending Alive. Candidates: something in this battle's ending
  (the mod turns B.Waltz back at the end of battle 37; spike C saw the mod's battle 222 not end), an ability-learned
  panel waiting, or input. **Next step:** a run with build `A4F159C11045` (or later), which logs
  `result screen up for N s: ...` (state, loading, ability queue, menu input, focused button) every 10 s; Ctrl+Shift+2
  dumps it. A single-PC run of battle 37 without a session would tell whether it's the mod.
- **Long entry:** ~4.5 s held on the field, then the owner waits for the participant's load and intro. Sending
  BattleStart at the trigger (pre-rolling the pattern on the owner) would let both PCs load in parallel.

## For spike C / Step 3

- Battle-start values (pattern, start type, initial ATB, seed) are here; spike C's replay forced them separately
  (`btl_init` start type): the two hooks touch the same line in `btl_init.SetupBattleEnemy` and both edit
  `HonoluluBattleMain.Start` and `battle.cs`. Merge by hand.
- The participant's hold (`MayLeaveIntro` false forever) is where passive mode takes over: after Go, run the passive
  battle instead of holding.
- End should become "the owner's last events are in" so the participant's passive battle ends through its own victory
  screen; the escape exit stays for drops and cancels.
- A free character's orders: from the owner's PC today; from the player it follows in Step 3.

## Files and hooks

New: `Memoria/Online/Battle/BattleEntry.cs` (flow, hooks, diagnostics), `BattleEntryMessages.cs` (formats),
`BattleEntryParty.cs` (subset party, guest results).

Engine edits (`[Online] Spike D`): `HonoluluFieldMain.FF9FieldMapMain` (field flow), `HonoluluBattleMain.Start`
(before/after init), `btl_init.SetupBattleEnemy` (start type), `battle.BattleMain` (intro gate),
`battle.ff9ShutdownStateBattleResult` (battle left), `FieldMapActorController` and `EventEngine.ProcessEvents` (hold
the player), `EventEngine.OnlineDebugEncounter` (debug), `PartySync.CanTouchPartyNow`, `OnlineSession` (wiring, GUI,
keys).

Debug keys (Ctrl+Shift, `OnlineDebug = 1`): **1** random encounter now (fields with random battles; not V, the game's
menu key), **U** free characters join the host's battles (default on), **2** dump the result screen's state.
