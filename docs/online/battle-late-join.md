# Late join: walking into another player's battle

Branch `online-battle-join`, from `online-meet-skip` (a99d891f), 2026-11-20; on `Steam-Multiplayer-v0.1.4` since
2026-11-22, with the joiner's character fighting (section 2, "The joiner's character fights"). **Built, not run in
game yet.**

When a player walks onto a field where another player (host or guest) is fighting, that player's PC now enters the
same battle, replays it from the start at up to 4x speed until it has caught up, and then follows it live to the end.
After that both players are back on the field together, as after any shared battle.

**The joiner's character joins the fight.** Once the joiner is in, the owner adds its character to the battle in a
free slot, and the joiner commands it, along with the free characters it leads that are in the battle. If the battle
party is full, the joiner watches.

Contents:
1. [How battles are shared today](#1-how-battles-are-shared-today)
2. [Design](#2-design)
3. [Edge cases](#3-edge-cases)
4. [Log lines](#4-log-lines)
5. [Two-PC test](#5-two-pc-test)
6. [What's left](#whats-left)

---

## 1. How battles are shared today

These are the Step 3 M1/M2 battle docs in brief (`spike-d-battle-entry.md`, `step3-m1-results.md`,
`step3-m2-results.md`).

- **The owner** is the PC whose field starts the battle: a random encounter, or a script. It runs FF9's battle engine.
  Its trigger hook (`BattleEntry.BeginOwned`) picks the **participants** from Presence: peers on the same field whose
  character is in the party, and who aren't in a battle or on the world map. The participants get Pending, then
  Start (battle, pattern, start type, seed, starting HP/MP/status), then load the battle. They hold at the end of the
  intro until **Go** (units, initial ATB, music, and who commands each unit).
- **The participants play passively.** The owner records each command, effect, tick and ATB sample (`BattleRecorder`)
  and streams it as frame-stamped messages (`BattleStreamOut`, type 28). The participants replay that stream
  (`BattleReplayer`), so they draw no random numbers and run no AI. Each event's state is delta-encoded against the
  last state sent, so **a reader needs every message from the start**.
- **Party slots:** the battle party holds the characters of the players in the battle, plus free characters whose
  leader is in it. A PC commands its own units by remote menus (M2 `BattleControl`); the owner drives the rest.
- **Busy rules:** a participant in a menu has 8 s to close it before it declines. One that's on another field by
  Start declines at once. Once the owner's Go was sent, nobody else could get in. Before this branch, a player who
  walked in mid-battle was just left out:
  - a **guest** arriving on the host's field is frozen ("The host is in a battle...", M5 `GuestFreeze`) until the host
    is back;
  - the **host** arriving on a guest's field roams freely, and can even trigger a second battle of its own.
- **Battles that aren't shared:** world map encounters. Only field battles go through `BattleEntry.OnFieldFlow`.

## 2. Design

### Chosen approach: replay from the start, then live

A late joiner becomes an ordinary participant that arrived late. It loads the battle from the owner's **original**
Start, gets the **original** Go, and then every stream message so far. Its passive battle replays the whole fight from
the start. It runs extra battle frames until it reaches the owner's frame, then follows the live stream like any
participant.

Why this approach and not a mid-battle snapshot:
- **It reuses the tested path.** Entry, replay, End, results and per-player flee are the same code as a participant
  from the start (0 mismatches in M1/M2 runs).
- **A snapshot of a running battle is much more than HP, MP and status.** It also needs enemy AI and script
  variables, units that left or changed by script, running animations, queued commands, and the trance and
  status-timer internals. None of that has a writer today. The replay rebuilds all of it the way the battle built it.
- **The cost is catch-up time.** A battle 60 s in takes about 20 s to catch up at 4x (plus about 10 s to load). Late
  joins are refused once the battle has run 180 s.

### Who knows what

| Step | Who | What |
|---|---|---|
| Detect | owner → every peer | **BattleOpen** [34, seq, field, battle, state, seconds] 1/s while its field battle runs, plus once with state "ending" when it's over. State 0 = joinable, else the refusal reason below. A PC forgets a battle after 3.5 s without one. |
| Ask | a PC on that field, with control | **JoinAsk** [35, seq] to the owner, once per battle. The player is held (no moving, no encounters) while it waits. If two battles run on its field, the host's is asked first. |
| Answer | owner | **JoinRefuse** [36, seq, reason], or the battle's **Start** (the one sent at the trigger, or built at the owner's battle init). The joiner goes through the participant path from here (`OnStart` → `CanJoinNow` → `JoinBattle`). |
| Load | joiner | Jumps to the battle and applies the owner's *starting* HP/MP/status and battle party. Holds at the intro end and sends **Ready**. The owner sends Alive meanwhile, so the joiner doesn't leave on silence. |
| In | owner | On that Ready: **Go** (the bytes sent at the battle's Go), then the history bundled into ≤ 8 KB stream messages. The joiner is then added to the participants, so the live stream follows in order (reliable channel, same frame). |
| Catch up | joiner | `BattleReplayer.ExtraFrames`: 3 extra battle frames per update when it's over 159 frames behind (4x), 1 extra when over 39 (2x, as before). A "Catching up..." notice shows. |
| Fight | owner → every participant | The joiner's character joins (below). The owner's control map then gives the joiner its units (**ControlUpdate**, 37): its own character, and any free character it leads that's in the battle. It ends through its own victory screen, like a participant. |

### The joiner's character fights

`Online/Battle/BattleUnitJoin.cs`, and `BattleEntry.AddJoinerUnits` on the owner.

1. **When.** On the joiner's Ready, the owner queues it. The owner adds its character at the first moment the battle
   is in its normal phase with no command running (`cur_cmd == null`), so nothing in progress sees the unit appear.
2. **Which character.** The one the joiner plays (`CharacterOfPeer`):
   - none: the joiner watches;
   - already in the battle (the owner drove it): it's only handed over.
3. **Where.**
   - a player unit (`btl_data` 0-3) never used in this battle, and a free party slot;
   - none free (four in the battle already), or the battle isn't in its normal phase: the joiner watches.
   - It stands at the end of the line, 632 past the last member. The others don't move.
4. **How it's built.** As the battle builds its party at the start, for one member:
   - the model (`BattlePlayerCharacter.CreatePlayer`);
   - `btl_init.OrganizePlayerData`: shadow, character record, commands, status, weapon, abilities, and the EXP flag,
     so it gets EXP;
   - the battle model;
   - `btl_sys.AddCharacter`;
   - `SetupBattlePlayerSingle` for its place, ATB at 0;
   - `SFX.InitBattleParty`;
   - the menu's ability lists and party rows (`BattleHUD.OnPlayerUnitAdded`).

   The character goes into the battle's party slot too. The field party is restored from its saved copy after the
   battle, as always.
5. **Everyone else.** The owner writes a **UnitJoin** stream record (16) at that frame: character u16, and unit index
   and party slot in one byte. Every passive adds the same unit at the same frame (`BattleStreamIn.ApplyAtb`, before
   that frame's events), the joiner included while it catches up. A PC whose slot is taken (its battle differs)
   logs it and skips.
6. **Control.** `ControlMap` follows the session a few times a second, so the next sync hands the unit to the
   joiner's PC (MsgControlUpdate). Its turns then go to the joiner like any remote unit's. A turn that comes while
   the joiner still catches up waits for its menu.

### Owner-side changes

- **Every owned field battle is recorded and streamed**, even with no participants (`BattleSync.BeginOwner`). Before,
  only battles with participants were. Recording is what every shared battle has run since M1, so it doesn't change
  the battle. The stream is always taken each frame, and the messages that have records in them are kept: **history**,
  capped at 256 KB. Empty heartbeats aren't kept, since the next live message carries the owner's frame.
- The Go bytes and the Go time are kept.
- Late joiners that were let in but aren't Ready yet get End: when the battle ends (with its result), when the owner
  leaves the battle, or 20 s after they were let in.

### Refusal reasons (BattleOpen state / JoinRefuse)

| Code | Reason |
|---|---|
| 1 | the battle is ending (End sent, result set, or the owner left the battle scene) |
| 2 | it has run over 180 s, or its history passed 256 KB |
| 3 | a party scene is running on the owner (`SceneSync.Active`): the scene's own battle path decides |
| 4 | the joiner's battle settings (Speed / SFXRework, from Presence) differ from the owner's |
| 5 | still starting (no Go yet): the joiner asks once Go is out |
| 6 | already in it, or already let in |
| 7 | no such battle (wrong seq) |
| 8 | late join is off on the owner (Ctrl+Shift+8) |
| 9 | debug battle |
| 10 | not streamed (settings that can't be shared) |

A PC asks each battle (owner/seq) once. It won't ask again about a battle it was refused from, fled, was dropped
from, or already played.

### Files

- `Online/Battle/BattleEntry.cs`: the late-join section (owner: `KeepHistory`, `LateJoinState`, `BroadcastOpen`,
  `OwnerLateJoin`, `OwnerAnswerAsk`, `OnLateJoinerReady`, `BundleHistory`; joiner: `UpdateLateJoin`, `LateCatchUp`).
  There are also small hooks in `Update`, `BattleInitStart` (record every owned battle), `LeaveIntro` (keep Go),
  `UpdateCurrent` (Alive to joiners, history, End to joiners), `BattleLeft` and `OnFleeResult` (no rejoin),
  `OnPeerLeft` and the Ready/Decline case.
- `Online/Battle/BattleEntryMessages.cs`: messages 34-36.
- `Online/Battle/BattleReplayer.cs`: the 4x catch-up tier.
- `Online/OnlineSession.cs`: Ctrl+Shift+8 toggles late join on this PC (OnlineDebug). The status box (`Describe`)
  shows the history size and joiners.

## 3. Edge cases

| Case | What happens |
|---|---|
| **The battle ends while the joiner asks** | The owner answers JoinRefuse "ending". BattleOpen "ending" also clears it on the joiner. |
| **It ends while the joiner loads** (let in, not Ready) | The owner sends End with the result. The joiner leaves through the participant exit (escape path), and its field sees the owner's result. If it hadn't jumped yet, it drops the battle ("ended before this PC joined"). |
| **It ends while the joiner catches up** | The owner's End and the stream's End record are in. The joiner plays on at 4x to the end and ends through its own screens. The "owner ended 45 s ago" fallback only counts once it has caught up. |
| **The owner leaves the session or goes silent** | The participant rules apply: the joiner leaves the battle ("The battle's owner left"). |
| **The joiner triggers its own encounter in the same frame** | The joiner's own battle wins and its ask is declined (Decline 2). The owner drops it. |
| **The joiner is in a menu, a scene, or a fade** | It only asks with field control (`Busy.Control`), after the arrival fade and the field's opening scripts. With a menu open it asks once the menu closes. Once let in, the usual 8 s busy rule applies. |
| **The joiner had declined the battle at its start** (menu open 8 s) | It can now join late once it has control. |
| **Scripted / boss battles** | Joinable like random encounters. The replay carries the battle scripts' writes. The owner's after-battle field script runs only on the owner's PC (as for every participant). Battles started inside a **party scene** (`SceneSync`) are refused (3): the scene's followers get in through the scene's own path. |
| **World map battles** | Joinable since v0.1.6 from the world map, by a player within 16 units of the owner's spot (world-map.md, "Coming back"). |
| **Joiner is the host, owner a guest** | Same as a host participant. The host's passive battle ends through its own victory screen and applies the rewards. The guest's results blob then comes in as "played here" (no double rewards). |
| **Joiner is a guest, owner the host** | The "The host is in a battle..." freeze shows until the ask goes out. After the battle the guest takes the host's next party update (as any guest participant), and the keep-spot meeting runs once both are back on the field. |
| **More than one battle at once** | Each owner broadcasts its own battle. A PC only asks about battles on its own field, the host's first. A PC already in a battle (or asking) asks nothing. |
| **A participant fled** | It's marked as asked, so after fleeing it doesn't walk back into the same battle. |
| **The battle's owner plays no character in it, and nobody else is in it** | That battle isn't shared (no `BattleEntry` battle), so it isn't joinable. |
| **The battle party is full** (four units) | The joiner's character isn't added. If it's one of the four (the owner drove it), it's handed over; otherwise the joiner watches. |
| **The joiner's character is KO'd** | It joins KO'd, as it would start a battle. |
| **A battle script hides or brings back units** | The added unit takes a never-used slot only, so script units keep theirs. |
| **The slot differs on a passive PC** | The UnitJoin record is skipped there (logged: `couldn't be added to the battle here`). |

## 4. Log lines

All in `Memoria.log`, prefixed `[Online] Battle join:`:

- Owner: `#N this PC's battle B on field F is open to players who walk in` / `closed to late joins: <reason>` (on change).
- Joiner: `#N detected #tag's battle B on this field (F, S s in): joinable` / `can't be joined: <reason>`.
- Joiner: `#N asked #tag to join its battle B (S s in); holding the player`.
- Owner: `#N #tag asked to join battle B (S s in); accepted: Start sent (...)` or `...; refused: <reason>`.
- Joiner: `#N refused by #tag: <reason>; carrying on on the field` (toast: "Can't join …'s battle: …").
- Joiner: `#N joined: entering #tag's running battle B (watching; ...)`.
- Owner: `#N #tag is in (T ms into the battle): Go + M stream messages (X B, in K sends) sent; ...`.
- Joiner: `#N caught up with #tag's battle, T ms after joining (extra frames, events applied, mismatches)`.
- Owner: `#N #tag not ready 20 s after it was let in; dropped` / `#N #tag was still loading the battle; dropped (<why>)`.
- Owner: `#N stream history over 256 KB; nobody can join this battle late any more`.
- Owner: `#N #tag's character C joins the fight as unit U (party slot P); recorded for every participant`, or
  `... is already in the battle: it's handed over` / `... can't join the fight (<why>): it watches` / `#N #tag plays no
  character: it watches`.
- Every PC (`[Battle]` notes): `Unit join: <name> (character C) added to the battle as unit U, party slot P`, then
  `Control: <unit> changes hands mid-battle: this PC → #tag` on the owner.

The usual `[Online] Battle entry:` lines (Start, units, Go, End) and `[Battle] Replay` lines follow as for any
participant.

## 5. Two-PC test

Build `87553C5503F1` (or later from this branch). Both PCs on this build, with Memoria.ini Debug Enabled=1 /
OnlineDebug=1. A field with random encounters is easiest, e.g. Ice Cavern (field 302, battle 22). PC1 / PC2 can each be host
or guest. Run LJ1 both ways round if there's time.

| # | Steps | Pass |
|---|---|---|
| **LJ1 guest walks in** | Host and guest on adjacent fields. The host walks until a random encounter starts and keeps fighting (don't win at once; Defend a few turns). The guest walks onto the host's field. | The guest's log shows detected → asked → joined. Its screen goes to the battle, plays the fight fast ("Catching up...") and then follows live. Both see the same victory. Back on the field together. Owner log: `is in ... Go + M stream messages`. Joiner: `caught up`, 0 or few mismatches. |
| **LJ2 host walks in** | As LJ1, with the guest fighting and the host walking in. | As LJ1. Also: the host's victory screen gives the rewards once (check gil before/after against the guest's), and `results of #tag's battle ... (played here)`. |
| **LJ3 ends while joining** | The owner finishes its battle in the few seconds after the joiner steps in (one strong hit on a weak enemy). | The joiner either gets a refusal ("ending") and stays on the field, or leaves its loading battle at once. No stuck screen. Both continue on the field. |
| **LJ4 long battle** | The owner fights more than 3 minutes before the other walks in (or toggle it off with Ctrl+Shift+8 on the owner). | The joiner logs `can't be joined: ...run over 180 s` / `late join is off` and stays on the field (a guest stays frozen with "The host is in a battle..."). |
| **LJ5 joiner in a menu** | The joiner walks in and opens the main menu at once. | No ask while the menu is open. It asks when the menu closes (if the battle still runs). |
| **LJ6 scripted battle** | A scripted fight (e.g. a Mog bag fight) on the owner, the other walks in. | As LJ1. After the battle the owner's field script goes on, on the owner only. Note what the joiner sees. |
| **LJ7 flee** | In LJ1, the owner's party flees after the joiner is in. | The joiner's passive ends with the flee. Both back on the field. |
| **LJ8 the joiner fights** | LJ1 with three in the owner's battle party (one slot free). | After the catch-up, the joiner's character appears at the end of the line on both PCs, at the same moment of the fight, with a party row. Its turn opens the joiner's menu; its attacks show on both. It gets EXP at the end. Owner log: `joins the fight as unit ...`, then `changes hands mid-battle`. |
| **LJ9 full battle** | LJ1 with four in the owner's battle party. | The joiner watches (`can't join the fight (the battle party is full)`), unless its character is one of the four: then it's handed over. |

Logs to keep from both PCs: `Memoria.log`, `x64\FF9_Data\output_log.txt`. On the joiner, also the `[Battle] Replay`
summary line (mismatches, early fx, caught-up frames).

Things to watch:
- how long the catch-up takes and whether 4x looks acceptable;
- sound effects during the fast-forward;
- the guest's meeting resync after the battle (the meeting only happens once both are back, so it may reload the
  field once).

## What's left

1. ~~Fighting with the joiner's own character~~: done (section 2, "The joiner's character fights"). A full battle
   party (four) still means watching. Swapping a free character out for the joiner's would be the next step.
2. ~~Commanding the free characters it leads~~: done. The mid-battle control update (37) hands over the ones in the
   battle. Free characters it leads that weren't in the battle at the trigger stay out.
3. **A faster way in for long battles.** A mid-battle snapshot would avoid the catch-up, but it needs the writers
   listed in section 2.
4. ~~World map battles~~: shared since v0.1.4, joinable late since v0.1.6 (world-map.md).
5. **Muting the fast-forward** (sound effects play at 4x) is cosmetic and left.
6. **Not run in game.** All of section 5 is pending.
