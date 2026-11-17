# Step 2 / M3: each player plays their own character — approaches

**Status: investigation, no code changes. For deciding together.** Replaces yesterday's first draft, which jumped
to a cosmetic re-skin. This version starts from how the engine and the sequel's scripts actually work (measured, not
guessed), lists what the solution has to hold up to in later steps, then compares approaches.

## 1. How the engine decides who the player is

| Piece | Where | What it does |
|---|---|---|
| Character entries | each field script; `ObjTable.player_link` (`Global/Objects/ObjTable.cs`) | Entries tied to a playable character. **Vanilla format: the last 9 entries of every script are the character entries** (Zidane..Beatrix by position; may be empty). Memoria's format stores `player_link` explicitly per entry. |
| Team slots 251-254 | `EventEngine.SetupPartyUID` (`EventEngine.cs`) | "Party member 1-4" → character entries, in a **fixed priority order** (Zidane, Eiko, Steiner, Vivi, Freya, others, Quina, Amarant, Garnet, Beatrix), falling back to another character's entry if the linked one is empty (`EventEngineUtils.GetEventCharacterSIdHacked`). Recomputed on field init and by `RemoveParty`/`UpdatePartyUID`. This is how vanilla changes the leader (Dagger, Steiner, Vivi segments). |
| The player | `DefinePlayerCharacter` (opcode `0x2C`, `EventEngine.DoEventCode.cs`) | Run in an entry's own code: `controlUID` = that entry's object = object 250, "the player". |
| Field player object | `HonoluluFieldMain.updatePlayerObj` | Whatever object 250 resolves to becomes the field's player (camera, input, `isPlayer`). |
| Body | `SetModel` (`0x2F`), `SetStandAnimation`/`SetWalkAnimation`/`SetRunAnimation` (`0x33`-`0x35`), `SetLeftAnimation`/`SetRightAnimation` (`0x7A`/`0x7B`), `SetJumpAnimation` (`0x94`), `SetInactiveAnimation` (`0x52`), `RunAnimation` (`0x40`), `RunAnimationEx` (`0xBD`, on another object) | `SetModel` builds the model **once, at object creation** (no "change model" path). Animation ids are per model. All arguments can be **expressions** (constants or variables), not just constants. |
| Script-visible identity | system variables, `EventEngine.GetSysvar.cs` | e.g. 5 `GetScriptCharacter` = an entry's `player_link`. Memoria already adds its own (191 = current field id). |

Outside fields: the world map uses the same script mechanism (`gMode 3`, `DefinePlayerCharacter`). The only code that
treats "party member 0" as the leader is in the item menu (`ItemUI.cs:316`). Battles: host-only in this step.

## 2. How the sequel's fields do it (measured)

`tools/online/scan_field_scripts.py` decodes field scripts with the opcode tables read from Memoria's own parser
(`EventEngineUtils.BinaryScript`). On the mod's 47 field scripts (`HadesWorkshopMod/.../eventbinary/field/us`):

| Finding | Numbers |
|---|---|
| **No field uses the team slots.** Every object is spawned explicitly by entry. | 522 explicit `InitObject`, 0 via 251-254 |
| **Clove is drawn on whichever character entry the field uses as the player**, not on "Clove's" entry: Zidane's entry in the Lindblum/cargo fields, Vivi's in the Alexandria ones. Clove's party identity is Garnet's id (2). | Zidane entry calls `DefinePlayerCharacter` in 31 fields, Vivi's in 16; model 202, idle/walk/run 2670/2655/2672 |
| Other character entries are mostly **empty**; some are populated (companions or leader switches) with their own bodies. | populated: Garnet 5, Steiner 6, Freya 3, Vivi (vanilla model 8) 6 |
| **Leader switches exist**: fields where a second entry also calls `DefinePlayerCharacter`. | Lindblum baz, wpn (Steiner), hig (Freya), itm (Vivi), cag (Garnet only), Alexandria pub (a non-character entry) |
| **The player plays many special animations** (beyond stand/walk/run/turn). | Clove: 30 distinct ids, 104 uses, in 35 of 47 fields; custom ids 6030x mostly (60305/60306: 27 uses each); jump/land/sleep via `0x94`/`0x52`; 7 more ids played on the player by other entries |
| Party changes | `RemoveParty`/`Party`/`SetPartyReserve`/`UpdatePartyUID` in 10 fields |

Consequence: vanilla's own leader mechanism (team slots + per-character entries) **isn't how the sequel is built**,
so "make the guest's character entry the player" has nothing to work with in the sequel.

## 3. What the solution has to hold up to

| Need | Now (step 2) | Later |
|---|---|---|
| Each player sees themselves as their character | walking around with the host | always |
| Correct special animations (6030x poses, jumps, ladders) | ladders/jumps while following | step 4: alone in a field, everything |
| **Same script logic on every PC** (object 250 is the same entry everywhere) | host authority, story only on the host | steps 3-5: syncing script state needs both sides running the same entries |
| Scripts can tell *which* character the local player is (dialogue, character-specific reactions) | not needed (guests can't interact) | step 4/5 |
| Party changes, leader switches | rare (host-only story) | common |
| Authoring cost for the sequel | | every new field |

## 4. Approaches

### A. Vanilla leader mechanism, per PC
On each PC, put that player's character first in `SetupPartyUID`, so team-slot spawns create their character's entry.
- Native for **vanilla** content that spawns the leader via slot 251 and has entries for every character.
- **Doesn't work for the sequel** (0 team-slot spawns, empty character entries) and makes object 250 a different
  entry on each PC (breaks "same script logic").
- Verdict: not the foundation. Possibly a vanilla-compatibility mode later.

### B. "Player body" as data, read by the scripts (content convention)
The engine exposes, per PC, the local player's character and its **body**: model, head height, and a set of
**named animation slots** (stand, walk, run, turn left/right, jump, land, plus the sequel's own: the 6030x actions).
New system variables (like Memoria's 191) let the player entry write `SetModel(PlayerModel, PlayerHeadHeight)`,
`SetStandAnimation(PlayerAnim(Stand))`, `RunAnimation(PlayerAnim(Surprised))`.
- Fully native: the object is born with the right body, every animation is the right one, the **script logic is the
  same entry on every PC**, and scripts can branch on the local character for dialogue or reactions.
- Cost: an authoring convention for the sequel's player entries (47 fields now). The rewrite is mechanical:
  constants → variables in the player entry. It could be done by a batch tool (the scanner already finds every
  site), or by Memoria patching the opcodes at script load (see C).
- Needs a body definition per playable character, including equivalents of every special animation Clove uses.

### C. Body substitution by the engine (no content change)
At field load, find the player entry statically (the entry whose code calls `DefinePlayerCharacter`; the scanner
proves it's cheap). When that entry runs body opcodes, the engine **substitutes the local player's body**: `SetModel`
gets the local character's model at creation (not a swap later), the stand/walk/run/turn/jump opcodes get its
animations, and `RunAnimation` / `RunAnimationEx` on the player go through an **animation map**
(Clove's id → slot → local character's id; unmapped → stand).
- Works with existing and future content as is; script logic stays the same entry everywhere.
- Same body data as B (per character, with the special-animation equivalents), plus the map from Clove's ids.
- Leader switches: the substitution follows whichever entry is currently the player; if that entry was created with
  another body, it's rebuilt at the switch (the only place a live swap is needed).
- Scripts can't (yet) ask which character the local player is; dialogue would still assume Clove.

### D. C now, B's variables available (recommended)
Build C as the engine behaviour, so everything works with today's scripts, **and** expose B's system variables, so the
sequel can switch its player entries to the convention where it wants exact control (special scenes, dialogue that
names or reacts to the local character). Both use the **same body data**, so nothing is thrown away. Over time the
sequel can move to B field by field; C remains the safety net for anything not converted.

### Rejected
- **Re-skin after creation** (the first draft): same visible result as C but swaps a live object and leaves every
  special animation wrong. C does it at the right layer.
- **Separate avatar + invisible script player** (guest drives an avatar, the real player object follows it): two
  objects for one player; triggers, collisions and camera have to be kept in step; high risk, no gain over C.

## 5. Common to every approach

- **Body data per playable character** (the core asset): model, head height, size, stand/walk/run/turn/jump/land, and
  equivalents for the special actions. Vanilla characters can be read from vanilla scripts; the sequel's from its
  own (the scanner can list every body the scripts use). Stored as a mod data file
  (`StreamingAssets/Data/Online/CharacterBodies.txt` or similar), like the New Game party.
- **Identity in text and logic**: `GetScriptCharacter` returns the entry's `player_link` (Zidane or Vivi for Clove's
  entry), not the party character. Dialogue that names the player uses party data. Needs a "local player character"
  system variable (B/D).
- **Party changes and leader switches**: tie the per-PC character to the in-game control system decided yesterday
  (Take / Swap / Release / Follow). When the story removes a guest's character: first free character, else Follow.
- **Follow (spectator)**: a guest with no character has no body; they follow the host's camera and the host's ghost.
- **World map**: same mechanism as fields; check its player entries with the scanner once world map scripts are in
  scope.

## 6. Open questions for you

1. **B, C or D?** I recommend D.
2. **Body data**: does each sequel character have (or will have) its own versions of Clove's special animations
   (the 6030x set)? If not, which ones matter (the rest fall back to stand)?
3. **Sequel convention**: are you open to the player entries in new fields using variables (B) as a house rule for
   the sequel? It removes the animation map for those fields.
4. **Vanilla support**: is vanilla FF9 co-op a goal, or only the sequel? (Decides whether A is worth a compatibility
   mode.)

## Appendix: running the scanner

```
python tools/online/scan_field_scripts.py Assembly-CSharp/Global/Event/Engine/EventEngineUtils.cs "<mod>/StreamingAssets/assets/resources/commonasset/eventengine/eventbinary/field/us" --json out.json
```
Per field: character entries (link, size, `SetModel`, stand/walk/run), which entries call `DefinePlayerCharacter`,
team-slot vs explicit `InitObject` counts, party opcodes.
