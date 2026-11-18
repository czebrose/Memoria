# Online play: the scene toolkit for field scripts

How a field script controls who appears in a scene and with which body, in online play. Design and reasons:
[step2-m3-own-leader.md](step2-m3-own-leader.md), sections 9-11. Code: `Assembly-CSharp/Memoria/Online/Bodies/BodySwap.cs`.

**Principle: one shared world.** One action has one actor, and every PC shows that actor with the same body. Who is in
a scene is decided scene by scene by the script; without any of the tools below, the engine's defaults apply (and
fields keep working as they do today).

Everything goes through **Memoria script dictionary 9900**: read an entry in an expression, write one with a variable
assignment, as for any Memoria dictionary (written `dict[9900][key]` below). Characters are party character ids
(`CharacterParameters.csv`: in the sequel 1 Duskly, 2 Clove, 3 Vayle, 11 B.Waltz). Bodies come from
`StreamingAssets/Data/Online/CharacterBodies.txt` ([example](CharacterBodies.example.txt)).

Dictionary 9900 is filled for mods that ship `CharacterBodies.txt` (and while online), is never saved, and the
script's requests (keys 100-102, 20000+, 30000+) are reset on every new field.

## Read (filled by the engine every frame)

| Key | Value |
|---|---|
| 0 | 1 while an online session is playing, else 0 |
| 1 | this PC's character (offline: the party leader) |
| 2, 3 | its body's model and head height |
| 4 | the **acting character**: who performs the player entry's actions right now |
| 10 - 23 | this PC's character's animation for each action (below) |
| 10000 + c×100 + 0 | who controls character c: 0 nobody (the host's game), 1 this PC, 2 another player |
| 10000 + c×100 + 1 | 1 if character c is in this field (this PC's player, or another player whose last position is here) |
| 10000 + c×100 + 2, + 3 | character c's model and head height (0: no body) |
| 10000 + c×100 + 10 ... 23 | character c's animation for each action |

Actions, in key order (+10 ... +23): Stand, Walk, Run, TurnLeft, TurnRight, KneelDown, Kneeling, KneelUp, OpenDoor,
LadderOn, LadderClimb, LadderOffTop, LadderOnDown, LadderOffBottom. A body without one of them gives its Stand.

## Write (requests from the script, for the current field)

| Key | Value | Effect |
|---|---|---|
| 100 | 0 automatic, 1 Player, 2 Cast | **Scene mode.** Cast: every entry shows the body it was written with (a story beat with the field's cast). Player: the player entry shows the acting character, each player's character appears once. Automatic: Player, and Cast while another entry directs the player entry by its UID with control off. |
| 101 | 0 automatic, 1 personal, 2 shared | **Scene scope**: personal = only the acting player's PC runs it, the others watch their ghost; shared = everyone sees it. *Recorded and readable now; acted on from M4/M5 (networking).* |
| 102 | 0 automatic, c + 1 | **Acting character**: who performs the player entry's actions (automatic: this PC's player). For a shared scene a player started, every PC sets the same actor. |
| 20000 + uid | 0 automatic, c + 1, 255, 254 | **Bind an entry** (by its object UID) to character c: it takes c's body and its animations are translated by action. 255: its own written body, whatever the defaults. 254: **not a party character**: own body, left as the field shows it, never counted as anyone (for an extra that only looks like a party member). |
| 30000 + c | 0 automatic, 1 show, 2 hide | **Show or hide character c** in this scene, wherever it's shown (an entry or its player's ghost). |

Defaults (automatic everywhere): the player entry (written with the lead's body, Clove) shows the acting character.
Each character is shown once per PC, in this order: entries shown on purpose (the player entry, bound entries, the
whole cast in Cast mode), then other players' ghosts on this field, then the field's other entries (extras). An extra
is hidden only if its character is already shown that way; a character the script doesn't mention is otherwise left
where the field put it. A character the script hides (30000 + c = 2) is hidden everywhere. A `SetModel` whose model is an expression
is the script's own choice: never swapped, bound or hidden.

**Who is who** comes from the model an entry is created with (`CharacterBodies.txt`). A body marked `Borrowed 1` (a
stand-in model that belongs to someone else, like Duskly using Vivi's) doesn't identify entries: they're left alone
unless a script binds them. Per scene, `20000 + uid = 254` does the same for one entry, and `20000 + uid = c + 1` makes
any entry (known model or not) character c.

## Examples

**A shared scene started by another player** (phase 2 sets this from the network; the debug key F8 does it now):

    dict[9900][102] = actingCharacter + 1     // everyone sees that character on the player entry
    dict[9900][101] = 2                       // shared
    ... the scene ...
    dict[9900][102] = 0                       // back to automatic

**Clove belongs in the scene even though someone else acts** (entry uid 30 is a Clove extra in this field):

    dict[9900][20000 + 30] = 2 + 1            // entry 30 shows Clove (character 2)

**Clove has nothing to do with this scene:**

    dict[9900][30000 + 2] = 2                 // hide Clove (her entry or her player's ghost)

**This Vivi-looking extra is Vivi, not Duskly** (entry uid 14):

    dict[9900][20000 + 14] = 254              // not a party character: shown as the field shows it

**This extra is Duskly** (when Duskly's body is marked Borrowed, entries drawn with it aren't Duskly by default):

    dict[9900][20000 + 14] = 1 + 1            // entry 14 is Duskly (character 1)

**React to who is playing:**

    if (dict[9900][4] == 11) { ... B.Waltz's line ... }

**Use a character's own animation without a lookup table:**

    RunAnimation( dict[9900][10000 + c * 100 + 15] )   // c's KneelDown

## Debug keys (`[Debug] Enabled = 1`, `OnlineDebug = 1`; Ctrl+Shift+...)

| Key | Effect |
|---|---|
| C | pretend to be a guest playing the next party member (reloads the field) |
| F7 | Player ↔ Cast (as if the script wrote key 100) |
| F8 | next acting character: automatic → each party member → automatic (as if the script wrote key 102) |

The status box (Ctrl+Shift+O) lists the roles, the mode, every swapped or hidden entry and every hidden ghost.
