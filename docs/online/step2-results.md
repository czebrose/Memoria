# Step 2: join the host's game — results

Branch `online-step2` (from `online-ghosts`). Code: `Assembly-CSharp/Memoria/Online/` (`Session/` for this step).
M1 (save transfer) details: [step2-m1-save-transfer.md](step2-m1-save-transfer.md).

**Status: M1 and M2 done and tested on two PCs; M3 built and tested on one PC; M4-M7 not started.**

## Milestones

| | What | State |
|---|---|---|
| M1 | Save transfer | **Done.** A save loaded from memory gives exactly the state of the game's own Continue; 83 KB blob, 16 KB chunks with CRC; guests' save files untouched (autosave blocked). |
| M2 | Title-screen lobby, character pick, start | **Done** (two PCs, 2026-11-16); New Game party declared by the mod, picks by character (after the test, not run yet). |
| M3 | Each player plays their own character | **Built** (approach D + Player/Cast scene modes), tested on one PC with the offline debug body; two-PC test and the sequel's other bodies open: [step2-m3-own-leader.md](step2-m3-own-leader.md#10-implementation-2026-11-17) |
| M4 | Host-authoritative party state | not started |
| M5 | Tether and story flags | not started |
| M6 | Host-only battles | not started |
| M7 | Disconnects | not started |

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

## Known issues / open points

- **New Game party: declared by the mod (decided 2026-11-16).** At the title screen the game only knows the vanilla
  initial party (Zidane); the sequel's first field builds the real one. A mod ships
  `StreamingAssets/Data/Online/NewGameParty.txt` (read through Memoria's asset manager, so it lives in the mod folder):
  one character per line, `characterId[, display name]`, first line = the host's character, `#` comments. Without the
  file the lobby shows the game's initial party. Example for the sequel (IDs to confirm):

      # New Game party for online play: characterId, name. First = the host's (Clove).
      2, Clove
      11, B.Waltz
      10, Blank
      1, Duskly

- **Picks are by character, not party slot** (lobby data `assign` = `characterId:accountId`), so they survive party
  reordering and the story changing the party.
- **Planned with M3/M4 (decided 2026-11-16): in-game control changes.** Take a free character, Swap with another
  player (they accept), Release yours to the host, Follow the host (release everything and spectate). Host-arbitrated.
  If the story removes your character: first free character, else Follow. In-game debug panel first; a real entry in
  FF9's main menu later.
- **Ghosts show the sender's leader**, which is slot 1 (Clove) on every PC until M3.
- A save made with a temporary character in the party doesn't round-trip exactly even through the game's own load
  (the game's own behaviour); M4's state hash must compare post-load captures.
- Not blocked yet on guests (M4): the save menu and Moogle saves. Don't save on a guest PC.

## Verdict

*Pending M3-M7.*
