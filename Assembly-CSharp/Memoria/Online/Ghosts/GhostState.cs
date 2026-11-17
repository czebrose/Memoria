using System;

namespace Memoria.Online.Ghosts
{
    public enum GhostMotion : Byte
    {
        Idle = 0,
        Walk = 1,
        Run = 2,
        /// <summary>Any other animation (ladder, jump, scripted pose...): <see cref="GhostState.Anim"/> says which.</summary>
        Other = 3,
    }

    /// <summary>
    /// Everything another player needs to draw one player's field character. Sent on channel 1 (unreliable)
    /// at 10-15 Hz. 23 bytes on the wire, little-endian:
    ///
    ///   0  type      byte    MessageType
    ///   1  tick      uint32  sender's clock in milliseconds (ordering + interpolation timeline)
    ///   5  fieldId   uint16  FF9.fldMapNo; the ghost is only shown in the same field
    ///   7  model     uint16  the sender's field model (Actor.model)
    ///   9  x, y, z   int16×3 FieldMapActorController.curPos (field coordinates fit in 16 bits; y is sent because the
    ///                        ghost does no walkmesh collision of its own)
    ///  15  tri       int16   walkmesh triangle (-1 = none): gives the floor for lighting/shadows, which position alone
    ///                        can't pick where floors overlap
    ///  17  facing    uint16  Actor.rotAngle[1] (degrees) scaled to 65536 per turn
    ///  19  anim      uint16  the exact animation id playing (meaningful when the receiver uses the same model)
    ///  21  frame     byte    its frame, to resynchronise on animation changes
    ///  22  motion    byte    GhostMotion: idle/walk/run/other, for receivers drawing a different model
    /// </summary>
    public struct GhostState
    {
        public const Byte MessageType = 0x10;
        public const Int32 Size = 23;
        /// <summary>FieldId sent while the player isn't on a field (battle, world map, menus): receivers hide the ghost at once.</summary>
        public const UInt16 NotOnField = 0xFFFF;

        public UInt32 Tick;
        public UInt16 FieldId;
        public UInt16 Model;
        public Int16 X, Y, Z;
        public Int16 Tri;
        public UInt16 Facing;
        public UInt16 Anim;
        public Byte AnimFrame;
        public GhostMotion Motion;

        public Single FacingDegrees
        {
            get => Facing * (360f / 65536f);
            set => Facing = (UInt16)Math.Round(((value % 360f + 360f) % 360f) * (65536f / 360f));
        }

        /// <summary>Writes the state at <paramref name="offset"/>; returns the number of bytes written (<see cref="Size"/>).</summary>
        public Int32 Serialize(Byte[] buffer, Int32 offset)
        {
            buffer[offset] = MessageType;
            WriteU32(buffer, offset + 1, Tick);
            WriteU16(buffer, offset + 5, FieldId);
            WriteU16(buffer, offset + 7, Model);
            WriteU16(buffer, offset + 9, (UInt16)X);
            WriteU16(buffer, offset + 11, (UInt16)Y);
            WriteU16(buffer, offset + 13, (UInt16)Z);
            WriteU16(buffer, offset + 15, (UInt16)Tri);
            WriteU16(buffer, offset + 17, Facing);
            WriteU16(buffer, offset + 19, Anim);
            buffer[offset + 21] = AnimFrame;
            buffer[offset + 22] = (Byte)Motion;
            return Size;
        }

        /// <summary>Reads a state written by <see cref="Serialize"/>; false if the bytes aren't one.</summary>
        public Boolean Deserialize(Byte[] buffer, Int32 offset, Int32 length)
        {
            if (length < Size || buffer[offset] != MessageType)
                return false;
            Tick = ReadU32(buffer, offset + 1);
            FieldId = ReadU16(buffer, offset + 5);
            Model = ReadU16(buffer, offset + 7);
            X = (Int16)ReadU16(buffer, offset + 9);
            Y = (Int16)ReadU16(buffer, offset + 11);
            Z = (Int16)ReadU16(buffer, offset + 13);
            Tri = (Int16)ReadU16(buffer, offset + 15);
            Facing = ReadU16(buffer, offset + 17);
            Anim = ReadU16(buffer, offset + 19);
            AnimFrame = buffer[offset + 21];
            Motion = buffer[offset + 22] <= (Byte)GhostMotion.Other ? (GhostMotion)buffer[offset + 22] : GhostMotion.Idle;
            return true;
        }

        public override String ToString() => $"tick {Tick} field {FieldId} model {Model} pos ({X},{Y},{Z}) tri {Tri} facing {FacingDegrees:0} anim {Anim}:{AnimFrame} {Motion}";

        private static void WriteU16(Byte[] b, Int32 o, UInt16 v)
        {
            b[o] = (Byte)v;
            b[o + 1] = (Byte)(v >> 8);
        }

        private static void WriteU32(Byte[] b, Int32 o, UInt32 v)
        {
            b[o] = (Byte)v;
            b[o + 1] = (Byte)(v >> 8);
            b[o + 2] = (Byte)(v >> 16);
            b[o + 3] = (Byte)(v >> 24);
        }

        private static UInt16 ReadU16(Byte[] b, Int32 o) => (UInt16)(b[o] | (b[o + 1] << 8));

        private static UInt32 ReadU32(Byte[] b, Int32 o) => b[o] | ((UInt32)b[o + 1] << 8) | ((UInt32)b[o + 2] << 16) | ((UInt32)b[o + 3] << 24);
    }
}
