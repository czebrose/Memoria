# Shared battles: a guest's menu stuck after Sleep

Branch `online-sleep-menu` (from `online-step3-m2`, 056cdcd8), 2026-11-20.

## The report

Two-PC run of 2026-11-19, Ice Cavern battle 22. The host #6647 owns the battle; the guest #6374 commands B.Waltz (P1).
After B.Waltz had been put to Sleep, the guest couldn't give him commands. Logs: `Output\logs\*-SpikeE3-20261119-2354.*`
in the `spike-e-dialog-sync` checkout; guest battle log `watched-20261119-234444-battle22.txt`.

## What the logs show

Battle frames run at 15 per second, so the battle log's frames line up with the session logs:

| Battle frame (time) | Owner (host) | Guest |
|---|---|---|
| 85 (23:42:31) | an enemy puts P1 to Sleep | |
| 320 (23:42:47) | Clove's Potion removes Sleep; P1's ATB is full: **TurnReady** in the same frame | "P1's turn: menu opens here", but no menu |
| 1010 (23:43:33) | Sleep again: TurnCancel | "turn was taken away" |
| 1077 (23:43:38) | a hit wakes P1: TurnReady | "menu opens here", no menu |
| 1324 (23:43:54) | Sleep again: TurnCancel | |
| 1480 (23:44:05) | Potion: TurnReady | "menu opens here", no menu; the guest flees |

Not one command request in the battle. Battle 27 right after (no Sleep) worked.

## Cause

On the guest, a TurnReady from the owner puts the unit into the battle menu's ready queue with
`BattleHUD.AddPlayerToReady`. That function silently does nothing for a unit in the HUD's "unconscious" list (units
that can't take input: KO, Sleep, Stop...). The HUD updates that list once per HUD frame (`CheckPlayerState`).

The owner sends TurnReady in the very frame the status goes (the waking unit's ATB is already full), so on the guest
the TurnReady arrives while its HUD still counts the unit as asleep. The unit never gets into the ready queue, no menu
opens, and the owner waits for a command forever (until the next Sleep cancels the turn). `BattleControl.OnTurn`
logged "menu opens here" without checking.

## Fix

`BattleControl` (controlling PC) keeps the turns the owner gave it that the menu hasn't taken yet. `OnTurn` offers the
turn to the menu and logs whether it really opened. Every frame (`Flush`) it offers the remaining ones again, until
the menu takes the turn (a command is chosen, `CaptureCommand`) or the owner takes it back (TurnCancel). Log:
`P1's turn came, but the menu here still counts it unable to act ...: it opens as soon as it can`, then `P1's turn:
menu opens here now (N ms after the turn came)`.

Only `Memoria/Online/Battle/BattleControl.cs` changes.

## Test (two PCs)

Any battle where an enemy can put the guest's unit to Sleep (Ice Cavern: battle 22 has it) or any status that stops
input and is then cured (Sleep, Stop, Freeze...). Cure it (Potion / Eye Drops / a hit) while the unit's ATB is full.
- **Pass:** the guest's menu opens for that unit right after it wakes, and its commands run. The guest's log shows
  `turn came, but the menu here still counts it unable to act` followed by `menu opens here now` within a frame or two.
