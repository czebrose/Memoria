using Memoria.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Memoria.Online.Battle
{
    public enum BattleEventType : byte
    {
        Start = 1,   // the battle reached its first PHASE_NORMAL: initial state
        Command = 2, // the owner admitted a command (cur_cmd_list): what to run, plus its RNG tape
        Effect = 3,  // one calculation (SBattleCalculator.CalcMain on one target): ops + damage numbers + state after
        CommandEnd = 4, // FinishCommand: state after the action (comparison point)
        Tick = 5,    // a unit's per-frame status loop changed something (poison, regen, expiry, low HP, death)
        Sync = 6,    // state changed outside any command / tick / calculation (AI script, queue checks, ...)
        Atb = 7,     // ATB gauges (display)
        End = 8,     // the battle ended: result and rewards
        Script = 9,  // a battle script (AI / event) changed a unit or the battle outside a calculation
    }

    /// <summary>One entry of the owner's battle log. A single class with optional parts keeps the spike small.</summary>
    public sealed class BattleEvent
    {
        public BattleEventType Type;
        public Int32 Frame;
        public Int32 Seq = -1;              // command sequence number (Command, Effect, CommandEnd)
        public BattleSnapshot State;        // Start, Effect (after), CommandEnd (after), Tick (after), Sync, End

        // Command
        public UInt16 Caster;               // regist btl_id (0: none)
        public SByte Slot;                  // index in regist.cmd, -1 = btlsys.cmd_escape
        public BattleCommandId CmdNo;
        public Int32 SubNo;
        public UInt16 TarId;
        public Byte Cursor, Priority, MonReflec;
        public BattleCommandMenu Menu;
        public Int32 CustomMp = -1;
        public UInt16 MagicCaster;
        public Int32 ScriptId, HitRate, Power;
        public Byte Category, AbilType;
        public Boolean ShortRange;
        public UInt64 AbilityStatus;
        public readonly List<TapeEntry> Tape = new List<TapeEntry>();
        public SByte ShortSummon = -1;      // summons: the owner's long (0) / short (1) choice

        // Effect (Caster = caster, Target = target) and Tick (Target = the unit)
        public UInt16 Target;
        public readonly List<BattleOp> Ops = new List<BattleOp>();
        public readonly List<FigState> Figs = new List<FigState>();
        public Byte CmdDodge;               // cmd.info.dodge after the calculation
        public Byte TargetDodge;            // target.bi.dodge after the calculation

        // Atb
        public Int16[] AtbValues;           // in btl_list order

        // Start
        public Int32 MapNo, Pattern, StartType;

        // Script: the owner's battle phase when the script wrote (some writes only act in one phase, e.g. "end battle" in PHASE_EVENT)
        public Byte Phase;

        // End
        public Byte Result;
        public UInt32 Exp;
        public Int32 BonusGil;
        public UInt16 Ap;
        public List<RegularItem> BonusItems;
        public TetraMasterCardId Card;

        public void Write(BinaryWriter w)
        {
            w.Write((Byte)Type);
            switch (Type)
            {
                case BattleEventType.Start:
                    w.Write((UInt16)MapNo); w.Write((SByte)Pattern); w.Write((Byte)StartType);
                    State.Write(w);
                    break;
                case BattleEventType.Command:
                    w.Write((UInt16)Seq); w.Write((Byte)BattleSync.UnitIndex(Caster)); w.Write(Slot);
                    w.Write((Byte)CmdNo); w.Write((UInt16)SubNo); w.Write(TarId);
                    w.Write((Byte)(Cursor | (Priority << 2) | (MonReflec << 4) | (ShortRange ? 0x80 : 0)));
                    w.Write((Byte)Menu); w.Write((Int16)CustomMp); w.Write(MagicCaster);
                    // Ability fields that SA features may have patched (ScriptId, rate, power, ...)
                    w.Write((Byte)ScriptId); w.Write((Byte)HitRate); w.Write((Byte)Power); w.Write(Category); w.Write(AbilType);
                    w.Write(AbilityStatus);
                    w.Write(ShortSummon);
                    w.Write((Byte)Tape.Count);
                    foreach (TapeEntry t in Tape)
                    {
                        w.Write((Byte)t.Kind);
                        w.Write(t.Value);
                    }
                    break;
                case BattleEventType.Effect:
                    w.Write((UInt16)Seq); w.Write((Byte)BattleSync.UnitIndex(Caster)); w.Write((Byte)BattleSync.UnitIndex(Target));
                    w.Write((Byte)Ops.Count);
                    foreach (BattleOp op in Ops)
                        op.Write(w);
                    w.Write((Byte)Figs.Count);
                    foreach (FigState f in Figs)
                    {
                        w.Write((Byte)BattleSync.UnitIndex(f.Unit)); w.Write(f.Info); w.Write(f.Hp); w.Write(f.Mp);
                    }
                    w.Write((Byte)(CmdDodge | (TargetDodge << 1)));
                    break;
                case BattleEventType.CommandEnd:
                    w.Write((UInt16)Seq);
                    break;
                case BattleEventType.Tick:
                case BattleEventType.Script:
                    if (Type == BattleEventType.Script)
                        w.Write(Phase);
                    w.Write((Byte)BattleSync.UnitIndex(Target));
                    w.Write((Byte)Ops.Count);
                    foreach (BattleOp op in Ops)
                        op.Write(w);
                    break;
                case BattleEventType.Sync:
                    State.Write(w);
                    break;
                case BattleEventType.Atb:
                    w.Write((Byte)AtbValues.Length);
                    foreach (Int16 v in AtbValues)
                        w.Write(v);
                    break;
                case BattleEventType.End:
                    w.Write(Result); w.Write(Exp); w.Write(BonusGil); w.Write(Ap); w.Write((UInt16)Card);
                    w.Write((Byte)BonusItems.Count);
                    foreach (RegularItem item in BonusItems)
                        w.Write((UInt16)item);
                    break;
            }
        }

        /// <summary>Size of the event as it would be sent; the state snapshot used for checking is not counted
        /// (except for Start and Sync, where it is the content).</summary>
        public Int32 WireSize()
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                Write(w);
                w.Flush();
                return (Int32)ms.Length;
            }
        }

        public override String ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append($"[{Frame,6}] {Type}");
            switch (Type)
            {
                case BattleEventType.Start:
                    sb.Append($" battle {MapNo} pattern {Pattern} start {StartType}\n{State}");
                    break;
                case BattleEventType.Command:
                    sb.Append($" #{Seq} {BattleSync.UnitName(Caster)}[{Slot}] {CmdNo} sub {SubNo} → {TarId:X2} script {ScriptId}{(ShortSummon >= 0 ? $" summon {(ShortSummon != 0 ? "short" : "long")}" : "")} tape [{String.Join(",", Tape.ConvertAll(t => t.ToString()).ToArray())}]");
                    break;
                case BattleEventType.Effect:
                    sb.Append($" #{Seq} {BattleSync.UnitName(Caster)} → {BattleSync.UnitName(Target)}");
                    foreach (BattleOp op in Ops)
                        sb.Append("\n      ").Append(op);
                    foreach (FigState f in Figs)
                        sb.Append($"\n      fig {BattleSync.UnitName(f.Unit)} info {f.Info:X} hp {f.Hp} mp {f.Mp}");
                    if (CmdDodge != 0 || TargetDodge != 0)
                        sb.Append($"\n      dodge cmd {CmdDodge} target {TargetDodge}");
                    break;
                case BattleEventType.CommandEnd:
                    sb.Append($" #{Seq}\n{State}");
                    break;
                case BattleEventType.Tick:
                case BattleEventType.Script:
                    sb.Append($" {BattleSync.UnitName(Target)}{(Type == BattleEventType.Script ? $" (phase {Phase})" : "")}");
                    foreach (BattleOp op in Ops)
                        sb.Append("\n      ").Append(op);
                    break;
                case BattleEventType.Sync:
                    sb.Append($"\n{State}");
                    break;
                case BattleEventType.Atb:
                    sb.Append(" ").Append(String.Join(" ", Array.ConvertAll(AtbValues, v => v.ToString())));
                    break;
                case BattleEventType.End:
                    sb.Append($" result {Result} exp {Exp} gil {BonusGil} ap {Ap} card {Card} items [{String.Join(",", BonusItems.ConvertAll(i => i.ToString()).ToArray())}]\n{State}");
                    break;
            }
            return sb.ToString();
        }
    }

    public enum TapeKind : byte { Random8 = 1, Random16 = 2, Range = 3, RangeSingle = 4 }

    /// <summary>One random number drawn on the owner while running a command outside the calculation (targets,
    /// reflect bounces, random-target hits, "can't concentrate"...). The passive client draws the same values.</summary>
    public struct TapeEntry
    {
        public TapeKind Kind;
        public Int32 Value;

        public override String ToString() => $"{(Char)('a' + (Int32)Kind - 1)}{Value}";
    }

    /// <summary>The whole recording of one battle.</summary>
    public sealed class BattleLog
    {
        public readonly List<BattleEvent> Events = new List<BattleEvent>();
        public Int32 MapNo = -1, Pattern = -1, StartType = -1;
        public Byte StartPhase = FF9StateBattleSystem.PHASE_NORMAL;
        public Boolean Complete;

        public Int32 CountOf(BattleEventType type)
        {
            Int32 n = 0;
            foreach (BattleEvent ev in Events)
                if (ev.Type == type)
                    n++;
            return n;
        }

        public String SizeReport()
        {
            Dictionary<BattleEventType, Int32> count = new Dictionary<BattleEventType, Int32>();
            Dictionary<BattleEventType, Int32> bytes = new Dictionary<BattleEventType, Int32>();
            Dictionary<BattleEventType, Int32> max = new Dictionary<BattleEventType, Int32>();
            Int32 total = 0;
            foreach (BattleEvent ev in Events)
            {
                Int32 size = ev.WireSize();
                total += size;
                count.TryGetValue(ev.Type, out Int32 c);
                bytes.TryGetValue(ev.Type, out Int32 b);
                max.TryGetValue(ev.Type, out Int32 m);
                count[ev.Type] = c + 1;
                bytes[ev.Type] = b + size;
                max[ev.Type] = Math.Max(m, size);
            }
            StringBuilder sb = new StringBuilder();
            sb.Append($"{Events.Count} events, {total} bytes:");
            foreach (KeyValuePair<BattleEventType, Int32> kv in count)
                sb.Append($" {kv.Key} {kv.Value}x avg {bytes[kv.Key] / kv.Value} max {max[kv.Key]} B;");
            return sb.ToString();
        }

        public void Save(String path)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append($"Spike C battle log: battle {MapNo} pattern {Pattern} start {StartType}\n{SizeReport()}\n\n");
                foreach (BattleEvent ev in Events)
                    if (ev.Type != BattleEventType.Atb)
                        sb.Append(ev).Append('\n');
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception err)
            {
                Prime.Log.Error(err, "[Battle] Can't write the battle log");
            }
        }
    }
}
