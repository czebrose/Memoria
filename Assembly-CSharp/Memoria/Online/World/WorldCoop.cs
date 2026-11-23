using Memoria.Prime;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Memoria.Online.World
{
    /// <summary>
    /// Players together on the world map, for the mod's world map co-op (docs/online/world-map.md):
    ///
    ///  - One ship: while another player flies the airship, this PC's own ship (ff9.w_movePlanePtr) is moved to the
    ///    pilot's, smoothly (<see cref="WorldGhosts"/>' interpolation), every world frame before the camera.
    ///  - Passenger camera: while this player rides as a passenger (<see cref="SetPassenger"/>), the world camera
    ///    follows the ship, with the camera of the pilot's way of moving (ff9.w_moveCHRControl[their control]), blended
    ///    in and out as the game blends when you board or leave a vehicle.
    ///
    /// Boarding, landing, the ship's save data and which ship is newest stay the mod's. The mod finds this class by
    /// reflection: <see cref="DrawsGhosts"/>, <see cref="MovesSharedShip"/> and <see cref="HasPassengerCamera"/> say what
    /// the build does, so the mod can leave those parts to it.
    /// </summary>
    public static class WorldCoop
    {
        /// <summary>The build draws the other players on the world map (<see cref="WorldGhosts"/>).</summary>
        public static Boolean DrawsGhosts => true;
        /// <summary>The build moves this PC's ship to another player's while they fly it.</summary>
        public static Boolean MovesSharedShip => true;
        /// <summary>The build's world camera follows the ship while <see cref="SetPassenger"/> is set.</summary>
        public static Boolean HasPassengerCamera => true;

        /// <summary>The player this PC rides with (0: not a passenger).</summary>
        public static UInt64 Passenger { get; private set; }

        /// <summary>The mod: this player rides as a passenger of <paramref name="pilot"/> (a peer's Steam id), or no
        /// longer does (0). Hiding the player, control and encounters stay the mod's.</summary>
        public static void SetPassenger(UInt64 pilot)
        {
            if (pilot == Passenger)
                return;
            Log.Message(pilot != 0 ? $"[Online] World map: riding with {SteamNative.Tag(pilot)}" : "[Online] World map: no longer a passenger");
            Passenger = pilot;
            _noPlaneLogged = false;
        }

        /// <summary>The players on the world map whose state is fresh (any mode but Away), by Steam id.</summary>
        public static UInt64[] PlayersOnWorldMap()
        {
            List<UInt64> result = new List<UInt64>();
            foreach (KeyValuePair<UInt64, WorldMode> kv in WorldGhosts.FreshModes(Now))
                if (kv.Value != WorldMode.Away)
                    result.Add(kv.Key);
            return result.ToArray();
        }

        /// <summary>
        /// A player's world map state now, as the build draws it (interpolated ~110 ms behind): <paramref name="mode"/>
        /// is a <see cref="WorldMode"/> (0 on foot, 1 chocobo, 2 flying the airship, 3 another vehicle, 4 hidden or a
        /// passenger, 255 away); the position is absolute, in Unity units (WMActor.RealPosition; WMActor.SetPosition
        /// takes 256 times that). False if nothing fresh came from that player.
        /// </summary>
        public static Boolean TryGetPlayer(UInt64 peer, out Int32 mode, out Vector3 absolutePosition, out Quaternion rotation)
        {
            mode = (Int32)WorldMode.Away;
            absolutePosition = Vector3.zero;
            rotation = Quaternion.identity;
            if (!WorldGhosts.TryGetState(peer, Now, out WorldGhostState state))
                return false;
            mode = (Int32)state.Mode;
            absolutePosition = state.Position;
            rotation = state.Rotation;
            return true;
        }

        /// <summary>The ship as the player flying it has it now (interpolated): false if no other player flies it.</summary>
        public static Boolean TryGetPilotShip(out UInt64 pilot, out Vector3 absolutePosition, out Quaternion rotation)
        {
            absolutePosition = Vector3.zero;
            rotation = Quaternion.identity;
            pilot = FindPilot(Now);
            if (pilot == 0 || !WorldGhosts.TryGetState(pilot, Now, out WorldGhostState state))
                return false;
            absolutePosition = state.Position;
            rotation = state.Rotation;
            return true;
        }

        // ---- Hooks (ff9.w_cameraUpdate) ---------------------------------------------------------------------------

        private static WMActor _savedActor;
        private static ff9.s_moveCHRControl _savedControl;
        private static Boolean _swapped, _cameraOnShip, _noPlaneLogged, _musicBack;
        private static Int32 _cameraType = -1;

        private static Double Now => OnlineSession.NowMs;

        /// <summary>[Online] ff9.w_cameraUpdate, first: the shared ship follows its pilot; a passenger's camera is
        /// pointed at it (restored by <see cref="AfterCamera"/>).</summary>
        public static void BeforeCamera()
        {
            _swapped = false;
            try
            {
                WMActor plane = ff9.w_movePlanePtr;
                Double now = Now;
                UInt64 pilot = FindPilot(now);
                WorldGhostState ship = default(WorldGhostState);
                Boolean flying = pilot != 0 && plane != null && plane.World != null && WorldGhosts.TryGetState(pilot, now, out ship);
                if (Passenger != 0 && plane == null && !_noPlaneLogged)
                {
                    // The world map placed no ship on this PC (its save said airborne): nothing to follow or aim at here.
                    // The mod draws the ship and runs its own ride camera then.
                    _noPlaneLogged = true;
                    Log.Message($"[Online] World map: riding with {SteamNative.Tag(Passenger)}, but this PC's world map has no ship of its own: the build's passenger camera is off for this ride (the mod's ride camera takes over)");
                }
                if (flying && ff9.w_moveActorPtr != plane)
                    FollowPilot(plane, ship);
                Boolean riding = Passenger != 0 && flying && pilot == Passenger;
                if (riding && ship.Control >= 0 && ff9.w_moveCHRControl != null && ship.Control < ff9.w_moveCHRControl.Length)
                {
                    ff9.s_moveCHRControl control = ff9.w_moveCHRControl[ship.Control];
                    if (!_cameraOnShip || _cameraType != control.type_cam)
                    {
                        if (!_cameraOnShip)
                            Log.Message($"[Online] World map: the camera follows {SteamNative.Tag(pilot)}'s ship (control {ship.Control}, camera {control.type_cam})");
                        ff9.w_cameraChange(control.type_cam, 64);
                        _cameraOnShip = true;
                        _cameraType = control.type_cam;
                    }
                    // The flight music: the pilot's way of moving has its own (the airship theme), as the game plays it
                    // when you board; w_musicRequestSend does nothing while it's already that one or a change is under way
                    ff9.w_musicRequestSend(control.music);
                    _savedActor = ff9.w_moveActorPtr;
                    _savedControl = ff9.w_moveCHRControlPtr;
                    ff9.w_moveActorPtr = plane;
                    ff9.w_moveCHRControlPtr = control;
                    _swapped = true;
                }
                else if (_cameraOnShip)
                {
                    _cameraOnShip = false;
                    _cameraType = -1;
                    Log.Message(Passenger != 0 ? $"[Online] World map: {SteamNative.Tag(Passenger)} isn't flying the ship now; the camera is back on this player" : "[Online] World map: the camera is back on this player");
                    if (ff9.w_moveCHRControlPtr != null)
                    {
                        ff9.w_cameraChange(ff9.w_moveCHRControlPtr.type_cam, 64); // back to this player's own camera
                        _musicBack = true;
                    }
                }
                if (_musicBack && !_cameraOnShip)
                {
                    // This player's own music again (on foot, the world map's theme). Asked until it's taken: a request
                    // while another change is under way is dropped.
                    if (ff9.w_moveCHRControlPtr == null || ff9.w_musicRequestNoIdx == ff9.w_moveCHRControlPtr.music)
                        _musicBack = false;
                    else
                        ff9.w_musicRequestSend(ff9.w_moveCHRControlPtr.music);
                }
            }
            catch (Exception err)
            {
                Log.Error(err, "[Online] World map: the shared ship / passenger camera failed this frame");
                AfterCamera();
            }
        }

        /// <summary>[Online] ff9.w_cameraUpdate, last: puts back what <see cref="BeforeCamera"/> pointed at the ship.</summary>
        public static void AfterCamera()
        {
            if (!_swapped)
                return;
            _swapped = false;
            ff9.w_moveActorPtr = _savedActor;
            ff9.w_moveCHRControlPtr = _savedControl;
            _savedActor = null;
            _savedControl = null;
        }

        private static void FollowPilot(WMActor plane, WorldGhostState ship)
        {
            // WMActor.SetPosition takes game units (256 per Unity unit) and wraps the world itself
            plane.SetPosition(ship.Position.x * 256f, ship.Position.y * 256f, ship.Position.z * 256f);
            // SetPosition moves only the transform: the engine's own position (originalActor.pos, which the world code
            // reads and puts back) would stay where the ship was, and the ship would jump back between frames
            plane.pos = plane.transform.position;
            plane.transform.rotation = ship.Rotation;
        }

        /// <summary>The player flying the airship: the one this PC rides with, else any other with a fresh airship state.</summary>
        private static UInt64 FindPilot(Double now)
        {
            UInt64 found = 0;
            foreach (KeyValuePair<UInt64, WorldMode> kv in WorldGhosts.FreshModes(now))
            {
                if (kv.Value != WorldMode.Airship)
                    continue;
                if (kv.Key == Passenger)
                    return kv.Key;
                if (found == 0)
                    found = kv.Key;
            }
            return found;
        }

        /// <summary>The session ended: nothing to ride or follow.</summary>
        public static void Reset()
        {
            Passenger = 0;
            _cameraOnShip = false;
            _cameraType = -1;
            _musicBack = false;
        }
    }
}
