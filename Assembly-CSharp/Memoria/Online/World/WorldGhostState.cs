using System;
using UnityEngine;

namespace Memoria.Online.World
{
    /// <summary>What one player is doing on the world map, for the others' world ghosts (<see cref="WorldGhosts"/>).</summary>
    public enum WorldMode : Byte
    {
        /// <summary>Walking (Zidane's or Garnet's world entry): drawn as a ghost.</summary>
        Foot = 0,
        /// <summary>Riding a chocobo: drawn as a ghost (the chocobo's model).</summary>
        Chocobo = 1,
        /// <summary>Flying the airship (the controlled actor is ff9.w_movePlanePtr): not drawn; every other PC's own ship
        /// follows it instead (<see cref="WorldCoop"/>).</summary>
        Airship = 2,
        /// <summary>Another vehicle (a boat...): not drawn.</summary>
        Vehicle = 3,
        /// <summary>On the world map but not to be drawn: a passenger (<see cref="WorldCoop.SetPassenger"/>), or the
        /// player's actor is hidden.</summary>
        Hidden = 4,
        /// <summary>Not on the world map (a field, a battle, a menu, loading).</summary>
        Away = 255,
    }

    /// <summary>
    /// A player's world map state, sent on channel 1 (unreliable) with the field ghosts' states, ~15 times a second
    /// while on the world map (and a few times after leaving it, as Away). 48 bytes, little-endian:
    ///
    ///   0  type     byte     MessageType (0x11; the field ghosts' GhostState is 0x10)
    ///   1  tick     uint32   sender's clock in milliseconds
    ///   5  mode     byte     WorldMode
    ///   6  control  sbyte    ff9.w_moveCHRControl_No: how it moves, and which world camera it uses
    ///   7  model    uint16   the controlled actor's model (PosObj.model)
    ///   9  anim     uint16   its animation playing (PosObj.anim)
    ///  11  x, y, z  float×3  WMActor.RealPosition: the absolute world position, in Unity units
    ///  23  rot      float×4  the WMActor's transform rotation (a quaternion)
    ///  39  ground   float    ff9.w_moveCHRStatus[].ground_height: the ground under it (its shadow)
    ///  43  flags    byte     reserved (0)
    ///  44  -        4 bytes  reserved (0)
    /// Older builds ignore it (an unknown type on channel 1).
    /// </summary>
    public struct WorldGhostState
    {
        public const Byte MessageType = 0x11;
        public const Int32 Size = 48;

        public UInt32 Tick;
        public WorldMode Mode;
        public SByte Control;
        public UInt16 Model;
        public UInt16 Anim;
        public Vector3 Position;
        public Quaternion Rotation;
        public Single Ground;

        public Boolean Drawn => Mode == WorldMode.Foot || Mode == WorldMode.Chocobo;

        public Int32 Serialize(Byte[] b, Int32 o)
        {
            Array.Clear(b, o, Size);
            b[o] = MessageType;
            PutU32(b, o + 1, Tick);
            b[o + 5] = (Byte)Mode;
            b[o + 6] = unchecked((Byte)Control);
            PutU16(b, o + 7, Model);
            PutU16(b, o + 9, Anim);
            PutF(b, o + 11, Position.x);
            PutF(b, o + 15, Position.y);
            PutF(b, o + 19, Position.z);
            PutF(b, o + 23, Rotation.x);
            PutF(b, o + 27, Rotation.y);
            PutF(b, o + 31, Rotation.z);
            PutF(b, o + 35, Rotation.w);
            PutF(b, o + 39, Ground);
            return Size;
        }

        public Boolean Deserialize(Byte[] b, Int32 o, Int32 length)
        {
            if (length < Size || b[o] != MessageType)
                return false;
            Tick = BitConverter.ToUInt32(b, o + 1);
            Mode = (WorldMode)b[o + 5];
            Control = unchecked((SByte)b[o + 6]);
            Model = BitConverter.ToUInt16(b, o + 7);
            Anim = BitConverter.ToUInt16(b, o + 9);
            Position = new Vector3(BitConverter.ToSingle(b, o + 11), BitConverter.ToSingle(b, o + 15), BitConverter.ToSingle(b, o + 19));
            Rotation = new Quaternion(BitConverter.ToSingle(b, o + 23), BitConverter.ToSingle(b, o + 27), BitConverter.ToSingle(b, o + 31), BitConverter.ToSingle(b, o + 35));
            Ground = BitConverter.ToSingle(b, o + 39);
            return !Single.IsNaN(Position.x) && !Single.IsNaN(Rotation.w);
        }

        private static void PutU32(Byte[] b, Int32 o, UInt32 v) => Array.Copy(BitConverter.GetBytes(v), 0, b, o, 4);
        private static void PutU16(Byte[] b, Int32 o, UInt16 v) => Array.Copy(BitConverter.GetBytes(v), 0, b, o, 2);
        private static void PutF(Byte[] b, Int32 o, Single v) => Array.Copy(BitConverter.GetBytes(v), 0, b, o, 4);
    }
}
