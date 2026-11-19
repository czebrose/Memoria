using Assets.Sources.Scripts.UI.Common;
using FF9;
using Memoria.Data;
using System;
using UnityEngine;

namespace Memoria.Online.Battle
{
    /// <summary>
    /// Spike C: what the passive client does instead of the parts of the battle it must not run itself.
    /// - ATB: not filled (HonoluluBattleMain skips YMenu_ManagerActiveTime), set from the owner's Atb events. With no ATB,
    ///   no player ever becomes "ready" (no menu) and no enemy AI turn is requested.
    /// - The local command queue is never read (RunCommandFromQueue); commands come from the owner.
    /// - Calculations are replaced by the owner's ops (SBattleCalculator.CalcMain).
    /// - Status ticks (btl_para.CheckPointData + btl_stat.CheckStatusLoop) don't run; the owner's Tick events do the
    ///   same work. <see cref="VisualTick"/> keeps what those two methods do for the visuals.
    /// </summary>
    public static class PassiveBattle
    {
        public static Boolean Active => BattleSync.Passive;

        public static void VisualTick(BattleUnit unit)
        {
            BTL_DATA btl = unit.Data;
            btl_stat.SetStatusVfx(unit);
            if (unit.IsUnderStatus(BattleStatus.Death))
            {
                btl_mot.DieSequence(btl);
                return;
            }
            if (!unit.IsUnderAnyStatus(BattleStatusConst.StopAtb))
                btl.bi.atb = 1;
            if (unit.IsNonMorphedPlayer)
                btl.bi.def_idle = (Byte)(btl_stat.CheckStatus(unit, BattleStatusConst.IdleDying) ? 1 : 0);
            if (unit.IsPlayer)
            {
                unit.UIColorHP = unit.CurrentHp * 6 <= unit.MaximumHp ? FF9TextTool.Yellow : FF9TextTool.White;
                unit.UIColorMP = unit.CurrentMp <= unit.MaximumMp / 6f ? FF9TextTool.Yellow : FF9TextTool.White;
            }
        }

        /// <summary>The dodge block of SBattleCalculator.CalcResult (a visual reaction plus the flags FinishCommand reads).</summary>
        public static void ApplyDodge(BTL_DATA target, CMD_DATA cmd)
        {
            if (target.bi.player != 0)
            {
                Int32 direction = btl_mot.GetDirection(target);
                target.rot.eulerAngles = new Vector3(target.rot.eulerAngles.x, direction, target.rot.eulerAngles.z);
            }
            else if (target.bi.slave == 0)
            {
                if (!btl_util.IsBtlBusy(target, btl_util.BusyMode.CASTER))
                    target.pos[2] -= -400f;
            }
            else
            {
                btl_util.GetMasterEnemyBtlPtr(target).Data.pos[2] -= -400f;
            }
            target.bi.dodge = 1;
            btl_mot.SetDefaultIdle(target);
            if (cmd != null)
                cmd.info.dodge = 1;
        }
    }
}
