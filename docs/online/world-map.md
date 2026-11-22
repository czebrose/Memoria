# World map: other players, the shared ship, the passenger camera

The Black Waltz mod's `WorldTogether.cs` puts the players together on the world map: one shared Cargo Ship, and
passengers who ride with its pilot. The build does three parts of it natively (`Memoria/Online/World/`), so the mod
can leave them out:

| Part | Where | What |
|---|---|---|
| World ghosts | `WorldGhosts`, `WorldGhostState` | Every other player on foot or on a chocobo is drawn with their own model and animation, interpolated, with a world shadow and a name tag. |
| Shared ship | `WorldCoop` | While another player flies the airship, this PC's own ship follows theirs, smoothly. |
| Passenger camera | `WorldCoop` | While this player rides as a passenger, the world camera follows the ship with the pilot's airship camera. |

Boarding and landing, picking up passengers, hiding a passenger's player, which ship is newest, and the ship's save
data all stay the mod's. Random battles on the world map stay each player's own: `BattleEntry` doesn't share them.

**Status:** built and passing both mod checks, but never run in game.

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
the world itself. The pilot is the one this PC rides with, else the first with a fresh Airship state.

## Passenger camera

The mod calls `WorldCoop.SetPassenger(pilotSteamId)` when this player boards as a passenger, and `SetPassenger(0)`
when they get off. While the pilot's state is Airship, `BeforeCamera` sets up the camera update like this:
- it points `ff9.w_moveActorPtr` at this PC's ship, so the camera aims there;
- it points `ff9.w_moveCHRControlPtr` at the pilot's control type (`ff9.w_moveCHRControl[state.Control]`), so the
  camera's distance, height and field of view are the airship's;
- `AfterCamera` puts both back.

Switching to the ship's camera and back uses `ff9.w_cameraChange(type_cam, 64)`, the blend the game uses when you
board or leave a vehicle. The passenger's own actor, control and encounters stay the mod's.

## For the mod

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
