# Step 3 planning input: Spike C and Spike D findings (2026-11-18)

For the Step 3 (shared battles) planner. Both spikes are done; both verdicts are **yes**. Full docs (read them for
code references):
- Spike C, `docs/online/spike-c-battle-replay.md` on branch `spike-c-battle` (48a40a4e).
- Spike D, `docs/online/spike-d-battle-entry.md` on branch `spike-d-battle-entry` (544413ed).

Repo `C:\Users\corde\Documents\Projects\FF9\Memoria-Step2` (czebrose/Memoria). Both branches come from
`online-step2` (Step 2, closed: `docs/online/step2-closeout.md`). Neither spike branch is merged or pushed.

---

## The design both spikes confirm

**The PC that starts a battle owns it.** It runs FF9's real battle engine and sends one start description, then a
stream of commands plus outcomes. Every other participant enters the same battle from the description and **plays it
back passively** through the game's own animation code, with the calculation replaced by the owner's results.
- Shared-seed lockstep is **not** viable. Cosmetic code (particles, camera, voices) draws from the same
  `UnityEngine.Random` at frame-dependent times, and per-PC state (achievements) changes code paths.

## Spike C: inside the battle (one PC, record and replay)

**Verdict: forced-outcome replay works.** Every replay ended with 0 mismatches after the fixes. Victory and escape
come through the normal screens.

| Run | Battle | Result |
|---|---|---|
| R1 | 182: attacks, Cure, item, injected attack | 0 mismatches, 0 corrections |
| R2 | 37: Carbuncle/Reflect, Steal, Slow, heal-all, script-driven enemies | 0 mismatches. Two cross-PC issues (the stolen item, summon length) were fixed, but the fixes weren't re-run. |
| R3 | 45: enemies leaving by script, Mug, poison, Reflect | Hung at the end. The cause (an enemy removed by a script) was fixed, but not re-run. |
| R4 | 182: summon, flee | 0 everything, normal escape |
| R4 defeat | n/a | **not tested** |

**How it works:**
- **Commands** are injected straight into the running-command list (`cur_cmd_list`), not the queue. The queue's ATB
  and priority gates would reorder them.
- **The calculation hook** is `SBattleCalculator.CalcMain`, the single per-target entry, below Memoria's and the
  mod's calculator scripts. On the passive PC it returns at once. The owner's **ops** are applied instead: the
  engine-level apply calls, recorded on the owner. They are Damage/Recover/MP, Alter/RemoveStatus, Kill, number
  displays, battle messages, item add/remove, and script writes to units and the battle.
- **What gets sent, in order:** Start, Command (with its tape), Effect, CommandEnd, Tick, Script, Sync, Atb and End.
  - One Effect per target per effect point. Multi-target and multi-hit actions are a sequence of Effects.
  - Counters, Auto-Potion and Return Magic are separate commands.
  - Order is everything: the passive PC consumes events strictly in order.
- **The tape:** random numbers the command pipeline draws outside the calculation are recorded on the owner and
  returned on the passive PC (about 0–1 per command). They cover reflect bounces, retargeting, random-target hits,
  Garnet's concentration, double cast's target, and cover.
- **Passive mode:**
  - no ATB filling, so no menus and no enemy turns;
  - status ticks and death checks don't run locally, and come from the owner as Tick events;
  - battle scripts still run (setup, dialogs, camera), but their writes to units are blocked once replay starts;
  - ATB gauges are set from the owner's samples;
  - rewards are replaced by the owner's at the end.
- **Sizes:** an Effect is 15–26 B on average (44 B max), a Command 30–36 B. Commands plus outcomes come to about
  **1–2 KB per battle**. ATB samples dominate the rest (2–10 KB): send them less often, or as "value + time" with
  local interpolation. Syncs should be diffs.

**Gaps C found, with effort:**

| Gap | Effort |
|---|---|
| Per-PC state in command paths: summon length depends on achievements (fixed by sending the owner's choice). Others may exist. | Small each, but needs an audit of anything reading achievements or settings in battle |
| Battle scripts' effects other than on units: dialogs, music, camera, model swaps not made through `btl_scrp.SetCharacterData` / `SetBattleData` | Medium: let them run on the passive PC (if they don't use `rand`) or record more opcodes. Needs a scripted-boss test. |
| The mod's `IOverloadOnCommandRunScript` (B.Waltz's Ascend, spell graphics) runs at queue admission, which the passive PC bypasses | Small: run it at injection, or record what it changed |
| Abilities learned in battle (Eat, Blue Magic); the Libra/Scan display | Small: a learn op; check the UI |
| Direct state writes in scripts (trance gauge, poison HP) | Covered by snapshot correction. For Step 3, send a per-effect state diff as a safety net. |
| Timing: the passive PC can show a death or tick slightly early compared with its own animation | Cosmetic: buffer the stream by one action |
| `Battle.Speed >= 3` (simultaneous actions) and `SFXRework = 0` | Out of scope: the spike assumes the defaults |

**Never exercised:** Trance, a KO and revive, counterattacks, a Reflect bounce, defeat. Their code paths are mapped,
and C expects them to fit. These are the first things to test in Step 3.

## Spike D: entering and leaving together (two PCs)

**Verdict: synced entry and exit work, and leaving characters out of a battle needs no big engine change.**
- **The runs:** two runs of the mod's scripted battle 37 on field 507, both owned by the host. The guest was pulled
  into the same scene, enemy group, start type and units, waited in it, and left on the host's victory.

**What starts a battle:**
- **Random encounter:** `EventEngine.ProcessEncount` → `SelectScene`.
- **Scripted battle:** the `ENCOUNT` / `ENCOUNT2` opcodes.
- **Both** pass through `HonoluluFieldMain.FF9FieldMapMain` → the BattleMap scene → `HonoluluBattleMain.Start` /
  `InitBattleScene`.

**Random at battle start:** all of it is decided on the owner and forced on the participant:

| What | How it's forced |
|---|---|
| Encounter roll, which battle | The owner's trigger |
| Enemy group (`ChoicePattern`) | `btlSubMapNo`; no hook needed |
| Back attack / preemptive (`btl_sys.StartType`) | `btl_init.SetupBattleEnemy` |
| Initial ATB (`btl_init`) | Sent with Go |
| Battle camera | Cosmetic, left alone |

**Messages** (reliable control channel, type bytes 20–27): Presence (4 B, twice a second), Pending (7 B),
**BattleStart** (16 B + 17 per character: 51 B with two), Ready (3 B), Decline (4 B), Go (4 B + 3 per unit), End
(4 B) and Alive (3 B, once a second).
- **Dead end:** the units can't go in BattleStart. The battle list is still empty right after init, so they ride on
  Go, after the intro.

**Leaving characters out:**
- FF9 builds the battle from `party.member[0..3]` and skips empty slots.
- `BattleEntryParty.Apply` packs the battle's characters into the slots and remembers the full party. `Restore` puts
  it back when the result screen closes, with a fallback for when the battle scene is gone.
- Character records aren't touched. EXP and AP go only to the characters in the battle, as in vanilla.
- **Who joins:** the players on the owner's field, plus party members nobody plays when the host is in the battle
  ("free characters are the host's", Ctrl+Shift+U). A guest fighting without the host fights with only its own
  character.

**The entry flow:**
1. The owner's trigger sends Pending, and participants stop moving.
2. The owner initialises its battle, then sends BattleStart, 4.2 s after the trigger.
3. Each participant leaves its field through the game's own battle jump, 14–21 ms after BattleStart arrives.
4. Both hold at the end of the intro. The participant sends Ready, 11.2 s after BattleStart in battle 37.
5. The owner sends Go with the units and their initial ATB. It times out after 20 s and drops anyone not ready.

**Busy-participant rules:**

| Participant is... | Rule |
|---|---|
| In a menu | Waits up to 8 s for it to close, otherwise declines |
| In a scene, a fade or a field change | Waits |
| On another field | Declines |
| In its own battle | Declines |
| Gone | The owner stops waiting |

Force-closing menus wasn't tried: it risks the Step 2 half-open-menu bug.

**The exit flow:**
1. The owner sends End with the result.
2. The participant leaves as an escape: no rewards, no result screen, back to its own spot about 3 s later.
3. The participant then sets `btl_result` to the owner's result.
4. **Party state:** if the host owned the battle, PartySync broadcasts its results. If a guest owned it, a party diff
   goes to the host as a `BTR1` blob, the host applies it and broadcasts. **Not tested.**
5. Every guest takes a fresh PartySync baseline after a shared battle.

**A PC dropping:** the owner sends Alive every second. A participant that hears nothing for 6 s, or sees the owner
leave, exits with a message. **Not tested.**

**Open from D:**
- **The host's result screen stuck after battle 37, in both runs.** It's unexplained, since D runs nothing on the
  owner then. A diagnostic build (`A4F159C11045`) logs the screen's state. A single-PC run of battle 37 without a
  session would show whether it's the mod. (Spike C also saw the mod's battle 222 not end after the last enemy
  died.)
- **Not run:** a guest-owned battle (D-T2), a guest alone (D-T3), a random encounter, joining while in a menu (D-T5),
  flee and defeat (D-T6), a PC quitting mid-battle (D-T7). The code for each exists.
- **Slow entry:** the guest is held about 4.5 s, then the owner waits through the guest's load and intro. Sending
  BattleStart at the trigger, with the pattern pre-rolled, would let both PCs load in parallel.

## How C and D fit together for Step 3

- **D is the envelope, C is the contents.** After Go, the participant runs C's passive battle instead of D's hold at
  the intro gate.
- **Ending:** End becomes "the owner's last events are in", so the passive battle reaches victory through its own
  normal screen. D's escape exit stays for drops and cancels.
- **Battle-start values belong in D's BattleStart and Go,** and C's separate forcing of them goes. Both spikes edit
  the same line in `btl_init.SetupBattleEnemy`, and both edit `HonoluluBattleMain.Start` and `battle.cs`: merge
  these by hand.
- **The user's design: free characters follow a player.** A free character joins the battles of the player it
  follows, and that player gives its orders.
  - On the entry side this is one rule (`BattleEntry.BeginOwned`).
  - The real cost is elsewhere: who-follows-whom data (next to the lobby's `assign` data), a Follow control, and a
    follower's orders coming from its leader's PC into the owner's battle. That last part means **remote command
    input**, which neither spike built.
  - "Follow the host" is the default special case.

## Not covered by either spike (Step 3 has to design these)

- **Remote command menus:** a non-owner player choosing their character's commands, sent to the owner and injected
  there. C proved injection works.
- **ATB display sync** at a sensible rate, and what a non-owner sees while waiting for its turn.
- **Defeat and game over:** untested in both spikes. What a guest's game over means is an open design question.
- **Late join** (walking onto the field mid-battle) and per-player flee.
- **Guest-owned battle results reaching the host** (D's `BTR1` path is untested).
- **Step 2's known gaps** that touch battles: guests can't buy in shops, and the "slot" rejection toast at the start
  of a session (see `step2-closeout.md`).

## Housekeeping

- **A Step 2 load bug that both spikes depend on:** a snapshot load replayed the field's entrance and could drop
  party members. It's fixed on `spike-c-battle` (b9529e02) and ported to `online-step2-snapshot-entrance`
  (2c99e01e). **It isn't merged into `online-step2` yet.**
- **An unattributed Unity error:** one `NullReferenceException` per session in
  `BattleSPSSystem.GenerateSPS` / `SmoothFrameUpdater_Battle.Apply` when leaving a battle. Both spikes saw it. It
  still needs a run with the spike code off.
- **Debug code left on the branches:** C's `PartyTrace` debug logging; D's result-screen diagnostics.
- **Debug keys:** C uses E (record/replay), A (inject) and Q (dump); D uses 1 (encounter now), U (free characters
  join) and 2 (dump the result screen).
- **The game folder** on the main PC holds whichever spike DLL was installed last. Reinstall before testing.
