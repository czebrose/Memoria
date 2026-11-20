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
- **Field jumps and battles** a script starts (`Field`, `Battle`, world map) are logged on both PCs with the scene
  clock and the window count, and the owner's are sent, so a follower's log shows how far its script was ahead or
  behind. Nothing is blocked: two battles can still start (the battle owner rules decide, as before).
- **No meeting resync** (whole-game reload) during a scene; the host still publishes its state.
- **Logs** (`Memoria.log`, all lines start with `[Online] Scene`): `OPEN`, `PAGE`, `CLOSE` with who closed it (player
  input / owner / timer / script), `applied owner's input ... N ms after it arrived`, `TIMING wN` (follower: open and
  close times, owner vs here), `HOLD` / `Go` / `RUN`, `script field jump / battle`, and an `END` summary with drift
  stats. The status box (Ctrl+Shift+O) has a "Party scene" line; a follower sees "the owner's player advances the
  text".

Control messages (reliable): 40 Start, 41 Loaded, 42 Go, 43 Window, 44 Jump, 45 End.

## 3. Two-PC test

Build `11E582A4A49E`. PC2: `Output\OnlineSpikeE-PC2.zip` with `INSTALL-FOR-AGENT.md`.

1. Both PCs: Continue the mod's save on the cargo ship (field 507), host + guest in a session, both on 507.
2. **Host** presses Ctrl+Shift+2 (status box: "Party scene ... owner"; guest: "following").
3. Play the festival scene as in the earlier run (507 → 574 → 576 → 575 → 556, up to and including battle 31). Only
   the host presses confirm / picks choices; the guest presses confirm now and then to check it's ignored.
4. After the battle (or when the scene is over), the host presses Ctrl+Shift+2 again to end it.
5. A choice window, if the festival had none: any NPC or Moogle whose first box asks a question. Both PCs on its
   field; the **guest** starts a scene this time (Ctrl+Shift+2 on the guest). Each player talks to it (each PC's own
   talk opens its window); the guest picks an option that doesn't open a menu (e.g. "Cancel"); the host's box should
   take the same option. The guest ends the scene with Ctrl+Shift+2.
6. Send both PCs' `Memoria.log` and `x64\FF9_Data\output_log.txt`.

## 4. Results

*Pending the two-PC run.*

## 5. Verdict

*Pending.*

## 6. What the party-scenes milestone needs

*Pending (after the results).*
