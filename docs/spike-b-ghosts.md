# Spike B: Ghost actors in fields — findings

Branch `spike-b-ghosts` (off `main` at `c716e1fa`, like Spike A). Code: `Assembly-CSharp/Memoria/Online/Ghosts/`.

**Status: in-game test passed (2026-11-16, one PC, shadow ghost).** Statements still marked *(to verify)* weren't
covered by that run. The verdict is at the end.

### Test results (2026-11-16, ~6 minutes, cargo ship fields 504/505/507 + the Black Waltz No. 3 battle)

Tester: "didn't notice anything" wrong visually (floor contact, animations, smoothness, lighting, shadow, depth,
walking through it, NPCs and the field's script). From `Memoria.log` (saved as
`Output/logs/Memoria-shadow-test-2026-11-16-2046.log`, not committed) and Unity's `output_log.txt` (no exceptions):

| Check | Result |
|---|---|
| Spawn | on the first state for the field, 1 s after you could move, on the right floor (e.g. field 504, tri 38, floor 2) |
| 15 Hz / 10 Hz | ~3600 states sampled; no snapping at either rate, but **10 Hz looks noticeably worse than 15 Hz** (tester); interpolation off shows the steps (as expected) |
| Feed paused 6 s | hidden exactly 5.0 s after the last state, shown again 2 s later at the new position |
| Network sim (10% loss, 0-80 ms jitter) | 116 states lost, 12 late ones dropped as old, still smooth |
| Field changes | 504 ↔ 505 ↔ 507 four times: ghost hidden as soon as its states name the other field, dropped with the old map, respawned in the new one; no leftovers, 1 snap (a > 400-unit jump at a field entrance) |
| Battle | ghost gone with the field at battle start, respawned after the battle (08:46:05) |
| Errors | none in `Memoria.log` or `output_log.txt` |

Not exercised in this run: `Ctrl+Shift+M` (other models, and the "same as you" model's exact animations on ladders).

Much of B1/B2 was already worked out by the local-multiplayer branch (`multiplayer`), whose "field followers" are
extra characters spawned from C# in any field (`Memoria/Multiplayer/MultiplayerField.cs`, notes in
`docs/multiplayer-plan.md` there). Spike B ports the parts a ghost needs onto `main` instead of rediscovering them;
the ghost is simpler than a follower (no input, no collision, no interactions).

## B1. How field actors are created

- **Script side.** A field's event script creates each object in its init code:
  - `InitObject` (opcode `0x09`, `NEW3`, `EventEngine.DoEventCode.cs`) → `new Actor(sid, uid)`: allocates the object
    *and links it into the event engine's object list* with the entry's bytecode (`Actor(Int32 sid, Int32 uid)` in
    `Global/Actor/Actor.cs` calls the `PosObj`/`Obj` constructors that register it). `sid` 251-254 means "party
    member N".
  - Then the object's own code sets it up: `SetModel` (`0x2F`, `MODEL`: model id + head height; builds the GameObject
    with `ModelFactory.CreateModel` and `GeoTexAnim.addTexAnim`), `SetStandAnimation` / `SetWalkAnimation` /
    `SetRunAnimation` / `SetLeftAnimation` / `SetRightAnimation` (`0x33`, `0x34`, `0x7A`, `0x7B`: animation ids,
    loaded with `AnimationFactory.AddAnimWithAnimatioName` and `FF9DBAll.AnimationDB`), `SetObjectLogicalSize`,
    `SetWalkSpeed` (`0x26`), `SetRunSpeedLimit` (`0xA6`).
  - `CreateObject` (`0x1D`, `POS`) places it: it ends in `FieldMap.AddFieldChar(go, pos, rot, isPlayer, actor)`
    (`Global/Field/Map/FieldMap.cs`), which parents the model under the field map and adds the `FieldMapActor` and
    `FieldMapActorController` behaviours, and activates it on the walkmesh (`BGI_charSetActive`).
  - `DefinePlayerCharacter` (`0x2C`) picks the player; `HonoluluFieldMain.updatePlayerObj` follows object 250.
- **Walkmesh movement** is `FieldMapActorController` (`Global/Field/Map/Actor/`), one per actor, ticked by
  `HonoBehaviorSystem`: `HonoUpdate` → `UpdateMovement` → `MovePC` (player input) or `MoveNPC` (script targets),
  `ServiceChar`, `UpdateActiveTri` (triangle/floor tracking). `HonoLateUpdate` copies `curPos` to the transform and
  to `Actor.pos`. Animation: `PlayAnimationViaEventScript` samples `Actor.anim` at
  `Actor.animFrame / frameN`; the event engine advances frames (`EventEngine.ProcessAnime.cs`: `SetAnim`,
  `NextFrame`, `aspeed` 16 = one frame per field tick).
- **Engine loops that matter for an extra actor** all walk the *active object list*: `EventEngine.ProcessEvents`
  (script, animation frames, mesh show/hide flags), `FieldMap.ff9fieldCharService` (rotation etc.),
  `EventCollision` (talk / push / collision), and Memoria's 60 FPS smoothing (`SmoothFrameUpdater_Field`). Loops keyed
  by UID instead: `FF9.charArray`, `charStateArray`, `shadowArray` (renderer, shadows) and the lighting in
  `fldmcf.FieldMCFService`.

## B2. Spawning outside the script — yes

`GhostActor.Spawn` does what `InitObject` + `SetModel` + the animation opcodes + `CreateObject` do, except the first
step: it uses the **parameterless `Actor()`**, which does *not* link the object into the event engine's list. So:

- no script entry, no thread, no event ID: the field script can't see, run or move it, and `FindObjByUID` /
  `GetObjByUID` don't return it; the field's own script and NPCs are untouched;
- no collision with the player or NPCs (collision only checks active objects);
- it must do itself what the active-list loops would do for it (see the next point).

What the ghost needs, and where it comes from:

| Need | How |
|---|---|
| Model, animations | `ModelFactory.CreateModel`, `GeoTexAnim.addTexAnim`, `AnimationFactory.AddAnimWithAnimatioName`; other animations are loaded on first use. |
| Renderer, shadow, lighting registration | `charArray` / `charStateArray` / `shadowArray` entries under a ghost UID (244-247: custom range 128-249, below the 250+ references; skipped if the field's script uses it). |
| Placement, draw order vs. background | `FieldMap.AddFieldChar`, like any field character: parented under the field map, depth handled like other characters. *(to verify on layered fields)* |
| No script movement | **Engine edit** in `FieldMapActorController.UpdateMovement`: returns early for a ghost. |
| Position, floor | `GhostActor` writes `curPos`/`lastPos`/`activeTri`/`activeFloor` from the received state; no walkmesh collision of its own. |
| Smooth motion at any frame rate | Not in the 60 FPS smoothing (active list only), so `GhostActor.ApplyToTransform` sets the transform and samples the animation every rendered frame from `Camera.onPreCull`. |
| Animation | Its own frame clock (`FieldTPS × aspeed/16` frames per second). |
| Lighting | **Engine edit** in `fldmcf`: lights detached actors whose controller is a ghost, using the ghost's floor (`BGI_charGetInfo` only knows active actors). Without it a ghost is drawn at full brightness (found by the multiplayer branch). |
| Hide / show | Renderers off + `ff9shadow.FF9ShadowOffField`; the engine's mesh-flag loop doesn't touch detached actors, so nothing turns them back on. |
| Removal mid-field | **Engine edit**: `HonoBehaviorSystem.Exists`. Removal goes through `UnregisterHonoBehavior(true)` like `EventEngine.DisposeObj`; a plain `Destroy` leaves a dead controller in `HonoBehaviorSystem`'s list and its `Update` throws every frame (multiplayer branch finding). UID entries are removed once the object is gone. |
| Field changes, battles | The ghost is a child of the field map, which is destroyed on field changes and battles; `GhostManager` notices the new map (or `gMode != 1`) and drops its bookkeeping and UID entries. Ghosts come back on the next state for the new field. |

Engine edits are all marked `[Ghosts]`: `FieldMapActorController.cs` (5 lines), `fldmcf.cs` (8),
`HonoBehaviorSystem.cs` (2), plus the test driver in `SceneDirector.Awake` (4). They're anchored away from Spike A's
edits so A and B merge cleanly.

**Not needed** (multiplayer branch needed it for followers): the `HonoluluFieldMain.updatePlayerObj` guard. A follower
could become object 250 during its talk; a ghost never interacts, so it can't.

Limits:
- 4 ghosts max (UIDs 244-247); a field whose script uses one of these UIDs gets fewer (logged).
- Per-actor cost is one field character (model + animations); the engine has no fixed actor pool for these, since
  `charArray` is a dictionary.

## B3. Ghost state — the sync payload

`GhostState` (`GhostState.cs`), **23 bytes**, 1-byte message type first:

| Offset | Field | Type | Why |
|---|---|---|---|
| 0 | type | byte | `0x10` |
| 1 | tick | uint32 | sender's clock in ms: ordering, and the interpolation timeline |
| 5 | fieldId | uint16 | `FF9.fldMapNo`; ghost only shown in the same field |
| 7 | model | uint16 | sender's `Actor.model` |
| 9 | x, y, z | int16 ×3 | `curPos`; **y is sent**: the ghost does no collision, so the sender's height is the truth |
| 15 | tri | int16 | walkmesh triangle → floor for lighting/shadows; position alone can't pick the floor where floors overlap |
| 17 | facing | uint16 | `rotAngle[1]`, 65536 per turn |
| 19 | anim | uint16 | exact animation id (ladders, jumps, scripted poses) when the receiver uses the same model |
| 21 | frame | byte | frame, to resync on animation changes |
| 22 | motion | byte | idle / walk / run / other, for a receiver drawing a different model |

`Serialize(byte[], offset)` → length and `Deserialize(byte[], offset, length)` → bool; no allocation. The sender
reuses one buffer for `ITransport.Send(peer, TransportChannel.Movement, buffer, 23, reliable: false)`.

Answers:
- **Position alone or triangle too?** Position *with y* is enough to draw it without snapping or falling through,
  since nothing makes the ghost fall. The triangle is still sent (2 bytes) for the floor (lighting, shadow).
- **Field coordinates fit in int16** (walkmesh data is 16-bit); the recorder logs a warning if one ever doesn't.
- **Different model than the sender:** animation ids are per model, so it uses its own idle/walk/run from `motion`
  and idle for "other". Same model: the exact animation.
- **Timeline** (`GhostActor.Update`): each state's tick is mapped to local time with the lowest
  `localNow - tick` seen; the ghost renders `InterpolationDelayMs` (100 ms) behind the newest state, lerping position
  and facing between the two states around that time. A jump over 400 units between consecutive states (warp,
  ladder) snaps instead of sliding.
- **Gaps and staleness** (`GhostManager`): states not newer than the newest are dropped; the last pose is held
  through gaps; after 5 s without a state the ghost is hidden; the next state shows it again *at its new position*
  (timeline reset, no slide from the old spot). A ghost whose player is in another field is hidden.
- **Spawned lazily** on the first state for the local field, never on "peer joined".
- *(to verify)* smoothness at 10 and 15 Hz with linear interpolation.

## Prototype

| File | What |
|---|---|
| `GhostState.cs` | the payload + serialization |
| `GhostModels.cs` | playable characters' field models and idle/walk/run (from the multiplayer branch's defaults) |
| `GhostActor.cs` | spawn / push state / interpolate / apply at render time / hide / despawn |
| `GhostManager.cs` | ghosts by peer id: lazy spawn, ordering, staleness, field changes, cleanup |
| `FieldMapActorController.Ghost.cs` | partial: `ghost` field, `IsGhost` |
| `GhostSpike.cs` | test driver: the "shadow" ghost |

Wiring for the online version (spike A + B): on `ITransport.Poll`, for each channel-1 message call
`GhostManager.OnPacket(msg.Peer, msg.Data, 0, msg.Data.Length, nowMs)`; on `PeerLeft` call
`GhostManager.Remove(peer, …)`; each frame sample the leader as `GhostSpike.Sample` does and send it.

## How to test

Build/install (already done on PC 1 at the time of writing):

```powershell
cd ..\Memoria-Ghosts
.\tools\multiplayer\build.ps1
.\tools\multiplayer\install.ps1
```

In game, in a normal walkable field (Ctrl+Shift + key; a status box appears bottom-left once it's on):

1. **G**: shadow ghost on. A Vivi appears where you were ~1 s ago and replays your path: walk, run, stop, turn.
   Check: stays on the floor, right animations, no visible snapping, lit like the other characters, has a shadow,
   correct depth when walking behind/in front of background objects, and you can walk through it.
2. **R**: switch to 10 Hz and repeat; **N** turns interpolation off to see the difference (it should jerk).
3. **P**: pauses the feed for 6 s. The ghost stops where its last state was, hides ~5 s after the last state, then
   reappears *at your recent position* (not sliding there) when the feed resumes.
4. **K**: network simulation (10% loss, 0-80 ms jitter). Still smooth; the status box's "old" counter shows dropped
   out-of-order states.
5. **M**: cycles the model (Vivi → Garnet → … → "same as you": then it plays your exact animations, e.g. on ladders).
6. Walk to another field and back, several times; enter a battle and finish it. No leftover ghost, no crash, the ghost
   reappears in each new field ~1 s after you can move.
7. Talk to NPCs, open a chest, trigger a cutscene: the field behaves normally.

Then send `Memoria.log` (`[Ghosts]` lines) and, if anything glitched, `x64\FF9_Data\output_log.txt` (Unity's own
exceptions only go there).

## Known / expected breakages (to verify)

- **Cutscenes:** ghosts are hidden only by silence or another field, so a ghost keeps showing (and moving, if its
  player moves) during the local player's cutscenes. Intended for co-op ghosts, odd in the shadow test.
- **Fields without a player** (FMV, some cutscene-only fields, mini-games) don't spawn ghosts (`playerController` is
  required).
- **Fields where the leader is something else** (chocobo riding, mini-games, the Prima Vista sword fight...): the
  recorder sends whatever the field's player object is; a ghost with a different model then shows idle/walk/run of
  its own model.
- **Field-specific position fixes** in `FieldMapActorController` (e.g. per-`sid` offsets on some maps) don't apply to
  ghosts; the sender's position already includes them.
- **Animation speed:** with a different model, walk/run cycles use that model's default speed, so feet may slide a
  little.
- **Interpolation delay** is 100 ms: fine for 15 Hz on a direct connection (spike A: 3-14 ms RTT, no loss); relay or
  jittery links may need more.

## Verdict

**Spawning ghosts outside the field script works**, generically, with ~20 lines of marked engine edits and no
per-field script changes. The 23-byte `GhostState` at 15 Hz with 100 ms of interpolation looks smooth on a direct
connection, survives loss, reordering and a 6 s outage, and ghosts clean up and come back across field changes and
battles. Ready to wire to Spike A's transport.

**Decision: send ghost state at 15 Hz.** 10 Hz was visibly worse in the test. At 23 bytes that's ~350 bytes/s per
peer before Steam's overhead, trivial next to what spike A carried (48-byte packets at 15 Hz, no loss).

Still open: the model switch and exact-animation path (ladders, jumps) with the "same as you" model; fields with
heavy background layering; FMV/mini-game fields; a real network link (relay or jitter beyond 80 ms may need a
longer interpolation delay).
