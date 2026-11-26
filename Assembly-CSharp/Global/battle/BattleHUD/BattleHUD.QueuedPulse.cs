using Memoria;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A slow, dim pulse of the ATB bar for "waiting for the turn", so it looks different from "choose a command" (the
/// game's fast blink) and from "acting" (solid). The game shows a full, solid bar for both waiting and acting, until the
/// action ends and the bar empties. With the pulse:
///   a character this PC commands:  filling → blinking (choose) → pulse (chosen, waiting) → solid (acting) → empty
///   another player's character:    filling → pulse (full: being chosen, or waiting) → solid (acting) → empty
///
/// "Waiting" for this PC's character: its command is in this PC's battle queue, not running yet; or, in a shared
/// battle another PC runs, the command this PC chose hasn't run here yet (<see cref="Memoria.Online.Battle.BattleControl.HasPending"/>).
/// "Acting": its command (its main command slot) is running in this PC's battle (<c>cur_cmd_list</c>; a shared battle's
/// other PCs run each command when the owner's stream reaches it, so the animation and the solid bar go together).
/// Outside an online session every character is this PC's.
/// </summary>
public partial class BattleHUD
{
    private const Single QueuedPulsePeriod = 1.6f; // seconds per pulse (the ready blink is about 0.7 s)
    private const Single QueuedPulseLow = 0.4f;    // the dimmest the bar gets

    private Single _queuedPulseTime;
    private readonly HashSet<UI.PanelParty.Character> _queuedPulsing = new HashSet<UI.PanelParty.Character>();

    /// <summary>Every frame, before the ready blink (UpdatePlayer).</summary>
    private void UpdateQueuedPulse()
    {
        _queuedPulseTime = (_queuedPulseTime + RealTime.deltaTime) % QueuedPulsePeriod;
        Single wave = 0.5f + 0.5f * Mathf.Cos(_queuedPulseTime / QueuedPulsePeriod * 2f * Mathf.PI);
        Single alpha = QueuedPulseLow + (1f - QueuedPulseLow) * wave;
        HashSet<BTL_DATA> queued = new HashSet<BTL_DATA>(), acting = new HashSet<BTL_DATA>();
        ReadQueue(queued, acting);
        foreach (UI.PanelParty.Character character in _partyDetail.Characters.Entries)
        {
            BattleUnit unit = character.PlayerId >= 0 ? FF9StateSystem.Battle.FF9Battle.GetUnit(character.PlayerId) : null;
            Boolean pulse = false;
            if (unit != null && !character.ATBBlink && unit.CurrentHp > 0 && !acting.Contains(unit.Data))
            {
                if (Memoria.Online.Battle.BattleControl.IsCommandedHere(unit.Data.btl_id))
                    pulse = queued.Contains(unit.Data) || Memoria.Online.Battle.BattleControl.HasPending(unit.Data.btl_id);
                else
                    pulse = unit.MaximumAtb > 0 && unit.CurrentAtb >= unit.MaximumAtb; // another player's: full until it acts
            }
            if (pulse)
            {
                character.ATBBar.Foreground.Widget.alpha = alpha;
                _queuedPulsing.Add(character);
            }
            else if (_queuedPulsing.Remove(character) && !character.ATBBlink)
            {
                character.ATBBar.Foreground.Widget.alpha = 1f; // solid again: acting, or nothing to wait for
            }
        }
    }

    /// <summary>The party's chosen commands (their main command slot) in this PC's battle: waiting in the queue, or running.</summary>
    private static void ReadQueue(HashSet<BTL_DATA> queued, HashSet<BTL_DATA> acting)
    {
        FF9StateBattleSystem battle = FF9StateSystem.Battle?.FF9Battle;
        if (battle == null)
            return;
        if (battle.cur_cmd_list != null)
            foreach (CMD_DATA cmd in battle.cur_cmd_list)
                if (IsChosenCommand(cmd))
                    acting.Add(cmd.regist);
        for (CMD_DATA cmd = battle.cmd_queue?.next; cmd != null; cmd = cmd.next)
            if (IsChosenCommand(cmd) && !acting.Contains(cmd.regist))
                queued.Add(cmd.regist);
    }

    /// <summary>A party member's own command (not an enemy's, nor a counter or auto-potion).</summary>
    private static Boolean IsChosenCommand(CMD_DATA cmd)
    {
        BTL_DATA btl = cmd?.regist;
        return btl != null && btl.bi.player != 0 && btl.cmd.Count > 0 && cmd == btl.cmd[0];
    }
}
