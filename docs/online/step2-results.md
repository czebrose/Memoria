# Step 2: join the host's game — results

Branch `online-step2` (from `online-ghosts`). Code: `Assembly-CSharp/Memoria/Online/` (`Session/` for this step).
M1 (save transfer) details: [step2-m1-save-transfer.md](step2-m1-save-transfer.md).

**Status: Step 2 closed on 2026-11-18 after M5 (all tested on two PCs). M6 (battles) moves into Step 3, M7 (disconnects) into the bug-fix pass after Step 3: [step2-closeout.md](step2-closeout.md).**

## Milestones

| | What | State |
|---|---|---|
| M1 | Save transfer | **Done.** A save loaded from memory gives exactly the state of the game's own Continue; 83 KB blob, 16 KB chunks with CRC; guests' save files untouched (autosave blocked). |
| M2 | Title-screen lobby, character pick, start | **Done** (two PCs, 2026-11-16); New Game party declared by the mod, picks by character (after the test, not run yet). |
| M3 | Each player plays their own character | **Done** (approach D, Player/Cast modes, the scene toolkit; bodies for Clove, Duskly, B.Waltz; Vayle has no field model yet). Tested on one PC and on two PCs (2026-11-17): [step2-m3-own-leader.md](step2-m3-own-leader.md#10-implementation-2026-11-17), [script-toolkit.md](script-toolkit.md) |
| M4 | Host-authoritative party state | **Sync done** (guest requests validated and applied by the host, full-state updates, guest blocks for save/arrange/card discard/order), tested on two PCs (2026-11-18): [step2-m4-party-state.md](step2-m4-party-state.md#7-two-pc-test-of-the-sync-2026-11-18). Control changes (Take/Swap/Release, watching) tested on two PCs (C1, 2026-11-18). |
| M5 | Free roaming, meeting resyncs, guests' changes to the host (revised from "tether") | part 1 (meeting resync keeping the guest's spot, Join host, event-byte log) tested on two PCs (R1, 2026-11-18); parts 2-3 (guests' finds and flags to the host, freeze during the host's scenes) tested (R2): [step2-m5-roaming.md](step2-m5-roaming.md); plan: [step2-m5-m7-plan.md](step2-m5-m7-plan.md) |
| M6 | Host-only battles | moved into Step 3 (battles): [step2-closeout.md](step2-closeout.md#what-step-3-inherits-battles) |
| M7 | Disconnects | moved to the bug-fix pass after Step 3: [step2-closeout.md](step2-closeout.md#deferred-to-the-bug-fix-pass-m7-and-known-bugs) |

## What changed

### M1 (see the M1 doc)
- `Session/GameSnapshot.cs`: capture the running game or a save slot as the game's own JSON save tree (SimpleJSON
  binary), load it through the game's parser and the title screen's enter-game sequence. Guests keep their own options
  and achievement progress.
- `Session/BlobTransfer.cs`: 16 KB reliable chunks (channel 2) with size, index and CRC32; paced 4 per frame per peer.
- `Session/GuestGuard.cs` + one `[Online]` line in `EventEngine`'s autosave: a guest's game never autosaves.

### M2
- `Session/SessionLobby.cs`, ticked by `OnlineSession`:
  - **Host** (Ctrl+Shift+H at the title): F5 cycles the game (New Game, or the saves of the latest save page, newest
    first); the chosen game is put in the host's state at the title to read its party; the party, the game's label and
    the assignments are published as Steam lobby data.
  - **Guests** pick a free party slot (panel buttons or F2-F4, F1 releases) as lobby member data; the **host
    arbitrates**, first come first served; slot 1 is always the host's and slots nobody picks stay the host's.
  - **Start** (F6) is enabled once every member is at the title screen and connected by P2P. Continue: the host sends
    the save's snapshot and everyone loads it from memory (guests as guests). New Game: everyone runs the game's own
    New Game; the host sends a resync snapshot as soon as it has control on the new game's first field.
  - A guest who joins after Start sees "already started" (joining mid-game is out of scope).
  - Debug-styled panel at the top right of the title screen.
- `SteamNative` / `SteamLobby`: `SetLobbyMemberData` / `GetLobbyMemberData` bindings, `SetData` (host).
- Ghosts now take the local leader's model scale (fields that shrink characters, like the cargo ship's engine room,
  only shrink their own; the ghost was drawn bigger).

## Tests so far

| # | Setup | Result |
|---|---|---|
| M1 | single PC: slot → memory → game, mid-game S → R | memory load = the game's own load of the slot; mid-game round trip exact; no guest save written |
| M2a | single PC: lobby panel, game cycling, Start with Continue and with New Game | works; fixed: clipped panel title, New Game resync sent before the new field had loaded |
| M2b | two PCs: Continue, guest picks, Start | both loaded the host's game (tester: "everything seemed fine") |
| M2c | two PCs: New Game, Start | both started; the host's resync (85 KB) went 1 s after Start from field 507 and loaded on the guest in 16 ms; guest's save files unchanged (host's changed: its own New Game autosave, as expected) |
| M3 | two PCs: Continue, guest picks B.Waltz | guest plays B.Waltz on every field (cargo ship, Ice Cavern); host sees B.Waltz's ghost; debug F7/F8 hide a ghost while its character is shown by an entry; logs clean |
| M4 | two PCs: guest equips, Potions both ways and on another player's character, blocks, saves, resyncs | all synced; the last-item race never collided, so its rejection path is untested |
| M4 C1 | two PCs: guest watches, takes, swaps with the host, releases, takes again | all worked; fixed after: a character with no field body (Clove stand-in) hid the host's Clove ghost |

## Known issues / open points

- **New Game party: declared by the mod (decided 2026-11-16).** At the title screen the game only knows the vanilla
  initial party (Zidane); the sequel's first field builds the real one. A mod ships
  `StreamingAssets/Data/Online/NewGameParty.txt` (read through Memoria's asset manager, so it lives in the mod folder):
  one character per line, `characterId[, display name]`, first line = the host's character, `#` comments. Without the
  file the lobby shows the game's initial party. The sequel's (IDs from its CharacterParameters.csv and
  DictionaryPatch.txt): [NewGameParty.example.txt](NewGameParty.example.txt) (2 Clove, 1 Duskly, 3 Vayle, 11 B.Waltz).

- **Picks are by character, not party slot** (lobby data `assign` = `characterId:accountId`), so they survive party
  reordering and the story changing the party.
- **Planned with M3/M4 (decided 2026-11-16): in-game control changes.** Take a free character, Swap with another
  player (they accept), Release yours to the host, Follow the host (release everything and spectate). Host-arbitrated.
  If the story removes your character: first free character, else Follow. In-game debug panel first; a real entry in
  FF9's main menu later.
- **Ghosts show the sender's leader**, which is slot 1 (Clove) on every PC until M3.
- A save made with a temporary character in the party doesn't round-trip exactly even through the game's own load
  (the game's own behaviour); M4's state hash must compare post-load captures.

## Verdict

*Pending M3-M7.*
