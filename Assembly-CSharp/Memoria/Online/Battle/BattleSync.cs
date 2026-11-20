using FF9;
using Memoria.Data;
using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.IO;

namespace Memoria.Online.Battle
{
    public enum DebugReplayMode { Off, Record, Replay }

    /// <summary>
    /// Spike C: "calculate on one PC, play back on the others", tested on one PC by record and replay.
    /// Every engine hook (marked <c>[Online] Spike C</c>) calls into this class.
    ///
    /// Record: the battle runs normally; <see cref="BattleRecorder"/> logs each admitted command (with the random numbers
    /// it draws outside the calculation), each calculation's engine-level effects ("ops"), status ticks, ATB and the end.
    /// Replay (passive mode, <see cref="PassiveBattle"/>): no ATB, no enemy AI, no menu, no calculation;
    /// <see cref="BattleReplayer"/> injects the recorded commands and replaces each calculation by its recorded ops,
    /// and compares the state after every action.
    /// </summary>
    public static class BattleSync
    {
        public static DebugReplayMode Mode = DebugReplayMode.Off;
        public static BattleRecorder Recorder;
        public static BattleReplayer Replayer;
        public static BattleLog LastLog;
        public static String LastSummary = String.Empty;
        public static Int32 CalcDepth;
        public static Int32 Applying; // > 0 while the replayer applies the owner's ops / state
        public static Int32 BlockedScriptWrites;
        private static Int32 _opDepth;

        public static Boolean Passive => Replayer != null;

        // ---- helpers ------------------------------------------------------------------------------------------------

        public static BTL_DATA FindBtl(UInt16 id)
        {
            if (id == 0)
                return null;
            for (BTL_DATA btl = FF9StateSystem.Battle.FF9Battle.btl_list.next; btl != null; btl = btl.next)
                if (btl.btl_id == id)
                    return btl;
            return null;
        }

        public static Int32 UnitIndex(UInt16 id)
        {
            for (Int32 i = 0; i < 16; i++)
                if (id == 1 << i)
                    return i;
            return 255;
        }

        public static String UnitName(UInt16 id)
        {
            if (id == 0)
                return "-";
            Int32 index = UnitIndex(id);
            if (index == 255)
                return $"{id:X2}";
            return index < 4 ? $"P{index}" : $"E{index - 4}";
        }

        public static void Note(String message)
        {
            Log.Message("[Battle] " + message);
        }

        public static String LogFolder
        {
            get
            {
                String folder = Path.Combine(Directory.GetCurrentDirectory(), "BattleLogs");
                if (!Directory.Exists(folder))
                    Directory.CreateDirectory(folder);
                return folder;
            }
        }

        public static void CycleMode()
        {
            Mode = Mode == DebugReplayMode.Off ? DebugReplayMode.Record : Mode == DebugReplayMode.Record ? DebugReplayMode.Replay : DebugReplayMode.Off;
            if (Mode == DebugReplayMode.Replay && (LastLog == null || !LastLog.Complete))
                Note("Replay mode, but no complete recording yet: battles run normally");
            Note($"Mode: {Mode}");
        }

        // ---- battle lifecycle (HonoluluBattleMain) --------------------------------------------------------------------

        /// <summary>HonoluluBattleMain.Start, before the scene is built.</summary>
        public static void OnBattleSceneStart()
        {
            Recorder = null;
            Replayer = null;
            BattleEntry.ClearDebugStart();
            CalcDepth = 0;
            _opDepth = 0;
            OwnerStream = null;
            if (BattleEntry.InSharedBattle)
            {
                if (Mode != DebugReplayMode.Off)
                    Note($"Debug {Mode} mode ignored: this is a shared battle");
                return; // BattleEntry.BeforeBattleInit sets up the owner's recorder or the passive replay
            }
            if (Mode != DebugReplayMode.Off && (!Configuration.Battle.SFXRework || Configuration.Battle.Speed >= 3))
                Note($"warning: replay assumes SFXRework=1 and Speed<3 (have {Configuration.Battle.SFXRework}, {Configuration.Battle.Speed})");
            if (Mode == DebugReplayMode.Replay && LastLog != null && LastLog.Complete)
            {
                // The recorded battle, pattern and start type are forced through BattleEntry, like a participant's
                BattleEntry.ForceDebugStart(LastLog.MapNo, LastLog.Pattern, LastLog.StartType);
                Replayer = new BattleReplayer(LastLog);
                Note($"Replay: passive battle {LastLog.MapNo} pattern {LastLog.Pattern}, {LastLog.Events.Count} events");
            }
            else if (Mode == DebugReplayMode.Record)
            {
                Recorder = new BattleRecorder();
                Note($"Record: battle {FF9StateSystem.Common.FF9.btlMapNo}");
            }
        }

        /// <summary>HonoluluBattleMain.UpdateBattleFrame, before battle.BattleMain.</summary>
        public static void BeforeFrame()
        {
            try
            {
                Recorder?.BeforeFrame();
                Replayer?.BeforeFrame();
            }
            catch (Exception err)
            {
                Log.Error(err, "[Battle] BeforeFrame");
            }
        }

        /// <summary>HonoluluBattleMain.UpdateBattleFrame, after the frame (battleResult 1 = the battle is over).</summary>
        public static void AfterFrame(UInt32 battleResult)
        {
            try
            {
                Recorder?.AfterFrame(battleResult);
                BattleControl.OwnerSweep();
                OwnerStream?.SampleAtb();
                Replayer?.AfterFrame(battleResult);
                EndWatchdog(battleResult);
            }
            catch (Exception err)
            {
                Log.Error(err, "[Battle] AfterFrame");
            }
        }

        private static Single _allEnemiesDownSince = -1f;
        private static Boolean _watchdogLogged;

        /// <summary>Debug: the battle should end once every enemy is down; if it hasn't 8 s later, log why.</summary>
        private static void EndWatchdog(UInt32 battleResult)
        {
            if (!Configuration.Debug.OnlineDebug)
                return;
            Boolean enemyUp = false, anyEnemy = false;
            for (BTL_DATA btl = FF9StateSystem.Battle.FF9Battle.btl_list.next; btl != null; btl = btl.next)
                if (btl.bi.player == 0)
                {
                    anyEnemy = true;
                    if (!btl_stat.CheckStatus(btl, BattleStatusConst.BattleEndFull))
                        enemyUp = true;
                }
            if (enemyUp || battleResult == 1)
            {
                _allEnemiesDownSince = -1f;
                _watchdogLogged = false;
                return;
            }
            if (_allEnemiesDownSince < 0f)
            {
                _allEnemiesDownSince = UnityEngine.Time.realtimeSinceStartup;
                Note($"watchdog: no enemy standing ({(anyEnemy ? "all down" : "none left in btl_list")})\n" + DescribeBattleFlow());
            }
            else if (!_watchdogLogged && UnityEngine.Time.realtimeSinceStartup - _allEnemiesDownSince > 20f)
            {
                _watchdogLogged = true;
                Note("watchdog: the battle hasn't ended 20 s after the last enemy went down\n" + DescribeBattleFlow());
            }
        }

        public static String DescribeBattleFlow()
        {
            FF9StateBattleSystem btlsys = FF9StateSystem.Battle.FF9Battle;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append($"    phase {btlsys.btl_phase} seq {btlsys.btl_seq} result {FF9StateSystem.Common.FF9.btl_result} cmd_status {btlsys.cmd_status} SFX running {SFX.IsRunning()} actions {UnifiedBattleSequencer.runningActions.Count} enemy_die {btlsys.enemy_die}\n");
            foreach (CMD_DATA cmd in btlsys.cur_cmd_list)
                sb.Append($"    running: {cmd.cmd_no} sub {cmd.sub_no} by {UnitName(cmd.regist?.btl_id ?? 0)} mode {cmd.info.mode} → {cmd.tar_id:X2}\n");
            for (CMD_DATA cmd = btlsys.cmd_queue.next; cmd != null; cmd = cmd.next)
                sb.Append($"    queued: {cmd.cmd_no} sub {cmd.sub_no} by {UnitName(cmd.regist?.btl_id ?? 0)}\n");
            foreach (UnifiedBattleSequencer.BattleAction action in UnifiedBattleSequencer.runningActions)
                sb.Append($"    action: cmd {action.cmd?.cmd_no} by {UnitName(action.cmd?.regist?.btl_id ?? 0)} frame {action.frameIndex}\n");
            for (BTL_DATA btl = btlsys.btl_list.next; btl != null; btl = btl.next)
                sb.Append($"    {UnitName(btl.btl_id)} hp {btl.cur.hp} st [{btl.stat.cur}] die_seq {btl.die_seq} target {btl.bi.target} disappear {btl.bi.disappear} busy {btl_util.IsBtlBusy(btl, btl_util.BusyMode.ANY_CURRENT)} anim {btl.currentAnimationName} end {btl.animEndFrame}\n");
            return sb.ToString();
        }

        public static void OnRecordingDone(BattleLog log)
        {
            LastLog = CheckWire(log) ?? log; // the debug replay plays what came back through the wire format
            Recorder = null;
            Mode = DebugReplayMode.Replay; // one recording per Record: the next battle replays it
            String path = Path.Combine(LogFolder, $"record-{DateTime.Now:yyyyMMdd-HHmmss}-battle{log.MapNo}.txt");
            log.Save(path);
            LastSummary = $"Recorded battle {log.MapNo}: {log.CountOf(BattleEventType.Command)} commands, {log.CountOf(BattleEventType.Effect)} effects. {log.SizeReport()}";
            Note(LastSummary + " → " + path);
        }

        public static void OnReplayDone(BattleReplayer replayer)
        {
            LastSummary = $"{(replayer.Live ? "Shared battle (watched)" : "Replay")} done: {replayer.Status}";
            if (replayer.Live)
            {
                LastSummary += $"; {replayer.Stream.Status}";
                if (replayer.StartFixes > 0)
                    LastSummary += $"; {replayer.StartFixes} initial difference(s) set from the owner's";
                if (Configuration.Debug.OnlineDebug)
                    replayer.Log.Save(Path.Combine(LogFolder, $"watched-{DateTime.Now:yyyyMMdd-HHmmss}-battle{replayer.Log.MapNo}.txt"));
            }
            Note(LastSummary);
            Replayer = null;
        }

        // ---- Step 3: shared battles (driven by BattleEntry) -----------------------------------------------------------

        /// <summary>The owner of a shared battle streams it to the participants.</summary>
        public static BattleStreamOut OwnerStream;

        /// <summary>BattleEntry, owner, battle init: record this battle for the participants.</summary>
        public static void BeginOwner()
        {
            Recorder = new BattleRecorder { Network = true };
            OwnerStream = new BattleStreamOut(Recorder);
            Note($"Shared battle {FF9StateSystem.Common.FF9.btlMapNo}: this PC owns it and streams it");
        }

        /// <summary>BattleEntry, participant, battle init: play the owner's battle passively.</summary>
        public static void BeginPassive()
        {
            BattleStreamIn stream = new BattleStreamIn();
            Replayer = new BattleReplayer(stream.Log, stream);
            Note($"Shared battle {FF9StateSystem.Common.FF9.btlMapNo}: watching the owner's");
        }

        /// <summary>BattleEntry, participant: a BattleStream message from the owner.</summary>
        public static void OnStream(Byte[] message)
        {
            BattleStreamIn stream = Replayer?.Stream;
            if (stream == null)
            {
                Note($"Stream: {message.Length} bytes arrived with no shared battle running here; dropped");
                return;
            }
            try { stream.OnMessage(message); }
            catch (Exception err) { Log.Error(err, "[Battle] Can't read the owner's battle stream"); }
        }

        /// <summary>BattleEntry, owner: the next message for the participants (null: nothing new).</summary>
        public static Byte[] TakeStream(UInt16 seq) => OwnerStream?.TakeMessage(seq);

        /// <summary>The owner's stream has sent its End (the participants may end their battles).</summary>
        public static Boolean OwnerStreamEnded => OwnerStream != null && OwnerStream.EndSent;

        /// <summary>A participant's battle is being left early (owner gone, dropped): stop playing the stream.</summary>
        public static void AbortLive()
        {
            if (Replayer != null && Replayer.Live && !Replayer.Aborted)
            {
                Replayer.Aborted = true;
                Note("Shared battle: stopped playing the owner's events");
            }
        }

        public static Boolean LivePassive => Replayer != null && Replayer.Live;

        /// <summary>BattleHUD: a passive battle takes the owner's "run" buttons, not this PC's (null: not passive).</summary>
        public static Byte? PassiveEscapeKey => Replayer == null ? (Byte?)null : Replayer.Live && !Replayer.Aborted && Replayer.Stream.EndReceived == false ? Replayer.Stream.EscapeKey : (Byte)0;

        /// <summary>HonoluluBattleMain, before a battle frame: true = a passive battle waits for the owner (see
        /// <see cref="BattleReplayer.HoldFrame"/>).</summary>
        public static Boolean HoldPassiveFrame()
        {
            try { return Replayer != null && Replayer.HoldFrame(); }
            catch (Exception err) { Log.Error(err, "[Battle] HoldPassiveFrame"); return false; }
        }

        /// <summary>HonoluluBattleMain: battle frames to run this update on top of the usual (a passive battle catching up).</summary>
        public static Int32 PassiveExtraFrames()
        {
            try { return Replayer != null ? Replayer.ExtraFrames() : 0; }
            catch (Exception err) { Log.Error(err, "[Battle] PassiveExtraFrames"); return 0; }
        }

        /// <summary>The owner's recording ended (its battle is over): summary; the stream sends what's left.</summary>
        public static void OnOwnerBattleDone(BattleLog log)
        {
            Recorder = null;
            CheckWire(log);
            if (OwnerStream != null)
            {
                Int32 oldAtb = OwnerStream.OldAtbBytes();
                LastSummary = $"Shared battle {log.MapNo} done (owner): {log.CountOf(BattleEventType.Command)} commands, {log.CountOf(BattleEventType.Effect)} effects; {OwnerStream.Status}; "
                            + $"ATB in the spike's format would have been {oldAtb} B";
                Note(LastSummary);
            }
            if (Configuration.Debug.OnlineDebug)
                log.Save(Path.Combine(LogFolder, $"owned-{DateTime.Now:yyyyMMdd-HHmmss}-battle{log.MapNo}.txt"));
        }

        /// <summary>Self-check of the wire format on a finished log (logged); the copy, or null if it failed.</summary>
        private static BattleLog CheckWire(BattleLog log)
        {
            try
            {
                List<String> problems = new List<String>();
                BattleLog copy = BattleWire.RoundTrip(log, problems);
                Note(problems.Count == 0 ? $"Wire check: all {log.Events.Count} events came back the same" : $"Wire check: {problems.Count} difference(s): {String.Join(" | ", problems.GetRange(0, Math.Min(10, problems.Count)).ToArray())}");
                return copy;
            }
            catch (Exception err)
            {
                Log.Error(err, "[Battle] Wire check failed");
                return null;
            }
        }

        /// <summary>battle.cs, before the battle closes (last fade): false holds it there (a passive battle waits for
        /// the owner's End).</summary>
        public static Boolean MayEndBattle()
        {
            try { return Replayer == null || Replayer.MayEnd(); }
            catch (Exception err) { Log.Error(err, "[Battle] MayEndBattle"); return true; }
        }

        /// <summary>One line for the status box.</summary>
        public static String StatusLine
        {
            get
            {
                String control = BattleControl.StatusLine;
                String controlLine = control != null ? "\n  " + control : "";
                if (OwnerStream != null)
                    return $"owner: {OwnerStream.Status}{controlLine}";
                if (Replayer != null && Replayer.Live)
                    return $"watching: {Replayer.Stream.Status}; applied {Replayer.Applied}, corrections {Replayer.Corrections}, mismatches {Replayer.Mismatches}, early fx {Replayer.EarlyEffects}, mod hook {CommandInjector.OverloadRuns}{controlLine}";
                return null;
            }
        }

        // ---- command flow (btl_cmd) ---------------------------------------------------------------------------------

        /// <summary>RunCommandFromQueue: true = passive, don't take commands from the local queue.</summary>
        public static Boolean BlockQueue(FF9StateBattleSystem btlsys)
        {
            if (Replayer == null)
                return false;
            Replayer.OnLocalQueue(btlsys);
            return true;
        }

        /// <summary>RunCommandFromQueue: the owner just moved <paramref name="cmd"/> to cur_cmd_list.</summary>
        public static void OnCommandAdmitted(CMD_DATA cmd)
        {
            Recorder?.OnCommandAdmitted(cmd);
            BattleControl.OnCommandAdmitted(cmd);
        }

        /// <summary>CheckCommandCondition (summons): true = passive, the owner's long / short choice was applied.</summary>
        public static Boolean ReplaySummonType(CMD_DATA cmd)
        {
            Recorder?.BeforeSummonType(cmd);
            return Replayer != null && Replayer.ApplySummonType(cmd);
        }

        public static void OnSummonTypeDecided(CMD_DATA cmd)
        {
            Recorder?.OnSummonTypeDecided(cmd);
        }

        /// <summary>FinishCommand, at its end.</summary>
        public static void OnCommandFinished(CMD_DATA cmd)
        {
            Recorder?.OnCommandFinished(cmd);
            Replayer?.OnCommandFinished(cmd);
        }

        /// <summary>CommandEngine (per command) and the SFX sequencer (per action): code that runs on every PC.</summary>
        public static CMD_DATA EnterCommand(CMD_DATA cmd)
        {
            if (Recorder != null)
                Recorder.Checkpoint();
            return BattleTape.Enter(cmd);
        }

        public static void ExitCommand(CMD_DATA previous)
        {
            BattleTape.Exit(previous);
            if (Recorder != null)
                Recorder.Baseline();
        }

        // ---- calculation (SBattleCalculator.CalcMain) ---------------------------------------------------------------

        public const Int32 CalcHandled = -1;

        /// <summary>Start of a calculation. Returns <see cref="CalcHandled"/> when the calculation must not run
        /// (passive: its recorded outcome was applied instead).</summary>
        public static Int32 CalcBegin(BTL_DATA caster, BTL_DATA target, BattleCommand command)
        {
            CalcDepth++;
            if (CalcDepth > 1)
                return CalcDepth;
            try
            {
                if (Replayer != null)
                {
                    if (caster != null && target != null)
                        Replayer.ReplaceCalculation(caster, target, command.Data);
                    CalcDepth--;
                    return CalcHandled;
                }
                if (Recorder != null && caster != null && target != null)
                    Recorder.CalcBegin(caster, target, command.Data);
            }
            catch (Exception err)
            {
                Log.Error(err, "[Battle] CalcBegin");
            }
            return CalcDepth;
        }

        public static void CalcEnd(Int32 token)
        {
            if (token == CalcHandled)
                return;
            if (token == 1 && Recorder != null)
            {
                try { Recorder.CalcEnd(); }
                catch (Exception err) { Log.Error(err, "[Battle] CalcEnd"); }
            }
            CalcDepth--;
        }

        // ---- ticks (battle.BattleMainLoop) --------------------------------------------------------------------------

        /// <summary>Per unit and frame, around btl_para.CheckPointData + btl_stat.CheckStatusLoop. False = passive:
        /// skip them (statuses tick on the owner only); <see cref="PassiveBattle.VisualTick"/> keeps the visuals.</summary>
        public static Boolean TickBegin(BattleUnit unit)
        {
            if (Replayer != null)
            {
                PassiveBattle.VisualTick(unit);
                return false;
            }
            Recorder?.TickBegin(unit);
            return true;
        }

        public static void TickEnd(BattleUnit unit)
        {
            Recorder?.TickEnd(unit);
        }

        // ---- engine "apply" calls (btl_para, btl_stat, BattleUnit.Kill, btl2d) ------------------------------------------

        /// <summary>Entering an apply call. Returns the op when it is recorded (top-level call inside a calculation or
        /// a tick on the owner), else null. Always pair with <see cref="OpExit"/>.</summary>
        public static BattleOp OpEnter(OpKind kind, BTL_DATA unit, Int32 a = 0, Int32 b = 0, Int32 c = 0, BTL_DATA other = null, Object[] parameters = null)
        {
            _opDepth++;
            if (_opDepth != 1 || Recorder == null || unit == null)
                return null;
            return Recorder.AddOp(kind, unit, a, b, c, other, parameters);
        }

        /// <summary>btl_scrp.SetCharacterData / SetBattleData: true = don't run it (a passive battle's own battle
        /// script; the owner's writes arrive as ops). Writes made by a command's sequence run everywhere.</summary>
        public static Boolean BlockScriptWrite(String what)
        {
            if (Replayer == null || !Replayer.Started || Applying > 0 || BattleTape.Context != null)
                return false;
            BlockedScriptWrites++;
            Note("Replay: blocked a local battle-script write: " + what);
            return true;
        }

        /// <summary>Script writes are recorded wherever they happen, except in code every PC runs (a command's steps).</summary>
        public static BattleOp ScriptOpEnter(OpKind kind, BTL_DATA unit, Int32 id, Int32 value)
        {
            _opDepth++;
            if (_opDepth != 1 || Recorder == null || (BattleTape.Context != null && CalcDepth == 0))
                return null;
            return Recorder.AddScriptOp(kind, unit, id, value);
        }

        /// <summary>Inventory changes: recorded inside a calculation or tick only (item use at SELECT_VFX runs everywhere).</summary>
        public static BattleOp ItemOpEnter(OpKind kind, Int32 itemId, Int32 count)
        {
            _opDepth++;
            if (_opDepth != 1 || Recorder == null)
                return null;
            return Recorder.AddOp(kind, null, itemId, count, 0, null, null);
        }

        public static BattleOp MessageOpEnter(String text, Byte priority)
        {
            _opDepth++;
            if (_opDepth != 1 || Recorder == null)
                return null;
            BattleOp op = Recorder.AddOp(OpKind.Message, null, priority, 0, 0, null, null);
            if (op != null)
                op.Text = text;
            return op;
        }

        public static void OpExit(BattleOp op, UInt32 result = 0)
        {
            _opDepth--;
            if (op == null)
                return;
            Recorder?.OpDone(op);
            op.Result = result;
            if (op.Kind == OpKind.Alter && result != btl_stat.ALTER_SUCCESS && result != btl_stat.ALTER_SUCCESS_NO_SET)
                op.Discard = true; // no effect on the owner: nothing to replay
            if (op.Kind == OpKind.Remove && result != 2)
                op.Discard = true;
        }
    }
}
