# Step 3 · M2 results: playable shared battles

Branch `online-step3-m2` (2026-11-19), from `online-step3` (M1, `d810d891`). Every player now commands their own
characters in a shared battle from their own battle menu, with no waiting on each other. Free characters follow a player
and take orders from that player's PC. A non-owner can flee on its own. A session plays by the host's battle settings,
and the battle values the online code can't replay are blocked in the launcher and the game.

**Verdict: M2 works for the paths we ran.** One two-PC round (three tests, chosen to cover T1, T2, T3, T7, T8 and T10)
passed as the tester saw it, after one wire-format bug was fixed. Both directions (host-owned and guest-owned) ran
commands from the other PC's menu. A follower was commanded by its leader's PC. A guest fled alone and came back on the
field it came from, while the host finished the battle. Two things are left open:
- **B.Waltz's remote commands waited 3-5 s on the owner** between being accepted and starting (Clove's: 0.35-0.75 s).
  Nobody waits on anybody's menu, but that command starts late. Not explained yet (known issue 1).
- **One early effect** on B.Waltz's stance move in the guest-owned battle, seen from the host's PC (known issue 2). Task 6
  removed the per-battle early effects of M1 in the other runs, but not this one.

Not run this round (reduced to three tests by Cordell): T4 item race, T5 target dies, T6 Trance / statuses / Defend /
Change, T9 owner flees, T11 guest alone elsewhere, T12 a Fast session. The launcher changes are built (`build-launcher.ps1`
with `-TargetFramework v4.7.1`) and installed on PC 1 but weren't tried in the round.

Contents:
1. [What changed](#1-what-changed)
2. [Session battle settings (task 1)](#2-session-battle-settings-task-1)
3. [Messages and sizes](#3-messages-and-sizes)
4. [Tests](#4-tests)
5. [Known issues](#5-known-issues)
6. [Commits and builds](#6-commits-and-builds)

---

## 1. What changed

New: `Online/Battle/BattleControl.cs` (control map, remote menus, per-player run and flee),
`Online/Session/SessionSettings.cs` + `SessionSettings.Configuration.cs` (session battle settings). Engine edits are
tagged `[Online] M2`.

**Who controls which unit (task 2).** At Go the owner sends a control map: each player unit commanded by another PC in
this battle → that PC (`BattleEntry.ControlMap`). The controller of a character is its player, or for a free character
its leader (`SessionLobby.ControllerOf`). A controller that isn't in the battle → the owner commands the unit. Enemies are
always the owner's.

**Remote command menus (task 3).**

| Step | Where | How |
|---|---|---|
| Turn | owner, `HonoluluBattleMain.ProcessActiveTime` | where the game would put a full-ATB character in the ready queue, `BattleControl.RouteReady` writes a **TurnReady** record into the stream instead; the unit waits (Active ATB: the battle goes on) |
| Menu | controlling PC (passive battle) | TurnReady at its frame → `BattleHUD.AddPlayerToReady`: the game's own battle menu opens (commands, abilities, items, targets from the passive battle's state; the switch-character button among this PC's ready units) |
| Choice | controlling PC, `btl_cmd.SetCommand` | the menu's SetCommand calls (one, or two for double cast) are captured instead of queued and sent as one **CommandRequest** per unit and frame; the unit stays "input finished" and a "Queued: B.Waltz → Fire" line shows until the command runs |
| Entry | owner | checked (still that PC's unit, still waiting for a command, item still there counting the queued uses, MP enough), then `btl_cmd.SetCommand` with the same arguments and `sel_mode = 1`, exactly what `BattleHUD.SendCommand` does: the normal queue, ATB and priority rules apply. **Spike C's direct injection isn't used for remote commands.** |
| Reject | owner → PC | **CommandRejected** (reason): the PC's menu reopens for that unit with a toast ("the item is gone; choose again"), or closes if the turn is over |
| Statuses | owner | vanilla: a unit under a status that takes its turn (Confuse, Berserk, Stop, Sleep...) never reaches the ready step, so no TurnReady. A waiting unit that can't act any more (KO, status, ATB reset) gets a **TurnCancel** (TurnReady with ready 0) from a per-frame sweep; the PC's menu closes. The status itself arrives as an op and runs the game's own menu cleanup too |
| Drop | owner | a controlling PC that leaves or declines: its units go to the owner's own menu (`BattleControl.ReleasePeer`) |

The "normal menu → queue" path took remote commands with small hooks (no stop-and-report needed).

**Followers (task 4).** Lobby data `follow` = `characterId:accountId|...` (free characters following a guest; the rest
follow the host), kept by the host next to `assign`. **Follow** in the party panel (button, or Ctrl+Shift+F9-F12 for slot
1-4) makes a free character follow the next player (host → guests → host). It applies from the next battle (the follow
map is read at the trigger, the control map at Go). A follower whose leader leaves, or which someone takes, follows the
host again. Battle entry: **a free character joins when its leader is in the battle**.

**Per-player flee (task 5).**
- Run pose: each player unit runs while **its controlling PC** holds the run buttons (`BattleControl.EscapeKeyFor`, in
  `btl_mot`, `HonoluluBattleMain.UpdateData` and `BattleUnit.IsRunningAway`). The owner streams a run mask of units
  (replaces M1's single escape key); a controlling PC shows its own units at once.
- A non-owner holding run for a second sends **FleeTry**; the owner runs the game's own flee roll (script 56) for it. On
  success the escape command it would set is caught (`BattleControl.CatchFleeCommand`): that PC's units (own +
  followers) are taken out with `BattleUnit.Remove` (the game's way of taking a party member out of a battle: no EXP,
  party data saved, battle-end check), a **UnitsLeave** record makes every other PC do the same at that frame, and the PC
  gets **FleeResult 0** and leaves through spike D's escape exit. It's dropped from the owner's participants.
- It reappears on **the field it came from** (tracked per PC: field and the entrance it was entered by), through the
  game's field jump. Unknown → the same field, by the entrance it was entered by. No rewards (an escape).
- If nobody else is standing after it leaves, the escape goes on as vanilla: the battle ends as an escape for everyone.
- **The owner fleeing** stays vanilla: everyone in the battle flees (M2 default; not run this round).

**Mod command overloads on the passive PC (task 6).** Chosen: **run the overload on the passive PC when the command is
injected** (`CommandInjector.RunOverload`), on the command as the owner admitted it. Why: the mod's overload swaps
B.Waltz's animation (his ground / airborne casting moves as new `AA_DATA`), the spell graphics (`PatchedVfx`) and his
move-list slots for the duration. These are local objects; streaming them would mean serializing ability data and
sequencer state. Running it gives the same animation, so the same timing. What it changes in the outcome (power) doesn't
matter on the passive, and the stance switch it makes on the owner (Ascend → his flying move) is already in the command
number, so it isn't redone. Commands it queues itself (his landing) are dropped on the passive; the owner's come in the
stream. Result: 0 early effects in Test 1 (host-owned, guest watching, Waltz Mag Thunder and Ascend); 1 in Test 2
(known issue 2).

**Ending a battle early.** A PC that leaves a shared battle early (fled, dropped) now logs a note instead of a mismatch.

## 2. Session battle settings (task 1)

The host's settings become the session's when its lobby opens (lobby data `bset`, `key=value` lines, ~1.6 KB). A guest
applies them **in memory** once the session plays: `IniValue.Value` is set, nothing calls `SaveValue`, and guests never
save, so **`Memoria.ini` and saves are never written**. The guest's own values come back when it leaves the lobby; on a
crash nothing on disk changed. The Config menu values are set again every frame (a resync loads the host's save, Config
included). While a session plays, the Config menu's battle options and the battle boosters are locked ("set by the host
for this session"). Every forced ini value is read where it's used, at the time it's used (checked in the code), so
forcing in memory works for all of them: no stop-and-report.

The owner-side check of M1 stays as an **assertion**: settings that differ at battle start log
`ASSERTION: ... battle settings differ` (error) and the peer sits the battle out. It never fired.

### Inventory

| Setting | Where | Class | Unsupported values (session: replaced by) |
|---|---|---|---|
| `Speed` | ini [Battle] | forced | 2-5 (→ 0). `1` (Fast) kept until T12 is run |
| `SFXRework` | ini [Battle] | forced | 0 (→ 1) |
| `BattleSpeed` (legacy) | ini [Hacks] | forced | 2-5 (→ 0); it feeds `Speed` |
| `ATBSpeedFactor`, `NoAutoTrance`, `GarnetConcentrate`, `BreakDamageLimit`, `AutoPotionOverhealLimit`, `SelectBestTarget`, `ViviAutoAttack`, `CountersBetterTarget`, `LockEquippedAbilities`, `SummonPriorityCount`, `CurseUseWeaponElement`, `FloatEvadeBonus`, `CustomBattleFlagsMeaning`, `KeepRestTimeInBattle` | ini [Battle] | forced | — |
| `EncounterInterval`, `EncounterInitial`, `PersistentDangerValue`, `PSXEncounterMethod` | ini [Battle] | forced | — |
| `AccessMenus`, `AvailableMenus` (menus in battle) | ini [Battle] | forced | — |
| `SpareChangeGilSpentFormula`, `StatusDurationFormula`, `StatusTickFormula`, `Speed/Strength/Magic/Spirit/MagicStoneStockFormula`, `TranceDecreaseFormula` | ini [Battle] | forced (empty = the game's built-in formula) | — |
| `StealingAlwaysWorks` | ini [Hacks] | forced | — |
| Battle style (`cfg.atb`) | Config menu | forced | Wait (→ Active) |
| Battle speed (`cfg.btl_speed`, ATB rate) | Config menu | forced | — |
| Battle camera (`cfg.camera`) | Config menu | forced: the camera choice draws a random number in the SFX code (taped on the owner) | — |
| ATB mode slider (= `Speed`) | Config menu | forced | Turn-based, Dynamic |
| Boosters: Battle Assistance, Attack 9999, No Random Encounter, Master Skill | in game | forced (host's state; toggles blocked while playing; Lv Max / Gil Max blocked too) | — |
| Booster: High Speed Mode | in game | unsupported in sessions (frame lockstep can't follow a 3× owner) | on (→ off) |
| Cursor memory (`cfg.cursor`), battle messages speed (`cfg.btl_msg`), skip battle camera (`cfg.skip_btl_camera`, intro only, which the entry gate syncs), sound, movement, window colour, vibration, controller | Config menu | local | — |
| Camera rotation / perspective boosters | in game | local | — |
| Everything else in Memoria.ini (graphics, audio, interface, fonts, mods, ...) | ini | local | — |

### Blocked values outside sessions (Cordell's amendment)

`Battle.Speed` 2-5, `SFXRework = 0` and battle style Wait are now blocked in single-player too. `Speed = 1` (Fast) stays
(`MaxSupportedSpeed = 1`, one constant in the game and one in the launcher, until T12 decides).

| Value | Launcher | In-game Config menu | Game, ini already holding it |
|---|---|---|---|
| Speed 2-5 | **removed from the ATB Mode dropdown** (Normal, Fast only); on load an ini with 2-5 (or `[Hacks] BattleSpeed` > 1) is **set to 0 and saved** | ATB mode slider has 2 steps (Normal, Fast) | `Configuration.Battle.Speed` returns 0, logs one error; **the ini isn't rewritten** |
| SFXRework 0 | no control existed; on load an ini with 0 is **set to 1 and saved** | — | `Configuration.Battle.SFXRework` returns true, logs one error; the ini isn't rewritten |
| Battle style Wait | — (not a launcher setting) | choosing Wait plays the buzzer and stays Active | `cfg.atb` defaults to Active, a save's Wait loads as Active |

**Presets:** both launcher presets (PSX and Steam) had `SFXRework = 0` (inactive while their `[Battle] Enabled = 0`,
active as soon as the ATB Mode choice enabled the section). Changed to `SFXRework = 1`. A preset is applied by writing
it to Memoria.ini and reloading, so the load clamp also covers user presets.

Session backstop: if the host's ini still holds a blocked value (edited after the launcher ran), its session uses the
supported default and the host sees a notice at lobby creation listing what was replaced (`Session battle settings
changed (not supported online): Battle Speed 3 -> 0, ...`). Not exercised this round (the launcher had cleaned the ini).

## 3. Messages and sizes

| Message | Format | Size | Direction |
|---|---|---|---|
| Go (extended) | ... music i16, control count u8, (unit u8, account u32)* | +1 B, +5 per remote unit (24-27 B seen) | owner → participants |
| CommandRequest (29) | seq u16, req u16, unit u8, count u8, (slot u8, cmd u16, sub i32, target u16, cursor u8, menu u8, priority u8)* | 7 + 12 per command (19 B seen; 31 B double cast) | controlling PC → owner |
| CommandRejected (30) | seq u16, req u16, unit u8, reason u8 (1 turn over, 2 item gone, 3 MP, 4 bad) | 7 B | owner → PC |
| RunHeld (31) | seq u16, held u8 | 4 B, on change | PC → owner |
| FleeTry (32) | seq u16 | 3 B, each second of run held | PC → owner |
| FleeResult (33) | seq u16, result u8 (0 fled, 1 can't flee here, 2 not now: silent) | 4 B | owner → PC |
| Stream TurnReady (14) | unit u16, ready u8 | 3 B + 7 B record header | owner → participants, at its frame |
| Stream RunMask (13, replaces EscapeKey 12) | units u16 | 2 B + 7 | on change |
| Stream UnitsLeave (15) | units u16 | 2 B + 7 | when a PC fled |
| Lobby data `bset` | `key=value` lines | ~1.6 KB | host |
| Lobby data `follow` | `characterId:accountId\|...` | a few bytes | host |

Battle streams this round: 4.6-6.0 KB per battle of 222 (30-40 events), as in M1.

## 4. Tests

Two PCs, two Steam accounts (host #6647, guest #6374), mod save on the cargo ship (field 507), battle 222 (B.Waltz's
mog bag fight). Build `BCAE33ED48A4` on both (after the fix below). "RT" = round trip: on the controlling PC, menu choice
→ the owner's command starting there; on the owner, request accepted → command admitted.

**First attempt** (build `E2C8F956D065`): every remote request was rejected (`0 command(s)`). The owner read 13 bytes per
command instead of 12, so it found none (fixed in `3ab02a34`). The guest's menu reopened each time, which looked like
"B.Waltz's moves are blocked".

| # | Result | Numbers | Notes |
|---|---|---|---|
| T1 settings | **pass** | guest: "the host's battle settings applied in memory (10 differed: Speed 1 → 0, CountersBetterTarget, 8 formulas)"; on leaving, the same 10 restored; `Memoria.ini` hash `C98FABA741AF` before and after | the formulas differ only in form (the host's ini leaves the section disabled: built-in formulas; the guest's lists them) |
| T2 host-owned, both command | **pass** | guest watching: 0 mismatches, 0 corrections, 0 early fx, tape 0; requests 2/2 accepted, 0 rejected | B.Waltz (guest): Ascend, then Waltz Mag Thunder on all enemies |
| T10 B.Waltz moves | **pass in T2, 1 early fx in T3** | see known issue 2 | |
| T2 RT | slow for B.Waltz | guest RT 7.1 s and 4.0 s; owner: accepted → running **5.1 s and 3.3 s** | known issue 1 |
| T3 guest-owned, both command | **pass with known issue** | host watching: 2 mismatches = 1 early effect on B.Waltz's YellowMagic1 on himself ("finished before its effect"), 0 corrections; requests 2/2 accepted; BTR1 3671 B applied on the host | host commanded Clove (two white-magic spells on the party) |
| T3 RT | good | host RT 1.4 s and 1.0 s; owner: accepted → running 0.74 s and 0.35 s | |
| T7 follower | **pass** | Vayle (3) set to follow the guest (Ctrl+Shift+F9-F12, logged on both PCs), joined the guest's battle (party 2 11 3), commanded on the guest's PC; set back to the host before the next battle | the guest's switching between two ready units wasn't logged (local menu) |
| T8 non-owner flees | **pass** | owner: "#6374 fled: P1 left the battle"; the owner finished the battle alone; guest: "back to field 504 (entrance 1, the field it came from)" | the guest's 1 logged mismatch was the abort, now a note |
| T4, T5, T6, T9, T11, T12 | not run | — | reduced round |

Entry times: 12.0-12.3 s trigger → Go (as M1). Unity logs: only the known `Can't add component 'Animation'` and
`level1` messages. The mod's CoopFootsteps NRE showed once on the guest (known, handed off).

## 5. Known issues

1. **B.Waltz's remote commands wait 3-5 s on the owner after being accepted** (Clove's: 0.35-0.75 s). The owner enters
   the command through `SetCommand` like its own menu, so the wait is in the queue: something holds it before admission
   (another action running, or `RunCommandFromQueue`'s "caster still in its normal-to-command motion" gate, which a
   transformed B.Waltz may take long to pass). Not yet compared with B.Waltz's own commands on a single PC. To check
   first in M3: if vanilla B.Waltz waits the same, it's the mod's; if not, the remote path needs his ready pose.
2. **One early effect on B.Waltz's stance move**, host watching the guest-owned battle: the host's animation reached the
   effect 9 frames ahead of the owner's (it was at the margin). The overload now runs on the passive (task 6) and no
   other B.Waltz move gave an early effect. Candidates: his airborne idle flag (`bi.def_idle`, flipped every frame by
   the mod's watcher) or the mod's move-list swap state differing on the two PCs. Pass criterion of task 6 (0 early
   effects) not fully met.
3. **Mix commands with an unregistered recipe** send a negative id that only the controlling PC knows; the owner would
   reject or misrun them. No mix command in the mod's party; not handled.
4. **Untested**: item race (T4), target dies before a remote command (T5), Trance / statuses / Defend / Change on a
   remote unit (T6), owner flees (T9), guest alone on another field (T11), a Fast session (T12), the launcher UI, the
   host's override notice, a controlling PC dropping mid-battle.
5. **Booster lock is coarse**: battle boosters are forced to the host's state and locked for everyone while playing,
   and High Speed Mode is off in sessions.

## 6. Commits and builds

- `a3a749b0`: M2 (control map, remote menus, followers, per-player flee, overload on the passive, session settings,
  blocked values in game and launcher).
- `3ab02a34`: CommandRequest read 13 bytes per command instead of 12.
- `2afeceb7`: leaving a shared battle early logs a note, not a mismatch.

DLLs: `E2C8F956D065` (first attempt), `BCAE33ED48A4` (the round), `537C4802E628` (last, log line only; installed on
PC 1). PC 2 package: `Output/OnlineStep3-PC2.zip` (`BCAE33ED48A4`).
