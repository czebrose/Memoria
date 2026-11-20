# Step 3 · M1 results: foundation and spectator battles

Branch `online-step3` (2026-11-18/19). The battle owner (the PC that triggers the battle) runs FF9's battle engine.
Every player on its field enters the same battle and watches it play out, ending through their own victory screen
with the owner's rewards.

**Verdict: M1 works for every path we ran.** Three two-PC rounds covered:
- host-owned and guest-owned battles;
- Mog bag fights (scripted) and random encounters;
- a summon, Steal, Reflect, a KO + Phoenix Down, a counter, enemies leaving by script;
- a flee;
- a player joining from a menu.

The first round found two design problems: the passive ran one action or more behind, and the menu bug. Both were
fixed. In the last two rounds every watched battle ended through its own screens, about 0.3 s behind the owner.
The only mismatches came from the mod's command overloads (known issue 1), plus one compare made before its Sync
(known issue 5).

**Not run:**
- T3 (guest alone on another field);
- T8 (defeat);
- T10 (quit mid-battle).

Ending and disconnect handling was put off by Cordell; T3 wasn't reached. The last build, `A1BE9EA91D6B`, fixes the
ATB rate and the music of random encounters, and hasn't been run in game.

Contents:
1. [What changed](#1-what-changed)
2. [The stuck endings and the NRE](#2-the-stuck-endings-and-the-nre-task-14)
3. [Settings lock](#3-settings-lock-task-3-refuse)
4. [Tests](#4-tests)
5. [Known issues](#5-known-issues)
6. [Commits](#6-commits)

---

## 1. What changed

**Groundwork.**
- Merged `online-step2-snapshot-entrance` (2c99e01e) into `online-step2`.
- `online-step3` merges `spike-c-battle` and `spike-d-battle-entry` by hand: `HonoluluBattleMain.Start`,
  `btl_init.SetupBattleEnemy` and the csproj.
- Battle-start values (battle, pattern, start type, initial ATB) now live only in `BattleEntry` (BattleStart / Go).
  The spike's debug replay forces them through the same code (`BattleEntry.ForceDebugStart`).
- Renames: `BattleSpike` → `BattleSync`, `[SpikeC]` → `[Battle]`, log folder `SpikeC` → `BattleLogs`.
- Removed `PartyTrace` and spike D's result-screen diagnostics.
- The record/replay tool stays behind `OnlineDebug`.

**Spectator battles** (`BattleSync.BeginOwner` / `BeginPassive`):

| Piece | How |
|---|---|
| Flow | Pending → BattleStart **at the trigger** (pattern and start type pre-rolled with the game's own logic, forced on the owner too) → both load in parallel → Ready at the intro gate → Go (units, initial ATB, music) → the participant plays the owner's stream in spike C's passive mode → End |
| Ending | End = "the owner's last events are in". The passive battle holds at its last fade (`battle.cs`, `BattleSync.MayEndBattle`) until it has played them, then takes the owner's result and rewards and finishes through its own screens. Spike D's escape-style exit is kept for drops, cancels and timeouts. A lost battle ends as an escape on participants (no game over in M1). |
| Stream | `BattleWire` / `BattleStream`, control message 28. Every event goes out as soon as it is recorded, stamped with the owner's battle frame. Each message says how far the owner has run (a heartbeat at least every 2 frames). |
| Pacing | **Frame lockstep with a margin** instead of the brief's "buffer by one action" (why: below). The passive plays each event at the owner's frame for it and never runs a battle frame the owner hasn't passed by 9 frames (0.3 s). When over a second behind (load hitch, slower animation), it runs two battle frames per update until it catches up. |
| Tape | Random draws outside the calculation are recorded per command. Draws made after the Command went out follow in `CmdMore` records, together with the summon's long/short choice. |
| ATB | Value + rate samples per unit: when the passive's extrapolation would be off by more than 1/64 of the gauge, at most 4 Hz per unit, at once on a reset. The rate is the gauge's speed over the last 0.2 s. The passive applies samples at their frame, interpolates, and never steps a gauge back by less than 1/16. |
| Run pose | The owner's "run" buttons (`btl_escape_key`) are streamed. The passive party turns to run when the owner's does. |
| Music | Go carries the owner's song. BattleStart carries it too for scripted battles, whose song the owner's field script started; a random encounter's song comes from the encounter swirl on every PC. |
| Safety net | Every Effect, CommandEnd, Tick and Script event carries a diff of the owner's state (HP, MP, status, trance, max, stats, party gil and items). The passive keeps a mirror of it and corrects and logs anything the ops didn't reproduce. Trance, poison/regen HP and revive HP are known direct writes, counted apart. |
| Results | Host-owned: PartySync as before. Guest-owned: `BTR1` to the host, now with the battle's seq. A host that watched the battle to its own end applies the characters only (it already got the items and gil in its copy). Then a fresh baseline. |
| Status box | "shared battle owner: …" or "watching: …": events, bytes, ATB rate, applied, corrections, mismatches, frames behind, held frames. |

**Why the pacing changed.** The brief's "buffer by one action" was built first: the owner sent a command only once it
had finished, so the passive never reached an effect before its outcome arrived. In the first two-PC round it put the
watching PC one whole action or more behind:
- Garnet's long summon started on the watching PC 3 s after it had finished on the owner's;
- the watcher was 10 events behind when the owner's battle ended;
- it was cut off mid-animation by the end-of-battle timeouts.

Frame lockstep keeps the two screens within about 0.3 s. A death or tick still never shows before its animation,
because it plays at the owner's frame for it.

**Defects found and fixed along the way:**
- The owner sent End at the trigger: `btl_result` still held the last battle's result. A player in a menu was then
  told the battle was over. End now waits for Go.
- A participant left on owner silence (6 s) even after the End was in: the owner stops its keep-alives when it leaves
  its battle. Now: no silence check after the End; a 45 s fallback if the passive battle doesn't end.
- The forced start type was cleared before `SetupBattleEnemy` drew it. That function runs in the load loop, after
  `InitBattleScene`. Spike D's owner had also sent an unrolled start type.
- A battle-script "end battle" (`SetBattleData 33`) was applied before PHASE_EVENT and did nothing (battle 45 never
  ended). Script events now carry the owner's phase and wait for it.
- Steal/summon recordings: the summon's long/short draws are off the tape (the passive takes the owner's choice).
  Phoenix Down's HP is a known direct write.

## 2. The stuck endings and the NRE (task 1.4)

Run on one PC with no session and the mod installed, before any shared-battle testing:

| Symptom (spikes) | No session | Attribution |
|---|---|---|
| Battle 37's result screen stuck | not reproduced | Spike D's own result-screen handling (removed with its diagnostics); not seen since |
| Battle 222 doesn't end after the last enemy | not reproduced (normal and Record mode) | Not the mod; spike C's replay applying the "end battle" script write too early (fixed, above) |
| `NullReferenceException` leaving a battle | seen once (Record mode) in `SmoothFrameUpdater_Battle.Apply` | Memoria code: the animation/camera lookup during teardown. Null guards added; not seen since |

Nothing was attributed to the mod, so no mod handoff for task 1.4. One unrelated mod bug turned up in the two-PC
tests: an NRE in `HadesWorkshopMod.CoopFootsteps.CatchUpSteps` during a guest's resync load. Handoff:
`Memoria-Multiplayer/handoff-mod-bug-coopfootsteps-resync-nre.md`.

One-PC record/replay on the merged branch: battles 37, 222 and 182 gave 0 mismatches. Spike C's R2 (Steal + flee in
battle 182) first showed a Phoenix Down correction and a summon tape miss; both were fixed, and the final replay
wasn't re-checked. Spike C's R3 (battle 45, enemies leaving by script) found the phase bug above. Battle 45 then ran
cleanly in the two-PC tests (T6).

## 3. Settings lock (task 3): refuse

Presence carries each PC's `Battle.Speed` and `SFXRework`.
- The owner leaves out of its battle any peer whose settings differ.
- The owner shares nothing if its own `Speed` ≥ 3 or `SFXRework` = 0 (paths the replay doesn't support).
- A participant declines (reason 3) if BattleStart's settings differ from its own.

Forcing the owner's settings would change a player's options behind their back. Refusing is visible in the log, and
the full audit is M3. Both PCs ran the defaults (Speed 0, SFXRework 1) in every test.

## 4. Tests

Two PCs, two Steam accounts (#6647, #6374), all on the mod's save at the cargo ship (field 507) or field 302.
"Behind" is how far the watching PC ran behind the owner (frames at 30/s). "Early fx" counts effect points the
watching PC reached before the owner's outcome arrived.

**Entry time** (trigger → Go, both PCs loading in parallel):

| Case | Spike D | M1 |
|---|---|---|
| Mog bag 222 / 37 / 22 | 4.2 s to Start + 11.2 s to Ready ≈ **15.4 s** | **10.8–12.3 s** (Start at the trigger; load 4.2 s, then the battle intro) |
| Mog bag 45 (longer intro) | — | 17.3 s |
| Participant joining from a menu | — | 15.6 s (joined 4.6 s after Start) |

The intro (≈ 7 s; 13 s in battle 45) is now most of it. The owner waits at its intro gate for the participant.

**Bytes per battle** (the stream; spike C's numbers were the debug log format):

| Battle | Events | Stream | ATB samples | ATB in spike C's format |
|---|---|---|---|---|
| 222, 54–60 s | 17–35 | 4.2–5.6 KB in 410–520 messages | 428–834 B | 1550–3640 B |
| 37, 18 s | 15 | 2.6 KB in 205 messages | 316 B | 668 B |
| 22, random encounter, 67 s | 32 | 6.8 KB in 610 messages | 848 B | 3854 B |
| 45, 63–90 s | 76–81 | 10.3–10.4 KB in ~800 messages | 1064–1338 B | 5908–6262 B |

ATB is now 15–30% of its old size. About half of each stream is now frame heartbeats: 7 bytes, about 7 messages/s.
Spike C's 3–18 KB per battle was mostly ATB.

**T1–T10** (rounds 2 and 3 unless noted; round 1 is in section 1):

| # | Result | Numbers | Notes |
|---|---|---|---|
| T1 host-owned, guest watches | **pass** | 222 with Garnet's summon: 0 mismatches, 0 corrections, 11 held frames | Round 1 fail (one action+ behind, cut off) → pacing redesign |
| T2 guest-owned, host watches | **pass** | 222: 0 mismatches; `BTR1` 8017 B applied on the host, characters only (it watched) | Round 1 fail (End at the trigger) → fixed. Music late and ATB stagger → fixed |
| T3 guest alone elsewhere | not run | — | — |
| T4 scripted, battle 37 | **pass** | 0 mismatches, 2.6 KB | — |
| T5 counter, Reflect bounce, KO + Phoenix Down (Trance didn't come up) | **pass with known issue** | battle 45: 2 mismatches / 1 early effect, both right after B.Waltz's YellowMagic1; 0 corrections; Syncs for Defend/Reflect expiry | Mod overload (known issue 1). An animation looped more than expected at the Phoenix Down: neither log explains it, screen not noted |
| T6 Steal, summon, enemies leaving by script | **pass** | 45 owned by the guest, B.Waltz's moves used: 0 mismatches, 0 early effects, 2 MP corrections after ticks, 1 blocked local script write | Steal (Hi-Potion) and the summon matched; the script "end battle" waited for the owner's phase as designed |
| T7 owner flees | **pass** | 222: 1 early effect after a B.Waltz move; 22: 0 mismatches | Round 2: the run pose didn't show on the watcher → streamed |
| T8 defeat | not run | — | The code path exists (participant leaves as from an escape) |
| T9 participant in a menu | **pass** | menu kept open > 8 s: sat out (decline reason 0, by design); closed within 3 s: joined 4.6 s after Start | Round 1: the End-at-trigger bug kept it out |
| T10 quit mid-battle | not run | — | Exits on 6 s owner silence (spike D) are unchanged |
| Random encounter (T1 on field 302) | **pass** | battle 22, patterns 3 and 1, normal starts; 1 early effect (B.Waltz), 1 Reflect-expiry compare before its Sync | The watcher played the field song until Go → fixed in the last build |

Visible differences reported:
- the watcher about 0.3 s behind;
- late music (fixed);
- ATB gauges staggering, then pausing during attacks (both fixed, the second untested);
- no run pose (fixed);
- the Phoenix Down animation loop (unexplained).

## 5. Known issues

1. **The mod's command overloads run on the owner only.** Seen with B.Waltz's YellowMagic1 / airborne moves
   (`IOverloadOnCommandRunScript`, out of scope for M1). They make the owner's command longer. The watching PC's
   animation, without the overload, can reach the next effect point before the owner's outcome arrives. That effect
   then lands a moment later without its damage number ("finished before its effect"), and MP gets corrected after
   ticks. 1–2 per battle that uses them. The fix is to run the overload in passive mode, or to stream its timing (M2).
2. **Phoenix Down: an animation looped longer than expected.** Neither PC's log shows anything around it. It wasn't
   noted which screen it was on, so it may be vanilla or the mod.
3. **The watching PC holds a battle frame now and then** (9–28 per battle) waiting for the owner's stream. It's a
   short freeze, not noticed in the tests.
4. **Heartbeats are half the stream.** A message at least every 2 battle frames tells the watcher how far the owner
   is. That's fine at these sizes; it could be packed into fewer messages if bandwidth ever matters.
5. **Status expiry (Reflect) is compared before its Sync.** The owner's Reflect wear-off arrives as a Sync event just
   after the action. The passive compares at the action's end first, so one mismatch is logged and the Sync then fixes
   it. Cosmetic in the log.
6. **Party items at Start.** A participant's initial state is set from the owner's (`StartFixes`) if it differs, for
   example items as this PC last saw them. It didn't trigger in these tests (initial state matched every time).
7. **Untested: defeat, quit mid-battle, guest alone on another field, the last build.** The last build is the ATB rate
   over 0.2 s and no field song at Start for random encounters.

## 6. Commits

- `1c20e9cd`, `a50390fe`: merges.
- `da9209e9`: cleanup.
- `e3ce74c7`: script phase gating, null guards.
- `69a1f3dd`: start type, revive HP.
- `1750f4c6`: summon tape.
- `6218708a`: spectator battles.
- `c6f93cf1`: frame lockstep, owner's music, End timing.
- `fd6a6163`: music at Start, run pose.
- `580b06fb`, `be2cae8f`: ATB smoothing and rate; random-encounter music.

DLLs tested: `F21846BA597F` (round 1), `D3EB289474E8` (round 2), `FFC0F730D2F8` (round 3). Final: `A1BE9EA91D6B`.
