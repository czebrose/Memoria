using FF9;
using Memoria.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace Memoria.Online.Battle
{
    /// <summary>
    /// The battle's action queue, for the Actions window (Menu/BattleQueueHud.cs): the party's commands that are running
    /// or waiting their turn, in order, so a player sees the command they chose go in.
    ///
    /// The PC that runs the battle reads its own queue (<see cref="btl_cmd"/>: <c>cur_cmd_list</c>, then
    /// <c>cmd_queue</c>). A passive battle (a shared battle's participant) only runs each command when the owner's stream
    /// reaches it, so the owner sends its queue whenever it changes:
    ///   Queue [38, seq u16, count u8, (caster u16, cmd u16, sub i32, flags u8)*]   flags bit 0: running
    /// A command this PC sent and the owner hasn't queued yet shows as "sending" (<see cref="BattleControl.PendingCommands"/>).
    /// Only the party's commands are listed (no enemy's, no system command such as an escape or a trance).
    /// </summary>
    public static class BattleQueue
    {
        public const Byte MsgQueue = 38;
        private const Int32 MaxEntries = 12;
        private const Double MinSendMs = 100, ResendMs = 2000;

        public struct Entry
        {
            public UInt16 Caster;
            public BattleCommandId Cmd;
            public Int32 Sub;
            public Boolean Running;
        }

        private static readonly List<Entry> _fromOwner = new List<Entry>();
        private static UInt16 _ownerSeq;
        private static String _sentKey;
        private static Double _lastSend;

        public static void Reset()
        {
            _fromOwner.Clear();
            _sentKey = null;
            _lastSend = 0;
        }

        /// <summary>The queue to show: the owner's on a passive battle, else this PC's own.</summary>
        public static List<Entry> Current()
        {
            if (BattleControl.IsControllingPassive)
                return new List<Entry>(_fromOwner);
            return Local();
        }

        /// <summary>This PC's battle engine's queue: running commands first, then the waiting ones in order.</summary>
        public static List<Entry> Local()
        {
            List<Entry> result = new List<Entry>();
            FF9StateBattleSystem battle = FF9StateSystem.Battle?.FF9Battle;
            if (battle == null)
                return result;
            HashSet<CMD_DATA> seen = new HashSet<CMD_DATA>();
            if (battle.cur_cmd_list != null)
                foreach (CMD_DATA cmd in battle.cur_cmd_list)
                    Add(result, seen, cmd, true);
            for (CMD_DATA cmd = battle.cmd_queue?.next; cmd != null && result.Count < MaxEntries; cmd = cmd.next)
                Add(result, seen, cmd, false);
            return result;
        }

        private static void Add(List<Entry> list, HashSet<CMD_DATA> seen, CMD_DATA cmd, Boolean running)
        {
            if (cmd?.regist == null || !seen.Add(cmd) || cmd.regist.bi.player == 0 || !Shown(cmd.cmd_no))
                return;
            list.Add(new Entry { Caster = cmd.regist.btl_id, Cmd = cmd.cmd_no, Sub = cmd.sub_no, Running = running });
        }

        /// <summary>A command a player chose, or the party's automatic ones (counters, auto-potions, Charge!).</summary>
        private static Boolean Shown(BattleCommandId cmd)
        {
            if (cmd == BattleCommandId.None || cmd == BattleCommandId.Escape || cmd == BattleCommandId.FinishBlow || cmd == BattleCommandId.EnemyAtk || cmd == BattleCommandId.BoundaryCheck)
                return false;
            if (cmd >= BattleCommandId.EnemyCounter && cmd <= BattleCommandId.BoundaryUpperCheck)
                return false; // enemy reactions and the system commands (escape, trance, petrify...)
            return true;
        }

        // ---- Owner ----------------------------------------------------------------------------------------------

        /// <summary>Owner, every frame of a shared battle (BattleEntry): sends the queue when it changed.</summary>
        public static void OwnerSend(UInt16 seq, IEnumerable<UInt64> participants, Action<UInt64, Byte[]> send, Double nowMs)
        {
            List<Entry> queue = Local();
            String key = Key(queue);
            if (key == _sentKey && nowMs - _lastSend < ResendMs || nowMs - _lastSend < MinSendMs)
                return;
            _sentKey = key;
            _lastSend = nowMs;
            List<Byte> m = new List<Byte> { MsgQueue, (Byte)seq, (Byte)(seq >> 8), (Byte)queue.Count };
            foreach (Entry e in queue)
            {
                m.Add((Byte)e.Caster);
                m.Add((Byte)(e.Caster >> 8));
                m.Add((Byte)(Int32)e.Cmd);
                m.Add((Byte)((Int32)e.Cmd >> 8));
                m.Add((Byte)e.Sub);
                m.Add((Byte)(e.Sub >> 8));
                m.Add((Byte)(e.Sub >> 16));
                m.Add((Byte)(e.Sub >> 24));
                m.Add((Byte)(e.Running ? 1 : 0));
            }
            Byte[] bytes = m.ToArray();
            foreach (UInt64 peer in participants)
                send(peer, bytes);
        }

        private static String Key(List<Entry> queue)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Entry e in queue)
                sb.Append(e.Caster).Append(':').Append((Int32)e.Cmd).Append(':').Append(e.Sub).Append(e.Running ? "!" : "").Append('|');
            return sb.ToString();
        }

        // ---- Passive --------------------------------------------------------------------------------------------

        /// <summary>Passive: the owner's queue (BattleControl.OnMessage, from this battle's owner).</summary>
        public static void OnMessage(Byte[] d)
        {
            if (d.Length < 4)
                return;
            Int32 count = d[3];
            if (d.Length < 4 + count * 9)
                return;
            _ownerSeq = BattleEntryMessages.Seq(d);
            _fromOwner.Clear();
            for (Int32 i = 0, o = 4; i < count; i++, o += 9)
            {
                _fromOwner.Add(new Entry
                {
                    Caster = (UInt16)(d[o] | (d[o + 1] << 8)),
                    Cmd = (BattleCommandId)(d[o + 2] | (d[o + 3] << 8)),
                    Sub = d[o + 4] | (d[o + 5] << 8) | (d[o + 6] << 16) | (d[o + 7] << 24),
                    Running = (d[o + 8] & 1) != 0,
                });
            }
        }

        // ---- Text -----------------------------------------------------------------------------------------------

        public static String CasterName(UInt16 unit)
        {
            BTL_DATA btl = BattleSync.FindBtl(unit);
            PLAYER p = btl != null ? btl_util.getPlayerPtr(btl) : null;
            return p != null && !String.IsNullOrEmpty(p.Name) ? p.Name : BattleSync.UnitName(unit);
        }

        public static String ActionName(BattleCommandId cmd, Int32 sub) => BattleControl.CommandName(cmd, sub);
    }
}
