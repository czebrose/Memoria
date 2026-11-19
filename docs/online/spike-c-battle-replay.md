# Spike C: battle command flow, forced outcomes, passive battles (2026-11-18)

Question: can FF9's battle engine be split into **calculate** (on the battle owner's PC) and **play back** (on every
other participant's PC), with playback using the game's own animation code and the owner's results instead of its own
calculation? Tested on one PC by recording a battle and replaying it in a passive mode. No networking.

Branch `spike-c-battle` (from `online-step2`), code in `Assembly-CSharp/Memoria/Online/Battle/`. Line numbers below
are from that branch.

## Verdict

**Forced-outcome replay works.** The passive battle runs the game's own command pipeline: target checks, MP cost,
item use, the animation sequence, damage numbers, death and victory screens. Only the *calculation* is replaced, by
replaying the owner's engine-level "apply" calls (damage, recover, status add/remove, kill, numbers, messages,
items). After fixing what the test runs found, every replay ended with **0 mismatches** (state compared after every
action and at the end). The "calculate once, play back everywhere" shape is right for Step 3.

The cost is that **"the calculation" isn't the only place outcomes are decided**. The surprises the tests found were
all outside the calculator, and each needed its own hook:
- random numbers drawn by the command pipeline itself (retargeting, reflect bounces, random-target multi-hits);
- **per-PC state** that changes a command's path: summon length depends on the PC's achievements;
- the **battle event scripts** (enemy AI and scripted enemies), which change units directly. Enemies leaving the
  fight is what made an early replay hang.

| Run | Battle | Content | Result |
|---|---|---|---|
| R1 | 182 | attacks, Cure, item, C2 injected attack | **mismatch 0, corrections 0, tape 0**; normal victory |
| R2 | 37 | Carbuncle (Reflect on the party), Sword Art, Steal ("Stole Soft!"), Slow, a heal-all, script-driven enemies | mismatch 0; 1 correction (stolen item) + 1 tape miss (summon length): both fixed after the run, fixes not re-run |
| R3 | 45 | enemies leaving by script, Mug, poison ticks, Reflect | first replay hung at the end (enemy removed by script): fixed (script writes recorded, passive blocks its own); not re-run after the fix. Covered in part by R2. |
| R4 | 182 | summon, flee | **all counters 0**, escaped through the normal screens |
| R4 defeat | n/a | | **not tested**: the mod's mog-bag fights have no game over, and other battles end the game |

Not exercised in any run: **Trance, a KO + revive, a counterattack, a spell bouncing off Reflect**. Their code paths
are described below with where they would hook; they are the first things to test in Step 3.

## How the prototype works

- **Record** (`BattleRecorder`, owner): logs, in order: `Start` (initial state), `Command` (what the engine admitted
  to `cur_cmd_list`, with the random numbers it draws outside the calculation: the **tape**), `Effect` (one
  calculation on one target: the list of **ops** + damage-number state), `CommandEnd` (state after the action),
  `Tick` (status loop ops: poison, regen, expiry, low HP, death), `Script` (battle-script writes to units / the battle),
  `Sync` (any other state change, found by comparing snapshots at every context boundary), `Atb`, `End` (result,
  rewards).
- **Replay** (`BattleReplayer` + `PassiveBattle`, passive): no ATB filling, no enemy AI turns, no menu, the local
  command queue is never read. Commands are injected straight into `cur_cmd_list`; when the animation reaches an
  effect point the calculation is skipped and the recorded ops are applied. State is compared at every `CommandEnd`
  and at the end; anything the ops didn't reproduce is corrected from the owner's snapshot and counted.
- Debug keys (Ctrl+Shift, `OnlineDebug = 1`): **E** Off → Record → Replay (Record is one-shot, then switches to
  Replay), **A** C2 injected attack, **Q** state dump. Status box bottom-left. Recordings are written to
  `<game>\SpikeC\record-*.txt`. The replay forces the recorded battle id, pattern, start type and initial ATB, so any
  encounter on the field replays it.
- Testing used the Step 2 in-memory snapshot (Ctrl+Shift+S / R). It lost party members on reload; see
  [Found along the way](#found-along-the-way).

## C1. Anatomy of one command

### Input
- Player menu: `BattleHUD.SendCommand` (BattleHUD.cs:1692) → `btl_cmd.SetCommand(btl.cmd[0], cmdId, subNo, tarId,
  cursor, menu)` (btl_cmd.cs:153). Double cast fills `cmd[3]` and `cmd[0]`. Auto-battle uses
  `SendAutoAttackCommand` (BattleHUD.Unity.cs).
- A command is a `CMD_DATA` in one of the caster's 6 (+1) slots: `cmd[0]` main, `[1]` counter / enemy counter /
  dying, `[2]` system (death, trance...), `[3]` double cast, `[3..5]` script counters, `[6]` extra dying. Flee uses
  `btlsys.cmd_escape`. `SetCommand` copies the ability's fields (`SetAAData`: power, rate, script id, elements, status
  set), sets targets and cursor, and calls `EnqueueCommand`. It also removes `RemoveOnMainCommand` statuses (Defend).
- Enemies: ATB full → `EventEngine.RequestAction(EnemyAtk)` (HonoluluBattleMain.cs:529) runs the enemy's AI
  function in its battle script, which calls `btl_cmd.SetEnemyCommand` (btl_cmd.cs:346).

### Queue
- `EnqueueCommand` (btl_cmd.cs:836): a linked list (`btlsys.cmd_queue`), priority commands (counters, system
  commands) inserted ahead.
- ATB: `HonoluluBattleMain.ProcessActiveTime` (414) fills `cur.at` every frame while the menu allows it
  (`FF9BMenu_IsEnableAtb`). Full → players join `BattleHUD.ReadyQueue` (menu), enemies get an AI turn.
- `RunCommandFromQueue` (btl_cmd.cs:488) admits the first ready command (`GetFirstCommandReadyToDequeue`, 440) into
  `cur_cmd_list` when nothing runs (Speed < 3). Declarable commands also wait for the native ATB gate. A modded
  `IOverloadOnCommandRunScript` runs here; the Black Waltz mod has one (B.Waltz's Ascend, spell graphics).
- `CommandEngine` (567) moves each running command through `INSPECTION` → `SELECT_VFX` → `LOOP` → `DONE`.

### Calculation
- `INSPECTION`: `CheckCommandCondition` (944), `CheckTargetCondition` (1231), `CheckMpCondition` (MP is spent
  here). `SELECT_VFX`: cover (supporting abilities), `btl_vfx.SelectCommandVfx` (starts the sequence), **items are
  consumed here** (`UIManager.Battle.ItemUse`).
- The outcome is decided per target when the sequence reaches an **effect point**: `UnifiedBattleSequencer`
  "EffectPoint" (UnifiedBattleSequencer.cs:975) clears every unit's `fig` → `btl_cmd.ExecVfxCommand` (1537) →
  `SBattleCalculator.CalcMain` (SBattleCalculator.cs:35) → the battle script (`Memoria.Scripts`, the mod's
  `Memoria.Scripts.HadesWorkshopMod.dll` overrides some) → `CalcResult` (149). Hit/miss, damage, healing, statuses,
  steal, eat... are decided in the script; `CalcResult` applies them.

### Animation and damage numbers
- `SFXRework = 1` (default): `UnifiedBattleSequencer` runs the command's action threads (motions, moves, SFX,
  camera). Each effect point collects the units' `fig` (info flags + HP/MP numbers) into the action's `btl2dParam`;
  a later "Figure" code shows them (`btl2d.Btl2dReq`). So **numbers come from what the calculation leaves in `fig`**.
- Hit reactions: `btl_para.SetDamage(..., dmg_mot)` → `btl_mot.SetDamageMotion`.

### Application, death, end of command
- `btl_para.SetDamage / SetRecover / SetMpDamage / SetMpRecover` (btl_para.cs:119...) change HP/MP and fill `fig`.
  `btl_stat.AlterStatus / RemoveStatus` run the status scripts (visuals, SPS effects, durations).
- Death: HP 0 → `BattleUnit.Kill` (enemies: die sequence 3) and, in the next status loop,
  `btl_para.CheckPointData` (77) adds `Death`. `btl_mot.DieSequence` (btl_mot.cs:286) fades / removes the unit
  (`btl_sys.DelCharacter`) and calls `btl_sys.CheckBattlePhase` (btl_sys.cs:52), which starts victory or defeat.
- `FinishCommand` (1299): ATB reset, trance decrease, statuses' `OnFinishCommand`, clears the slot.
- End: `battle.BattleTrailingLoop` (battle.cs:287) → result → `UpdateOverFrame` → result screen. Rewards accumulate
  in `battle.btl_bonus` as enemies die (`btl_sys.SetBonus`, 215: item and card drops are random).

### Side paths (where they hook in)
| Side path | Where | Replay status |
|---|---|---|
| Enemy counters / reactions | `CalcResult` → `EventEngine.RequestAction(EnemyCounter/EnemyReaction/EnemyDying)` → AI script → `SetEnemyCommand(cmd[1..6])` | fits: they become separate owner commands; passive never runs CalcResult. **Not seen in a test.** |
| Player Counter, Auto-Potion, Return Magic | supporting abilities in `CalcResult` (`TriggerOnAbility "EffectDone"`) → `SetCounter(cmd[1])` | fits the same way (separate commands). Not tested. |
| Cover | `CommandEngine` SELECT_VFX, `TriggerOnCommand` → `CheckCoverAbility` (random) | runs on both sides with the tape |
| Reflect | `btl_cmd.CheckReflec` (881): random bounce targets (`Comn.random8`) | tape. Reflect applied in R2/R3; a bounce not seen. |
| Random-target / multi-hit | sequencer `RandomTarget` / random caster (`Comn.randomID`, `Comn.random8`) | tape; each hit = its own Effect |
| Retarget (dead target) | `CheckTargetCondition` → `GetRandomBtlID` (`UnityEngine.Random`) | tape (seen in R3: `tape [c0]`) |
| Status ticks (Poison/Regen/Venom, expiry, Doom...) | `btl_stat.CheckStatusLoop` (286), per frame, timer = `at_coef` | Tick events; passive skips the loop. **Poison/Regen write HP directly** in their scripts (only the number is an op). |
| Low HP, death from HP 0 | `btl_para.CheckPointData` | Tick events |
| Trance | gauge: `CalcResult.CheckDamageReaction` (direct write) → `AlterStatus(Trance)` → `SysTrans` command; `ExecVfxCommand` SysTrans branch has **no calculation** (model swap, glow) | should fit (gauge corrected, command replayed). **Not tested.** |
| Summons | `DecideSummonType` (1606): long/short by random **and by this PC's achievements** | owner's choice sent with the command (R2 fix) |
| Steal / Mug | script: item to inventory directly (`FF9Item_Add`), message | item ops + message ops (R2 fix) |
| Eat / Blue Magic learned, Scan / Libra | script changes outside the battle data (learned abilities) or UI | **gap**: learned abilities aren't in the outcome; Libra UI not checked |
| Flee | HUD hold → `btl_sys.CheckEscape` → flee script (random) → `SetCommand(cmd_escape, SysEscape)` | fits (R4: replayed, normal escape) |
| Heat, Garnet "can't concentrate" | `RunCommandFromQueue` (Heat: kill), `CheckCommandCondition` (random) | Heat → Sync; Garnet → tape |
| Enemy multi-part scripts | battle event script: `btl_scrp.SetCharacterData` (416: hide/remove/add a unit, HP, model...), `SetBattleData` (end battle, ATB off, cameras) | owner's writes recorded; passive blocks its own (R3 fix). **Other script effects aren't covered**: dialogs, music, camera cuts, model changes not made through those two calls. |
| Rewards | `btl_sys.SetBonus` (random drops) | passive's rewards replaced by the owner's at the end (seen: 2 vs 3 items) |
| Battle start | pattern (`ChoicePattern`, `UnityEngine.Random`), back/preemptive (`btl_sys.StartType`, 181), initial ATB, camera | forced from the owner (spike D's battle entry should carry them) |

## Random numbers

All of the battle's RNG goes through `UnityEngine.Random`. The game-logic entry points are `Comn.random8/random16`
(and `randomID`), `GameRandom.*` (Memoria's calculator scripts) and a few direct `UnityEngine.Random` calls.

**Outcome randomness**
- Calculator scripts (`GameRandom`, `Comn`): hit/miss, damage variance, criticals, statuses, steal, eat...
  **Replaced** (the passive doesn't run them).
- `btl_stat.AlterStatus` partial resist (`UnityEngine.Random`). Replaced (ops replay the result).
- Command pipeline: `CheckReflec`, `CheckTargetCondition` / `GetRandomBtlID`, `RandomTarget` and random caster in
  the sequencer, `DecideSummonType`, Garnet's concentration, double cast's random target (`SetCommand`), cover.
  **Taped**: `BattleTape` hooks `Comn.random8/16`, `GameRandom` and `GetRandomBtlID` while a command is "in
  context". The owner records each value and the passive returns the same one. Typical: 0-1 values per command.
- Enemy AI choices (event script `rand`), flee success, initial ATB (`btl_init`), pattern, start type, reward drops.
  Owner only; their results travel as commands, start data and rewards.

**Cosmetic randomness** (left alone, may differ between PCs): SFX particles (`SFXDataMesh`), `SFX_Rush`, battle
camera choice (`HonoluluBattleMain.Start`), idle animation start frame (`btl_init`), voice line choice
(`BattleVoice`), rain.

## C2. Injecting a command

**Yes.** Ctrl+Shift+A makes the first ready player character (in `ReadyQueue`, no command yet) Attack the first
enemy: close its menu if open (`BattleHUD.SetIdle`), `sel_mode = 1`, `SetCommand(cmd[0], Attack, 176, enemy)`,
`InputFinishList.Add`. It ran normally (R1, R3), queued and executed like a menu choice.
Preconditions: battle phase NORMAL, the character's ATB full (in `ReadyQueue`), slot `cmd[0]` free.

For playback, the queue is the wrong door: its ATB and priority gates would reorder things. `CommandInjector`
fills the slot like `SetCommand` / `SetEnemyCommand` do, with the owner's values, and adds it to `cur_cmd_list`
directly (`KillCommand` first if something local queued it). That's how all replays ran.

## C3. Forced outcomes

**Yes, with the hook below the calculator scripts.** `SBattleCalculator.CalcMain` is the single per-target entry,
for every script (Memoria's and the mod's DLL). On the passive it returns at once and the replayer applies the
owner's **ops**: the engine-level calls the calculation made, recorded at nesting depth 0 so their own internal calls
aren't doubled:

| Op | Engine call | Size |
|---|---|---|
| Damage / Recover / MpDamage / MpRecover | `btl_para.SetDamage(value, motion)` etc. (HP, `fig`, hit motion, kill) | 6-7 B |
| Alter / Remove | `btl_stat.AlterStatus` (status, inflicter, script parameters) / `RemoveStatus` (failed alters are dropped) | 3-5 B + params |
| Kill | `BattleUnit.Kill` | 3 B |
| Figure / StatFigure | `btl2d` number requests made directly | 11 B |
| Message | `BattleHUD.SetBattleMessage` ("Stole Soft!") | 3 B + text |
| ItemAdd / ItemRemove | `ff9item.FF9Item_Add / Remove` inside a calculation | 5 B |
| ScriptUnitSet / ScriptBattleSet | `btl_scrp.SetCharacterData / SetBattleData` | 7 B |

An **Effect** = caster, target, ops, final `fig` per unit (number display), dodge flags. Measured: **15-26 B on
average, 44 B max**. A **Command** is about 30-36 B including its tape; a CommandEnd is 3 B.

Why not hook at the scripts (Memoria's calculator API)? The mod ships its own script DLL, and `CalcResult` does a lot
after the script: damage limit, reflect multiplier, drain, trance gauge, supporting abilities, counters, AI
reactions. Replaying the final apply calls skips all of it. Why not only set the resulting state? Ops are what make
the hit motions, status visuals, deaths and numbers come out of the game itself. A state snapshot (the
`Effect.State` used for checking) is the safety net: anything the ops didn't reproduce is corrected and counted.
Known direct writes found so far: the trance gauge, poison / regen HP, steal's item (now an op).

**One record or a sequence?** A sequence. One Effect per target per effect point: multi-target spells give N Effects
in a row, multi-hit gives one per hit, reflect bounces are more effect points of the same command. Counters,
auto-potion and return magic are separate commands. Ordering is everything: the passive consumes events strictly in
order and an Effect waits for its effect point.

## C4. Passive battle mode

**Yes.** What the passive does instead:
- **ATB:** `YMenu_ManagerActiveTime` skipped (`HonoluluBattleMain.UpdateBattleFrame`). No player ever becomes ready,
  so there's no menu, and no enemy turn is requested. Gauges are **set from outside** each Atb event (owner sampled
  every 3 frames): smooth enough, and it shows the owner's gauges.
- **Queue:** `RunCommandFromQueue` never admits local commands (any local ones are dropped and logged: 0 in all
  runs).
- **Status ticks:** `CheckPointData` + `CheckStatusLoop` don't run (`battle.BattleMainLoop`); a visual-only stand-in
  keeps status effects, the die sequence and the dying idle pose. The owner's Tick events apply the ticks (they fire
  on their own timers; suppressing them is required).
- **Battle scripts:** still run on the passive (setup, dialogs, camera), but their writes to units / the battle are
  blocked once replay starts; the owner's arrive as Script events. Blocked writes in the runs: 0.
- **Ending:** no "victory / defeat" message is needed. The owner's deaths and escapes are replayed through the same
  engine calls, so the passive reaches victory and escape through its own normal screens and returns to the field.
  Rewards are replaced by the owner's at the end. Defeat follows the same path in theory (`CheckBattlePhase`) but
  wasn't tested.

## Data rates (from the recordings)

| Battle | Length | Events | Bytes | Of which ATB |
|---|---|---|---|---|
| R1 182 | ~1.5 min | 379 | 6.8 KB | ~5 KB |
| R2 37 | ~1.5 min | 327 | 5.2 KB | ~3.5 KB |
| R3 45 | ~3 min | 820 | 17.8 KB | ~10 KB (+ 11 Syncs, 4.4 KB) |
| R4 182 (flee) | ~1 min | 162 | 3.0 KB | ~2.3 KB |

Commands + outcomes are about **1-2 KB per battle**. ATB samples dominate; in Step 3 they should be sent at a low rate
or as "ATB value + time" with local interpolation. Syncs (state changes the model doesn't explain) should be rare and
sent as diffs, not full snapshots.

## What doesn't fit (and how hard)

| Gap | Effort |
|---|---|
| **Per-PC state in command paths** (summon length from achievements; others may exist: anything reading `FF9StateSystem.Achievement` / settings in battle) | small each, but they have to be found: audit command-pipeline reads of local state |
| **Battle scripts' non-unit effects** (dialogs, music, camera, model swaps by opcodes other than `SetCharacterData`) | medium: either let them run on the passive (deterministic if they don't use `rand`) or record more opcodes. Needs a scripted boss test. |
| **The mod's `IOverloadOnCommandRunScript`** runs at queue admission, which the passive bypasses (B.Waltz's Ascend / spell graphics) | small: run it at injection, or record what it changed |
| **Learned abilities** (Eat / Blue Magic), Libra / Scan display | small: an op for learning; UI to check |
| **Direct state writes in scripts** (trance gauge, poison HP, script stat changes) | handled by the snapshot correction; for Step 3, send a per-effect state diff (cheap) instead of trusting ops alone |
| **Timing**: the passive shows deaths / ticks when its event stream reaches them, which can be slightly early relative to its own animation | cosmetic; buffering the stream by one action would hide it |
| Defeat, Trance, KO + revive, counters, reflect bounce | untested; expected to fit (paths above) |
| `Configuration.Battle.Speed >= 3` (simultaneous actions) and `SFXRework = 0` | out of scope: the spike assumes the defaults (Speed 0, SFXRework 1) |

**Next-best approach** if this had failed: shared-seed lockstep (same commands + the same `UnityEngine.Random` seed
on every PC). It isn't viable here: cosmetic code draws from the same generator at frame-dependent times (particles,
camera, voices), and per-PC state (achievements) changes code paths.

## Found along the way

- **Step 2 snapshot bug (fixed on this branch, commit b9529e02):** a snapshot taken mid-field keeps the entrance the
  field was entered by. On reload the field's `Main_Init` replayed it: the mod's field 507 re-ran its New Game party
  setup and dropped Duskly and Vayle. Moogle saves avoid this by setting `General_LoadedGame`. Fix: snapshot loads
  enter fields with entrance 10000, as a loaded game. **This should go to `online-step2`**: guests' resyncs use the
  same load. Found with `PartyTrace` (debug log of every party change, kept on this branch).
- **Unity error, unattributed:** one `NullReferenceException` per session in `BattleSPSSystem.GenerateSPS` (called
  from `SmoothFrameUpdater_Battle.Apply`) when leaving a battle, right after a "Can't add component 'Animation' to
  9001" message. None of the spike's hooks are on that path, but a run with spike mode Off is still needed to rule it
  out.
- Battle 222 (the mod's B.Waltz fight) didn't end after the last enemy died, during recording (not replay). Not
  investigated; it may be a mod issue.

## Files and hooks

New: `Memoria/Online/Battle/` `BattleSpike` (facade, hooks), `BattleRecorder`, `BattleReplayer`, `BattleLog`
(events, sizes), `BattleOutcome` (snapshots, ops), `BattleTape`, `CommandInjector`, `PassiveBattle`, `BattleSpikeHud`;
`Memoria/Online/PartyTrace.cs` (debug).

Engine edits (`[Online] Spike C`): `btl_cmd` (RunCommandFromQueue, CommandEngine context, FinishCommand, summon
type), `SBattleCalculator.CalcMain`, `btl_para` Set*, `btl_stat` AlterStatus/RemoveStatus, `BattleUnit.Kill`, `btl2d`
Btl2dReq/Btl2dStatReq, `btl_scrp` SetCharacterData/SetBattleData, `BattleHUD.SetBattleMessage`, `ff9item`
Add/Remove, `Comn.random8/16`, `GameRandom`, `btl_util.GetRandomBtlID`, `battle.BattleMainLoop`,
`UnifiedBattleSequencer.Loop`, `HonoluluBattleMain` (Start, UpdateBattleFrame), `btl_init` (start type),
`SceneDirector` (HUD component). Plus `[Online]` debug lines in `ff9play` and the snapshot fix in `GameSnapshot`.
Most engine apply calls were split into a public wrapper and a private `...Core`: the wrapper records the op.
