# Spike E: dialog sync for party scenes

Branch `spike-e-dialog-sync` (from `online-step3-m2`, 056cdcd8), 2026-11-19. Design: the "party scenes" brief
(section 3, Risks). **Question:** can every PC run a story scene from its own field scripts and stay in step if the
scene owner's PC decides when each dialog window closes and which choice is taken? Or must followers watch a stream
of the owner's actors instead (the much bigger fallback)?

Contents:
1. [The window path in FF9 / Memoria](#1-the-window-path-in-ff9--memoria)
2. [What was built](#2-what-was-built)
3. [Two-PC test](#3-two-pc-test)
4. [Results](#4-results)
5. [Verdict](#5-verdict)
6. [What the party-scenes milestone needs](#6-what-the-party-scenes-milestone-needs)

---

## 1. The window path in FF9 / Memoria

### Opening

| Opcode | Name (HW) | What it does |
|---|---|---|
| `MES` 0x1F | WindowSync | `ETb.NewMesWin(text, winId, flags, talker)`, then the entry waits (`wait = WAIT_STATE_MESSAGE`, `winnum = winId`) |
| `MESN` 0x20 | WindowAsync | the same, without waiting |
| `MESA` 0x95 / `MESAN` 0x96 | WindowSyncEx / WindowAsyncEx | the same, with the talker given as an argument |
| `WAITMES` 0x54 | WaitWindow | waits until window `winId` is closed |
| `CLOSE` 0x21 / `CLOSEALL` 0xEB | CloseWindow / CloseAllWindows | closes by script (`DialogManager.Close(id, scripted: true)` → `Dialog.ForceClose`) |
| `NOINITMES` 0x53 | PreventWindowInit | new windows don't close older ones |
| `MESVALUE` 0x66 | SetTextVariable | a number / item shown in the text |

`ETb.NewMesWin` first closes any window with the same id, then `DialogManager.AttachDialog` takes a `Dialog` from a
pool, sets its text (split into pages on `[PAGE]`), and `Show()`s it. The window id is the script's (0-7 in practice);
the same id is reused by the next window.

**Waiting:** `EBin` (the script loop) skips an entry while `wait == WAIT_STATE_MESSAGE` and
`ETb.MesWinActive(winnum)` (= `DialogManager.CheckDialogShowing`, "a dialog with that id is in the active list"). So
a script continues on the frame after the window is gone. Nothing else ties a script to a window.

### Closing

Everything goes through **`Dialog.Hide()`**, once per page. On a page that isn't the last, `Hide` shows the next one;
on the last, it plays the close animation and `AfterHidden` releases the dialog (then `MesWinActive` is false).

| Who closes | Path |
|---|---|
| The player | `UIKeyTrigger` (or a mouse click, `DialogManager.OnClick`) → `DialogManager.OnKeyConfirm` → `Dialog.OnKeyConfirm` → `Hide()`, only in `CompleteAnimation` (text fully shown) and if the window isn't input-inhibited (`FlagButtonInh`). A confirm during the typing shows the rest of the text instead. |
| A timer | text tags `[TIME=n]` set `EndMode`; when the text is shown, the `AutoHide` coroutine waits n/30 s and calls `Hide()`. No input. |
| The script | `CloseWindow` / `CloseAllWindows` → `ForceClose()` → `Hide()`, often after `WindowAsync` + `Wait(n)`. No input. |
| A new window with the same id | `NewMesWin` → `DisposWindowByID` → `ForceClose()`. |
| Voice acting (Memoria option) | can hold a `CloseWindow` until the line ends. Off in these tests. |

So the windows that close without input are the timed ones (`[TIME]`) and the script-closed ones; their timing comes
from the PC's own script and frame clock.

### Choices

A window has choices when its text has `[CHOO]` (and `[MASK]` / `[DEFT]` / `[CNCL]` set which are shown, the default and
the cancel option). The cursor is a button group; `Dialog.SelectChoice` (setter) stores the index in
**`DialogManager.SelectChoice`** (a static). Confirm takes the active button's index; cancel moves to the cancel choice.
The script reads it afterwards with the `GetDialogChoice` expression (`ETb.GetChoose()` = `DialogManager.SelectChoice`).
Nothing else is read: **setting `SelectChoice` and then calling `Hide()` is enough to make a choice from code.**

Other inputs a script can read (not windows): raw buttons (`ETb.KeyOn` / `EventInput`, e.g. "press X" minigames) and
number input windows (`[NUMB]`). Neither was in the test scenes; both need the owner's input forwarded the same way.

### Can it be driven remotely?

Yes, without engine changes beyond hooks: one hook in `Hide()` to see each page / close and who caused it, one at the
top of `OnKeyConfirm` / `OnKeyCancel` / `OnItemSelect` to ignore a follower's own input, and the remote close is
`SelectChoice = c; ForceClose()` (after `AdvanceProgressToMax` if the text is still typing, as a confirm does).

## 2. What was built

`Memoria/Online/Session/SceneSync.cs` (debug only: `[Debug] Enabled = 1`, `OnlineDebug = 1`) and `[Online] Spike E`
hooks in `Dialog.cs`, `ETb.NewMesWin`, `HonoluluFieldMain` and `SessionLobby.Meet`.

- **Start:** Ctrl+Shift+2 on a field starts a party scene owned by that PC (host or guest); pressed again by the owner,
  it ends. Every other PC follows. (Toolkit key 101 isn't read yet: a real scene marker is the milestone's job.)
- **Ready gate:** at the start, and at every field load during the scene (including the reload after a battle), each
  PC runs the field's opening scripts, then holds its field event loop (`ServiceEvents` isn't called). Followers send
  Loaded(field). The owner sends Go once all have loaded the same field (or after 10 s) and starts half an RTT later,
  so every PC starts the field's scripts at about the same moment. A follower that gets no Go goes on after 20 s.
- **Scene clock:** ms since the field's Go on each PC, so times compare across PCs without synced clocks (±RTT/2).
- **Windows** are numbered in opening order since the field load (`w1`, `w2`, ...); both PCs run the same scripts, so
  the numbers match (the owner also sends the text id, and a mismatch is logged as DIVERGED).
- **The owner paces:** when the owner's player advances a page or closes a window (or makes a choice), the owner
  sends (field load, window number, text id, page, choice). A follower ignores its own confirm / cancel / choice keys
  on scene windows; it applies the owner's input to its own window with that number, as soon as its own script has
  opened it and the window is shown (finishing the typing first). Timed and script-closed windows close by themselves
  on each PC: the owner's notice is only logged.
- **Not stuck:** a follower gets its input back on a window if the owner has gone past it without closing it by input,
  or after 30 s.
- **Triggers are the owner's.** What the owner's player starts by talking, checking or walking into a region (all
  through `EventCollision` → `EventEngine.Request` at level 1) is sent (object uid, tag) and started the same way on
  every follower, after setting the follower player's listener for a talk. A follower's own player triggers nothing
  during the scene. Added after the first try: Ctrl+Shift+2 only switched the pacing on, so the festival (started by
  talking to Mogwin) ran on the host alone.
- **Field jumps and battles** a script starts (`Field`, `Battle`, world map) are logged on both PCs with the scene
  clock and the window count, and the owner's are sent, so a follower's log shows how far its script was ahead or
  behind. Nothing is blocked: two battles can still start (the battle owner rules decide, as before).
- **No meeting resync** (whole-game reload) during a scene; the host still publishes its state.
- **Logs** (`Memoria.log`, all lines start with `[Online] Scene`): `OPEN`, `PAGE`, `CLOSE` with who closed it (player
  input / owner / timer / script), `applied owner's input ... N ms after it arrived`, `TIMING wN` (follower: open and
  close times, owner vs here), `HOLD` / `Go` / `RUN`, `script field jump / battle`, and an `END` summary with drift
  stats. The status box (Ctrl+Shift+O) has a "Party scene" line; a follower sees "the owner's player advances the
  text".

Control messages (reliable): 40 Start, 41 Loaded, 42 Go, 43 Window, 44 Jump, 45 End, 46 Trigger.

## 3. Two-PC test

Test build `C90366332894`: this branch (3fd5f717) merged with `online-live-flags` (80428d86; live chest / flag
sync, its own doc `live-flags.md`) in a detached commit (fc340fdc, on no branch), so one two-PC sitting tests both.
Earlier builds: `73165262932C` (run 1, no trigger mirror), `B2D31594794A` (not run), `24BC10E56C95` (run 2). PC2: `Output\OnlineSpikeE-PC2.zip` with `INSTALL-FOR-AGENT.md`. In a party scene, live flags
from the host can reach a follower before its own script sets them; the logs show these as `applied live` lines.

1. Both PCs: Continue the mod's save on the cargo ship (field 507), host + guest in a session, both on 507.
2. **Host** presses Ctrl+Shift+2 (status box: "Party scene ... owner"; guest: "following").
3. **Only the host** talks to Mogwin to start the festival; the guest just stands there (its talks are ignored now)
   and the scene should start on both. Play it (507 → 574 → 576 → 575 → 556, up to and including battle 31). Only the
   host presses confirm / picks choices; the guest presses confirm now and then to check it's ignored.
4. After the battle (or when the scene is over), the host presses Ctrl+Shift+2 again to end it.
5. A choice window, if the festival had none: any NPC or Moogle whose first box asks a question. Both PCs on its
   field; the **guest** starts a scene this time (Ctrl+Shift+2 on the guest). The guest talks to it (the host's PC
   follows); the guest picks an option that doesn't open a menu (e.g. "Cancel"); the host's box should
   take the same option. The guest ends the scene with Ctrl+Shift+2.
6. Send both PCs' `Memoria.log` and `x64\FF9_Data\output_log.txt`.

## 4. Results

### Run 1 (2026-11-19 22:43-22:48, build `73165262932C`, no trigger mirror)

Host #6647 (Clove), guest #6374 (Blank, then B.Waltz), cargo ship deck 507. Logs: `Output\logs\*-SpikeE-20261119-2249.*`.

- **The gate works.** 10 scene starts on 507 and two field loads: each held 4-20 ms before Go (RTT 3-20 ms). On the
  field load to 502 the guest arrived 1.7 s after the host; the gate waited for it.
- **The festival didn't start on the guest.** Ctrl+Shift+2 only switched the pacing on; the scene starts by talking to
  Mogwin, which ran on the host only (window w1, text 48). Fixed afterwards: the trigger mirror (section 2).
- **The bridge pilot (502), by accident the first choice test.** Each player talked to the pilot on their own: the
  guest's window opened 5.7 s after the host's (+5658 ms on the scene clock). The host's input was applied to it as
  soon as it opened (choice 2, 100 ms later). But **the guest's next window diverged** (host text 360, guest text 349):
  its script went to Lindblum (614) while the host's went to the Ice Cavern (300). The guest was then held on 614 for
  the 20 s timeout and used Join host.
- **Why it diverged: live flags.** The host's choice set the story counter (3150). The live-flags relay sent it to the
  guest at 22:47:28, before the guest's own pilot window opened (22:47:31). The guest's script then ran its choice
  with the new counter, and the same choice number led elsewhere. Fixed afterwards: a follower holds live flags until
  the scene ends (`GuestGuard.HoldLiveFlags`), and window OPEN lines now log the choice mask and default.
- **What this shows for the question:** applying the owner's choice to the follower's window works mechanically, but
  it is only right if both PCs' scripts read the same state at that moment. Anything that changes a follower's state
  from outside its own scripts during a scene (live flags, a resync) breaks it.
- One log defect fixed: DIVERGED was logged on every frame while waiting (17 lines).

### Run 2 (2026-11-19 22:59-23:02, build `24BC10E56C95`)

Host #6647 (Clove), guest #6374 (Blank). The festival from Mogwin (507) to battle 31 on Church Street (556). Logs:
`Output\logs\*-SpikeE*-2302*` / `*-SpikeE2-20261119-2303*`.

**The cutscenes stayed in step.** Guest's side (the owner's times come in its messages; scene clock = ms since the
field's Go):

| Field | Windows | Follower close after owner's | Notes |
|---|---|---|---|
| 507 (Mogwin) | 4, 3 with choices | +26 to +33 ms | two talks mirrored (started 0 ms after arrival, scene clock +32 ms); choices 0, 4, 4 applied |
| 574 | 8 | +51 to +69 ms | 2 closed by script on both PCs (+51, +53), 1 by the field change |
| 576 | 5 | +6 to +8 ms | |
| 575 | 9 | +13 to +27 ms | 1 closed by script on both |
| 556 | 1 | +24 ms | |

- **27 windows, no divergence, no follower stuck on input.** RTT 6-14 ms; every owner input was applied 0 ms after it
  arrived (the follower's window was always already open).
- **Opens agree too:** follower windows opened -45 to +88 ms from the owner's: the scripts run in step between
  windows, not only at closes.
- **Field jumps:** 507→574→576→575→556 came 7-284 ms apart (the larger ones when a window closed by input preceded
  the jump); each field's gate held 17-51 ms.
- **Pass criteria:** 10+ windows within ~1 RTT + 1 frame (here up to 69 ms at RTT 7 + 33 ms frame: 2 frames at
  worst): yes. Choice made on the owner applied on the follower: yes (3). No follower stuck: yes.

**Where it fell apart: battle 31.** Both scripts reached the battle together, the guest's 4 ms first on the scene
clock. The guest's BattleEntry started its own battle as owner and sent Start; it reached the host 1 ms before the
host's own script got there, so the host's BattleEntry declined it ("this PC's own battle started while #6374's was
coming") and ran its own. Two battles 31: the host with Clove, the guest with Clove and Blank. Exactly the design
brief's double battle; the spike had only logged follower battles so far.

Fixed afterwards (build `C90366332894`, not run yet): a follower's scripted battle doesn't start. The follower holds
its scripts at the battle (the battle opcode has run, so after the battle the script carries on from there) and
joins the owner's battle through BattleEntry's participant path when its Start comes; `BattleEntry.CanJoinNow` lets
it join although it's in a scene, only while it waits there. After 10 s without the owner's battle it fights its own
(no stuck PC). If the owner's Start comes first, the follower joins when its own script reaches the battle.

Also seen:
- The guest's script changed the party (slot) during the scene; the host rejected it (`only the host can change:
  slot`, a toast on the guest). A party scene needs the owner's party changes to count, not each PC's.
- On 556 the guest's player entry became Clove (Cast mode), as in the first festival run: bodies are part of the full
  feature (section 1.5 of the brief), not this spike.

### Run 3

*Pending (build `C90366332894`): the festival again, through battle 31 and the scene after it.*

## 5. Verdict

*Pending.*

## 6. What the party-scenes milestone needs

*Pending (after the results).*
