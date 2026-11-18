# Step 2 / M5-M7: initial plan (free roaming and guest changes, host-only battles, disconnects)

Written 2026-11-18, before M4's C1 test, as a starting point to review when each milestone begins. Scope and rules come
from the Step 2 brief (decisions: battles are host-only, game over and host-leave send everyone to the title, a guest who leaves is replaced by the
host's game). The brief's tether (guests locked to the host's field, flags only from the host) was replaced on
2026-11-18 by free roaming, with guests' changes sent to the host (M5). Nothing here is built yet; each milestone
starts with a short investigation and a report.

What M1-M4 already give us, and the plan leans on:

- **Full resync** (M1 blob, ~85 KB, loads in ~10 ms, waits until the guest is on the field with no menu open). It
  carries everything: field, position, story flags, party. It is the safest tool we have.
- **Party sync** (M4): incremental host → guest updates of the party records, items, gil, cards.
- **Watching** (M4): `GuestGuard.Spectating` blocks movement and interaction on a guest. M5 adds a cutscene freeze
  (no movement) for guests on the host's field.
- Each ghost state message carries its sender's field id: the guest knows when it shares the host's field.
- Ghosts already hide when the other player is "not on a field" (battle, menu, world map).

---

## M5. Free roaming, meeting resyncs and guest changes

### Goal (revised 2026-11-18)
Everyone wanders where they like. A guest who walks off runs that field themselves; nothing locks them to the host.
When a guest and the host are on the same field, the guest catches up with the host's world. What guests find and
change (items, chests, flags) is sent back to the host, so it isn't lost and the host's save stays the one true
world. Story progress isn't blocked: for now we trust players not to trigger story events early (see "Story
progress" below).

This replaces the first draft (tether, exit blocks, an interaction block for every guest).

### Plan
1. **Resync when players meet.** The guest knows its own field and the host's field: the field id is already in
   every ghost state message. The guest asks the host for a resync when:
   - the guest arrives on the field the host is on, or
   - the host arrives on the guest's field.

   The host sends the M1 blob, and the guest loads it **keeping its own position**. Today the resync puts the guest
   at the host's spot, so this needs a "keep my spot" load option (same field, so the position is valid). The world
   map counts as a field for this rule.
   - **Don't resync too often:** wait until the guest's field is on screen with control; one request per meeting;
     skip it if the guest just resynced on this field.
   - **The blink:** walking into the host's field loads it once with the guest's flags, then reloads with the host's.
     That's acceptable for the first version. Later, the host's flags could go in before the field loads, so there's
     only one load.
2. **"Join host" key** (Ctrl+Shift+J, plus a panel button): a resync that does move the guest to the host (the old
   behaviour). It's the way home after a guest gets lost or stuck.
3. **Tier 1: guests' finds go to the host.** A guest who gains items, gil or cards from a field script (chest, NPC
   gift, shop) sends a "found" request through M4's request path. The host checks it, applies it, and the normal
   party update reaches everyone. On the guest, the local change is kept until the host's update replaces it, so
   nothing flickers.
   - Investigate: the script opcodes and functions that add items, gil and cards (M4 already hooks the menu ones),
     so a find is told apart from a menu action or a sync.
   - Spending (shops, using items) already goes through M4.
4. **Tier 2: guests' flags go to the host.** On a guest, the event-state arrays (`gEventGlobal` and whatever else
   the field scripts write; to be investigated) are snapshotted at each field load and after each resync. When bytes
   change, the guest sends them (index and new value) to the host a few times a second. The host applies them, and
   they come back to every guest at their next meeting resync.
   - **Conflicts:** last writer wins (rare: two players changing one byte).
   - **Excluded:** bytes our own code writes (the Online script dictionary, body binds), so the mod's own bookkeeping
     isn't sent around. Collect the list during testing.
   - **First step:** log every byte a guest changes, with the field and the script entry if possible. That log tells
     us which bytes are chests, flags and story.
   - **Option, later:** the host relays accepted changes to other guests at once, instead of at their next meeting
     (matters with 3+ players).
5. **Host cutscenes on a shared field.** The host publishes a small state (playing / cutscene / menu / battle /
   world map) a few times a second. A guest **on the host's field** freezes with "Host is in a cutscene..." while it
   lasts. Guests elsewhere aren't affected. The same message serves M6.

### Story progress (not blocked, not resolved)
A guest can trigger story events alone. With tier 2, the scenario counter and story flags they set will reach the
host's game. For now we trust players not to trigger story events early. Things to watch during testing, to decide
later:
- whether changing the scenario counter under the host's running field breaks the host's scene;
- whether to exclude the scenario counter (or flags set during a solo cutscene) from tier 2;
- whether any script needs a guard.

### Hook points to investigate
- Item, gil and card gains from scripts: the event opcodes that add them.
- Event state: which arrays the field scripts write (`FF9StateSystem.EventState` and others), and a cheap way to
  snapshot and compare them.
- Resync loading: where the M1 load places the player, to add "keep my spot".
- Host state: `EventEngine.GetUserControl()` on the host (already used for the New Game resync), menus, battle and
  world map modes.

### Tests
| # | Pass |
|---|---|
| S5 | the guest walks off to other fields alone and back; the host changes fields; a resync happens each time they meet, and the guest keeps its spot |
| S5b | the guest opens a chest alone: the item reaches the host's inventory; at the next meeting the chest is open on every PC |
| S5c | the guest talks to an NPC that sets a flag alone; the host sees the result on that field (after visiting it) |
| S5d | host's cutscene on a shared field: guest frozen with the message; a guest on another field isn't |
| S5e | Join host: the guest appears next to the host from any field |

### Questions to decide at M5
- When the guest arrives on the host's field, should the resync wait until the guest is on screen, or happen during
  the field load (the blink)?
- Harmless interactions are no longer an issue (guests may interact).

---

## M6. Host-only battles

### Goal
Only the host fights, with the whole party, using the normal battle system; guests wait, then see the result.

### Plan
1. **No encounters on guests:** force the built-in "no encounters" booster on guests (investigate the flag; restore
   it when the session ends), plus a guard where the field starts an encounter, in case a booster isn't enough.
2. **No scripted battles on guests:** guests roam freely (M5), so they can reach battle triggers. Block the
   battle-start opcode on guests (the trigger's script stops there; log it). To decide at M6: whether a blocked
   story battle leaves a field stuck, and whether "Join host" is enough to get out.
3. **Host in battle:** the host's state message (M5 step 5) says "battle". Guests **on the host's field** freeze with
   "Host is in battle..."; guests elsewhere keep roaming. The host's ghost already hides.
4. **The host controls every party member in battle**, including guests' characters, with the normal game: nothing
   to build (characters nobody plays are already the host's; in battle all of them are).
5. **After the battle:** guests on the host's field get a meeting resync once the host is back with control (the
   same rule as M5: the host "arrives" again). It carries HP/MP/status, EXP, AP, levels, learned abilities, items,
   gil, and any flags the battle set. Guests elsewhere get the party through M4's updates at once, and the flags at
   their next meeting. Check that M4's updates carry EXP, AP, levels and abilities; the resync covers them otherwise.
6. **Game over on the host:** a "game over" control message; every PC goes back to the title, the session returns to
   the lobby (Gathering) so the host can Continue. Investigate the game's own game-over → title path and reuse it.

### Hook points to investigate
- Random encounters: the encounter roll on fields and the world map; the booster flag.
- Battle start: the battle opcode and `SceneDirector` change to the battle scene; the battle end → field return.
- Game over: where the game decides it and goes to the title.

### Tests
| # | Pass |
|---|---|
| S6 | host gets a random encounter and wins (then a scripted battle if handy); a guest on the same field is frozen during the battle, then sees the same HP/EXP/items/gil |
| S6b | guest walks a long time on an encounter field: no battle; a guest on another field keeps roaming while the host fights |
| S10 | game over (force with low HP): both PCs at the title / lobby |

---

## M7. Disconnects

### Goal
A guest leaving never stops the host; the host leaving ends the session cleanly for everyone.

### Plan
1. **Guest leaves or drops:** M4 already frees their character when they leave the lobby (the host's game drives it;
   in the field it just stays in the party). Add: a P2P silence timeout (no pong for 10 s) counts as a drop even if
   Steam still lists the member; remove their ghost; toast "X left" on the host.
2. **Host leaves or drops:** guests watch for the lobby owner changing (we never follow Steam's owner migration) or
   the host silent > 10 s. Then: "Host left" message, leave the lobby, go back to the title (same path as game
   over). Guests' save files were never written, so nothing to undo.
3. **A guest reconnecting / joining mid-game** stays out of scope for Step 2 (the lobby says "already started").

### Tests
| # | Pass |
|---|---|
| S8 | guest quits mid-session; host keeps playing with that character |
| S9 | host quits mid-session; guest returns to the title with a message |
| S9b | pull the network on one PC (or kill the game): the other side reacts within ~10 s |

---

## Open items carried over (M3/M4)
- Vayle's field body (no model yet); a body switch fade; ghosts playing more than stand/walk/run.
- Walkmesh NREs seen once with B.Waltz.
- M4's last-item race was never forced to collide (the rejection path is untested); a debug key could force it.

## Suggested order
M5 steps 1-2 (meeting resync keeping the guest's spot, Join host) → S5/S5e test → step 4's logging of guests'
changed bytes (play a while, read the log) → steps 3-4 (finds and flags to the host) → S5b/S5c → step 5 (host state
message, cutscene freeze) → M6 (reuses the state message and the resync) → M7 (small, mostly the host-left path, which shares the
return-to-title path with game over).
