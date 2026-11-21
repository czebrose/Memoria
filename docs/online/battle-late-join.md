# Late join: walking into another player's battle

Branch `online-battle-join`, from `online-meet-skip` (a99d891f), 2026-11-20. **Built, not run in game yet.**

When a player walks onto a field where another player (host or guest) is fighting, that player's PC now enters the
same battle, replays it from the start at up to 4x speed until it has caught up, and then watches it live to the end.
After that both players are back on the field together, as after any shared battle.

**Limit: the late joiner watches. Its own character isn't in the battle.** Adding a unit to a battle that's already
running would change the owner's battle itself (see [What's left](#whats-left)). The joiner's character fights from the
next battle on, through the normal shared entry.

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
| Watch | joiner | It commands no unit: Go's control map was made before it came, so its menu stays shut. It ends through its own victory screen, like a participant. |

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
| **World map random encounters** | Not joinable. World map battles aren't shared at all today (no `BattleEntry` owner), so there's no BattleOpen, and a PC only asks from a field. |
| **Joiner is the host, owner a guest** | Same as a host participant. The host's passive battle ends through its own victory screen and applies the rewards. The guest's results blob then comes in as "played here" (no double rewards). |
| **Joiner is a guest, owner the host** | The "The host is in a battle..." freeze shows until the ask goes out. After the battle the guest takes the host's next party update (as any guest participant), and the keep-spot meeting runs once both are back on the field. |
| **More than one battle at once** | Each owner broadcasts its own battle. A PC only asks about battles on its own field, the host's first. A PC already in a battle (or asking) asks nothing. |
| **A participant fled** | It's marked as asked, so after fleeing it doesn't walk back into the same battle. |
| **The battle's owner plays no character in it, and nobody else is in it** | That battle isn't shared (no `BattleEntry` battle), so it isn't joinable. |

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

Logs to keep from both PCs: `Memoria.log`, `x64\FF9_Data\output_log.txt`. On the joiner, also the `[Battle] Replay`
summary line (mismatches, early fx, caught-up frames).

Things to watch:
- how long the catch-up takes and whether 4x looks acceptable;
- sound effects during the fast-forward;
- the guest's meeting resync after the battle (the meeting only happens once both are back, so it may reload the
  field once).

## What's left

1. **Fighting with the joiner's own character.** It would need a player unit added to the owner's running battle:
   the `btl_init` / `BattlePlayerCharacter.CreatePlayer` path that Memoria's in-battle party change uses
   (`btl_init.cs:618`, `BattleHUD.cs:2756`). The add would be recorded as a new stream event that the passive
   replays, and the control map would grow mid-battle (`BattleControl` takes its remote units only at Go today).
   Until then, the joiner's character fights from the next battle on.
2. **Commanding the free characters it leads.** They're in the battle only if their leader was there at the trigger.
   Handing an owner-driven unit to the joiner needs a control-map update message.
3. **A faster way in for long battles.** A mid-battle snapshot would avoid the catch-up, but it needs the writers
   listed in section 2.
4. **World map battles** aren't shared at all yet, so they can't be joined.
5. **Muting the fast-forward** (sound effects play at 4x) is cosmetic and left.
6. **Not run in game.** All of section 5 is pending.
