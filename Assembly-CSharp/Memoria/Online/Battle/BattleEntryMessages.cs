using System;
using System.Collections.Generic;

namespace Memoria.Online.Battle
{
    /// <summary>
    /// Spike D: the battle-entry messages (control channel, reliable). Little-endian. Type bytes 20-27 (Step 2 uses
    /// 1-9). "seq" is the owner's battle counter, so a late message about an older battle is ignored.
    ///
    ///   Presence  [20, field u16, state]                                     4 B, every PC → every peer, 2/s + on change
    ///   Pending   [21, seq u16, field u16, battle u16]                       7 B, owner → participants, at the trigger
    ///   Start     [22, seq u16, ...BattleStartData]                          ~30 B + 17/character + 3/unit
    ///   Ready     [23, seq u16]                                              3 B, participant → owner (its battle is loaded, intro done)
    ///   Decline   [24, seq u16, reason]                                      4 B, participant → owner
    ///   Go        [25, seq u16]                                              3 B, owner → participants (everyone ready; owner's ATB runs)
    ///   End       [26, seq u16, result]                                      4 B, owner → participants (FF9StateGlobal.BTL_RESULT_*; 0 = cancelled)
    ///   Alive     [27, seq u16]                                              3 B, owner → participants, 1/s while its battle runs
    /// </summary>
    internal static class BattleEntryMessages
    {
        public const Byte Presence = 20;
        public const Byte Pending = 21;
        public const Byte Start = 22;
        public const Byte Ready = 23;
        public const Byte Decline = 24;
        public const Byte Go = 25;
        public const Byte End = 26;
        public const Byte Alive = 27;

        public static Boolean IsBattleEntry(Byte[] data) => data != null && data.Length >= 3 && data[0] >= Presence && data[0] <= Alive;

        public static Byte[] Short(Byte type, UInt16 seq, params Byte[] extra)
        {
            Byte[] message = new Byte[3 + extra.Length];
            message[0] = type;
            message[1] = (Byte)seq;
            message[2] = (Byte)(seq >> 8);
            Buffer.BlockCopy(extra, 0, message, 3, extra.Length);
            return message;
        }

        public static UInt16 Seq(Byte[] data) => (UInt16)(data[1] | (data[2] << 8));
    }

    /// <summary>What a participant's PC needs to start the owner's battle: same scene, enemies, layout, flags, party.</summary>
    internal sealed class BattleStartData
    {
        public const Byte FlagRandomEncounter = 1;
        public const Byte FlagSteiner = 2;
        public const Byte FlagEncount = 4;

        public sealed class Member
        {
            public Byte Character;   // CharacterId
            public UInt32 Hp, Mp;
            public UInt64 Status;    // PLAYER.status (BattleStatus)
            public Byte Trance;
        }

        public UInt16 Seq;
        public UInt16 BattleId;      // FF9StateSystem.Battle.battleMapIndex: scene, background, enemy groups, battle scripts
        public Byte Pattern;         // btl_scene.PatNum: the enemy group (forced on the participant through btlSubMapNo)
        public Byte StartType;       // battle_start_type_tags: normal / back attack / preemptive
        public Byte Flags;
        public UInt16 Field;         // the owner's field (the participant must be on it: battle music and the return spot)
        public Int32 Seed;           // a fresh UnityEngine.Random seed (applied on both PCs at battle init; extra safety, not relied on)
        public readonly List<Member> Members = new List<Member>();  // battle party, in battle order
        public readonly List<KeyValuePair<Byte, Int16>> Units = new List<KeyValuePair<Byte, Int16>>(); // every unit in btl_list order: (character id | 0x80+enemy type, initial ATB)

        public Byte[] Serialize()
        {
            List<Byte> b = new List<Byte> { BattleEntryMessages.Start };
            U16(b, Seq);
            U16(b, BattleId);
            b.Add(Pattern);
            b.Add(StartType);
            b.Add(Flags);
            U16(b, Field);
            U32(b, (UInt32)Seed);
            b.Add((Byte)Members.Count);
            foreach (Member m in Members)
            {
                b.Add(m.Character);
                U32(b, m.Hp);
                U32(b, m.Mp);
                U32(b, (UInt32)m.Status);
                U32(b, (UInt32)(m.Status >> 32));
                b.Add(m.Trance);
            }
            b.Add((Byte)Units.Count);
            foreach (KeyValuePair<Byte, Int16> u in Units)
            {
                b.Add(u.Key);
                U16(b, (UInt16)u.Value);
            }
            return b.ToArray();
        }

        public static BattleStartData Parse(Byte[] d)
        {
            Int32 o = 1;
            BattleStartData s = new BattleStartData();
            s.Seq = R16(d, ref o);
            s.BattleId = R16(d, ref o);
            s.Pattern = d[o++];
            s.StartType = d[o++];
            s.Flags = d[o++];
            s.Field = R16(d, ref o);
            s.Seed = (Int32)R32(d, ref o);
            Int32 count = d[o++];
            for (Int32 i = 0; i < count; i++)
            {
                Member m = new Member { Character = d[o++], Hp = R32(d, ref o), Mp = R32(d, ref o) };
                UInt64 lo = R32(d, ref o), hi = R32(d, ref o);
                m.Status = lo | (hi << 32);
                m.Trance = d[o++];
                s.Members.Add(m);
            }
            Int32 units = d[o++];
            for (Int32 i = 0; i < units; i++)
            {
                Byte key = d[o++];
                s.Units.Add(new KeyValuePair<Byte, Int16>(key, (Int16)R16(d, ref o)));
            }
            if (o != d.Length)
                throw new FormatException($"BattleStart: {d.Length} bytes, {o} read");
            return s;
        }

        public String Describe()
        {
            List<String> members = new List<String>();
            foreach (Member m in Members)
                members.Add($"{m.Character} (hp {m.Hp}, mp {m.Mp}{(m.Status != 0 ? $", status 0x{m.Status:X}" : "")})");
            List<String> units = new List<String>();
            foreach (KeyValuePair<Byte, Int16> u in Units)
                units.Add($"{((u.Key & 0x80) != 0 ? $"enemy type {u.Key & 0x7F}" : $"char {u.Key}")} at {u.Value}");
            return $"battle {BattleId}, pattern {Pattern}, start {StartType}, flags {Flags}, field {Field}, party [{String.Join(", ", members.ToArray())}], units [{String.Join(", ", units.ToArray())}]";
        }

        private static void U16(List<Byte> b, UInt16 v) { b.Add((Byte)v); b.Add((Byte)(v >> 8)); }
        private static void U32(List<Byte> b, UInt32 v) { b.Add((Byte)v); b.Add((Byte)(v >> 8)); b.Add((Byte)(v >> 16)); b.Add((Byte)(v >> 24)); }
        private static UInt16 R16(Byte[] d, ref Int32 o) { UInt16 v = (UInt16)(d[o] | (d[o + 1] << 8)); o += 2; return v; }
        private static UInt32 R32(Byte[] d, ref Int32 o) { UInt32 v = d[o] | ((UInt32)d[o + 1] << 8) | ((UInt32)d[o + 2] << 16) | ((UInt32)d[o + 3] << 24); o += 4; return v; }
    }
}
