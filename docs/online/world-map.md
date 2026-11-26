# World map: other players, the shared ship, the passenger camera

The Black Waltz mod's `WorldTogether.cs` puts the players together on the world map: one shared Cargo Ship, and
passengers who ride with its pilot. The build does three parts of it natively (`Memoria/Online/World/`), so the mod
can leave them out:

| Part | Where | What |
|---|---|---|
| World ghosts | `WorldGhosts`, `WorldGhostState` | Every other player on foot or on a chocobo is drawn with their own model and animation, interpolated, with a world shadow and a name tag. |
| Shared ship | `WorldCoop` | While another player flies the airship, this PC's own ship follows theirs, smoothly. |
| Passenger camera | `WorldCoop` | While this player rides as a passenger, the world camera follows the ship with the pilot's airship camera. |

| Shared battles | `BattleEntry` (world map path) | A battle that starts on one player's world map pulls in the other players on the world map near them. Afterwards everyone is back where they stood. |

Boarding and landing, picking up passengers, hiding a passenger's player, which ship is newest, and the ship's save
data all stay the mod's.

**Status:** v0.1.4 ran in three two-PC tests; the fixes from them (the v0.1.4 update: each player's own world map bytes, the
world map join check, encounters at once, the ship's engine position, flight music) are built and pass both mod
checks, but haven't run on two PCs yet.

## Each player's own world map bytes

The event bytes from 64 up are shared between the PCs (`EventWatch`), but a few belong to each player on the world
map, since every PC runs its own world map. They're never sent or applied (`EventWatch.PlayerBits`), and the meeting
digest leaves them out:

| Bytes | What |
|---|---|
| 64-72 | where this player stands and faces (`VAR_GenInt24_64`, `VAR_GenInt16_67`, `VAR_GenInt24_69`, `World_PlayerAngle`), saved by the world script's loop and restored after a battle or a load |
| 74-82, 1005 bits 0-1 (flags 8040-8041) | this PC's ship: where, which way, landed. The Black Waltz mod syncs these with its own ship versions |
| 101 | `World_CurrentTransport` |
| 149 | the model this PC's world entry loaded (`VAR_GenUInt8_149`: 1 Clove, 0 Zidane) |
| 190 | `ff9.w_moveCHRControl_No` |

In v0.1.4 they were shared: players came back from a battle at the other player's spot, and the other PC's 149 gave
Clove Zidane's on-foot clips. A meeting resync (the host's whole game) still brings the host's copy, as before.

## World ghosts

Each PC sends a `WorldGhostState` on channel 1 (unreliable), next to its field ghost state and at the same rate
(15 Hz). It sends while it's on the world map, and as Away for 1 s after leaving it. It's 48 bytes, type byte `0x11`
(the field `GhostState` is `0x10`), and older builds ignore it. The fields:

- the mode: Foot, Chocobo, Airship, Vehicle, Hidden or Away;
- `ff9.w_moveCHRControl_No`;
- the controlled actor's model and animation;
- its absolute position (`WMActor.RealPosition`, Unity units) and transform rotation;
- the ground height under it (`w_moveCHRStatus[].ground_height`).

The sender decides the mode:
- the controlled actor is `ff9.w_movePlanePtr`: **Airship**;
- a chocobo index: **Chocobo**;
- Zidane's or Garnet's entry: **Foot**, unless `WorldCoop.Passenger` is set or the actor's renderers are all off
  (the mod hides a passenger), then **Hidden**;
- anything else: **Vehicle**.

The receiver (`WorldGhosts.ApplyRender`, `Camera.onPreCull`) draws only Foot and Chocobo. Airships aren't drawn,
because every PC has its own ship and it follows the pilot's (below).

- **Timeline:** the clock offset is the smallest (receive time − sender time) seen, drifting slowly. A ghost is drawn
  110 ms in the past, between the two states around that moment, and holds the last pose through gaps. It goes after
  2.5 s without a state. A jump over 24 units (a wrap, a warp) snaps instead of sliding.
- **Placement:** `WMWorld.SetAbsolutePositionOf(out unity, abs)` turns the absolute position into this PC's Unity
  position, because the world map wraps and its blocks move.
- **Model:** built like a world actor's (`WMWorld.addGameObjectToWMActor`): `ModelFactory.CreateModel` of
  `FF9BattleDB.GEO[model]` under a root with the sender's rotation, turned 180° about z, at 1/256 scale, on the local
  player's layer. Its animation is the sender's (`FF9DBAll.AnimationDB[anim]`, looped, cross-faded).
- **Shadow:** the world's own shadow prefab (`WorldMap_Local/Shadow/WMShadow`), on the ground the sender stands on,
  sized and shaded like this player's own shadow.
- **Name tag:** the player's Steam name (on screen only, never logged), above the model, facing the camera, a quarter
  of the model's height. `WorldGhosts.ShowNames` turns tags off.
- Log: `[Online] World ghost of #tag shown (model N GEO_...)`.

## Shared ship

`ff9.w_cameraUpdate` calls `WorldCoop.BeforeCamera` first and `AfterCamera` last. While another player's state says
Airship and this PC isn't flying, `BeforeCamera` moves this PC's `ff9.w_movePlanePtr` to that player's interpolated
position and rotation, every world frame. `WMActor.SetPosition` takes game units (256 per Unity unit), and it wraps
the world itself. It moves only the transform, so `FollowPilot` then sets `plane.pos` too: that's the engine's own
position (`originalActor.pos`), which the world code reads and puts back. The pilot is the one this PC rides with,
else the first with a fresh Airship state.

## Passenger camera

The mod calls `WorldCoop.SetPassenger(pilotSteamId)` when this player boards as a passenger, and `SetPassenger(0)`
when they get off. While the pilot's state is Airship, `BeforeCamera` sets up the camera update like this:
- it points `ff9.w_moveActorPtr` at this PC's ship, so the camera aims there;
- it points `ff9.w_moveCHRControlPtr` at the pilot's control type (`ff9.w_moveCHRControl[state.Control]`), so the
  camera's distance, height and field of view are the airship's;
- `AfterCamera` puts both back.

Switching to the ship's camera and back uses `ff9.w_cameraChange(type_cam, 64)`, the blend the game uses when you
board or leave a vehicle. The passenger's own actor, control and encounters stay the mod's.

While the camera is on the ship, the passenger hears the pilot's music: the music of the pilot's control type
(`ff9.w_moveCHRControl[state.Control].music`, the airship theme), through `ff9.w_musicRequestSend`, as the game asks for
it when you board. When the camera comes back, this player's own control's music is asked for again.

Log: `World map: the camera follows #tag's ship (control N, camera N)` when it really switches, and `the camera is back
on this player`.

## Shared battles

A battle that starts on the world map (a random encounter, or a world script's `Battle()`) goes through the same
shared battle entry as a field battle: Pending, Start, Ready, Go, the stream, End, results. Only the way in and the way
back differ. A battle's "field" is `BattleEntry.WorldMapField` (`0xFFFE`) in Pending, Start and BattleOpen.

**Who's in it.** `WMScriptDirector` passes the world map's frame result through `BattleEntry.OnWorldFlow`. When it's
the battle jump, `BeginOwned` runs with the world map as the place. Its participants are the other players whose:
- presence says world map (`Busy.WorldMap`);
- world ghost state (`WorldGhosts`) says on foot or on a chocobo (pilots, passengers and hidden players have no
  battles);
- flat distance from this player is at most `BattleEntry.WorldBattleRange` (16 Unity units, about two ship lengths).

As on fields, they also need their character in the party and the same battle settings. A player farther away is
logged with the distance and keeps walking. Ctrl+Shift+9 (OnlineDebug) turns shared world map battles off on that PC.

**Getting in.** A participant on the world map joins when:
- the Start has come;
- it has the world HUD and control, no fade, and isn't flying or riding as a passenger (`CanJoinNow`).

Unlike on fields, there's no `FF9StateSystem.attr` test: a field's jump to the world map sets its bit 8, and only the
next field's init clears it, so it stays set the whole time on the world map. In v0.1.4 that made every world map
join wait out the 8 s and give up ("waiting for the screen change").

A participant that gives up sends its reason with the Decline (since the v0.1.4 update: the text after the reason byte), so the owner's
log says why, and the player sees `Couldn't join <name>'s battle: <why>.`

**Encounters at once.** Both players start walking when the ship lands, so both PCs often roll an encounter within a
second of each other. The battle whose Pending arrives first wins: a PC whose own world map random encounter comes
while another player's battle is already coming drops its own (`DropOwnWorldEncounter`: `nextMapNo` back to the world
map, `isRandomEncounter` and `isEncount` off; a random encounter is only a flag, nothing to undo) and joins theirs. A
scripted world map battle isn't dropped: it declines the other one, as before. If both start in the same instant,
before either has heard of the other, they're separate battles, and both logs say so (`started at the same moment`).

`JoinBattle` then sets the world map's next battle (`FF9World.map.nextMapNo`), pattern and flags, and `OnWorldFlow`
returns the battle jump. The world map director starts it as its own encounter (`ff9worldInternalBattleEncountStart`).
A participant whose own world map battle starts first declines the other one, as on fields. One that leaves the world
map, or takes off, before it can join is out of that battle.

**Getting back.** Nothing extra: the world script's own loop saves where the player stands
(`VAR_GenInt24_64` / `VAR_GenInt16_67` / `VAR_GenInt24_69`, `World_PlayerAngle`). After the battle the game goes back
to the world map (the battle was entered from it), and the world script puts each player back at its own saved spot.
A participant that flees a world map battle isn't sent to a field (the field battles' flee return): it's back on the
world map where it stood.

**Music.** At the trigger, the world map's theme is playing, so the Start carries no music. Go carries the battle's,
as for random encounters on fields.

**Coming back (v0.1.6).** In v0.1.4-v0.1.5 tests, a player in a world map battle's result screens was gone from the
world map for the others: a new encounter right after didn't take them, and the airship could leave without them.
- **They stay visible.** When a world map battle takes a player (`OnWorldFlow`: their own, or another player's they
  join), their PC keeps the spot they left from (`WorldGhosts.NoteBattleFromWorld`). Until they're back, it sends
  that as their world state, mode **`Battle` (5)**. The others draw them standing there, with the model's standing
  animation if it was seen. `WorldCoop.TryGetPlayer` gives the mod mode 5, and `WorldCoop.InWorldBattle` is true on
  their own PC meanwhile.
- **A battle there can be joined late.** `UpdateLateJoin` also asks from the world map, for a world map battle
  (BattleOpen's field `0xFFFE`) whose owner is within 16 units. The owner's spot is its mode-5 state. The joiner must
  be able to join now: walking or on a chocobo, world HUD, control, no fade (`WorldJoinBlocker`, the same rule as a
  participant's). Then it's the field late join: Start, Ready, Go and the stream so far, catch-up, and its character
  joins the fight.
- **A random encounter waits for them.** When this PC rolls a world map random encounter while a player within 16
  units is in mode 5, the encounter is dropped (`WaitForNearWorldBattle`, as for two encounters at once):
  - if that player's battle is open, this PC joins it instead;
  - otherwise encounters wait for them to come back, 20 s at most (`WorldGraceMs`).

  Log: `this PC's world map encounter (battle B) dropped: #tag is in a battle D away, which this PC joins instead` /
  `...and should be back soon`.
- **The airship** is the mod's: its pick-up runs on the passenger's PC when it sees the pilot board, and that PC
  wasn't on the world map then. The hand-off for the mod suggests picking up a player who comes back after the ship
  was boarded near them.

**Not covered:**
- a world map battle's participants aren't held in place while it starts (fields hold them). They can walk a step or
  two, which changes nothing, since their spot is saved when they leave.

The host's state message (`MsgHostState`) says no field during a battle from the world map: `fldMapNo` still says the
last field the host left, and guests standing there were told to wait for the host's battle.

Log: `Battle entry: battle B #N on the world map (world map encounter); this PC owns it; participants: ...`, or
`#tag is on the world map D away (over 16.0): not in this battle`; participant: `joining #tag's battle B from the world
map (back to this spot after it)`.

**A passenger with no ship of their own.** If this PC's world map placed no ship (its save said airborne, so
`ff9.w_movePlanePtr` is null), there's nothing to follow or aim at. `BeforeCamera` logs it once per ride (`riding
with #tag, but this PC's world map has no ship of its own`), and the mod's own ride camera takes over. The mod avoids
this by writing the pilot's position into the ship's save, marked landed, before a ride-along player goes onto the
world map. World map 9007's ship init (`MoveInstantXZY` from `VAR_GenInt24_74` / `VAR_GenInt16_77` /
`VAR_GenInt24_79`) places it at exactly that height, in mid-air, without snapping to the ground, and `FollowPilot`
moves it from there.

## For the mod

`WorldCoop.PlayersOnWorldMap()` and `WorldCoop.TryGetPlayer(peer, out mode, out absolutePosition, out rotation)` give
the players' world map states as the build draws them (interpolated). The mode is a `WorldMode` number, and the
position is absolute in Unity units. The mod can read those instead of its own channel 7 packets.

`WorldCoop.DrawsGhosts`, `MovesSharedShip` and `HasPassengerCamera` are `true` in a build that has these parts. The
mod can check them by reflection and leave those parts to the build. Until it does, both draw world ghosts, and both
move the ship (the mod's raw, at 10 Hz, after the build's smooth one). The hand-off for the mod lists the changes.

## Two-PC test

1. Both players on the world map, on foot. Each sees the other running and standing, with a shadow and a name tag.
   Walking across a world wrap doesn't make the ghost slide.
2. One rides a chocobo: the other sees the chocobo.
3. One boards the Cargo Ship and flies it: the other's ship flies along smoothly, and no ghost is drawn for the pilot.
4. With the mod's passenger update (`SetPassenger`): the passenger's camera follows the ship with the airship's camera,
   and blends back to the on-foot camera after landing.
5. Both on foot close together; walk until one gets a random battle. Both are in it, and after it each is back where
   they stood (not at the other's spot). Then far apart (over 16): only the one whose battle it is fights, and the
   other's log says how far.
6. In 5, the participant flees: it's back on the world map where it stood, not on a field.
7. Land the ship and both walk off at once: the first encounter takes both players into one battle (the other PC's
   log: `dropping this PC's and joining theirs`).
8. Enter the world map as Clove from a field (not through the pilot): Clove's on-foot clips are Clove's.
9. As a passenger: the airship music plays while riding, and the world map's music after the landing.
