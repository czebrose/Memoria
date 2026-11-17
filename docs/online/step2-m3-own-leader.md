# Step 2 / M3: each player's leader is their own character — findings

**Status: investigated, not built.** Recommendation and open questions at the end.

## How a field decides who the player is

| Piece | Where | What it does |
|---|---|---|
| Character entries | each field's event script; `sObjTable[sid].player_link` | A field script has one entry per playable character it supports (`player_link` = `CharacterId`), with that character's `SetModel` and animations. |
| Party slots 251-254 | `EventEngine.SetupPartyUID` (`EventEngine.cs`) | Maps "party member 1-4" to those entries in a **fixed priority order**: Zidane, Eiko, Steiner, Vivi, Freya, others, Quina, Amarant, Garnet, Beatrix; not the party's slot order. Scripts that spawn "party member 1" (`InitObject` with sid 251, `NEW3`) get the first one present. |
| The player | `DefinePlayerCharacter` (opcode `0x2C`, `CC`, `EventEngine.DoEventCode.cs`) | Run by an entry's own code: sets `_context.controlUID` (object 250, "the player") to that entry's object and clears `isPlayer` on the previous one. |
| The field's player object | `HonoluluFieldMain.updatePlayerObj` | Every frame, makes whatever object 250 resolves to the field's player (`FieldMap.updatePlayer`, camera, `isPlayer`). |
| Model | `SetModel` (opcode `0x2F`, `MODEL`) | Builds the model **once, when the object is created**: `ModelFactory.CreateModel`, then `FieldMap.AddFieldChar`; if the object already has a model it does nothing. There's no "change model" path. |
| Animations | `SetStandAnimation` / `SetWalkAnimation` / `SetRunAnimation` (`0x33`, `0x34`), `RunAnimation`, ladders, jumps | Animation ids are per model. |

Observations:
- **In vanilla only the leader exists in a field**; other party members aren't spawned. So a guest's character
  usually has **no object at all** in the field.
- **The sequel defines the player explicitly.** Its party (Clove = id 2, B.Waltz 11, Blank 10, Duskly 1) would put
  Duskly first by the priority order, yet the leader is Clove: the sequel's scripts run `DefinePlayerCharacter` on
  Clove's entry rather than going through "party member 1". So reordering party slots wouldn't change the sequel's
  player anyway.

## Options

**A. Re-skin the guest's player object (recommended).** On a guest, the object the script made the player (Clove's
entry) stays the player for every script purpose (object 250, triggers, exits, collisions), but is drawn as the
guest's character: its model and its idle/walk/run/turn animations.
- How: once the field's player is defined, rebuild that object's model (new model GameObject + `AddFieldChar`, the
  old one removed with `UnregisterHonoBehavior(true)`, position, walkmesh triangle, facing and `charArray` carried
  over), set `Actor.model`/`idle`/`walk`/`run`/`turnl`/`turnr` to the guest's character, keep it the field's player.
  Same building blocks as `GhostActor.Spawn`/`Despawn`.
- Engine edits: a hook after the player is defined (or after field init) on guests; the animation opcodes, when run
  on a re-skinned object, translated to the new model's idle/walk/run (other ids: play only if the new model has
  them, else keep the idle).
- Ghosts follow automatically: a guest's ghost state carries `Actor.model`, so the host sees the guest's character.
- Host: nothing changes; the host's character is the script's player.

**B. Make the guest's own character's entry the player.** Spawn that character's entry and redirect
`DefinePlayerCharacter` to it on the guest. Much more invasive: the entry often doesn't exist in the field's script,
object 250 then means a different object than on the host (scripts that move, check or talk to the player would
diverge), and every field script that assumes "the player is the entry that called `CC`" is at risk. Not recommended.

## Risks with A (scripts that assume the leader is the script's character)

- **Script-driven animations on the player** (cutscenes, ladders, jumps, kneeling at chests, "surprised" poses) use
  the original character's animation ids. On a re-skinned guest they fall back to idle/walk/run. Guests don't run
  the host's cutscenes in this step (frozen, M5) and can't interact (M5), so this mostly affects ladders/jumps, which
  will look like sliding.
- **Fields that change the player mid-field** (party splits, a cutscene that hands control to another character):
  each `DefinePlayerCharacter` re-skins the new player; vanilla party-split scenes then show the guest's character
  for whoever the script picked. Documented, not fixed.
- **Per-field model fixes** in the engine keyed by model id (e.g. offsets for vehicles) don't apply to a re-skinned
  player. Unlikely to matter for playable characters.

## What A needs from you

**Each playable character's field model and animations**, for the characters guests can pick. Vanilla characters
are already in `GhostModels` (model, head height, idle/walk/run, size). The sequel's characters (Clove's model is 202,
from the ghost logs) need their own entries. Proposed: a mod data file next to the New Game party,
`StreamingAssets/Data/Online/FieldModels.txt`, one line per character:

    # characterId, model, headHeight, idle, walk, run, turnLeft, turnRight, size
    2, 202, ..., ..., ..., ..., ..., ..., ...

The values are what the character's entry passes to `SetModel` / `SetStandAnimation` / ... in any field script
(Hades Workshop shows them). Without an entry, a guest playing that character stays drawn as the script's character.

## Recommendation

Build **A**. It's a contained change (a re-skin helper plus two small engine hooks), it doesn't touch script logic, and
it gives the ghosts the right characters for free. Not a large engine change, so no design conversation beyond the
open questions:
1. OK with A (cosmetic re-skin, scripts keep their own leader)?
2. The sequel's characters' field models/animations: will you provide `FieldModels.txt` values (from Hades Workshop),
   or should I add an automatic fallback that learns a character's model the first time a field script creates that
   character's entry?
