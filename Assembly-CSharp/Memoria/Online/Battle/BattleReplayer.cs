using FF9;
using Memoria.Data;
using Memoria.Scripts;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Memoria.Online.Battle
{
    /// <summary>
    /// Spike C, passive side: feeds a recorded battle to a battle running in passive mode. Events are consumed in order;
    /// a Command is injected at once, an Effect waits until the engine reaches that effect point (the animation's own
    /// timing), a CommandEnd waits until the command finished and is where the state is compared.
    ///
    /// Counters: mismatches = differences after an action (CommandEnd) or at the end; corrections = state an Effect /
    /// Tick's ops didn't reproduce (fixed from the snapshot; tells which side paths write state directly); syncs = state
    /// changed outside the model on the owner; tape misses = random draws that didn't line up.
    /// </summary>
    public sealed class BattleReplayer
    {
        public readonly BattleLog Log;
        private Int32 _pos;
        private Int32 _frame;
        private Boolean _started, _ended;
        private readonly Dictionary<Int32, CMD_DATA> _bySeq = new Dictionary<Int32, CMD_DATA>();
        private readonly Dictionary<CMD_DATA, Int32> _seqOf = new Dictionary<CMD_DATA, Int32>();
        private readonly Dictionary<Int32, Int32> _tapePos = new Dictionary<Int32, Int32>();
        private readonly HashSet<Int32> _finished = new HashSet<Int32>();
        private readonly Stopwatch _blocked = new Stopwatch();
        private Boolean _stallLogged;

        public Boolean Started => _started;
        public Int32 Mismatches, Corrections, Syncs, TapeMisses, Actions, Effects, LocalQueueDrops, TranceWrites, TickWrites;

        public BattleReplayer(BattleLog log)
        {
            Log = log;
        }

        public String Status => $"replay {_pos}/{Log.Events.Count} actions {Actions} effects {Effects} | mismatch {Mismatches} corr {Corrections} trance {TranceWrites} tickhp {TickWrites} sync {Syncs} tape {TapeMisses} drop {LocalQueueDrops} blocked {BattleSpike.BlockedScriptWrites}{(_ended ? " | ended" : "")}";

        private void Mismatch(String what)
        {
            Mismatches++;
            BattleSpike.Note($"Replay MISMATCH (event {_pos}, frame {_frame}): {what}");
        }

        public void BeforeFrame()
        {
            _frame++;
            if (!_started)
            {
                if (FF9StateSystem.Battle.FF9Battle.btl_phase != Log.StartPhase)
                    return;
                Begin();
            }
            Pump();
            if (_pos < Log.Events.Count && Log.Events[_pos].Type != BattleEventType.End)
            {
                if (!_blocked.IsRunning)
                    _blocked.Start();
                if (_blocked.Elapsed.TotalSeconds > 20 && !_stallLogged)
                {
                    _stallLogged = true;
                    BattleSpike.Note($"Replay stalled 20 s at event {_pos}: {Log.Events[_pos].ToString().Split('\n')[0]}");
                }
            }
        }

        private void Begin()
        {
            _started = true;
            BattleEvent start = Log.Events[0];
            List<String> diffs = start.State.Diff(BattleSnapshot.Capture());
            if (diffs.Count > 0)
                Mismatch("initial state: " + String.Join("; ", diffs.ToArray()));
            SetAtb(start.State);
            _pos = 1;
            BattleSpike.Note($"Replay: started, initial state {(diffs.Count == 0 ? "matches" : "differs")}");
        }

        /// <summary>Consume events until one has to wait for the engine.</summary>
        private void Pump()
        {
            while (_pos < Log.Events.Count)
            {
                BattleEvent ev = Log.Events[_pos];
                switch (ev.Type)
                {
                    case BattleEventType.Command:
                        Inject(ev);
                        _pos++;
                        break;
                    case BattleEventType.Effect:
                        if (ev.Seq >= 0 && _bySeq.ContainsKey(ev.Seq))
                            return; // the running command will reach this effect point
                        if (ev.Seq >= 0 && _finished.Contains(ev.Seq))
                            Mismatch($"command #{ev.Seq} finished before its effect on {BattleSpike.UnitName(ev.Target)}");
                        ApplyEffect(ev, null, BattleSpike.FindBtl(ev.Target)); // an effect outside a command
                        _pos++;
                        break;
                    case BattleEventType.CommandEnd:
                        if (_bySeq.ContainsKey(ev.Seq))
                            return; // still running
                        CompareAfterAction(ev);
                        _pos++;
                        break;
                    case BattleEventType.Tick:
                    case BattleEventType.Script:
                        ApplyTick(ev);
                        _pos++;
                        break;
                    case BattleEventType.Sync:
                        ApplySync(ev);
                        _pos++;
                        break;
                    case BattleEventType.Atb:
                        SetAtb(ev.AtbValues);
                        _pos++;
                        break;
                    case BattleEventType.End:
                        return;
                    default:
                        _pos++;
                        break;
                }
                _blocked.Reset();
                _stallLogged = false;
            }
        }

        private void Inject(BattleEvent ev)
        {
            CMD_DATA cmd = CommandInjector.InjectRecorded(ev, out String error);
            if (cmd == null)
            {
                Mismatch($"can't run command #{ev.Seq}: {error}");
                _finished.Add(ev.Seq);
                return;
            }
            _bySeq[ev.Seq] = cmd;
            _seqOf[cmd] = ev.Seq;
            _tapePos[ev.Seq] = 0;
        }

        // ---- engine callbacks ---------------------------------------------------------------------------------------

        /// <summary>The engine reached an effect point of a running command: apply the recorded outcome.</summary>
        public void ReplaceCalculation(BTL_DATA caster, BTL_DATA target, CMD_DATA cmd)
        {
            Int32 seq = cmd != null && _seqOf.TryGetValue(cmd, out Int32 s) ? s : -1;
            Pump();
            BattleEvent ev = _pos < Log.Events.Count ? Log.Events[_pos] : null;
            if (ev == null || ev.Type != BattleEventType.Effect || ev.Seq != seq || ev.Target != target.btl_id)
            {
                Mismatch($"engine reached an effect point (#{seq} {cmd?.cmd_no} → {BattleSpike.UnitName(target.btl_id)}) but the log has {(ev == null ? "nothing" : ev.ToString().Split('\n')[0])}; skipped");
                return;
            }
            ApplyEffect(ev, cmd, target);
            _pos++;
            Pump();
        }

        private void ApplyOps(BattleEvent ev, CMD_DATA cmd)
        {
            BattleSpike.Applying++;
            try
            {
                foreach (BattleOp op in ev.Ops)
                {
                    String problem = op.Apply(cmd);
                    if (problem != null)
                        Mismatch(problem);
                }
            }
            finally
            {
                BattleSpike.Applying--;
            }
        }

        private void ApplyEffect(BattleEvent ev, CMD_DATA cmd, BTL_DATA target)
        {
            Effects++;
            ApplyOps(ev, cmd);
            for (BTL_DATA btl = FF9StateSystem.Battle.FF9Battle.btl_list.next; btl != null; btl = btl.next)
            {
                btl.fig.info = 0;
                btl.fig.hp = 0;
                btl.fig.mp = 0;
            }
            foreach (FigState f in ev.Figs)
            {
                BTL_DATA btl = BattleSpike.FindBtl(f.Unit);
                if (btl == null)
                    continue;
                btl.fig.info = f.Info;
                btl.fig.hp = f.Hp;
                btl.fig.mp = f.Mp;
            }
            if (target != null)
            {
                if (ev.TargetDodge != 0 && target.bi.dodge == 0)
                    PassiveBattle.ApplyDodge(target, cmd);
                if (ev.CmdDodge != 0 && cmd != null)
                    cmd.info.dodge = ev.CmdDodge;
                // What CalcResult does for the target's damage numbers and the HUD
                target.fig.modifiers.Clear();
                foreach (BattleStatusId statusId in target.stat.cur.ToStatusList())
                    if (target.stat.effects.TryGetValue(statusId, out StatusScriptBase effect) && effect is IFigurePointStatusScript)
                        target.fig.modifiers.Add(effect as IFigurePointStatusScript);
                BattleHUD.EffectedBtlId |= target.btl_id;
            }
            Correct(ev, "effect");
        }

        private void ApplyTick(BattleEvent ev)
        {
            ApplyOps(ev, null);
            Correct(ev, ev.Type == BattleEventType.Script ? "script" : "tick");
        }

        /// <summary>State the ops didn't reproduce: set it from the owner's snapshot and count it.</summary>
        private void Correct(BattleEvent ev, String what)
        {
            if (ev.State == null)
                return;
            // Known direct write: the Trance gauge (CalcResult's CheckDamageReaction, FinishCommand) isn't an op
            foreach (UnitState u in ev.State.Units)
            {
                BTL_DATA btl = BattleSpike.FindBtl(u.Id);
                if (btl != null && btl.trance != u.Trance)
                {
                    btl.trance = u.Trance;
                    TranceWrites++;
                }
            }
            // Known direct write: Poison / Regen / Venom ticks change HP / MP in the status script (OnOpr), only the
            // damage number is an op
            if (ev.Type == BattleEventType.Tick)
            {
                UnitState u = ev.State.Find(ev.Target);
                BTL_DATA btl = BattleSpike.FindBtl(ev.Target);
                if (u != null && btl != null && (btl.cur.hp != u.Hp || btl.cur.mp != u.Mp) && u.Hp > 0)
                {
                    btl.cur.hp = u.Hp;
                    btl.cur.mp = u.Mp;
                    TickWrites++;
                }
            }
            List<String> diffs = ev.State.ApplyToBattle();
            if (diffs.Count == 0)
                return;
            Corrections += diffs.Count;
            BattleSpike.Note($"Replay correction after {what} (event {_pos}{(ev.Seq >= 0 ? $", #{ev.Seq}" : "")}): {String.Join("; ", diffs.ToArray())}");
        }

        private void ApplySync(BattleEvent ev)
        {
            List<String> diffs = ev.State.ApplyToBattle();
            if (diffs.Count == 0)
                return;
            Syncs++;
            BattleSpike.Note($"Replay sync (event {_pos}): {String.Join("; ", diffs.ToArray())}");
        }

        private void CompareAfterAction(BattleEvent ev)
        {
            Actions++;
            List<String> diffs = ev.State.Diff(BattleSnapshot.Capture());
            if (diffs.Count > 0)
            {
                Mismatch($"after action #{ev.Seq}: {String.Join("; ", diffs.ToArray())}");
                ev.State.ApplyToBattle();
            }
        }

        public void OnCommandFinished(CMD_DATA cmd)
        {
            if (!_seqOf.TryGetValue(cmd, out Int32 seq))
                return;
            _seqOf.Remove(cmd);
            _bySeq.Remove(seq);
            _finished.Add(seq);
            BattleEvent tapeOwner = FindCommand(seq);
            if (tapeOwner != null && _tapePos.TryGetValue(seq, out Int32 used) && used != tapeOwner.Tape.Count)
            {
                TapeMisses++;
                BattleSpike.Note($"Replay tape: command #{seq} used {used} of {tapeOwner.Tape.Count} random numbers");
            }
            Pump(); // compares at its CommandEnd if that is next
        }

        private BattleEvent FindCommand(Int32 seq)
        {
            foreach (BattleEvent ev in Log.Events)
                if (ev.Type == BattleEventType.Command && ev.Seq == seq)
                    return ev;
            return null;
        }

        public Int32 Tape(CMD_DATA cmd, TapeKind kind, Int32 value)
        {
            if (!_seqOf.TryGetValue(cmd, out Int32 seq))
                return value;
            BattleEvent ev = FindCommand(seq);
            Int32 index = _tapePos[seq];
            if (ev == null || index >= ev.Tape.Count || ev.Tape[index].Kind != kind)
            {
                TapeMisses++;
                BattleSpike.Note($"Replay tape miss: command #{seq} draw {index} ({kind}) not on the tape ({ev?.Tape.Count ?? 0} values)");
                return value;
            }
            _tapePos[seq] = index + 1;
            return ev.Tape[index].Value;
        }

        public void OnLocalQueue(FF9StateBattleSystem btlsys)
        {
            // Passive: the local queue only fills if something local (a battle script, the HUD) asked for a command
            for (CMD_DATA cmd = btlsys.cmd_queue.next; cmd != null; cmd = btlsys.cmd_queue.next)
            {
                LocalQueueDrops++;
                BattleSpike.Note($"Replay: dropped a locally queued command {cmd.cmd_no} by {BattleSpike.UnitName(cmd.regist?.btl_id ?? 0)}");
                btl_cmd.KillCommand(cmd);
                if (btlsys.cmd_queue.next == cmd)
                    break; // KillCommand didn't dequeue it (shouldn't happen)
            }
        }

        // ---- ATB and end --------------------------------------------------------------------------------------------

        private static void SetAtb(BattleSnapshot state)
        {
            foreach (UnitState u in state.Units)
            {
                BTL_DATA btl = BattleSpike.FindBtl(u.Id);
                if (btl != null)
                    btl.cur.at = u.Atb;
            }
        }

        private static void SetAtb(Int16[] values)
        {
            Int32 i = 0;
            for (BTL_DATA btl = FF9StateSystem.Battle.FF9Battle.btl_list.next; btl != null && i < values.Length; btl = btl.next, i++)
                btl.cur.at = values[i];
        }

        public void AfterFrame(UInt32 battleResult)
        {
            if (battleResult != 1 || _ended || !_started)
                return;
            _ended = true;
            Pump();
            BattleEvent end = null;
            if (_pos < Log.Events.Count && Log.Events[_pos].Type == BattleEventType.End)
                end = Log.Events[_pos];
            else
                Mismatch($"the passive battle ended (result {FF9StateSystem.Common.FF9.btl_result}) with {Log.Events.Count - _pos} events left");
            if (end == null)
                end = Log.Events.FindLast(e => e.Type == BattleEventType.End);
            if (end != null)
            {
                FF9StateGlobal ff9 = FF9StateSystem.Common.FF9;
                Byte result = ff9.btl_result;
                if (result != end.Result)
                    Mismatch($"result {result}, owner {end.Result}");
                List<String> diffs = end.State.Diff(BattleSnapshot.Capture());
                if (diffs.Count > 0)
                    Mismatch("final state: " + String.Join("; ", diffs.ToArray()));
                BONUS bonus = battle.btl_bonus;
                if (bonus.exp != end.Exp || bonus.gil != end.BonusGil || bonus.ap != end.Ap || bonus.card != end.Card || bonus.item.Count != end.BonusItems.Count)
                    BattleSpike.Note($"Replay: rewards computed locally (exp {bonus.exp} gil {bonus.gil} ap {bonus.ap} card {bonus.card} items {bonus.item.Count}) replaced by the owner's (exp {end.Exp} gil {end.BonusGil} ap {end.Ap} card {end.Card} items {end.BonusItems.Count})");
                bonus.exp = end.Exp;
                bonus.gil = end.BonusGil;
                bonus.ap = end.Ap;
                bonus.card = end.Card;
                bonus.item = new List<RegularItem>(end.BonusItems);
                _pos = Log.Events.Count;
            }
            BattleSpike.OnReplayDone($"Replay done: {Status}");
            BattleSpike.Replayer = null;
        }
    }
}
