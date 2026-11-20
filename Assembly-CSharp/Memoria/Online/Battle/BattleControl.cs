using FF9;
using Memoria.Data;
using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Memoria.Online.Battle
{
    /// <summary>
    /// Step 3 / M2: who commands which unit in a shared battle, and the remote command menus.
    ///
    /// At Go the owner sends a <b>control map</b>: each player unit → the PC that commands it (its player's, or for a
    /// free character its leader's; a PC not in the battle → the owner). Enemies are always the owner's.
    ///
    /// Owner (runs the real battle): a remote-controlled unit whose ATB fills isn't put in the owner's ready queue
    /// (<see cref="RouteReady"/>); a TurnReady record goes in the stream instead, and the unit waits for its command as a
    /// vanilla player character waits for input (Active ATB: the battle goes on). The controlling PC's CommandRequest is
    /// validated and entered through <see cref="btl_cmd.SetCommand"/>, exactly what the owner's own menu calls, so the
    /// normal queue, ATB and priority rules apply. An invalid request gets CommandRejected.
    ///
    /// Controlling PC (a passive battle): TurnReady opens the normal battle menu (<see cref="BattleHUD"/>) for that unit;
    /// the menu's SetCommand calls are captured (<see cref="CaptureCommand"/>) and sent as one CommandRequest. The
    /// command then arrives in the owner's stream like any other.
    ///
    /// Per-player flee: a non-owner's run buttons make only its units run (<see cref="EscapeKeyFor"/>); a held run asks
    /// the owner to try the flee roll for it; on success the owner removes that PC's units from the battle
    /// (<see cref="BattleUnit.Remove"/>, as the game does for a party member taken out of a battle), streams it, and
    /// the PC leaves through the escape exit. The owner fleeing is vanilla: everyone in the battle flees.
    ///
    /// Control messages (reliable; little-endian; seq = the battle's seq):
    ///   CommandRequest  [29, seq u16, req u16, unit u8, count u8, (slot u8, cmd u16, sub i32, target u16, cursor u8, menu u8, priority u8)*]
    ///   CommandRejected [30, seq u16, req u16, unit u8, reason u8]
    ///   RunHeld         [31, seq u16, held u8]           controlling PC → owner: its player holds / let go of the run buttons
    ///   FleeTry         [32, seq u16]                     controlling PC → owner: run held one more second (vanilla's flee roll)
    ///   FleeResult      [33, seq u16, result u8]          owner → that PC: 0 fled, 1 this battle can't be fled, 2 not now
    /// Stream records (frame-stamped, every participant): TurnReady (unit, ready u8), RunMask (u16), UnitsLeave (u16).
    /// </summary>
    public static class BattleControl
    {
        public const Byte MsgCommandRequest = 29;
        public const Byte MsgCommandRejected = 30;
        public const Byte MsgRunHeld = 31;
        public const Byte MsgFleeTry = 32;
        public const Byte MsgFleeResult = 33;

        public static Boolean IsControlMessage(Byte[] d) => d != null && d.Length >= 3 && d[0] >= MsgCommandRequest && d[0] <= MsgFleeResult;

        public const Byte RejectNotReady = 1, RejectNoItem = 2, RejectNoMp = 3, RejectBadCommand = 4;

        private enum Role { None, Owner, Passive }

        private sealed class Pending
        {
            public UInt16 Req;
            public Double SentAt;
            public String What;
        }

        private sealed class Call
        {
            public Byte Slot;
            public BattleCommandId Cmd;
            public Int32 Sub;
            public UInt16 Target;
            public Byte Cursor, Menu, Priority;
        }

        private static Role _role;
        private static UInt16 _seq;
        private static UInt64 _owner;
        private static Action<UInt64, Byte[]> _send;

        // Owner
        private static readonly Dictionary<UInt16, UInt64> _remote = new Dictionary<UInt16, UInt64>();   // unit → controlling PC
        private static readonly HashSet<UInt16> _awaiting = new HashSet<UInt16>();                     // TurnReady sent, no command yet
        private static readonly HashSet<UInt64> _remoteRunning = new HashSet<UInt64>();
        private static readonly Dictionary<UInt16, Double> _acceptedAt = new Dictionary<UInt16, Double>(); // request accepted → admitted
        private static UInt64 _fleeFor;
        public static Action<UInt64> OnPeerFled;   // BattleEntry: stop streaming to it

        // Controlling PC
        private static readonly HashSet<UInt16> _mine = new HashSet<UInt16>();
        private static readonly Dictionary<UInt16, List<Call>> _captured = new Dictionary<UInt16, List<Call>>();
        private static readonly Dictionary<UInt16, Pending> _pending = new Dictionary<UInt16, Pending>();
        private static UInt16 _reqSeq;
        private static Boolean _runHeld, _runHeldSent;
        private static Single _runTimer;
        public static Action<Byte> OnFleeResult;   // BattleEntry: leave the battle (0) or show why not

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static Double Now => Clock.Elapsed.TotalMilliseconds;

        // Numbers for the status box and the results
        public static Int32 TurnsSent, Requests, Accepted, Rejected, RoundTrips, Flees;
        public static Double RoundTripTotal, RoundTripMax;

        /// <summary>A short line for the screen: the command this PC queued and is waiting to see run (null: none).</summary>
        public static String Notice { get; private set; }

        public static Boolean IsOwner => _role == Role.Owner;
        public static Boolean IsControllingPassive => _role == Role.Passive;

        public static void Reset()
        {
            if (_role != Role.None)
                BattleSync.Note($"Control: battle #{_seq} done: TurnReady {TurnsSent}, requests {Requests} (accepted {Accepted}, rejected {Rejected}), round trips {RoundTrips} (avg {(RoundTrips > 0 ? RoundTripTotal / RoundTrips : 0):0} ms, max {RoundTripMax:0} ms), flees {Flees}");
            _role = Role.None;
            _remote.Clear();
            _awaiting.Clear();
            _remoteRunning.Clear();
            _acceptedAt.Clear();
            _mine.Clear();
            _captured.Clear();
            _pending.Clear();
            _runHeld = _runHeldSent = false;
            _runTimer = 0f;
            _fleeFor = 0;
            Notice = null;
            TurnsSent = Requests = Accepted = Rejected = RoundTrips = Flees = 0;
            RoundTripTotal = RoundTripMax = 0;
        }

        /// <summary>Owner, at Go: units commanded by other PCs.</summary>
        public static void BeginOwner(UInt16 seq, Dictionary<UInt16, UInt64> remote, Action<UInt64, Byte[]> send)
        {
            Reset();
            _role = Role.Owner;
            _seq = seq;
            _send = send;
            foreach (KeyValuePair<UInt16, UInt64> kv in remote)
                _remote[kv.Key] = kv.Value;
            List<String> who = new List<String>();
            foreach (KeyValuePair<UInt16, UInt64> kv in _remote)
                who.Add($"{BattleSync.UnitName(kv.Key)} → {SteamNative.Tag(kv.Value)}");
            BattleSync.Note($"Control: battle #{seq}, remote units: {(who.Count > 0 ? String.Join(", ", who.ToArray()) : "none")}");
        }

        /// <summary>Participant, at Go: the units this PC commands (may be none: it only watches).</summary>
        public static void BeginPassive(UInt16 seq, UInt64 owner, IEnumerable<UInt16> mine, Action<UInt64, Byte[]> send)
        {
            Reset();
            _role = Role.Passive;
            _seq = seq;
            _owner = owner;
            _send = send;
            foreach (UInt16 unit in mine)
                _mine.Add(unit);
            List<String> names = new List<String>();
            foreach (UInt16 unit in _mine)
                names.Add(BattleSync.UnitName(unit));
            BattleSync.Note($"Control: battle #{seq}, this PC commands {(names.Count > 0 ? String.Join(", ", names.ToArray()) : "no unit (watching)")}");
        }

        public static Boolean IsMine(UInt16 unit) => _role == Role.Passive && _mine.Contains(unit);
        public static Boolean HasOwnUnits => _role == Role.Passive && _mine.Count > 0;

        private static Int32 PlayerIndex(UInt16 btlId)
        {
            for (Int32 i = 0; i < 4; i++)
                if (btlId == 1 << i)
                    return i;
            return -1;
        }

        // ---- Owner: turns ---------------------------------------------------------------------------------------

        /// <summary>[Online] HonoluluBattleMain.ProcessActiveTime: a player unit's ATB is full and it has no command.
        /// True = another PC commands it (TurnReady sent; not in this PC's ready queue).</summary>
        public static Boolean RouteReady(BTL_DATA btl)
        {
            if (_role != Role.Owner || !_remote.ContainsKey(btl.btl_id))
                return false;
            if (_awaiting.Add(btl.btl_id))
            {
                TurnsSent++;
                BattleSync.OwnerStream?.WriteTurn(btl.btl_id, true);
                BattleSync.Note($"Control: TurnReady {BattleSync.UnitName(btl.btl_id)} → {SteamNative.Tag(_remote[btl.btl_id])}");
            }
            return true;
        }

        /// <summary>Owner, after each battle frame: a waiting remote unit that can't act any more (KO, a status took its
        /// turn away, auto-attack, ATB reset) gets a TurnCancel.</summary>
        public static void OwnerSweep()
        {
            if (_role != Role.Owner || _awaiting.Count == 0)
                return;
            List<UInt16> gone = null;
            foreach (UInt16 unit in _awaiting)
            {
                BTL_DATA btl = BattleSync.FindBtl(unit);
                if (btl != null && btl.cur.hp > 0 && btl.sel_mode == 0 && btl.cur.at >= btl.max.at && !btl_cmd.CheckUsingCommand(btl.cmd[0])
                    && !btl_stat.CheckStatus(btl, BattleStatusConst.PreventATBConfirm) && _remote.ContainsKey(unit))
                    continue;
                if (gone == null)
                    gone = new List<UInt16>();
                gone.Add(unit);
            }
            if (gone == null)
                return;
            foreach (UInt16 unit in gone)
            {
                _awaiting.Remove(unit);
                BattleSync.OwnerStream?.WriteTurn(unit, false);
                BattleSync.Note($"Control: TurnCancel {BattleSync.UnitName(unit)} (can't act now)");
            }
        }

        /// <summary>Owner: a command of a remote unit was admitted (round trip: request accepted → running).</summary>
        public static void OnCommandAdmitted(CMD_DATA cmd)
        {
            if (_role != Role.Owner || cmd?.regist == null || cmd != cmd.regist.cmd[0])
                return;
            if (_acceptedAt.TryGetValue(cmd.regist.btl_id, out Double at))
            {
                _acceptedAt.Remove(cmd.regist.btl_id);
                BattleSync.Note($"Control: {BattleSync.UnitName(cmd.regist.btl_id)}'s remote command {cmd.cmd_no} runs {Now - at:0} ms after it was accepted");
            }
        }

        /// <summary>A controlling PC dropped: its units go to this PC's own menu (M2's minimal rule).</summary>
        public static void ReleasePeer(UInt64 peer, String why)
        {
            if (_role != Role.Owner)
                return;
            List<UInt16> units = new List<UInt16>();
            foreach (KeyValuePair<UInt16, UInt64> kv in _remote)
                if (kv.Value == peer)
                    units.Add(kv.Key);
            foreach (UInt16 unit in units)
            {
                _remote.Remove(unit);
                _awaiting.Remove(unit);
            }
            _remoteRunning.Remove(peer);
            if (units.Count > 0)
                BattleSync.Note($"Control: {SteamNative.Tag(peer)} {why}: {units.Count} unit(s) now take this PC's commands");
        }

        // ---- Owner: requests --------------------------------------------------------------------------------------

        public static void OnMessage(UInt64 peer, Byte[] d)
        {
            try
            {
                if (BattleEntryMessages.Seq(d) != _seq)
                    return;
                switch (d[0])
                {
                    case MsgCommandRequest:
                        if (_role == Role.Owner) OnRequest(peer, d);
                        break;
                    case MsgCommandRejected:
                        if (_role == Role.Passive && peer == _owner) OnRejected(d);
                        break;
                    case MsgRunHeld:
                        if (_role == Role.Owner && d.Length >= 4)
                        {
                            if (d[3] != 0) _remoteRunning.Add(peer);
                            else _remoteRunning.Remove(peer);
                        }
                        break;
                    case MsgFleeTry:
                        if (_role == Role.Owner) OnFleeTry(peer);
                        break;
                    case MsgFleeResult:
                        if (_role == Role.Passive && peer == _owner && d.Length >= 4)
                        {
                            BattleSync.Note($"Control: flee result {d[3]} ({(d[3] == 0 ? "fled" : d[3] == 1 ? "this battle can't be fled" : "not this time")})");
                            if (d[3] == 1)
                                UIManager.Battle.SetBattleFollowMessage(BattleMesages.CannotEscape);
                            OnFleeResult?.Invoke(d[3]);
                        }
                        break;
                }
            }
            catch (Exception err)
            {
                Log.Error(err, $"[Battle] Control message {d[0]} from {SteamNative.Tag(peer)} failed");
            }
        }

        private static void OnRequest(UInt64 peer, Byte[] d)
        {
            Requests++;
            Int32 o = 3;
            UInt16 req = (UInt16)(d[o] | (d[o + 1] << 8)); o += 2;
            UInt16 unit = d[o++];
            Int32 count = d[o++];
            List<Call> calls = new List<Call>();
            for (Int32 i = 0; i < count && o + 12 <= d.Length; i++) // 12 bytes per call
            {
                Call c = new Call { Slot = d[o++] };
                c.Cmd = (BattleCommandId)(d[o] | (d[o + 1] << 8)); o += 2;
                c.Sub = d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24); o += 4;
                c.Target = (UInt16)(d[o] | (d[o + 1] << 8)); o += 2;
                c.Cursor = d[o++];
                c.Menu = d[o++];
                c.Priority = d[o++];
                calls.Add(c);
            }
            BTL_DATA btl = BattleSync.FindBtl(unit);
            String what = calls.Count > 0 ? $"{calls[calls.Count - 1].Cmd} {calls[calls.Count - 1].Sub} → {calls[calls.Count - 1].Target:X2}{(calls.Count > 1 ? $" (+{calls.Count - 1})" : "")}" : "nothing";
            Byte reason = 0;
            String why = null;
            if (btl == null || !_remote.TryGetValue(unit, out UInt64 controller) || controller != peer)
            {
                reason = RejectNotReady;
                why = "not this PC's unit";
            }
            else if (!_awaiting.Contains(unit) || btl.sel_mode != 0 || btl_cmd.CheckUsingCommand(btl.cmd[0]) || btl.cur.hp == 0
                     || FF9StateSystem.Battle.FF9Battle.btl_phase != FF9StateBattleSystem.PHASE_NORMAL)
            {
                reason = RejectNotReady;
                why = "its turn is over (status, KO or the battle phase)";
            }
            else if (calls.Count == 0 || calls.Count > 2 || calls.Count != count)
            {
                reason = RejectBadCommand;
                why = $"{calls.Count} command(s) read of {count}, {d.Length} bytes";
            }
            else
            {
                foreach (Call c in calls)
                {
                    reason = Validate(btl, c, out why);
                    if (reason != 0)
                        break;
                }
            }
            if (reason != 0)
            {
                Rejected++;
                BattleSync.Note($"Control: request #{req} from {SteamNative.Tag(peer)} for {BattleSync.UnitName(unit)} ({what}) REJECTED: {why}");
                _send(peer, new Byte[] { MsgCommandRejected, (Byte)_seq, (Byte)(_seq >> 8), (Byte)req, (Byte)(req >> 8), (Byte)unit, reason });
                return;
            }
            // As the owner's own menu does (BattleHUD.SendCommand / SendDoubleCastCommand)
            _awaiting.Remove(unit);
            btl.sel_mode = 1;
            foreach (Call c in calls)
            {
                CMD_DATA cmd = c.Slot < btl.cmd.Count ? btl.cmd[c.Slot] : btl.cmd[0];
                btl_cmd.SetCommand(cmd, c.Cmd, c.Sub, c.Target, c.Cursor, c.Priority != 0, (BattleCommandMenu)c.Menu);
            }
            Accepted++;
            _acceptedAt[unit] = Now;
            BattleSync.Note($"Control: request #{req} from {SteamNative.Tag(peer)} for {BattleSync.UnitName(unit)} accepted: {what} (queued: {btl_cmd.CheckCommandQueued(btl.cmd[0])})");
        }

        /// <summary>What the owner checks before entering a remote command: the item is still there (counting the uses
        /// already queued), the MP is enough.</summary>
        private static Byte Validate(BTL_DATA btl, Call c, out String why)
        {
            why = null;
            if (c.Slot != 0 && c.Slot != 3)
            {
                why = $"slot {c.Slot}";
                return RejectBadCommand;
            }
            try
            {
                CMD_DATA test = new CMD_DATA { regist = btl, cmd_no = c.Cmd, sub_no = c.Sub };
                btl_cmd.ClearCommand(test);
                test.cmd_no = c.Cmd;
                test.sub_no = c.Sub;
                test.info.cmdMenu = (BattleCommandMenu)c.Menu;
                test.SetAAData(btl_util.GetCommandAction(test));
                RegularItem item = btl_util.GetCommandItem(test);
                if (item != RegularItem.NoItem)
                {
                    Int32 have = ff9item.FF9Item_GetCount(item);
                    Int32 queued = 0;
                    FF9StateBattleSystem btlsys = FF9StateSystem.Battle.FF9Battle;
                    for (CMD_DATA q = btlsys.cmd_queue.next; q != null; q = q.next)
                        if (btl_util.GetCommandItem(q) == item)
                            queued++;
                    foreach (CMD_DATA q in btlsys.cur_cmd_list)
                        if (q.info.mode == command_mode_index.CMD_MODE_INSPECTION && btl_util.GetCommandItem(q) == item)
                            queued++;
                    if (have - queued <= 0)
                    {
                        why = $"item {item}: {have} left, {queued} already queued";
                        return RejectNoItem;
                    }
                }
                Int32 mp = test.GetCommandMPCost();
                BattleAbilityHelper.GetPatchedMPCost(ref mp, btl_util.GetCommandMainActionIndex(test), new BattleUnit(btl), test.cmd_no, test.info.cmdMenu, test.aa, test);
                if (battle.GARNET_SUMMON_FLAG != 0 && (test.AbilityType & 4) != 0)
                    mp *= 4;
                if (btl.bi.player != 0)
                    mp = mp * FF9StateSystem.Common.FF9.player[(CharacterId)btl.bi.slot_no].mpCostFactor / 100;
                if (c.Slot == 0 && mp > btl.cur.mp && c.Cmd != BattleCommandId.MagicCounter)
                {
                    why = $"{mp} MP needed, {btl.cur.mp} left";
                    return RejectNoMp;
                }
            }
            catch (Exception err)
            {
                BattleSync.Note($"Control: couldn't check {c.Cmd} {c.Sub} ({err.Message}); accepted unchecked");
            }
            return 0;
        }

        // ---- Owner: run and flee ----------------------------------------------------------------------------------

        /// <summary>The "run" state of a player unit: its controlling PC's run buttons (vanilla: the one battle-wide key).
        /// [Online] btl_mot (run pose), HonoluluBattleMain.UpdateData, BattleUnit.IsRunningAway.</summary>
        public static Byte EscapeKeyFor(BTL_DATA btl)
        {
            Byte global = FF9StateSystem.Battle.FF9Battle.btl_escape_key;
            if (btl == null || btl.bi.player == 0 || _role == Role.None)
                return global;
            if (_role == Role.Owner)
            {
                if (_remote.TryGetValue(btl.btl_id, out UInt64 peer))
                    return (Byte)(_remoteRunning.Contains(peer) ? 1 : 0);
                return global;
            }
            if (_mine.Contains(btl.btl_id))
                return (Byte)(_runHeld ? 1 : 0);
            BattleStreamIn stream = BattleSync.Replayer?.Stream;
            return stream != null ? (Byte)((stream.RunMask & btl.btl_id) != 0 ? 1 : 0) : global;
        }

        /// <summary>Owner, each frame: the units running (for the participants' screens).</summary>
        public static UInt16 OwnerRunMask()
        {
            UInt16 mask = 0;
            for (BTL_DATA btl = FF9StateSystem.Battle.FF9Battle.btl_list.next; btl != null; btl = btl.next)
                if (btl.bi.player != 0 && EscapeKeyFor(btl) != 0)
                    mask |= btl.btl_id;
            return mask;
        }

        private static void OnFleeTry(UInt64 peer)
        {
            FF9StateBattleSystem btlsys = FF9StateSystem.Battle.FF9Battle;
            Byte result = 2;
            if (!btlsys.btl_scene.Info.Runaway)
                result = 1;
            else if ((btlsys.cmd_status & 1) == 0 && btlsys.btl_phase == FF9StateBattleSystem.PHASE_NORMAL && UnitsOf(peer).Count > 0)
            {
                // The game's own flee roll (btl_sys.CheckEscape, script 56); its "escape" command is caught in SetCommand
                _fleeFor = peer;
                _fleeDone = false;
                try { SBattleCalculator.CalcMain(null, null, null, btl_sys.fleeScriptId); }
                finally { _fleeFor = 0; }
                if (_fleeDone)
                    return; // FleePeer answered
            }
            if (result == 1)
                _send(peer, new Byte[] { MsgFleeResult, (Byte)_seq, (Byte)(_seq >> 8), result });
        }

        private static Boolean _fleeDone;

        private static List<BTL_DATA> UnitsOf(UInt64 peer)
        {
            List<BTL_DATA> units = new List<BTL_DATA>();
            foreach (KeyValuePair<UInt16, UInt64> kv in _remote)
            {
                BTL_DATA btl = kv.Value == peer ? BattleSync.FindBtl(kv.Key) : null;
                if (btl != null)
                    units.Add(btl);
            }
            return units;
        }

        /// <summary>[Online] btl_cmd.SetCommand, owner: the flee roll for another PC succeeded (the escape command is
        /// being set). True = handled: only that PC's units leave.</summary>
        public static Boolean CatchFleeCommand(CMD_DATA cmd, BattleCommandId commandId)
        {
            if (_role != Role.Owner || _fleeFor == 0 || commandId != BattleCommandId.SysEscape)
                return false;
            UInt64 peer = _fleeFor;
            _fleeFor = 0;
            _fleeDone = true;
            List<BTL_DATA> units = UnitsOf(peer);
            Boolean othersStand = false;
            for (BTL_DATA btl = FF9StateSystem.Battle.FF9Battle.btl_list.next; btl != null; btl = btl.next)
                if (btl.bi.player != 0 && !units.Contains(btl) && !btl_stat.CheckStatus(btl, BattleStatusConst.BattleEndFull))
                    othersStand = true;
            if (!othersStand)
            {
                // The last player-controlled units flee: the battle ends as an escape for everyone left
                BattleSync.Note($"Control: {SteamNative.Tag(peer)} flees and nobody else is standing: the whole battle flees");
                Flees++;
                return false; // the escape command goes on as vanilla
            }
            UInt16 mask = 0;
            foreach (BTL_DATA btl in units)
                mask |= btl.btl_id;
            BattleSync.OwnerStream?.WriteUnitsLeave(mask);
            BattleSync.Applying++;
            try
            {
                foreach (BTL_DATA btl in units)
                {
                    _remote.Remove(btl.btl_id);
                    _awaiting.Remove(btl.btl_id);
                    new BattleUnit(btl).Remove(true);
                }
            }
            finally { BattleSync.Applying--; }
            _remoteRunning.Remove(peer);
            Flees++;
            List<String> names = new List<String>();
            foreach (BTL_DATA btl in units)
                names.Add(BattleSync.UnitName(btl.btl_id));
            BattleSync.Note($"Control: {SteamNative.Tag(peer)} fled: {String.Join(", ", names.ToArray())} left the battle");
            _send(peer, new Byte[] { MsgFleeResult, (Byte)_seq, (Byte)(_seq >> 8), 0 });
            OnPeerFled?.Invoke(peer);
            return true;
        }

        /// <summary>Participant: the owner's stream says these units left (another PC fled).</summary>
        public static void ApplyUnitsLeave(UInt16 mask)
        {
            List<String> names = new List<String>();
            BattleSync.Applying++;
            try
            {
                for (BTL_DATA btl = FF9StateSystem.Battle.FF9Battle.btl_list.next; btl != null;)
                {
                    BTL_DATA next = btl.next;
                    if ((btl.btl_id & mask) != 0 && btl.bi.player != 0)
                    {
                        names.Add(BattleSync.UnitName(btl.btl_id));
                        new BattleUnit(btl).Remove(true);
                    }
                    btl = next;
                }
            }
            finally { BattleSync.Applying--; }
            BattleSync.Note($"Control: {(names.Count > 0 ? String.Join(", ", names.ToArray()) : "no unit")} left the battle (a player fled)");
        }

        // ---- Controlling PC -------------------------------------------------------------------------------------

        /// <summary>Stream: the owner says this unit's turn came (or went).</summary>
        public static void OnTurn(UInt16 unit, Boolean ready)
        {
            if (_role != Role.Passive || !_mine.Contains(unit))
                return;
            Int32 index = PlayerIndex(unit);
            if (index < 0)
                return;
            BattleHUD hud = UIManager.Battle;
            if (ready)
            {
                hud.InputFinishList.Remove(index);
                if (!hud.ReadyQueue.Contains(index))
                    hud.AddPlayerToReady(index);
                BTL_DATA btl = BattleSync.FindBtl(unit);
                if (btl != null)
                    btl.sel_mode = 0;
                BattleSync.Note($"Control: {BattleSync.UnitName(unit)}'s turn: menu opens here");
            }
            else
            {
                _pending.Remove(unit);
                _captured.Remove(unit);
                if (hud.CurrentPlayerIndex == index)
                    hud.SwitchAvailablePlayerOrIdle();
                hud.RemovePlayerFromAction(unit, true);
                BattleSync.Note($"Control: {BattleSync.UnitName(unit)}'s turn was taken away (status, KO...)");
            }
        }

        /// <summary>[Online] btl_cmd.SetCommand, passive: this PC's menu chose a command for one of its units. True = it
        /// goes to the owner (nothing runs here: the command comes back in the owner's stream).</summary>
        public static Boolean CaptureCommand(CMD_DATA cmd, BattleCommandId commandId, Int32 subNo, UInt16 targetId, UInt32 cursor, Boolean forcePriority, BattleCommandMenu menu)
        {
            if (!BattleSync.LivePassive || BattleSync.Applying > 0)
                return false;
            BTL_DATA btl = cmd?.regist;
            if (commandId == BattleCommandId.SysEscape || btl == null)
                return true; // a passive never sets its own escape (per-player flee goes to the owner)
            if (_role != Role.Passive || !_mine.Contains(btl.btl_id))
            {
                BattleSync.Note($"Control: a local command {commandId} for {BattleSync.UnitName(btl.btl_id)}, not this PC's unit: dropped");
                return true;
            }
            Int32 slot = btl.cmd.IndexOf(cmd);
            if (!_captured.TryGetValue(btl.btl_id, out List<Call> calls))
                _captured[btl.btl_id] = calls = new List<Call>();
            calls.Add(new Call { Slot = (Byte)Math.Max(0, slot), Cmd = commandId, Sub = subNo, Target = targetId, Cursor = (Byte)cursor, Menu = (Byte)menu, Priority = (Byte)(forcePriority ? 1 : 0) });
            return true;
        }

        /// <summary>Per frame (BattleEntry.Update): send what the menu chose this frame, one request per unit.</summary>
        public static void Flush()
        {
            if (_role != Role.Passive)
                return;
            if (_captured.Count > 0)
            {
                foreach (KeyValuePair<UInt16, List<Call>> kv in _captured)
                {
                    List<Call> calls = kv.Value;
                    UInt16 req = ++_reqSeq;
                    List<Byte> m = new List<Byte> { MsgCommandRequest, (Byte)_seq, (Byte)(_seq >> 8), (Byte)req, (Byte)(req >> 8), (Byte)kv.Key, (Byte)calls.Count };
                    foreach (Call c in calls)
                    {
                        m.Add(c.Slot);
                        m.Add((Byte)(Int32)c.Cmd);
                        m.Add((Byte)((Int32)c.Cmd >> 8));
                        m.Add((Byte)c.Sub);
                        m.Add((Byte)(c.Sub >> 8));
                        m.Add((Byte)(c.Sub >> 16));
                        m.Add((Byte)(c.Sub >> 24));
                        m.Add((Byte)c.Target);
                        m.Add((Byte)(c.Target >> 8));
                        m.Add(c.Cursor);
                        m.Add(c.Menu);
                        m.Add(c.Priority);
                    }
                    Byte[] message = m.ToArray();
                    _send(_owner, message);
                    Requests++;
                    Call last = calls[calls.Count - 1];
                    String what = $"{CommandName(last.Cmd, last.Sub)}";
                    _pending[kv.Key] = new Pending { Req = req, SentAt = Now, What = what };
                    BattleSync.Note($"Control: request #{req} for {BattleSync.UnitName(kv.Key)}: {last.Cmd} {last.Sub} → {last.Target:X2}{(calls.Count > 1 ? $" (+{calls.Count - 1})" : "")}, {message.Length} bytes");
                }
                _captured.Clear();
            }
            Notice = null;
            foreach (KeyValuePair<UInt16, Pending> kv in _pending)
            {
                Notice = $"Queued: {UnitLabel(kv.Key)} → {kv.Value.What}";
                break;
            }
        }

        private static String UnitLabel(UInt16 unit)
        {
            BTL_DATA btl = BattleSync.FindBtl(unit);
            PLAYER p = btl != null ? btl_util.getPlayerPtr(btl) : null;
            return p != null && !String.IsNullOrEmpty(p.Name) ? p.Name : BattleSync.UnitName(unit);
        }

        private static String CommandName(BattleCommandId cmd, Int32 sub)
        {
            try
            {
                CMD_DATA test = new CMD_DATA { cmd_no = cmd, sub_no = sub };
                AA_DATA aa = btl_util.GetCommandAction(test);
                if (aa != null && !String.IsNullOrEmpty(aa.Name))
                    return aa.Name;
            }
            catch (Exception)
            {
                // a name is only for the screen
            }
            return cmd.ToString();
        }

        /// <summary>Passive: a command of the owner's stream is about to run here (the round trip for a request ends).</summary>
        public static void OnInjected(BattleEvent ev)
        {
            if (_role != Role.Passive || ev.Slot != 0 || !_pending.TryGetValue(ev.Caster, out Pending p))
                return;
            _pending.Remove(ev.Caster);
            Double ms = Now - p.SentAt;
            RoundTrips++;
            RoundTripTotal += ms;
            RoundTripMax = Math.Max(RoundTripMax, ms);
            Accepted++;
            BattleSync.Note($"Control: request #{p.Req} ({BattleSync.UnitName(ev.Caster)}) runs: {ms:0} ms from the menu to the owner's command");
        }

        private static void OnRejected(Byte[] d)
        {
            if (d.Length < 7)
                return;
            UInt16 req = (UInt16)(d[3] | (d[4] << 8));
            UInt16 unit = d[5];
            Byte reason = d[6];
            Rejected++;
            if (_pending.TryGetValue(unit, out Pending p) && p.Req == req)
                _pending.Remove(unit);
            Int32 index = PlayerIndex(unit);
            BattleHUD hud = UIManager.Battle;
            BTL_DATA btl = BattleSync.FindBtl(unit);
            String why = reason == RejectNoItem ? "the item is gone" : reason == RejectNoMp ? "not enough MP" : reason == RejectNotReady ? "the turn is over" : "the command can't be used";
            BattleSync.Note($"Control: request #{req} for {BattleSync.UnitName(unit)} rejected ({why})");
            if (index < 0)
                return;
            if (reason == RejectNotReady)
            {
                hud.RemovePlayerFromAction(unit, true);
                return;
            }
            // The menu opens again for this unit
            if (btl != null)
                btl.sel_mode = 0;
            hud.InputFinishList.Remove(index);
            if (!hud.ReadyQueue.Contains(index))
                hud.AddPlayerToReady(index);
            Memoria.Online.Party.PartySync.Toast($"{UnitLabel(unit)}: {why}; choose again.");
        }

        /// <summary>[Online] BattleHUD.Update on a passive battle: this PC's run buttons, for its own units only. Held for
        /// a second → the owner tries the flee roll for this PC (vanilla tries it every second while held).</summary>
        public static void LocalRun(Boolean held, Single deltaTime)
        {
            if (_role != Role.Passive || _mine.Count == 0)
                return;
            _runHeld = held;
            if (held != _runHeldSent)
            {
                _runHeldSent = held;
                _send(_owner, new Byte[] { MsgRunHeld, (Byte)_seq, (Byte)(_seq >> 8), (Byte)(held ? 1 : 0) });
            }
            if (!held)
            {
                _runTimer = 0f;
                return;
            }
            _runTimer += deltaTime;
            if (_runTimer > 1f)
            {
                _runTimer = 0f;
                _send(_owner, new Byte[] { MsgFleeTry, (Byte)_seq, (Byte)(_seq >> 8) });
            }
        }

        public static String StatusLine => _role == Role.None ? null
            : _role == Role.Owner ? $"control: remote units {_remote.Count}, waiting {_awaiting.Count}; TurnReady {TurnsSent}, requests {Requests} (ok {Accepted}, rejected {Rejected}), flees {Flees}"
            : $"control: my units {_mine.Count}, queued {_pending.Count}; requests {Requests}, round trips {RoundTrips} avg {(RoundTrips > 0 ? RoundTripTotal / RoundTrips : 0):0} ms max {RoundTripMax:0} ms, rejected {Rejected}";
    }
}
