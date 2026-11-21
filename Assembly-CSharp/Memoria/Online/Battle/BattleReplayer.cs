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
        private Int32 _runFrame;      // live: battle frames run since Begin (the owner's Start event is its frame 0)
        public Int32 HeldFrames, CaughtUpFrames, EarlyEffects;
        private Boolean _started, _ended;
        private readonly Dictionary<Int32, CMD_DATA> _bySeq = new Dictionary<Int32, CMD_DATA>();
        private readonly Dictionary<CMD_DATA, Int32> _seqOf = new Dictionary<CMD_DATA, Int32>();
        private readonly Dictionary<Int32, Int32> _tapePos = new Dictionary<Int32, Int32>();
        private readonly HashSet<Int32> _finished = new HashSet<Int32>();
        private readonly Stopwatch _blocked = new Stopwatch();
        private Boolean _stallLogged;

        public Boolean Started => _started;
        public Int32 Mismatches, Corrections, Syncs, TapeMisses, Actions, Effects, LocalQueueDrops, TranceWrites, TickWrites, ReviveWrites;

        private readonly BattleStreamIn _stream;  // Step 3: a live battle from the owner's stream (null: a debug recording)
        public Int32 StartFixes;

        public BattleReplayer(BattleLog log, BattleStreamIn stream = null)
        {
            Log = log;
            _stream = stream;
        }

        public Boolean Live => _stream != null;
        public BattleStreamIn Stream => _stream;

        /// <summary>Events that may be consumed: all of a recording; of a live stream, everything received (each event
        /// waits for its frame in <see cref="Pump"/>).</summary>
        private Int32 Available => _stream != null ? _stream.Released : Log.Events.Count;

        public Int32 Applied => _pos;

        /// <summary>Live: how many frames this PC runs behind the owner (by what it last heard).</summary>
        public Int32 FramesBehind => _stream == null || !_started ? 0 : _stream.OwnerFrame - _runFrame;

        /// <summary>
        /// Live, before each battle frame: true = don't run it yet (the owner hasn't got <see cref="BattleStreamIn.Margin"/>
        /// frames past it, so an effect the animation may reach could still be on its way). Not before the replay
        /// started (the intro runs on its own), nor once the owner's End is in.
        /// </summary>
        public Boolean HoldFrame()
        {
            if (_stream == null || !_started || Aborted || _ended || _done || _stream.EndReceived)
                return false;
            if (_runFrame + BattleStreamIn.Margin <= _stream.OwnerFrame)
                return false;
            HeldFrames++;
            return true;
        }

        /// <summary>Live: an extra battle frame this update when this PC fell over a second behind (load hitch, slower
        /// animation): it catches up at double speed.</summary>
        public Int32 ExtraFrames()
        {
            if (_stream == null || !_started || Aborted || _ended || _done)
                return 0;
            Int32 behind = _stream.OwnerFrame - _runFrame;
            if (behind > BattleStreamIn.Margin + 150)
            {
                // Late join (docs/online/battle-late-join.md): the whole battle so far is replayed, at 4x until close
                CaughtUpFrames += 3;
                return 3;
            }
            if (behind > BattleStreamIn.Margin + 30)
            {
                CaughtUpFrames++;
                return 1;
            }
            return 0;
        }

        public String Status => $"replay {_pos}/{Log.Events.Count} actions {Actions} effects {Effects} | mismatch {Mismatches} corr {Corrections} trance {TranceWrites} tickhp {TickWrites} revive {ReviveWrites} sync {Syncs} tape {TapeMisses} drop {LocalQueueDrops} blocked {BattleSync.BlockedScriptWrites}{(Live ? $" | behind {FramesBehind}f held {HeldFrames} caught up {CaughtUpFrames} early fx {EarlyEffects}" : "")}{(_ended ? " | ended" : "")}";

        private void Mismatch(String what)
        {
            Mismatches++;
            BattleSync.Note($"Replay MISMATCH (event {_pos}, frame {_frame}): {what}");
        }

        public void BeforeFrame()
        {
            _frame++;
            if (_started)
                _runFrame++;
            _stream?.ApplyAtb(_started ? _runFrame : Int32.MaxValue);
            if (_stream != null && _started)
                FF9StateSystem.Battle.FF9Battle.btl_escape_key = Aborted ? (Byte)0 : _stream.EscapeKey; // the owner's party "running"
            if (Aborted)
                return;
            if (!_started)
            {
                if (FF9StateSystem.Battle.FF9Battle.btl_phase != Log.StartPhase || Available == 0)
                    return;
                Begin();
            }
            Pump();
            if (_pos < Available && Log.Events[_pos].Type != BattleEventType.End)
            {
                if (!_blocked.IsRunning)
                    _blocked.Start();
                if (_blocked.Elapsed.TotalSeconds > 20 && !_stallLogged)
                {
                    _stallLogged = true;
                    BattleSync.Note($"Replay stalled 20 s at event {_pos}: {Log.Events[_pos].ToString().Split('\n')[0]}");
                }
            }
        }

        private void Begin()
        {
            _started = true;
            BattleEvent start = Log.Events[0];
            if (start.Type != BattleEventType.Start)
                Mismatch($"the stream starts with {start.Type}");
            List<String> diffs = start.State.Diff(BattleSnapshot.Capture());
            if (Live)
            {
                // Battle, pattern and start type came with BattleStart, the initial ATB with Go. Anything else that differs
                // (the party's items as this PC last saw them...) is set from the owner's state.
                FF9StateBattleSystem btlsys = FF9StateSystem.Battle.FF9Battle;
                if (start.MapNo != FF9StateSystem.Battle.battleMapIndex || start.Pattern != btlsys.btl_scene.PatNum || start.StartType != (Int32)btlsys.btl_scene.Info.StartType)
                    Mismatch($"battle {FF9StateSystem.Battle.battleMapIndex} pattern {btlsys.btl_scene.PatNum} start {(Int32)btlsys.btl_scene.Info.StartType}, owner {start.MapNo} / {start.Pattern} / {start.StartType}");
                if (diffs.Count > 0)
                {
                    StartFixes = diffs.Count;
                    BattleSync.Note("Replay: initial state set from the owner's: " + String.Join("; ", diffs.ToArray()));
                    start.State.ApplyToBattle();
                }
            }
            else
            {
                if (diffs.Count > 0)
                    Mismatch("initial state: " + String.Join("; ", diffs.ToArray()));
                BattleEntry.SetInitialAtb(start.State.Units.ConvertAll(u => u.Atb)); // initial ATB: the same path as a participant's Go
            }
            _pos = 1;
            _runFrame = 0;
            BattleSync.Note($"Replay: started, initial state {(diffs.Count == 0 ? "matches" : "differs")}");
        }

        /// <summary>Consume events until one has to wait for the engine.</summary>
        private void Pump(Boolean ignoreFrames = false)
        {
            while (_pos < Available && !Aborted)
            {
                BattleEvent ev = Log.Events[_pos];
                // Live: an event plays at the owner's frame for it (a command starts where it started on the owner).
                // An effect inside a running command comes when the animation reaches it; CommandEnd and End wait for
                // this battle anyway.
                if (Live && !ignoreFrames && ev.Frame > _runFrame && ev.Type != BattleEventType.CommandEnd && ev.Type != BattleEventType.End
                    && !(ev.Type == BattleEventType.Effect && ev.Seq >= 0 && _bySeq.ContainsKey(ev.Seq)))
                    return;
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
                            Mismatch($"command #{ev.Seq} finished before its effect on {BattleSync.UnitName(ev.Target)}");
                        ApplyEffect(ev, null, BattleSync.FindBtl(ev.Target)); // an effect outside a command
                        _pos++;
                        break;
                    case BattleEventType.CommandEnd:
                        if (_bySeq.ContainsKey(ev.Seq))
                            return; // still running
                        CompareAfterAction(ev);
                        _pos++;
                        break;
                    case BattleEventType.Script:
                        if (!PhaseReached(ev))
                            return; // the owner's script wrote in another battle phase: wait for this battle to get there
                        ApplyTick(ev);
                        _pos++;
                        break;
                    case BattleEventType.Tick:
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

        private readonly Stopwatch _phaseWait = new Stopwatch();

        /// <summary>
        /// A battle-script write acts according to the battle phase (SetBattleData "end battle" only works in
        /// PHASE_EVENT, which the passive reaches after its own die sequences). Applying it before this battle reaches the
        /// owner's phase makes it a no-op and the battle never ends (seen in battle 45). Waits up to 10 s.
        /// </summary>
        private Boolean PhaseReached(BattleEvent ev)
        {
            Byte phase = FF9StateSystem.Battle.FF9Battle.btl_phase;
            if (ev.Phase == 0 || phase == ev.Phase)
            {
                _phaseWait.Reset();
                return true;
            }
            if (!_phaseWait.IsRunning)
            {
                _phaseWait.Start();
                BattleSync.Note($"Replay: script write (event {_pos}) waits for battle phase {ev.Phase} (now {phase})");
            }
            if (_phaseWait.Elapsed.TotalSeconds < 10)
                return false;
            _phaseWait.Reset();
            Mismatch($"script write waited 10 s for battle phase {ev.Phase} (still {phase}); applied anyway");
            return true;
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
            if (Live)
                BattleControl.OnInjected(ev); // M2: a command this PC asked for is running
        }

        // ---- engine callbacks ---------------------------------------------------------------------------------------

        /// <summary>The engine reached an effect point of a running command: apply the recorded outcome.</summary>
        public void ReplaceCalculation(BTL_DATA caster, BTL_DATA target, CMD_DATA cmd)
        {
            Int32 seq = cmd != null && _seqOf.TryGetValue(cmd, out Int32 s) ? s : -1;
            Pump(true); // the animation is here: what the owner had before this effect plays now, even if a frame or two early
            BattleEvent ev = _pos < Available ? Log.Events[_pos] : null;
            if (ev == null || ev.Type != BattleEventType.Effect || ev.Seq != seq || ev.Target != target.btl_id)
            {
                if (ev == null && Live && !_stream.EndReceived)
                    EarlyEffects++;
                String where = Live ? $" (owner at frame {_stream.OwnerFrame}, this PC at {_runFrame})" : "";
                Mismatch($"engine reached an effect point (#{seq} {cmd?.cmd_no} → {BattleSync.UnitName(target.btl_id)}) but the log has {(ev == null ? "nothing yet" : ev.ToString().Split('\n')[0])}{where}; skipped");
                return;
            }
            ApplyEffect(ev, cmd, target);
            _pos++;
            Pump();
        }

        private void ApplyOps(BattleEvent ev, CMD_DATA cmd)
        {
            BattleSync.Applying++;
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
                BattleSync.Applying--;
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
                BTL_DATA btl = BattleSync.FindBtl(f.Unit);
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
                BTL_DATA btl = BattleSync.FindBtl(u.Id);
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
                BTL_DATA btl = BattleSync.FindBtl(ev.Target);
                if (u != null && btl != null && (btl.cur.hp != u.Hp || btl.cur.mp != u.Mp) && u.Hp > 0)
                {
                    btl.cur.hp = u.Hp;
                    btl.cur.mp = u.Mp;
                    TickWrites++;
                }
            }
            // Known direct write: a revive (Phoenix Down, Life...) removes Death through the engine (an op), then the
            // ability script sets the HP directly
            foreach (BattleOp op in ev.Ops)
            {
                if (op.Kind != OpKind.Remove || op.A != (Int32)BattleStatusId.Death)
                    continue;
                UnitState u = ev.State.Find(op.Unit);
                BTL_DATA btl = BattleSync.FindBtl(op.Unit);
                if (u != null && btl != null && btl.cur.hp != u.Hp && u.Hp > 0)
                {
                    btl.cur.hp = u.Hp;
                    ReviveWrites++;
                }
            }
            List<String> diffs = ev.State.ApplyToBattle();
            if (diffs.Count == 0)
                return;
            Corrections += diffs.Count;
            BattleSync.Note($"Replay correction after {what} (event {_pos}{(ev.Seq >= 0 ? $", #{ev.Seq}" : "")}): {String.Join("; ", diffs.ToArray())}");
        }

        private void ApplySync(BattleEvent ev)
        {
            List<String> diffs = ev.State.ApplyToBattle();
            if (diffs.Count == 0)
                return;
            Syncs++;
            BattleSync.Note($"Replay sync (event {_pos}): {String.Join("; ", diffs.ToArray())}");
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
                BattleSync.Note($"Replay tape: command #{seq} used {used} of {tapeOwner.Tape.Count} random numbers");
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

        public Boolean ApplySummonType(CMD_DATA cmd)
        {
            if (!_seqOf.TryGetValue(cmd, out Int32 seq))
                return false;
            BattleEvent ev = FindCommand(seq);
            if (ev == null || ev.ShortSummon < 0)
                return false;
            cmd.info.short_summon = (Byte)ev.ShortSummon;
            return true;
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
                BattleSync.Note($"Replay tape miss: command #{seq} draw {index} ({kind}) not on the tape ({ev?.Tape.Count ?? 0} values)");
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
                BattleSync.Note($"Replay: dropped a locally queued command {cmd.cmd_no} by {BattleSync.UnitName(cmd.regist?.btl_id ?? 0)}");
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
                BTL_DATA btl = BattleSync.FindBtl(u.Id);
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

        /// <summary>The owner's battle is over for this PC (it left, or it was dropped): stop playing its events.</summary>
        public Boolean Aborted;

        private Boolean _done;
        private readonly Stopwatch _endWait = new Stopwatch();

        /// <summary>
        /// battle.cs, at the last fade of the battle (victory, defeat, flee), before the battle is closed: true = it may
        /// close. A passive battle waits there for the owner's End (result, rewards), then takes the owner's rewards.
        /// Gives up after 20 s (End missing) or when the owner is gone.
        /// </summary>
        public Boolean MayEnd()
        {
            if (_done || !_started || Aborted)
                return true;
            if (!_endWait.IsRunning)
                _endWait.Start();
            if (_stream != null && _stream.EndReceived)
                _stream.ReleaseAll();
            Pump(true);
            Int32 endIndex = Log.Events.FindIndex(e => e.Type == BattleEventType.End);
            Double waited = _endWait.Elapsed.TotalSeconds;
            if (endIndex < 0 && waited < 20)
                return false; // the owner's last events aren't in yet
            if (endIndex >= 0 && _pos < endIndex && waited < 10)
                return false; // still playing them
            if (endIndex < 0)
            {
                Mismatch("the owner's End didn't arrive within 20 s; ending with this PC's own result and rewards");
                _done = true;
                return true;
            }
            if (_pos < endIndex)
                Mismatch($"ending with {endIndex - _pos} event(s) not played (stuck at {Log.Events[_pos].Type})");
            _pos = endIndex;
            Finish(Log.Events[endIndex]);
            return true;
        }

        private void Finish(BattleEvent end)
        {
            _done = true;
            FF9StateGlobal ff9 = FF9StateSystem.Common.FF9;
            Byte result = ff9.btl_result;
            if (result != end.Result)
                Mismatch($"result {result}, owner {end.Result}");
            List<String> diffs = end.State.Diff(BattleSnapshot.Capture());
            if (diffs.Count > 0)
                Mismatch("final state: " + String.Join("; ", diffs.ToArray()));
            BONUS bonus = battle.btl_bonus;
            if (bonus.exp != end.Exp || bonus.gil != end.BonusGil || bonus.ap != end.Ap || bonus.card != end.Card || bonus.item.Count != end.BonusItems.Count)
                BattleSync.Note($"Replay: rewards computed locally (exp {bonus.exp} gil {bonus.gil} ap {bonus.ap} card {bonus.card} items {bonus.item.Count}) replaced by the owner's (exp {end.Exp} gil {end.BonusGil} ap {end.Ap} card {end.Card} items {end.BonusItems.Count})");
            bonus.exp = end.Exp;
            bonus.gil = end.BonusGil;
            bonus.ap = end.Ap;
            bonus.card = end.Card;
            bonus.item = new List<RegularItem>(end.BonusItems);
            _pos = Log.Events.Count;
            // M1: a participant never goes to the game over screen (the defeat rules are M3): it leaves as from a battle
            // without a game over and goes back to its field
            if (Live && (ff9.btl_result == FF9StateGlobal.BTL_RESULT_DEFEAT || ff9.btl_result == FF9StateGlobal.BTL_RESULT_GAMEOVER) && !FF9StateSystem.Battle.FF9Battle.btl_scene.Info.NoGameOver)
            {
                BattleSync.Note("Replay: the owner's battle was lost; this PC leaves it without a game over (M1)");
                ff9.btl_result = FF9StateGlobal.BTL_RESULT_ESCAPE;
            }
        }

        public void AfterFrame(UInt32 battleResult)
        {
            if (battleResult != 1 || _ended || !_started)
                return;
            _ended = true;
            if (!_done && Aborted)
                BattleSync.Note($"Replay: this PC left the owner's battle early (result {FF9StateSystem.Common.FF9.btl_result}); {Log.Events.Count - _pos} event(s) not played");
            else if (!_done)
            {
                Pump(true);
                if (_pos < Log.Events.Count && Log.Events[_pos].Type == BattleEventType.End)
                    Finish(Log.Events[_pos]);
                else
                    Mismatch($"the passive battle ended (result {FF9StateSystem.Common.FF9.btl_result}) with {Log.Events.Count - _pos} events left");
            }
            BattleSync.OnReplayDone(this);
        }
    }
}
