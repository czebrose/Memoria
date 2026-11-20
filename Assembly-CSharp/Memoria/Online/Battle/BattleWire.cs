using Memoria.Data;
using System;
using System.Collections.Generic;
using System.IO;

namespace Memoria.Online.Battle
{
    /// <summary>
    /// Step 3: the battle event stream on the wire (owner → participants, control channel, reliable and ordered).
    ///
    ///   BattleStream [28, seq u16, owner frame u32, (type u8, length u16, frame u32, body)*]
    ///
    /// Frames count the owner's battle frames from its Start event (frame 0). The message's frame says "the owner has
    /// run up to here": the passive PC never runs a battle frame the owner hasn't run plus a margin, and plays each event
    /// at its frame (see <see cref="BattleStreamIn"/>).
    ///
    /// Bodies (little-endian; a unit is its btl_id, u16; 0 = none):
    ///   Start      map u16, pattern u8, start type u8, phase u8, state
    ///   Command    seq u16, caster, slot s8, cmd u8, sub u16, target u16, cursor u8, priority u8, reflect u8, short range u8,
    ///              menu u8, custom MP i16, magic caster, script id i32, hit rate i32, power i32, category u8, type u8,
    ///              status u64, short summon s8, tape count u8, (kind u8, value i32)*
    ///   Effect     seq u16, caster, target, ops, figs count u8, (unit, info u16, hp i32, mp i32)*, dodge u8, state
    ///   CommandEnd seq u16, state
    ///   Tick       target, ops, state
    ///   Script     phase u8, target, ops, state
    ///   Sync       state
    ///   End        result u8, exp u32, gil i32, ap u16, card u16, items count u8, item u16*, state
    ///   AtbSample  count u8, (unit, value i16, rate i16 per second)*   (applied at its frame)
    ///   EscapeKey  held u8   (the party turns to run while the owner holds the run buttons; applied at its frame)
    ///   CmdMore    seq u16, short summon s8, tape count u8, (kind u8, value i32)*   (what a running command drew since
    ///              its Command went out; commands go out when they start, their tape grows while they run)
    ///
    /// "state" is the safety net: the units (and the party's gil / items) that changed since the last state sent, as
    /// <see cref="WriteState"/> describes. The passive PC keeps a mirror of the owner's state from these diffs, so every
    /// event carries the owner's full state at that point while only changes travel.
    /// </summary>
    public static class BattleWire
    {
        public const Byte MsgStream = 28;
        public const Byte AtbSample = 10; // stream-only event type (the debug log's Atb events use the old format)
        public const Byte EscapeKey = 12; // stream-only: the owner's player started / stopped holding "run" (btl_escape_key u8)
        public const Byte CmdMore = 11;   // stream-only: tape entries / summon choice of a command already sent

        // ---- state diffs --------------------------------------------------------------------------------------

        private const Byte FHp = 1, FMp = 2, FStatus = 4, FTrance = 8, FMax = 16, FPermanent = 32, FStats = 64, FRemoved = 128;

        /// <summary>
        /// The changes from <paramref name="before"/> (null: nothing known) to <paramref name="now"/>:
        /// [unit count u8, (unit u16, mask u8, fields by mask)*, party mask u8, gil u32 if 1, items if 2: count u16, (item u16, count u8)*].
        /// Unit mask: 1 HP u32, 2 MP u32, 4 status u64, 8 trance u8, 16 max HP/MP u32×2, 32 permanent status u64,
        /// 64 stats (str/mgc/wpr/dex u8, defences i16×4, targetable u8, disappear u8), 128 left the battle.
        /// </summary>
        public static void WriteState(BinaryWriter w, BattleSnapshot before, BattleSnapshot now)
        {
            List<KeyValuePair<UnitState, Byte>> units = new List<KeyValuePair<UnitState, Byte>>();
            foreach (UnitState u in now.Units)
            {
                UnitState b = before?.Find(u.Id);
                Byte mask = 0;
                if (b == null || b.Hp != u.Hp) mask |= FHp;
                if (b == null || b.Mp != u.Mp) mask |= FMp;
                if (b == null || b.Status != u.Status) mask |= FStatus;
                if (b == null || b.Trance != u.Trance) mask |= FTrance;
                if (b == null || b.MaxHp != u.MaxHp || b.MaxMp != u.MaxMp) mask |= FMax;
                if (b == null || b.Permanent != u.Permanent) mask |= FPermanent;
                if (b == null || b.Str != u.Str || b.Mgc != u.Mgc || b.Wpr != u.Wpr || b.Dex != u.Dex || b.PDef != u.PDef || b.PEva != u.PEva
                    || b.MDef != u.MDef || b.MEva != u.MEva || b.Targetable != u.Targetable || b.Disappear != u.Disappear) mask |= FStats;
                if (mask != 0)
                    units.Add(new KeyValuePair<UnitState, Byte>(u, mask));
            }
            if (before != null)
                foreach (UnitState b in before.Units)
                    if (now.Find(b.Id) == null)
                        units.Add(new KeyValuePair<UnitState, Byte>(b, FRemoved));
            w.Write((Byte)units.Count);
            foreach (KeyValuePair<UnitState, Byte> kv in units)
            {
                UnitState u = kv.Key;
                Byte mask = kv.Value;
                w.Write(u.Id);
                w.Write(mask);
                if ((mask & FHp) != 0) w.Write(u.Hp);
                if ((mask & FMp) != 0) w.Write(u.Mp);
                if ((mask & FStatus) != 0) w.Write(u.Status);
                if ((mask & FTrance) != 0) w.Write(u.Trance);
                if ((mask & FMax) != 0) { w.Write(u.MaxHp); w.Write(u.MaxMp); }
                if ((mask & FPermanent) != 0) w.Write(u.Permanent);
                if ((mask & FStats) != 0)
                {
                    w.Write(u.Str); w.Write(u.Mgc); w.Write(u.Wpr); w.Write(u.Dex);
                    w.Write((Int16)u.PDef); w.Write((Int16)u.PEva); w.Write((Int16)u.MDef); w.Write((Int16)u.MEva);
                    w.Write(u.Targetable); w.Write(u.Disappear);
                }
            }
            Byte party = 0;
            if (before == null || before.Gil != now.Gil)
                party |= 1;
            List<KeyValuePair<Int32, Int32>> items = new List<KeyValuePair<Int32, Int32>>();
            foreach (KeyValuePair<Int32, Int32> kv in now.Items)
            {
                Int32 had = 0;
                if (before == null || !before.Items.TryGetValue(kv.Key, out had) || had != kv.Value)
                    items.Add(kv);
            }
            if (before != null)
                foreach (KeyValuePair<Int32, Int32> kv in before.Items)
                    if (!now.Items.ContainsKey(kv.Key))
                        items.Add(new KeyValuePair<Int32, Int32>(kv.Key, 0));
            if (items.Count > 0)
                party |= 2;
            w.Write(party);
            if ((party & 1) != 0)
                w.Write(now.Gil);
            if ((party & 2) != 0)
            {
                w.Write((UInt16)items.Count);
                foreach (KeyValuePair<Int32, Int32> kv in items)
                {
                    w.Write((UInt16)kv.Key);
                    w.Write((Byte)Math.Min(255, kv.Value));
                }
            }
        }

        /// <summary>Applies a state diff to the mirror of the owner's state (changed in place).</summary>
        public static void ReadState(BinaryReader r, BattleSnapshot mirror)
        {
            Int32 count = r.ReadByte();
            for (Int32 i = 0; i < count; i++)
            {
                UInt16 id = r.ReadUInt16();
                Byte mask = r.ReadByte();
                UnitState u = mirror.Find(id);
                if ((mask & FRemoved) != 0)
                {
                    if (u != null)
                        mirror.Units.Remove(u);
                    continue;
                }
                if (u == null)
                {
                    u = new UnitState { Id = id };
                    mirror.Units.Add(u);
                }
                if ((mask & FHp) != 0) u.Hp = r.ReadUInt32();
                if ((mask & FMp) != 0) u.Mp = r.ReadUInt32();
                if ((mask & FStatus) != 0) u.Status = r.ReadUInt64();
                if ((mask & FTrance) != 0) u.Trance = r.ReadByte();
                if ((mask & FMax) != 0) { u.MaxHp = r.ReadUInt32(); u.MaxMp = r.ReadUInt32(); }
                if ((mask & FPermanent) != 0) u.Permanent = r.ReadUInt64();
                if ((mask & FStats) != 0)
                {
                    u.Str = r.ReadByte(); u.Mgc = r.ReadByte(); u.Wpr = r.ReadByte(); u.Dex = r.ReadByte();
                    u.PDef = r.ReadInt16(); u.PEva = r.ReadInt16(); u.MDef = r.ReadInt16(); u.MEva = r.ReadInt16();
                    u.Targetable = r.ReadByte(); u.Disappear = r.ReadByte();
                }
            }
            Byte party = r.ReadByte();
            if ((party & 1) != 0)
                mirror.Gil = r.ReadUInt32();
            if ((party & 2) != 0)
            {
                Int32 items = r.ReadUInt16();
                for (Int32 i = 0; i < items; i++)
                {
                    Int32 item = r.ReadUInt16();
                    Int32 n = r.ReadByte();
                    if (n == 0)
                        mirror.Items.Remove(item);
                    else
                        mirror.Items[item] = n;
                }
            }
        }

        public static BattleSnapshot Copy(BattleSnapshot s)
        {
            BattleSnapshot c = new BattleSnapshot();
            foreach (UnitState u in s.Units)
                c.Units.Add(new UnitState
                {
                    Id = u.Id, Hp = u.Hp, Mp = u.Mp, MaxHp = u.MaxHp, MaxMp = u.MaxMp, Status = u.Status, Permanent = u.Permanent, Trance = u.Trance,
                    Str = u.Str, Mgc = u.Mgc, Wpr = u.Wpr, Dex = u.Dex, PDef = u.PDef, PEva = u.PEva, MDef = u.MDef, MEva = u.MEva,
                    Targetable = u.Targetable, Disappear = u.Disappear, Atb = u.Atb
                });
            c.Gil = s.Gil;
            foreach (KeyValuePair<Int32, Int32> kv in s.Items)
                c.Items[kv.Key] = kv.Value;
            return c;
        }

        // ---- events -------------------------------------------------------------------------------------------

        /// <summary>Writes one event's body (without type and length). <paramref name="sent"/> is the owner's state as
        /// last sent; updated when the event carries a state.</summary>
        public static void WriteEvent(BinaryWriter w, BattleEvent ev, ref BattleSnapshot sent)
        {
            switch (ev.Type)
            {
                case BattleEventType.Start:
                    w.Write((UInt16)ev.MapNo); w.Write((Byte)ev.Pattern); w.Write((Byte)ev.StartType); w.Write(ev.Result);
                    break;
                case BattleEventType.Command:
                    w.Write((UInt16)ev.Seq); w.Write(ev.Caster); w.Write(ev.Slot); w.Write((Byte)ev.CmdNo); w.Write((UInt16)ev.SubNo); w.Write(ev.TarId);
                    w.Write(ev.Cursor); w.Write(ev.Priority); w.Write(ev.MonReflec); w.Write(ev.ShortRange);
                    w.Write((Byte)ev.Menu); w.Write((Int16)ev.CustomMp); w.Write(ev.MagicCaster);
                    w.Write(ev.ScriptId); w.Write(ev.HitRate); w.Write(ev.Power); w.Write(ev.Category); w.Write(ev.AbilType);
                    w.Write(ev.AbilityStatus); w.Write(ev.ShortSummon);
                    w.Write((Byte)ev.Tape.Count);
                    foreach (TapeEntry t in ev.Tape)
                    {
                        w.Write((Byte)t.Kind);
                        w.Write(t.Value);
                    }
                    return; // no state
                case BattleEventType.Effect:
                    w.Write((UInt16)ev.Seq); w.Write(ev.Caster); w.Write(ev.Target);
                    WriteOps(w, ev.Ops);
                    w.Write((Byte)ev.Figs.Count);
                    foreach (FigState f in ev.Figs)
                    {
                        w.Write(f.Unit); w.Write(f.Info); w.Write(f.Hp); w.Write(f.Mp);
                    }
                    w.Write((Byte)(ev.CmdDodge | (ev.TargetDodge << 1)));
                    break;
                case BattleEventType.CommandEnd:
                    w.Write((UInt16)ev.Seq);
                    break;
                case BattleEventType.Script:
                    w.Write(ev.Phase);
                    w.Write(ev.Target);
                    WriteOps(w, ev.Ops);
                    break;
                case BattleEventType.Tick:
                    w.Write(ev.Target);
                    WriteOps(w, ev.Ops);
                    break;
                case BattleEventType.Sync:
                    break;
                case BattleEventType.End:
                    w.Write(ev.Result); w.Write(ev.Exp); w.Write(ev.BonusGil); w.Write(ev.Ap); w.Write((UInt16)ev.Card);
                    w.Write((Byte)ev.BonusItems.Count);
                    foreach (RegularItem item in ev.BonusItems)
                        w.Write((UInt16)item);
                    break;
                default:
                    throw new ArgumentException($"event {ev.Type} isn't sent");
            }
            BattleSnapshot state = ev.State ?? sent ?? new BattleSnapshot();
            WriteState(w, sent, state);
            sent = state;
        }

        /// <summary>Reads one event body. <paramref name="mirror"/> is the owner's state as last received: updated, and
        /// a copy becomes the event's State.</summary>
        public static BattleEvent ReadEvent(BattleEventType type, BinaryReader r, BattleSnapshot mirror)
        {
            BattleEvent ev = new BattleEvent { Type = type };
            switch (type)
            {
                case BattleEventType.Start:
                    ev.MapNo = r.ReadUInt16(); ev.Pattern = r.ReadByte(); ev.StartType = r.ReadByte(); ev.Result = r.ReadByte();
                    break;
                case BattleEventType.Command:
                    ev.Seq = r.ReadUInt16(); ev.Caster = r.ReadUInt16(); ev.Slot = r.ReadSByte(); ev.CmdNo = (BattleCommandId)r.ReadByte();
                    ev.SubNo = r.ReadUInt16(); ev.TarId = r.ReadUInt16();
                    ev.Cursor = r.ReadByte(); ev.Priority = r.ReadByte(); ev.MonReflec = r.ReadByte(); ev.ShortRange = r.ReadBoolean();
                    ev.Menu = (BattleCommandMenu)r.ReadByte(); ev.CustomMp = r.ReadInt16(); ev.MagicCaster = r.ReadUInt16();
                    ev.ScriptId = r.ReadInt32(); ev.HitRate = r.ReadInt32(); ev.Power = r.ReadInt32(); ev.Category = r.ReadByte(); ev.AbilType = r.ReadByte();
                    ev.AbilityStatus = r.ReadUInt64(); ev.ShortSummon = r.ReadSByte();
                    Int32 tape = r.ReadByte();
                    for (Int32 i = 0; i < tape; i++)
                        ev.Tape.Add(new TapeEntry { Kind = (TapeKind)r.ReadByte(), Value = r.ReadInt32() });
                    return ev;
                case BattleEventType.Effect:
                    ev.Seq = r.ReadUInt16(); ev.Caster = r.ReadUInt16(); ev.Target = r.ReadUInt16();
                    ReadOps(r, ev.Ops);
                    Int32 figs = r.ReadByte();
                    for (Int32 i = 0; i < figs; i++)
                        ev.Figs.Add(new FigState { Unit = r.ReadUInt16(), Info = r.ReadUInt16(), Hp = r.ReadInt32(), Mp = r.ReadInt32() });
                    Byte dodge = r.ReadByte();
                    ev.CmdDodge = (Byte)(dodge & 1);
                    ev.TargetDodge = (Byte)((dodge >> 1) & 1);
                    break;
                case BattleEventType.CommandEnd:
                    ev.Seq = r.ReadUInt16();
                    break;
                case BattleEventType.Script:
                    ev.Phase = r.ReadByte();
                    ev.Target = r.ReadUInt16();
                    ReadOps(r, ev.Ops);
                    break;
                case BattleEventType.Tick:
                    ev.Target = r.ReadUInt16();
                    ReadOps(r, ev.Ops);
                    break;
                case BattleEventType.Sync:
                    break;
                case BattleEventType.End:
                    ev.Result = r.ReadByte(); ev.Exp = r.ReadUInt32(); ev.BonusGil = r.ReadInt32(); ev.Ap = r.ReadUInt16(); ev.Card = (TetraMasterCardId)r.ReadUInt16();
                    Int32 n = r.ReadByte();
                    ev.BonusItems = new List<RegularItem>();
                    for (Int32 i = 0; i < n; i++)
                        ev.BonusItems.Add((RegularItem)r.ReadUInt16());
                    break;
                default:
                    throw new FormatException($"unknown battle event type {type}");
            }
            ReadState(r, mirror);
            ev.State = Copy(mirror);
            return ev;
        }

        // ---- self-check -------------------------------------------------------------------------------------

        /// <summary>Sends a whole log through the wire format and back (no network): the copy, and what differs.
        /// Run at the end of every recorded or owned battle; the debug replay plays the copy.</summary>
        public static BattleLog RoundTrip(BattleLog log, List<String> problems)
        {
            BattleLog copy = new BattleLog { MapNo = log.MapNo, Pattern = log.Pattern, StartType = log.StartType, StartPhase = log.StartPhase, Complete = log.Complete };
            BattleSnapshot sent = null;
            BattleSnapshot mirror = new BattleSnapshot();
            foreach (BattleEvent ev in log.Events)
            {
                if (ev.Type == BattleEventType.Atb)
                {
                    copy.Events.Add(ev); // the debug replay's gauges (not part of the stream)
                    continue;
                }
                MemoryStream ms = new MemoryStream();
                BinaryWriter w = new BinaryWriter(ms);
                WriteEvent(w, ev, ref sent);
                w.Flush();
                ms.Position = 0;
                BinaryReader r = new BinaryReader(ms);
                BattleEvent back = ReadEvent(ev.Type, r, mirror);
                if (ms.Position != ms.Length)
                    problems.Add($"{ev.Type}: {ms.Length - ms.Position} byte(s) not read");
                back.Frame = ev.Frame;
                if (back.Type == BattleEventType.Command)
                    back.State = null;
                String a = Signature(ev), b = Signature(back);
                if (a != b)
                    problems.Add($"{a.Split('\n')[0]} came back as {b.Split('\n')[0]}");
                if (ev.State != null && back.State != null)
                {
                    List<String> d = ev.State.Diff(back.State);
                    d.AddRange(back.State.Diff(ev.State));
                    if (d.Count > 0)
                        problems.Add($"{ev.Type} state: {String.Join("; ", d.ToArray())}");
                }
                copy.Events.Add(back);
            }
            return copy;
        }

        private static String Signature(BattleEvent ev)
        {
            String s = ev.ToString();
            switch (ev.Type)
            {
                case BattleEventType.Start:
                case BattleEventType.CommandEnd:
                case BattleEventType.Sync:
                case BattleEventType.End:
                    Int32 cut = s.IndexOf('\n');
                    return cut < 0 ? s : s.Substring(0, cut); // the state is compared on its own
                default:
                    return s;
            }
        }

        // ---- ops ----------------------------------------------------------------------------------------------

        private static void WriteOps(BinaryWriter w, List<BattleOp> ops)
        {
            w.Write((Byte)ops.Count);
            foreach (BattleOp op in ops)
            {
                w.Write((Byte)op.Kind);
                w.Write(op.Unit);
                w.Write(op.A); w.Write(op.B); w.Write(op.C);
                w.Write(op.Other);
                w.Write(op.Result);
                if (op.Kind == OpKind.Message)
                    w.Write(op.Text ?? String.Empty);
                if (op.Kind == OpKind.Alter)
                    WriteParams(w, op.Params);
            }
        }

        private static void ReadOps(BinaryReader r, List<BattleOp> ops)
        {
            Int32 count = r.ReadByte();
            for (Int32 i = 0; i < count; i++)
            {
                BattleOp op = new BattleOp { Kind = (OpKind)r.ReadByte(), Unit = r.ReadUInt16(), A = r.ReadInt32(), B = r.ReadInt32(), C = r.ReadInt32(), Other = r.ReadUInt16(), Result = r.ReadUInt32() };
                if (op.Kind == OpKind.Message)
                    op.Text = r.ReadString();
                if (op.Kind == OpKind.Alter)
                    op.Params = ReadParams(r);
                ops.Add(op);
            }
        }

        // Status parameters (AlterStatus(..., params Object[])): enums, numbers, booleans, strings
        private static void WriteParams(BinaryWriter w, Object[] p)
        {
            Int32 count = p?.Length ?? 0;
            w.Write((Byte)count);
            for (Int32 i = 0; i < count; i++)
            {
                Object o = p[i];
                if (o == null) { w.Write((Byte)0); continue; }
                Type t = o.GetType();
                if (t.IsEnum) { w.Write((Byte)1); w.Write(t.FullName); w.Write(Convert.ToInt64(o)); }
                else if (o is Boolean) { w.Write((Byte)2); w.Write((Boolean)o); }
                else if (o is Single) { w.Write((Byte)3); w.Write((Single)o); }
                else if (o is String) { w.Write((Byte)4); w.Write((String)o); }
                else if (o is IConvertible) { w.Write((Byte)5); w.Write(t.FullName); w.Write(Convert.ToInt64(o)); }
                else { w.Write((Byte)0); BattleSync.Note($"status parameter of type {t.Name} can't be sent (sent as null)"); }
            }
        }

        private static readonly Dictionary<String, Type> TypeCache = new Dictionary<String, Type>();

        private static Type FindType(String name)
        {
            if (TypeCache.TryGetValue(name, out Type t))
                return t;
            t = Type.GetType(name);
            if (t == null)
                foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    if ((t = a.GetType(name)) != null)
                        break;
            TypeCache[name] = t;
            return t;
        }

        private static Object[] ReadParams(BinaryReader r)
        {
            Int32 count = r.ReadByte();
            Object[] p = new Object[count];
            for (Int32 i = 0; i < count; i++)
            {
                switch (r.ReadByte())
                {
                    case 1:
                    {
                        Type t = FindType(r.ReadString());
                        Int64 v = r.ReadInt64();
                        p[i] = t != null ? Enum.ToObject(t, v) : (Object)(Int32)v;
                        break;
                    }
                    case 2: p[i] = r.ReadBoolean(); break;
                    case 3: p[i] = r.ReadSingle(); break;
                    case 4: p[i] = r.ReadString(); break;
                    case 5:
                    {
                        Type t = FindType(r.ReadString());
                        Int64 v = r.ReadInt64();
                        p[i] = t != null ? Convert.ChangeType(v, t) : (Object)(Int32)v;
                        break;
                    }
                }
            }
            return p;
        }
    }
}
