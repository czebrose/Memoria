using FF9;
using Memoria.Data;
using System;

namespace Memoria.Online.Battle
{
    /// <summary>
    /// Spike C: putting commands into a battle without the menu.
    /// C2 (debug hotkey): a ready player character uses Attack on the first enemy, through the normal queue (exactly what
    /// the menu's SendCommand does). Passive replay: a recorded command goes straight into cur_cmd_list, bypassing the
    /// queue and its ATB / priority gates, because the owner already decided when it runs.
    /// </summary>
    public static class CommandInjector
    {
        /// <summary>C2 prototype. Returns a description of what happened.</summary>
        public static String InjectAttackForReadyPlayer()
        {
            FF9StateBattleSystem btlsys = FF9StateSystem.Battle.FF9Battle;
            if (btlsys.btl_phase != FF9StateBattleSystem.PHASE_NORMAL)
                return "not in the normal battle phase";
            BattleHUD hud = UIManager.Battle;
            Int32 playerIndex = -1;
            foreach (Int32 index in hud.ReadyQueue)
            {
                if (hud.InputFinishList.Contains(index))
                    continue;
                BTL_DATA candidate = btlsys.btl_data[index];
                if (!btl_cmd.CheckUsingCommand(candidate.cmd[0]))
                {
                    playerIndex = index;
                    break;
                }
            }
            if (playerIndex < 0)
                return "no ready player character without a command";
            BTL_DATA btl = btlsys.btl_data[playerIndex];
            BTL_DATA enemy = null;
            for (BTL_DATA next = btlsys.btl_list.next; next != null; next = next.next)
                if (next.bi.player == 0 && next.bi.target != 0 && !btl_stat.CheckStatus(next, BattleStatus.Death))
                {
                    enemy = next;
                    break;
                }
            if (enemy == null)
                return "no enemy to target";
            if (hud.CurrentPlayerIndex == playerIndex)
                hud.SetIdle(); // close the menu that was open for this character
            btl.sel_mode = 1;
            btl_cmd.SetCommand(btl.cmd[0], BattleCommandId.Attack, (Int32)BattleAbilityId.Attack, enemy.btl_id, 0u);
            hud.InputFinishList.Add(playerIndex);
            return $"P{playerIndex} → Attack {BattleSpike.UnitName(enemy.btl_id)} (queued: {btl_cmd.CheckCommandQueued(btl.cmd[0])})";
        }

        /// <summary>Passive replay: run a recorded command now. Returns the command, or null with <paramref name="error"/>.</summary>
        public static CMD_DATA InjectRecorded(BattleEvent ev, out String error)
        {
            error = null;
            FF9StateBattleSystem btlsys = FF9StateSystem.Battle.FF9Battle;
            CMD_DATA cmd;
            BTL_DATA caster;
            if (ev.Slot < 0)
            {
                cmd = btlsys.cmd_escape;
                caster = cmd.regist;
            }
            else
            {
                caster = BattleSpike.FindBtl(ev.Caster);
                if (caster == null)
                {
                    error = $"caster {BattleSpike.UnitName(ev.Caster)} not in the battle";
                    return null;
                }
                while (caster.cmd.Count <= ev.Slot)
                {
                    CMD_DATA extra = new CMD_DATA { regist = caster };
                    btl_cmd.ClearCommand(extra);
                    btl_cmd.ClearReflecData(extra);
                    caster.cmd.Add(extra);
                }
                cmd = caster.cmd[ev.Slot];
            }
            if (btlsys.cur_cmd_list.Contains(cmd))
            {
                error = $"slot {ev.Slot} of {BattleSpike.UnitName(ev.Caster)} is still running {cmd.cmd_no}";
                return null;
            }
            if (btl_cmd.CheckCommandQueued(cmd))
                btl_cmd.KillCommand(cmd); // something local queued it (shouldn't happen in passive mode)

            // What SetCommand / SetEnemyCommand do, with the owner's values
            btl_cmd.ClearCommand(cmd);
            btl_cmd.ClearReflecData(cmd);
            cmd.cmd_no = ev.CmdNo;
            cmd.sub_no = ev.SubNo;
            cmd.info.cmdMenu = ev.Menu;
            cmd.SetAAData(btl_util.GetCommandAction(cmd));
            cmd.ScriptId = ev.ScriptId;
            cmd.HitRate = ev.HitRate;
            cmd.Power = ev.Power;
            cmd.AbilityCategory = ev.Category;
            cmd.AbilityType = ev.AbilType;
            cmd.AbilityStatus = (BattleStatus)ev.AbilityStatus;
            cmd.IsShortRange = ev.ShortRange;
            cmd.tar_id = ev.TarId;
            cmd.magic_caster_id = ev.MagicCaster;
            cmd.info.cursor = ev.Cursor;
            cmd.info.priority = ev.Priority;
            cmd.info.mon_reflec = ev.MonReflec;
            if (ev.CustomMp >= 0)
                cmd.info.CustomMPCost = ev.CustomMp;
            if (ev.CmdNo == BattleCommandId.SysEscape)
                btlsys.cmd_status |= 1;
            if (caster != null && ev.Slot >= 0)
            {
                if ((ev.CmdNo < BattleCommandId.EnemyReaction || ev.CmdNo > BattleCommandId.BoundaryUpperCheck) && cmd != caster.cmd[3])
                {
                    caster.bi.cmd_idle = 1;
                    if (Configuration.Battle.Speed < 3 && caster.bi.player != 0)
                        btl_mot.SetDefaultIdle(caster);
                }
                if (ev.CmdNo == BattleCommandId.SummonGarnet || ev.CmdNo == BattleCommandId.Phantom || ev.CmdNo == BattleCommandId.SummonEiko)
                    caster.summon_count++;
            }
            btlsys.cur_cmd_list.Add(cmd);
            return cmd;
        }
    }
}
