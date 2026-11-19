using FF9;
using Memoria.Data;
using System;
using System.Collections.Generic;

namespace Memoria.Online.Battle
{
    /// <summary>
    /// Spike C, owner side: logs everything a passive client needs to play the battle back (see
    /// <see cref="BattleEventType"/>). State changes are attributed to the code that made them: a calculation (Effect),
    /// a status tick (Tick), code every PC runs anyway (command steps: nothing logged but the RNG tape) or anything
    /// else (Sync, found by comparing snapshots at every context boundary).
    /// </summary>
    public sealed class BattleRecorder
    {
        public readonly BattleLog Log = new BattleLog();
        private readonly Dictionary<CMD_DATA, BattleEvent> _running = new Dictionary<CMD_DATA, BattleEvent>();
        private Int32 _nextSeq;
        private Int32 _frame;
        private Boolean _started;
        private BattleSnapshot _baseline;
        private BattleEvent _collecting; // the Effect or Tick receiving ops
        private BTL_DATA _calcTarget;
        private CMD_DATA _calcCmd;
        private Byte _targetDodgeBefore;

        public Int32 EventCount => Log.Events.Count;

        private BattleEvent Add(BattleEvent ev)
        {
            ev.Frame = _frame;
            Log.Events.Add(ev);
            return ev;
        }

        private void Start()
        {
            if (_started)
                return;
            _started = true;
            FF9StateBattleSystem btlsys = FF9StateSystem.Battle.FF9Battle;
            Log.MapNo = FF9StateSystem.Battle.battleMapIndex;
            Log.Pattern = btlsys.btl_scene.PatNum;
            Log.StartType = (Int32)btlsys.btl_scene.Info.StartType;
            _baseline = BattleSnapshot.Capture();
            Log.StartPhase = btlsys.btl_phase;
            Add(new BattleEvent { Type = BattleEventType.Start, MapNo = Log.MapNo, Pattern = Log.Pattern, StartType = Log.StartType, Result = btlsys.btl_phase, State = _baseline });
            BattleSync.Note($"Record: started in phase {btlsys.btl_phase} (battle {Log.MapNo}, pattern {Log.Pattern}, start type {Log.StartType})");
        }

        public void BeforeFrame()
        {
            _frame++;
            if (!_started && FF9StateSystem.Battle.FF9Battle.btl_phase == FF9StateBattleSystem.PHASE_NORMAL)
                Start();
        }

        public void AfterFrame(UInt32 battleResult)
        {
            if (!_started || Log.Complete)
                return;
            Checkpoint();
            if (_frame % 3 == 0)
                RecordAtb();
            if (battleResult == 1)
                End();
        }

        private void RecordAtb()
        {
            List<Int16> values = new List<Int16>();
            for (BTL_DATA btl = FF9StateSystem.Battle.FF9Battle.btl_list.next; btl != null; btl = btl.next)
                values.Add(btl.cur.at);
            BattleEvent last = null;
            for (Int32 i = Log.Events.Count - 1; i >= 0; i--)
                if (Log.Events[i].Type == BattleEventType.Atb)
                {
                    last = Log.Events[i];
                    break;
                }
            Int16[] array = values.ToArray();
            if (last != null && last.AtbValues.Length == array.Length)
            {
                Boolean same = true;
                for (Int32 i = 0; i < array.Length; i++)
                    same &= array[i] == last.AtbValues[i];
                if (same)
                    return;
            }
            Add(new BattleEvent { Type = BattleEventType.Atb, AtbValues = array });
        }

        private void End()
        {
            FF9StateGlobal ff9 = FF9StateSystem.Common.FF9;
            BONUS bonus = battle.btl_bonus;
            Add(new BattleEvent
            {
                Type = BattleEventType.End,
                Result = ff9.btl_result,
                Exp = bonus.exp,
                BonusGil = bonus.gil,
                Ap = bonus.ap,
                Card = bonus.card,
                BonusItems = new List<RegularItem>(bonus.item),
                State = BattleSnapshot.Capture()
            });
            Log.Complete = true;
            BattleSync.OnRecordingDone(Log);
        }

        /// <summary>Anything changed since the last context ended is outside the model: log it as a Sync.</summary>
        public void Checkpoint()
        {
            if (!_started)
                return;
            BattleSnapshot now = BattleSnapshot.Capture();
            List<String> diffs = _baseline.Diff(now);
            if (diffs.Count > 0)
            {
                Add(new BattleEvent { Type = BattleEventType.Sync, State = now });
                BattleSync.Note($"Record: sync at frame {_frame}: {String.Join("; ", diffs.ToArray())}");
            }
            _baseline = now;
        }

        public void Baseline()
        {
            if (_started)
                _baseline = BattleSnapshot.Capture();
        }

        // ---- commands -----------------------------------------------------------------------------------------------

        public void OnCommandAdmitted(CMD_DATA cmd)
        {
            Start();
            Checkpoint();
            FF9StateBattleSystem btlsys = FF9StateSystem.Battle.FF9Battle;
            BattleEvent ev = new BattleEvent { Type = BattleEventType.Command, Seq = _nextSeq++ };
            if (cmd == btlsys.cmd_escape)
            {
                ev.Slot = -1;
                ev.Caster = cmd.regist?.btl_id ?? 0;
            }
            else if (cmd.regist != null)
            {
                ev.Caster = cmd.regist.btl_id;
                ev.Slot = (SByte)cmd.regist.cmd.IndexOf(cmd);
                if (ev.Slot < 0)
                    BattleSync.Note($"Record: command {cmd.cmd_no} isn't in its caster's command slots");
            }
            ev.CmdNo = cmd.cmd_no;
            ev.SubNo = cmd.sub_no;
            ev.TarId = cmd.tar_id;
            ev.Cursor = cmd.info.cursor;
            ev.Priority = cmd.info.priority;
            ev.MonReflec = cmd.info.mon_reflec;
            ev.Menu = cmd.info.cmdMenu;
            ev.CustomMp = cmd.info.CustomMPCost ?? -1;
            ev.MagicCaster = cmd.magic_caster_id;
            ev.ScriptId = cmd.ScriptId;
            ev.HitRate = cmd.HitRate;
            ev.Power = cmd.Power;
            ev.Category = cmd.AbilityCategory;
            ev.AbilType = cmd.AbilityType;
            ev.ShortRange = cmd.IsShortRange;
            ev.AbilityStatus = (UInt64)cmd.AbilityStatus;
            Add(ev);
            _running[cmd] = ev;
        }

        public Int32 Tape(CMD_DATA cmd, TapeKind kind, Int32 value)
        {
            if (_running.TryGetValue(cmd, out BattleEvent ev))
                ev.Tape.Add(new TapeEntry { Kind = kind, Value = value });
            return value;
        }

        public void OnSummonTypeDecided(CMD_DATA cmd)
        {
            if (_running.TryGetValue(cmd, out BattleEvent ev))
                ev.ShortSummon = (SByte)cmd.info.short_summon;
        }

        public void OnCommandFinished(CMD_DATA cmd)
        {
            if (!_running.TryGetValue(cmd, out BattleEvent ev))
                return;
            _running.Remove(cmd);
            _baseline = BattleSnapshot.Capture();
            Add(new BattleEvent { Type = BattleEventType.CommandEnd, Seq = ev.Seq, State = _baseline });
        }

        // ---- calculations and ticks ---------------------------------------------------------------------------------

        public void CalcBegin(BTL_DATA caster, BTL_DATA target, CMD_DATA cmd)
        {
            Start();
            Checkpoint();
            Int32 seq = cmd != null && _running.TryGetValue(cmd, out BattleEvent cmdEv) ? cmdEv.Seq : -1;
            _collecting = new BattleEvent { Type = BattleEventType.Effect, Seq = seq, Caster = caster.btl_id, Target = target.btl_id };
            _calcTarget = target;
            _calcCmd = cmd;
            _targetDodgeBefore = target.bi.dodge;
        }

        public void CalcEnd()
        {
            BattleEvent ev = _collecting;
            _collecting = null;
            if (ev == null)
                return;
            ev.Ops.RemoveAll(op => op.Discard);
            for (BTL_DATA btl = FF9StateSystem.Battle.FF9Battle.btl_list.next; btl != null; btl = btl.next)
                if (btl.fig.info != 0 || btl.fig.hp != 0 || btl.fig.mp != 0)
                    ev.Figs.Add(new FigState { Unit = btl.btl_id, Info = btl.fig.info, Hp = btl.fig.hp, Mp = btl.fig.mp });
            ev.CmdDodge = _calcCmd?.info.dodge ?? 0;
            ev.TargetDodge = _calcTarget.bi.dodge != _targetDodgeBefore ? _calcTarget.bi.dodge : (Byte)0;
            ev.State = BattleSnapshot.Capture();
            _baseline = ev.State;
            Add(ev);
        }

        public void TickBegin(BattleUnit unit)
        {
            if (!_started)
                return;
            Checkpoint();
            _collecting = new BattleEvent { Type = BattleEventType.Tick, Target = unit.Id };
        }

        public void TickEnd(BattleUnit unit)
        {
            BattleEvent ev = _collecting;
            _collecting = null;
            if (ev == null)
                return;
            ev.Ops.RemoveAll(op => op.Discard);
            BattleSnapshot now = BattleSnapshot.Capture();
            if (ev.Ops.Count > 0 || _baseline.Diff(now).Count > 0)
            {
                ev.State = now;
                Add(ev);
            }
            _baseline = now;
        }

        public BattleOp AddOp(OpKind kind, BTL_DATA unit, Int32 a, Int32 b, Int32 c, BTL_DATA other, Object[] parameters)
        {
            if (_collecting == null)
                return null;
            BattleOp op = new BattleOp { Kind = kind, Unit = unit?.btl_id ?? 0, A = a, B = b, C = c, Other = other?.btl_id ?? 0, Params = parameters };
            _collecting.Ops.Add(op);
            return op;
        }

        private BattleEvent _scriptEvent;

        /// <summary>A battle script write: part of the current calculation / tick, else an event of its own.</summary>
        public BattleOp AddScriptOp(OpKind kind, BTL_DATA unit, Int32 id, Int32 value)
        {
            if (!_started)
                return null;
            if (_collecting != null)
                return AddOp(kind, unit, id, value, 0, null, null);
            Checkpoint();
            _scriptEvent = new BattleEvent { Type = BattleEventType.Script, Target = unit?.btl_id ?? 0 };
            BattleOp op = new BattleOp { Kind = kind, Unit = unit?.btl_id ?? 0, A = id, B = value };
            _scriptEvent.Ops.Add(op);
            Add(_scriptEvent);
            return op;
        }

        public void OpDone(BattleOp op)
        {
            if (_scriptEvent == null || !_scriptEvent.Ops.Contains(op))
                return;
            _scriptEvent.State = BattleSnapshot.Capture();
            _baseline = _scriptEvent.State;
            _scriptEvent = null;
        }
    }
}
