using FF9;
using Memoria.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Memoria.Online.Battle
{
    /// <summary>Spike C: the comparable state of one combatant (HP/MP/status and the stats an action can change).
    /// ATB is carried but never compared (it is display state, sent separately).</summary>
    public sealed class UnitState
    {
        public UInt16 Id;
        public UInt32 Hp, Mp, MaxHp, MaxMp;
        public UInt64 Status, Permanent;
        public Byte Trance;
        public Byte Str, Mgc, Wpr, Dex;
        public Int32 PDef, PEva, MDef, MEva;
        public Byte Targetable, Disappear;
        public Int16 Atb;

        public Boolean IsDown => Hp == 0 || (Status & (UInt64)BattleStatus.Death) != 0;

        public static UnitState Capture(BTL_DATA btl)
        {
            return new UnitState
            {
                Id = btl.btl_id,
                Hp = btl.cur.hp,
                Mp = btl.cur.mp,
                MaxHp = btl.max.hp,
                MaxMp = btl.max.mp,
                Status = (UInt64)btl.stat.cur,
                Permanent = (UInt64)btl.stat.permanent,
                Trance = btl.trance,
                Str = btl.elem.str,
                Mgc = btl.elem.mgc,
                Wpr = btl.elem.wpr,
                Dex = btl.elem.dex,
                PDef = btl.defence.PhysicalDefence,
                PEva = btl.defence.PhysicalEvade,
                MDef = btl.defence.MagicalDefence,
                MEva = btl.defence.MagicalEvade,
                Targetable = btl.bi.target,
                Disappear = btl.bi.disappear,
                Atb = btl.cur.at
            };
        }

        public void Diff(UnitState o, List<String> diffs)
        {
            String n = BattleSpike.UnitName(Id);
            if (Hp != o.Hp) diffs.Add($"{n}.hp {Hp}->{o.Hp}");
            if (Mp != o.Mp) diffs.Add($"{n}.mp {Mp}->{o.Mp}");
            if (MaxHp != o.MaxHp) diffs.Add($"{n}.maxhp {MaxHp}->{o.MaxHp}");
            if (MaxMp != o.MaxMp) diffs.Add($"{n}.maxmp {MaxMp}->{o.MaxMp}");
            if (Status != o.Status) diffs.Add($"{n}.status +[{(BattleStatus)(o.Status & ~Status)}] -[{(BattleStatus)(Status & ~o.Status)}]");
            if (Permanent != o.Permanent) diffs.Add($"{n}.permanent {(BattleStatus)Permanent}->{(BattleStatus)o.Permanent}");
            if (Trance != o.Trance) diffs.Add($"{n}.trance {Trance}->{o.Trance}");
            if (Str != o.Str || Mgc != o.Mgc || Wpr != o.Wpr || Dex != o.Dex) diffs.Add($"{n}.elem {Str}/{Mgc}/{Wpr}/{Dex}->{o.Str}/{o.Mgc}/{o.Wpr}/{o.Dex}");
            if (PDef != o.PDef || PEva != o.PEva || MDef != o.MDef || MEva != o.MEva) diffs.Add($"{n}.defence {PDef}/{PEva}/{MDef}/{MEva}->{o.PDef}/{o.PEva}/{o.MDef}/{o.MEva}");
            if (Targetable != o.Targetable) diffs.Add($"{n}.target {Targetable}->{o.Targetable}");
            // Disappear isn't compared: it follows the die sequence's own timing (visual)
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Id); w.Write(Hp); w.Write(Mp); w.Write(MaxHp); w.Write(MaxMp);
            w.Write(Status); w.Write(Permanent); w.Write(Trance);
            w.Write(Str); w.Write(Mgc); w.Write(Wpr); w.Write(Dex);
            w.Write((Int16)PDef); w.Write((Int16)PEva); w.Write((Int16)MDef); w.Write((Int16)MEva);
            w.Write(Targetable); w.Write(Disappear); w.Write(Atb);
        }

        public override String ToString()
        {
            return $"{BattleSpike.UnitName(Id)} hp {Hp}/{MaxHp} mp {Mp}/{MaxMp} st [{(BattleStatus)Status}] tr {Trance} at {Atb}";
        }
    }

    /// <summary>Spike C: the comparable state of the whole battle: every combatant, gil and the party's items.</summary>
    public sealed class BattleSnapshot
    {
        public readonly List<UnitState> Units = new List<UnitState>();
        public UInt32 Gil;
        public readonly Dictionary<Int32, Int32> Items = new Dictionary<Int32, Int32>();

        public static BattleSnapshot Capture()
        {
            BattleSnapshot s = new BattleSnapshot();
            for (BTL_DATA btl = FF9StateSystem.Battle.FF9Battle.btl_list.next; btl != null; btl = btl.next)
                s.Units.Add(UnitState.Capture(btl));
            s.Gil = FF9StateSystem.Common.FF9.party.gil;
            foreach (FF9ITEM item in FF9StateSystem.Common.FF9.item)
                if (item != null && item.count > 0)
                    s.Items[(Int32)item.id] = item.count;
            return s;
        }

        public UnitState Find(UInt16 id)
        {
            foreach (UnitState u in Units)
                if (u.Id == id)
                    return u;
            return null;
        }

        /// <summary>Differences from this (expected) to <paramref name="actual"/>; empty when they match.</summary>
        public List<String> Diff(BattleSnapshot actual)
        {
            List<String> diffs = new List<String>();
            foreach (UnitState u in Units)
            {
                UnitState o = actual.Find(u.Id);
                if (o == null && !u.IsDown)
                    diffs.Add($"{BattleSpike.UnitName(u.Id)} missing");
                else if (o == null)
                    continue; // a dead unit leaves btl_list at the end of its die sequence: timing, not state
                else
                    u.Diff(o, diffs);
            }
            foreach (UnitState o in actual.Units)
                if (Find(o.Id) == null && !o.IsDown)
                    diffs.Add($"{BattleSpike.UnitName(o.Id)} extra");
            if (Gil != actual.Gil)
                diffs.Add($"gil {Gil}->{actual.Gil}");
            foreach (KeyValuePair<Int32, Int32> kv in Items)
            {
                actual.Items.TryGetValue(kv.Key, out Int32 count);
                if (count != kv.Value)
                    diffs.Add($"item {(RegularItem)kv.Key} {kv.Value}->{count}");
            }
            foreach (KeyValuePair<Int32, Int32> kv in actual.Items)
                if (!Items.ContainsKey(kv.Key))
                    diffs.Add($"item {(RegularItem)kv.Key} 0->{kv.Value}");
            return diffs;
        }

        public void Write(BinaryWriter w)
        {
            w.Write((Byte)Units.Count);
            foreach (UnitState u in Units)
                u.Write(w);
            w.Write(Gil);
            w.Write((UInt16)Items.Count);
            foreach (KeyValuePair<Int32, Int32> kv in Items)
            {
                w.Write((UInt16)kv.Key);
                w.Write((Byte)kv.Value);
            }
        }

        /// <summary>Make the running battle match this snapshot. Statuses go through the engine (RemoveStatus /
        /// AlterStatus) so their visuals follow; numbers are set directly. Returns what had to change.</summary>
        public List<String> ApplyToBattle()
        {
            BattleSnapshot now = Capture();
            List<String> changed = now.Diff(this);
            BattleSpike.Applying++;
            try { ApplyCore(now); }
            finally { BattleSpike.Applying--; }
            return changed;
        }

        private void ApplyCore(BattleSnapshot now)
        {
            foreach (UnitState u in Units)
            {
                BTL_DATA btl = BattleSpike.FindBtl(u.Id);
                if (btl == null)
                    continue;
                BattleUnit unit = new BattleUnit(btl);
                btl.max.hp = u.MaxHp;
                btl.max.mp = u.MaxMp;
                btl.cur.hp = u.Hp;
                btl.cur.mp = u.Mp;
                btl.trance = u.Trance;
                btl.elem.str = u.Str; btl.elem.mgc = u.Mgc; btl.elem.wpr = u.Wpr; btl.elem.dex = u.Dex;
                btl.defence.PhysicalDefence = u.PDef; btl.defence.PhysicalEvade = u.PEva;
                btl.defence.MagicalDefence = u.MDef; btl.defence.MagicalEvade = u.MEva;
                btl.bi.target = u.Targetable;
                btl.stat.permanent = (BattleStatus)u.Permanent;
                BattleStatus want = (BattleStatus)u.Status;
                BattleStatus removed = btl.stat.cur & ~want;
                BattleStatus added = want & ~btl.stat.cur;
                foreach (BattleStatusId id in removed.ToStatusList())
                    btl_stat.RemoveStatus(unit, id);
                foreach (BattleStatusId id in added.ToStatusList())
                    btl_stat.AlterStatus(unit, id);
                if (btl.stat.cur != want)
                    btl.stat.cur = want; // the engine refused (e.g. a status blocked by another); force the bits
            }
            FF9StateSystem.Common.FF9.party.gil = Gil;
            foreach (KeyValuePair<Int32, Int32> kv in now.Items)
            {
                Items.TryGetValue(kv.Key, out Int32 want);
                if (kv.Value > want)
                    ff9item.FF9Item_Remove((RegularItem)kv.Key, kv.Value - want);
            }
            foreach (KeyValuePair<Int32, Int32> kv in Items)
            {
                now.Items.TryGetValue(kv.Key, out Int32 have);
                if (kv.Value > have)
                    ff9item.FF9Item_Add((RegularItem)kv.Key, kv.Value - have);
            }
        }

        public override String ToString()
        {
            StringBuilder sb = new StringBuilder();
            foreach (UnitState u in Units)
                sb.Append("    ").Append(u).Append('\n');
            sb.Append($"    gil {Gil}, {Items.Count} item kinds");
            return sb.ToString();
        }
    }

    public enum OpKind : byte
    {
        Damage = 1,     // btl_para.SetDamage(unit, A, (Byte)B, cmd, C != 0)
        Recover = 2,    // btl_para.SetRecover(unit, A, C != 0)
        MpDamage = 3,   // btl_para.SetMpDamage(unit, A)
        MpRecover = 4,  // btl_para.SetMpRecover(unit, A)
        Alter = 5,      // btl_stat.AlterStatus(unit, (BattleStatusId)A, Other, false, Params) → Result
        Remove = 6,     // btl_stat.RemoveStatus(unit, (BattleStatusId)A)
        Kill = 7,       // BattleUnit.Kill(Other)
        Figure = 8,     // btl2d.Btl2dReqInstant(unit, (UInt16)A, B, C)
        StatFigure = 9, // btl2d.Btl2dStatReq(unit, A, B)
        Message = 10,   // BattleHUD.SetBattleMessage(Text, (Byte)A)
        ScriptUnitSet = 11,   // btl_scrp.SetCharacterData(unit, (UInt32)A, B): a battle script changing a unit
        ScriptBattleSet = 12, // btl_scrp.SetBattleData((UInt32)A, B): a battle script changing the battle
    }

    /// <summary>Spike C: one engine-level "apply" call made while calculating an action (or a status tick). Replaying
    /// the same calls in the same order reproduces the outcome, its damage numbers, motions and status visuals.</summary>
    public sealed class BattleOp
    {
        public OpKind Kind;
        public UInt16 Unit;
        public Int32 A, B, C;
        public UInt16 Other;
        public Object[] Params;
        public UInt32 Result;
        public Boolean Discard;
        public String Text;

        public void Write(BinaryWriter w)
        {
            w.Write((Byte)Kind);
            w.Write((Byte)BattleSpike.UnitIndex(Unit));
            switch (Kind)
            {
                case OpKind.Damage:
                    w.Write(A); w.Write((Byte)(B | (C << 1))); break;
                case OpKind.Recover:
                    w.Write(A); w.Write((Byte)C); break;
                case OpKind.MpDamage:
                case OpKind.MpRecover:
                    w.Write(A); break;
                case OpKind.Alter:
                    w.Write((Byte)A); w.Write((Byte)BattleSpike.UnitIndex(Other)); w.Write((Byte)Result);
                    WriteParams(w, Params); break;
                case OpKind.Remove:
                    w.Write((Byte)A); break;
                case OpKind.Kill:
                    w.Write((Byte)BattleSpike.UnitIndex(Other)); break;
                case OpKind.Figure:
                    w.Write((UInt16)A); w.Write(B); w.Write(C); break;
                case OpKind.StatFigure:
                    w.Write(A); w.Write(B); break;
                case OpKind.Message:
                    w.Write((Byte)A); w.Write(Text ?? String.Empty); break;
                case OpKind.ScriptUnitSet:
                case OpKind.ScriptBattleSet:
                    w.Write((Byte)A); w.Write(B); break;
            }
        }

        private static void WriteParams(BinaryWriter w, Object[] p)
        {
            Int32 count = p?.Length ?? 0;
            w.Write((Byte)count);
            for (Int32 i = 0; i < count; i++)
            {
                Object o = p[i];
                if (o == null) { w.Write((Byte)0); continue; }
                Type t = o.GetType();
                if (t.IsEnum) { w.Write((Byte)1); w.Write(t.Name); w.Write(Convert.ToInt64(o)); }
                else if (o is Boolean) { w.Write((Byte)2); w.Write((Boolean)o); }
                else if (o is Single) { w.Write((Byte)3); w.Write((Single)o); }
                else if (o is String) { w.Write((Byte)4); w.Write((String)o); }
                else if (o is IConvertible) { w.Write((Byte)5); w.Write(t.Name); w.Write(Convert.ToInt64(o)); }
                else { w.Write((Byte)6); w.Write(t.Name); BattleSpike.Note($"status parameter of type {t.Name} can't be serialised"); }
            }
        }

        /// <summary>Run the op on the passive client. Returns a mismatch description, or null.</summary>
        public String Apply(CMD_DATA cmd)
        {
            if (Kind == OpKind.Message)
            {
                UIManager.Battle.SetBattleMessage(Text, (Byte)A);
                return null;
            }
            if (Kind == OpKind.ScriptBattleSet)
            {
                btl_scrp.SetBattleData((UInt32)A, B);
                return null;
            }
            BTL_DATA btl = BattleSpike.FindBtl(Unit);
            if (btl == null)
                return $"op {Kind} on missing unit {BattleSpike.UnitName(Unit)}";
            BattleUnit unit = new BattleUnit(btl);
            switch (Kind)
            {
                case OpKind.Damage: btl_para.SetDamage(unit, A, (Byte)B, cmd, C != 0); break;
                case OpKind.Recover: btl_para.SetRecover(unit, (UInt32)A, C != 0); break;
                case OpKind.MpDamage: btl_para.SetMpDamage(unit, (UInt32)A); break;
                case OpKind.MpRecover: btl_para.SetMpRecover(unit, (UInt32)A); break;
                case OpKind.Alter:
                {
                    BTL_DATA inflicter = BattleSpike.FindBtl(Other);
                    UInt32 r = btl_stat.AlterStatus(unit, (BattleStatusId)A, inflicter != null ? new BattleUnit(inflicter) : null, false, Params ?? new Object[0]);
                    if (r != Result)
                        return $"AlterStatus {(BattleStatusId)A} on {BattleSpike.UnitName(Unit)} returned {r}, owner {Result}";
                    break;
                }
                case OpKind.Remove: btl_stat.RemoveStatus(unit, (BattleStatusId)A); break;
                case OpKind.Kill:
                {
                    BTL_DATA killer = BattleSpike.FindBtl(Other);
                    unit.Kill(killer);
                    break;
                }
                case OpKind.Figure: btl2d.Btl2dReqInstant(btl, (UInt16)A, B, C); break;
                case OpKind.StatFigure: btl2d.Btl2dStatReq(btl, A, B); break;
                case OpKind.ScriptUnitSet: btl_scrp.SetCharacterData(unit, (UInt32)A, B); break;
            }
            return null;
        }

        public override String ToString()
        {
            String u = BattleSpike.UnitName(Unit);
            switch (Kind)
            {
                case OpKind.Damage: return $"Damage {u} {A}{(B != 0 ? " +motion" : "")}{(C != 0 ? " now" : "")}";
                case OpKind.Recover: return $"Recover {u} {A}";
                case OpKind.MpDamage: return $"MpDamage {u} {A}";
                case OpKind.MpRecover: return $"MpRecover {u} {A}";
                case OpKind.Alter: return $"Alter {u} {(BattleStatusId)A} by {BattleSpike.UnitName(Other)} = {Result}{(Params != null && Params.Length > 0 ? $" ({Params.Length} params)" : "")}";
                case OpKind.Remove: return $"Remove {u} {(BattleStatusId)A}";
                case OpKind.Kill: return $"Kill {u} by {BattleSpike.UnitName(Other)}";
                case OpKind.Figure: return $"Figure {u} info {A:X} hp {B} mp {C}";
                case OpKind.StatFigure: return $"StatFigure {u} hp {A} mp {B}";
                case OpKind.Message: return $"Message \"{Text}\" priority {A}";
                case OpKind.ScriptUnitSet: return $"ScriptUnitSet {u} [{A}] = {B}";
                case OpKind.ScriptBattleSet: return $"ScriptBattleSet [{A}] = {B}";
            }
            return Kind.ToString();
        }
    }

    /// <summary>A damage-number request left in a unit's <c>fig</c> after a calculation (shown by the sequencer's
    /// "Figure" code later).</summary>
    public struct FigState
    {
        public UInt16 Unit;
        public UInt16 Info;
        public Int32 Hp, Mp;
    }
}
