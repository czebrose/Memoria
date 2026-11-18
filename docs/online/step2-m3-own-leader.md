# Step 2 / M3: each player plays their own character

**Status: approach D decided (2026-11-17) and built; tested on one PC (offline debug), not yet on two PCs.**
Sections 1-9 are the investigation and the design; section 10 is what was built and how it was tested.

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

- **Body data per playable character** (the core asset; list in section 7): model, head height, size, stand/walk/run/turn/jump/land, and
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

## 6. Questions and answers

1. **B, C or D?** I recommend D. *Open.*
2. **Body data**: does each sequel character have its own versions of Clove's special animations?
   *Answer (2026-11-17): "If we had a list of animations that every character needed to have, we could do that."*
   The list is in section 7.
3. **Sequel convention**: would the sequel's player entries use variables (B) as a house rule?
   *Answer: open to it, but wants to understand it better.* Explained in section 8.
4. **Vanilla support**: *Answer: vanilla FF9 co-op would be great, but can be cut if it overcomplicates things.*
   So A isn't built now. The body data format covers vanilla characters too (their actions already exist under
   vanilla ids), so vanilla stays possible later at little cost.

## 7. The actions every playable character needs

Measured with `tools/online/list_player_animations.py`: every animation the 47 field scripts play on Clove's player
body (the body's own opcodes, plus `RunAnimationEx` from other entries aimed at the player).

| Action | Clove's animation (id) | Used | Notes |
|---|---|---|---|
| Stand | `F4_GRN_IDLE` (2670) | every field | |
| Walk | `F4_GRN_WALK` (2655) | every field | |
| Run | `F4_GRN_RUN` (2672) | every field | |
| Turn left | `F4_GRN_TURN_L` (2667) | every field | |
| Turn right | `F4_GRN_TURN_R` (2661) | every field | |
| Kneel down | `F3_GRN_HIZA_1` (60305) | 22 fields | examine/pick up: down, hold, up |
| Kneeling (hold) | `F3_GRN_HIZA_2` (60309) | 22 fields | set as the stand animation while kneeling |
| Stand up from kneeling | `F3_GRN_HIZA_3` (60306) | 22 fields | |
| Open door | `F3_GRN_OPEN_DOOR1` (60304) | 7 fields | |
| Ladder: get on | `F3_GRN_LADDER_1_1` (60301) | 4 fields | |
| Ladder: climb (loop) | `F3_GRN_LADDER_1_2` (60302) | 4 fields | |
| Ladder: get off at the top | `F3_GRN_LADDER_1_3` (60303) | 4 fields | |
| Ladder: get on going down | `F3_GRN_LADDER_2_1` (60307) | 4 fields | |
| Ladder: get off at the bottom | `F3_GRN_LADDER_2_3` (60308) | 4 fields | |

Those 14 cover nearly all of it. Used only in one or two fields, so per-scene rather than core (falling back to
Stand is acceptable at first): battle stance `KAMAE_1`/`KAMAE_2` (60310/60311), pleading `F4_GRN_ONEGAI_1/8/9`
(2646, 2664, 2668), in the cage `IN_CAGE1`/`IN_CAGE2` (60313/60314).

Also found: Clove's entry contains vanilla **Zidane** animations (door, peek, think, disappointed, ladder, kneel) and
Blank's sword set in 1-4 fields each. They're probably branches kept from the vanilla scripts those fields were built
from; worth checking whether they can run, since on Clove's model they'd play nothing useful.

Vanilla characters already have all 14 (Zidane: `ZDN_HIZA_1-3`, `ZDN_OPEN_DOOR1`, `ZDN_LADDER_*`, and so on), so
their body entries just point at existing ids.

Proposed data file (one per mod, read like `NewGameParty.txt`), `StreamingAssets/Data/Online/CharacterBodies.txt`:

    # character id: model, head height, then action = animation id
    2  Clove:  model 202, head 91, Stand 2670, Walk 2655, Run 2672, TurnLeft 2667, TurnRight 2661,
               KneelDown 60305, Kneeling 60309, KneelUp 60306, OpenDoor 60304,
               LadderOn 60301, LadderClimb 60302, LadderOffTop 60303, LadderOnDown 60307, LadderOffBottom 60308
    11 B.Waltz: model ..., Stand ..., ...

## 8. The convention (B), explained

**What a player entry looks like today.** Clove's identity is written into the field script as constants, for
example the kneel sequence used in 22 fields (Hades Workshop-style):

    SetModel( 202, 72 )                 // created as Clove
    SetStandAnimation( 2670 )
    ...
    RunAnimation( 60305 )               // kneel down
    WaitAnimation
    SetStandAnimation( 60309 )          // stay kneeling
    ...
    RunAnimation( 60306 )               // stand up
    SetStandAnimation( 2670 )

**Option C (engine only)** leaves this as it is. While running the player's entry on a guest's PC, the engine
sees "play 60305", looks it up (60305 = KneelDown for Clove), and plays the local character's KneelDown instead.
That needs one table, Clove's ids → action names, and it must be updated whenever the mod gives Clove a new
animation. An id missing from the table plays Stand.

**Option B (the convention)** writes the action instead of Clove's id:

    SetModel( PlayerModel, PlayerHeadHeight )
    SetStandAnimation( PlayerAnim_Stand )
    ...
    RunAnimation( PlayerAnim_KneelDown )
    WaitAnimation
    SetStandAnimation( PlayerAnim_Kneeling )
    ...
    RunAnimation( PlayerAnim_KneelUp )
    SetStandAnimation( PlayerAnim_Stand )

`PlayerModel`, `PlayerAnim_KneelDown` and so on are values the engine fills in **on each PC** from
`CharacterBodies.txt`: on the host they give Clove's ids, on a guest playing B.Waltz they give his. In single-player
(no online session) they give the party leader's body, so the field still works offline as it does today. The
script logic is identical on every PC; only the body differs.

What it gives over C:
- **No lookup table to maintain**: the script names the action, so a new Clove animation can't silently fall back
  to Stand online.
- **Scripts can know who's playing**: a `PlayerCharacter` value lets a field branch (`if PlayerCharacter == Blank`
  → his line, his reaction). With C the scripts always believe they're Clove.
- **Clear intent** in the scripts for anyone reading them later.

What it costs:
- Writing `PlayerAnim_X` instead of a number in player entries. Existing fields can be converted by a tool (the
  scanner already finds every site), so the cost is mostly a habit for new fields.
- Adding a new action means adding it to the list (and giving every character one).
- Under D, unconverted fields still go through C's table, so converting is never required, only better.

## 9. Player-focused scenes vs. cast scenes

How the 47 fields address the player body (from other entries and from itself; scratch scan, 2026-11-17):

| Addressed as | Uses | Fields | Mostly |
|---|---|---|---|
| object 250, "the player" | 358 | 45 | NPCs turning to face the player (207), running the player's functions (151) |
| Clove's entry by its UID | 49 | 16 | running its functions (scene choreography) |
| itself | 51 | 17 | `SetObjectSize` |
| team slots 251-254 | 0 | 0 | |

So the scripts mostly talk to "the player", but in the sequel **the player and Clove are the same object**, and a
script has no way to say which one it means. That's the gap behind two kinds of scene:

- **Player scenes** (exploring, examining, NPCs talking to you): each PC shows its own character on the player entry.
- **Cast scenes** (several characters acting a story beat): every PC shows the same bodies: Clove's entry is Clove,
  B.Waltz's entry is B.Waltz, whoever controls whom.

Proposal:
- Two modes, **Player** (default) and **Cast**. Switching rebuilds the player entry's body (local character ↔ its
  authored body), the one place a body changes on a live object, done while control is off, ideally under a fade.
- **Player mode, duplicates:** if the local character also has its own entry on screen (14 fields have companions),
  that entry gets the player entry's authored body (Clove) on that PC: the two bodies trade places, so nobody
  appears twice or goes missing.
- **Cast mode, ghosts:** a ghost whose character is on screen as a cast member is hidden.
- **Who decides:** with the convention, the script says it (`SetSceneMode(Cast)` / `SetSceneMode(Player)`, a new
  Memoria opcode). Without it, a fallback guess: Cast while control is off and another entry is running Clove's
  entry's functions by UID (the 49 uses above are where to look first). The guess can be wrong; the marker is exact.

Technical note: how the values are encoded (reserved system-variable numbers like Memoria's 191, or a small new
Memoria opcode) is decided when building it; either way Hades Workshop shows a number/name in the expression.

## 10. Implementation (2026-11-17)

Decided: **D** (engine body swap now, the convention available) with **Player / Cast scene modes**.

Code (`Assembly-CSharp/Memoria/Online/Bodies/`):
- `CharacterBodies`: built-in vanilla bodies (locomotion only) + the mod's `StreamingAssets/Data/Online/CharacterBodies.txt`
  (template: [CharacterBodies.example.txt](CharacterBodies.example.txt); format: `id Name: Model m, Head h, Action anim, ...`).
- `BodySwap`: roles (lead = the host's character, local = this PC's, controlled = every player's), creation-time swap
  of entries authored with the lead's body, animation translation by action, hiding entries of characters players
  control (Player mode), scene modes, the script dictionary, ghost hiding in Cast mode.
- `BodyRebuild`: replaces a live object's body (mode switches): model as SetModel builds it, same UID and name,
  controller state (pathing, speed, floor, walk in progress, exact position), attachment (AttachObject) and its
  restore position, objects carried on its bones, lighting re-applied, animations translated; the old body is
  disposed of by the behaviour system (rule 6).

Engine hooks (`[Online]`): `SetModel` (before/after building), the 9 animation opcodes, `HideObject`/`ShowObject`,
`RunScript*` (+ `GetObj1` records the raw object argument), `EnableMove`, and `JsonParser` (dictionary 9900 is never
saved). `OnlineSession` sets the roles every frame from the lobby (`HostCharacter`, `MyCharacter`, assignments).

Script dictionary **9900** (read in expressions, written by the script):

| Key | Value |
|---|---|
| 0 | 1 while an online session is playing |
| 1 | this PC's player character (offline: the party leader) |
| 2, 3 | its model and head height (`SetModel( dict[9900][2], dict[9900][3] )`) |
| 10-23 | its animation for each required action, in the order of section 7 (10 Stand ... 23 LadderOffBottom) |
| 100 | scene mode asked by the script: 0 automatic, 1 Player, 2 Cast (reset to 0 on each field) |

A `SetModel` whose model is an expression is the script's own choice: never swapped or hidden.

Automatic mode: Cast when, with control off, another entry runs a function of the swapped entry by its UID (not 250);
Player again on `EnableMove`. A switch waits until the field is on screen (no menu, no pause).

Debug (`[Debug] Enabled = 1`, `OnlineDebug = 1`): **Ctrl+Shift+C** pretend to be a guest playing the next character
(reloads the field), **Ctrl+Shift+F7** Player / Cast.

Tests (single PC, offline debug, cargo ship and Lindblum):

| Test | Result |
|---|---|
| Swap at creation, every vanilla body | each field's Clove entry created as the chosen body; Zidane kneels with his own animation (translated) |
| Player and Cast by hand, repeatedly | one body, animating, moving, lit correctly (after the fixes below) |
| Automatic Cast: Lindblum tram (`lb_ctm` to `lb_stn`) | Clove rides and walks off at the right height; Zidane is back on the platform with control |

Fixed along the way: the old body not disposed of and every character freezing (shadow destroyed too early, then
`SetActive(false)`: rule 6), lighting only applied on a floor change, the player's object renamed (it's found by
name "Player"), Clove's hidden meshes hiding Zidane's head (mesh numbers are per model), the dictionary's Cast request
coming back from a save, the rebuilt body losing pathing (walking off the tram) and its height.

Open:
- **Two-PC test** (a guest playing a character other than the host's).
- **Bodies for the sequel's other characters** in `CharacterBodies.txt` (only Clove and a Zidane test body now), and
  the character ids of the sequel's party (see `NewGameParty.txt`).
- A switch is visible (a body pops); a short fade would hide it.
- A hidden entry still collides; its meshes all reappear when shown again.
- The automatic guess only knows "run a function by UID": scenes that direct the player only through 250 stay in
  Player mode unless the script asks (key 100).
- Model 9004 (`GEO_MAIN_F0_904`, used by the tram station's Clove entry in one branch) isn't a known body: that
  branch isn't swapped.
- Take / Swap / Release / Follow (in-game control changes), planned with M4.

## Appendix: running the scanner

```
python tools/online/scan_field_scripts.py Assembly-CSharp/Global/Event/Engine/EventEngineUtils.cs "<mod>/StreamingAssets/assets/resources/commonasset/eventengine/eventbinary/field/us" --json out.json
```
Per field: character entries (link, size, `SetModel`, stand/walk/run), which entries call `DefinePlayerCharacter`,
team-slot vs explicit `InitObject` counts, party opcodes.
