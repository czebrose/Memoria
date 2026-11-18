# Step 2 / M5-M7: initial plan (tether and story flags, host-only battles, disconnects)

Written 2026-11-18, before M4's C1 test, as a starting point to review when each milestone begins. Scope and rules come
from the Step 2 brief (decisions: guests are tethered to the host's field, story flags change only on the host,
battles are host-only, game over and host-leave send everyone to the title, a guest who leaves is replaced by the
host's game). Nothing here is built yet; each milestone starts with a short investigation and a report.

What M1-M4 already give us, and the plan leans on:

- **Full resync** (M1 blob, ~85 KB, loads in ~10 ms, waits until the guest is on the field with no menu open). It
  carries everything: field, position, story flags, party. It is the safest tool we have.
- **Party sync** (M4): incremental host → guest updates of the party records, items, gil, cards.
- **Watching** (M4): `GuestGuard.Spectating` blocks movement and interaction on a guest. M5 splits it in two flags.
- Ghosts already hide when the other player is "not on a field" (battle, menu, world map).

---

## M5. Tether and story flags

### Goal
The guest is always on the host's field and sees the host's world state (chests, NPCs, story progress). Guests walk but
don't interact; the host's cutscenes freeze them.

### Plan
1. **Interaction block for every guest** (not only watchers): `GuestGuard.Spectating` becomes two flags,
   `NoInteract` (all guests: `CollisionRequest`, talk/check/chests) and `NoMove` (watchers, and everyone during a host
   cutscene/battle). Region triggers (walking into an exit or an event zone) are separate from `CollisionRequest`:
   investigate where the field runs them (`EventEngine` region checks) and block them on guests too.
2. **Guests can't leave on their own:** block the field-change opcode on guests unless the change comes from the
   tether (investigate the opcode and `FF9StateSystem.Common.FF9.fldMapNo`/entrance writes; bounce = cancel the
   change, the player stays put). Exits are already region triggers, so step 1 may cover most of it.
3. **Tether = a resync per host field change (recommended first version).** When the host gains control on a new
   field (the same signal the New Game resync uses: playable on a field map loaded after the change), it sends the
   M1 blob to every guest; guests load it. Story flags, chests, NPC states and the party all come with it, with no
   flag diffing. Costs: 85 KB per field change (fine on Steam P2P) and the guest appears at the host's entrance.
   - The world map: same rule when the host enters the world map (gMode 3), and back.
   - Fields the host passes through without gaining control (scripted chains) send nothing; the guest catches up at
     the next one.
4. **Host cutscenes:** the host publishes a small state (P2P control message, a few times a second): "playing",
   "cutscene" (user control off on a field), "menu", "battle", "world map". Guests show "Host is in a cutscene..." and
   set `NoMove` while it lasts. Showing the cutscene on guests is out of scope (brief).
5. **Later optimisation, only if the resync per field feels slow:** a light "field change" message (field, entrance,
   the host's event globals `gEventGlobal` + the few other event-state arrays) applied before the guest runs the
   game's own field change. Needs an investigation of which arrays the field scripts read and whether a field loaded
   that way behaves as after a Continue. Not for the first version.

### Hook points to investigate
- Field change: the event opcodes that change field (`Field`/`FieldMap` jumps) and `SceneDirector.Replace("FieldMap")`.
- Region triggers: where `EventEngine` runs an entry's region function when the player walks in.
- Cutscene signal: `EventEngine.GetUserControl()` on the host (already used for the New Game resync).

### Tests (from the brief)
| # | Pass |
|---|---|
| S5 | the host changes field several times; the guest follows each time; the guest can't leave alone; a chest the host opened is open for the guest |
| S5b | guest presses confirm on an NPC/chest: nothing happens; host's cutscene: guest frozen with the message |

### Questions to decide at M5
- Do guests keep their own position when the host's tether resync is for the **same** field (a resync for another
  reason)? The resync puts them at the host's spot today.
- Harmless interactions (NPC talk that changes no flags): the brief asks for a proposal only; collect candidates
  during M5 testing.

---

## M6. Host-only battles

### Goal
Only the host fights, with the whole party, using the normal battle system; guests wait, then see the result.

### Plan
1. **No encounters on guests:** force the built-in "no encounters" booster on guests (investigate the flag; restore
   it when the session ends), plus a guard where the field starts an encounter, in case a booster isn't enough.
2. **No scripted battles on guests:** block the battle-start opcode on guests (with M5's interaction block, guests
   shouldn't reach battle triggers at all; this is the safety net).
3. **Host in battle:** the host's state message (M5 step 4) says "battle"; guests freeze (`NoMove`) with "Host is in
   battle...". The host's ghost already hides.
4. **The host controls every party member in battle**, including guests' characters, with the normal game: nothing
   to build (characters nobody plays are already the host's; in battle all of them are).
5. **After the battle:** the host sends a full resync once it is back on the field (or the world map) with control.
   It carries HP/MP/status, EXP, AP, levels, learned abilities, items, gil, and any flags the battle set. Simple and
   reliable; incremental party updates are already running for anything that arrives earlier.
6. **Game over on the host:** a "game over" control message; every PC goes back to the title, the session returns to
   the lobby (Gathering) so the host can Continue. Investigate the game's own game-over → title path and reuse it.

### Hook points to investigate
- Random encounters: the encounter roll on fields and the world map; the booster flag.
- Battle start: the battle opcode and `SceneDirector` change to the battle scene; the battle end → field return.
- Game over: where the game decides it and goes to the title.

### Tests
| # | Pass |
|---|---|
| S6 | host gets a random encounter and wins (then a scripted battle if handy); the guest is frozen during the battle, then sees the same HP/EXP/items/gil |
| S6b | guest walks a long time on an encounter field: no battle |
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
M5 steps 1-3 (interaction block, exit block, tether by resync) → S5 test → M5 step 4 (host state message, cutscene
freeze) → M6 (reuses the state message and the resync) → M7 (small, mostly the host-left path, which shares the
return-to-title path with game over).
