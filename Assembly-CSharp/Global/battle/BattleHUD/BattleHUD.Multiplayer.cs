using Assets.Sources.Scripts.UI.Common;
using FF9;
using Memoria;
using Memoria.Assets;
using Memoria.Data;
using Memoria.Database;
using Memoria.Multiplayer;
using Memoria.Prime;
using Memoria.Scenes;
using NCalc;
using System;
using System.Collections.Generic;
using UnityEngine;

// [Multiplayer] Fork addition (Phase 4, "compact second menu"): while the main command menu is busy with one player's
// character, any other player whose character is ready gets a small menu of their own, driven only by their controller.
// It reuses the main menu's rules (command lists and enabled states, ability states incl. monster transforms, MP costs,
// item filtering, target types) and sends the command through the main menu's own ProcessCommand, run with that
// character's context swapped in. Commands it doesn't handle (double cast, Mix, the main menu access) say so and are left
// to the main menu. The main menu skips characters held by a compact menu (IsInMultiplayerMenu).
public partial class BattleHUD
{
    private enum MultiplayerMenuStage { Command, Sub, Target }

    private sealed class MultiplayerMenuRow
    {
        public String Text;
        public String Right;
        public Boolean Enabled;
        public Int32 Value;                 // command menu / list index / battle index (or a MultiplayerGroup* code)
        public BattleCommandId CommandId;   // command rows only
    }

    private sealed class MultiplayerMenu
    {
        public Int32 Player;
        public Int32 BtlIndex;
        public MultiplayerMenuStage Stage;
        public readonly List<MultiplayerMenuRow> Rows = new List<MultiplayerMenuRow>();
        public Int32 Cursor;
        public BattleCommandMenu CommandMenu;
        public BattleCommandId CommandId;
        public Int32 SubIndex = -1;
        public Boolean CameFromSub;
        public readonly List<RegularItem> Items = new List<RegularItem>();
        public TargetType TargetType;
        public Boolean TargetDead;
        public Boolean DefaultAlly;
        public Boolean AllMode;
        public Int32 CommandCursor;
        public Int32 SubCursor;
        public String Message;
        public Single MessageUntil;
        public Int32 NavDirection;
        public Single NavNextTime;
        public Int32 First;                 // list stages: first row shown (moves by whole visual rows)
        public Boolean PanelLogPending;     // diagnostics: dump the panel's rows once after a target list is built
    }

    // ---- Slots: the 6 rows of the command window, laid out like the main menu -----------------------------------
    // Command stage: each command sits in its own row of the window (Attack in Attack's row, ...). List stages: rows
    // filled in reading order (top to bottom, left to right), scrolling by one visual row. The cursor moves by position
    // (to the nearest slot in the pressed direction), as in the real window. Positions come from the copied window;
    // without one (overlay fallback) the slots are a single column.

    private Vector2[] _multiplayerSlotPos;
    private Int32[] _multiplayerReadingOrder;
    private Int32 _multiplayerColumns = 1;

    private void InitMultiplayerSlotGeometry(MultiplayerPanel panel)
    {
        Vector2[] pos = new Vector2[MultiplayerVisibleRows];
        for (Int32 i = 0; i < MultiplayerVisibleRows; i++)
            pos[i] = panel.Rows[i].transform.localPosition;
        Int32[] order = new Int32[MultiplayerVisibleRows];
        for (Int32 i = 0; i < order.Length; i++)
            order[i] = i;
        const Single tolerance = 4f;
        Array.Sort(order, (a, b) =>
        {
            if (Math.Abs(pos[a].y - pos[b].y) > tolerance)
                return pos[b].y.CompareTo(pos[a].y); // higher first
            return pos[a].x.CompareTo(pos[b].x);
        });
        List<Single> columns = new List<Single>();
        foreach (Vector2 p in pos)
            if (!columns.Exists(x => Math.Abs(x - p.x) <= tolerance))
                columns.Add(p.x);
        _multiplayerSlotPos = pos;
        _multiplayerReadingOrder = order;
        _multiplayerColumns = Math.Max(1, columns.Count);
        Log.Message($"[Multiplayer] Compact menu layout: {_multiplayerColumns} column(s), reading order {String.Join(",", Array.ConvertAll(order, i => i.ToString()))}");
    }

    private Vector2 GetMultiplayerSlotPos(Int32 slot) => _multiplayerSlotPos != null ? _multiplayerSlotPos[slot] : new Vector2(0f, -slot);
    private Int32 GetMultiplayerReadingSlot(Int32 index) => _multiplayerReadingOrder != null ? _multiplayerReadingOrder[index] : index;
    private Int32 MultiplayerColumns => _multiplayerReadingOrder != null ? _multiplayerColumns : 1;

    /// <summary>The window row (0-5) of the main command panel that shows a command.</summary>
    private static Int32 GetCommandSlot(BattleCommandMenu commandMenu)
    {
        switch (commandMenu)
        {
            case BattleCommandMenu.Attack: return 0;
            case BattleCommandMenu.Defend: return 1;
            case BattleCommandMenu.Ability1: return 2;
            case BattleCommandMenu.Ability2: return 3;
            case BattleCommandMenu.Item: return 4;
            case BattleCommandMenu.Change: return 5;
            default: return -1;
        }
    }

    /// <summary>The slot (window row) showing a menu row, or -1 if it's not shown.</summary>
    private Int32 GetMultiplayerRowSlot(MultiplayerMenu menu, Int32 rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= menu.Rows.Count)
            return -1;
        if (menu.Stage == MultiplayerMenuStage.Command)
            return GetCommandSlot((BattleCommandMenu)menu.Rows[rowIndex].Value);
        Int32 readingIndex = rowIndex - menu.First;
        return readingIndex >= 0 && readingIndex < MultiplayerVisibleRows ? GetMultiplayerReadingSlot(readingIndex) : -1;
    }

    /// <summary>The menu row shown in a slot (window row), or -1 if empty.</summary>
    private Int32 GetMultiplayerSlotRow(MultiplayerMenu menu, Int32 slot)
    {
        if (menu.Stage == MultiplayerMenuStage.Command)
        {
            for (Int32 i = 0; i < menu.Rows.Count; i++)
                if (GetCommandSlot((BattleCommandMenu)menu.Rows[i].Value) == slot)
                    return i;
            return -1;
        }
        for (Int32 r = 0; r < MultiplayerVisibleRows; r++)
            if (GetMultiplayerReadingSlot(r) == slot)
                return menu.First + r < menu.Rows.Count ? menu.First + r : -1;
        return -1;
    }

    private void EnsureMultiplayerCursorVisible(MultiplayerMenu menu)
    {
        if (menu.Stage == MultiplayerMenuStage.Command)
            return;
        Int32 columns = MultiplayerColumns;
        while (menu.Cursor < menu.First)
            menu.First -= columns;
        while (menu.Cursor >= menu.First + MultiplayerVisibleRows)
            menu.First += columns;
        menu.First = Math.Max(0, menu.First);
    }

    /// <summary>Move the cursor to the nearest shown row in a direction (dx, dy: -1/0/1, dy = +1 is up); lists scroll past the edge.</summary>
    private void NavigateMultiplayerMenu(MultiplayerMenu menu, Int32 dx, Int32 dy)
    {
        if (menu.Rows.Count == 0)
            return;
        Int32 current = GetMultiplayerRowSlot(menu, menu.Cursor);
        if (current < 0)
        {
            menu.Cursor = 0;
            EnsureMultiplayerCursorVisible(menu);
            return;
        }
        Vector2 from = GetMultiplayerSlotPos(current);
        Vector2 direction = new Vector2(dx, dy).normalized;
        Int32 bestRow = -1;
        Single bestScore = Single.MaxValue;
        for (Int32 slot = 0; slot < MultiplayerVisibleRows; slot++)
        {
            if (slot == current)
                continue;
            Int32 row = GetMultiplayerSlotRow(menu, slot);
            if (row < 0)
                continue;
            Vector2 delta = GetMultiplayerSlotPos(slot) - from;
            Single along = Vector2.Dot(delta, direction);
            if (along <= 0.5f)
                continue;
            Single across = Math.Abs(delta.x * direction.y - delta.y * direction.x);
            Single score = along + across * 2f;
            if (score < bestScore)
            {
                bestScore = score;
                bestRow = row;
            }
        }
        if (bestRow >= 0)
        {
            menu.Cursor = bestRow;
        }
        else if (menu.Stage != MultiplayerMenuStage.Command && dy != 0)
        {
            // Past the top or bottom of a list: scroll by one visual row
            Int32 columns = MultiplayerColumns;
            Int32 target = menu.Cursor + (dy < 0 ? columns : -columns);
            if (target >= menu.Rows.Count && dy < 0 && menu.First + MultiplayerVisibleRows < menu.Rows.Count)
                target = menu.Rows.Count - 1;
            if (target < 0 || target >= menu.Rows.Count)
                return;
            menu.Cursor = target;
            EnsureMultiplayerCursorVisible(menu);
        }
        else
        {
            return;
        }
        FF9Sfx.FF9SFX_Play(103);
    }

    private const Int32 MultiplayerGroupAllEnemies = -2;
    private const Int32 MultiplayerGroupAllAllies = -3;
    private const Int32 MultiplayerGroupEveryone = -4;

    private readonly Dictionary<Int32, MultiplayerMenu> _multiplayerMenus = new Dictionary<Int32, MultiplayerMenu>();
    private MultiplayerBattleMenuView _multiplayerMenuView;

    /// <summary>The main menu must not open (or cycle to) a character a compact menu is handling.</summary>
    public Boolean IsInMultiplayerMenu(Int32 btlIndex)
    {
        foreach (MultiplayerMenu menu in _multiplayerMenus.Values)
            if (menu.BtlIndex == btlIndex)
                return true;
        return false;
    }

    /// <summary>Called every frame from MultiplayerManager.Update during battles.</summary>
    public void UpdateMultiplayerMenus()
    {
        if (!MultiplayerManager.IsActive || !Configuration.Multiplayer.BattleMenus)
        {
            CloseAllMultiplayerMenus();
            SyncMultiplayerPanels(false);
            return;
        }
        if (_multiplayerMenuView == null)
        {
            GameObject go = new GameObject("MultiplayerBattleMenus");
            _multiplayerMenuView = go.AddComponent<MultiplayerBattleMenuView>();
        }

        // Drop menus whose character is no longer waiting for this player
        foreach (MultiplayerMenu menu in new List<MultiplayerMenu>(_multiplayerMenus.Values))
            if (!IsMultiplayerMenuValid(menu))
                CloseMultiplayerMenu(menu, null);

        Boolean usable = _commandEnable && !_usingMainMenu && !_hidingHud && PersistenSingleton<UIManager>.Instance.State == UIManager.UIState.BattleHUD;
        if (!usable)
        {
            SyncMultiplayerPanels(false);
            return;
        }

        // Open: only while the main menu is busy with someone else (when it's free, it takes the next character itself)
        if (CurrentPlayerIndex >= 0)
        {
            for (Int32 player = 1; player <= MultiplayerManager.MaxPlayers; player++)
            {
                if (player == MultiplayerManager.ActivePlayer || _multiplayerMenus.ContainsKey(player))
                    continue;
                if (!XInputPads.Get(MultiplayerManager.GetPadSlot(player)).Connected)
                    continue;
                foreach (Int32 btlIndex in ReadyQueue)
                {
                    if (MultiplayerBattle.OwnerOf(btlIndex) != player || !IsMultiplayerMenuCandidate(btlIndex))
                        continue;
                    OpenMultiplayerMenu(player, btlIndex);
                    break;
                }
            }
        }

        foreach (MultiplayerMenu menu in new List<MultiplayerMenu>(_multiplayerMenus.Values))
            HandleMultiplayerMenuInput(menu);
        SyncMultiplayerPanels(true);
        LogWaitingCharacters();
    }

    private readonly Dictionary<Int32, Single> _multiplayerWaitingSince = new Dictionary<Int32, Single>();
    private readonly HashSet<Int32> _multiplayerWaitingLogged = new HashSet<Int32>();

    /// <summary>Diagnostics: a ready character that gets no menu (main or compact) for a while is logged once, with why.</summary>
    private void LogWaitingCharacters()
    {
        Single now = Time.realtimeSinceStartup;
        foreach (Int32 btlIndex in new List<Int32>(_multiplayerWaitingSince.Keys))
            if (!ReadyQueue.Contains(btlIndex) || InputFinishList.Contains(btlIndex) || btlIndex == CurrentPlayerIndex || IsInMultiplayerMenu(btlIndex))
            {
                _multiplayerWaitingSince.Remove(btlIndex);
                _multiplayerWaitingLogged.Remove(btlIndex);
            }
        foreach (Int32 btlIndex in ReadyQueue)
        {
            if (InputFinishList.Contains(btlIndex) || btlIndex == CurrentPlayerIndex || IsInMultiplayerMenu(btlIndex) || _multiplayerWaitingLogged.Contains(btlIndex))
                continue;
            if (!_multiplayerWaitingSince.TryGetValue(btlIndex, out Single since))
            {
                _multiplayerWaitingSince[btlIndex] = now;
                continue;
            }
            if (now - since < 3f)
                continue;
            _multiplayerWaitingLogged.Add(btlIndex);
            Int32 owner = MultiplayerBattle.OwnerOf(btlIndex);
            String reason = _unconsciousStateList.Contains(btlIndex) ? "cannot act (status)"
                : CurrentPlayerIndex < 0 ? "the main menu is free but did not take it"
                : owner == MultiplayerManager.ActivePlayer ? "its owner is using the main menu (one menu per player)"
                : _multiplayerMenus.ContainsKey(owner) ? "its owner's compact menu is busy with another character"
                : !XInputPads.Get(MultiplayerManager.GetPadSlot(owner)).Connected ? $"its owner's controller (slot {MultiplayerManager.GetPadSlot(owner)}) is not connected"
                : "unknown";
            Log.Message($"[Multiplayer] {FF9StateSystem.Battle.FF9Battle.GetUnit(btlIndex).Name} (P{owner}) has been ready for 3 s without a menu: {reason}");
        }
    }

    private Boolean IsMultiplayerMenuCandidate(Int32 btlIndex)
    {
        return btlIndex != CurrentPlayerIndex && !InputFinishList.Contains(btlIndex) && !_unconsciousStateList.Contains(btlIndex) && !IsInMultiplayerMenu(btlIndex);
    }

    private Boolean IsMultiplayerMenuValid(MultiplayerMenu menu)
    {
        return menu.Player != MultiplayerManager.ActivePlayer
            && ReadyQueue.Contains(menu.BtlIndex)
            && !InputFinishList.Contains(menu.BtlIndex)
            && !_unconsciousStateList.Contains(menu.BtlIndex)
            && menu.BtlIndex != CurrentPlayerIndex
            && MultiplayerBattle.OwnerOf(menu.BtlIndex) == menu.Player;
    }

    private void OpenMultiplayerMenu(Int32 player, Int32 btlIndex)
    {
        MultiplayerMenu menu = new MultiplayerMenu { Player = player, BtlIndex = btlIndex };
        _multiplayerMenus[player] = menu;
        BuildMultiplayerCommandRows(menu);
        FF9Sfx.FF9SFX_Play(1044);
        Log.Message($"[Multiplayer] P{player} opens a compact command menu for {FF9StateSystem.Battle.FF9Battle.GetUnit(btlIndex).Name}");
    }

    private void CloseMultiplayerMenu(MultiplayerMenu menu, String reason)
    {
        _multiplayerMenus.Remove(menu.Player);
        if (reason != null)
            Log.Message($"[Multiplayer] P{menu.Player}'s compact menu closed ({reason})");
    }

    private void CloseAllMultiplayerMenus()
    {
        _multiplayerMenus.Clear();
    }

    // ---- Rows ----------------------------------------------------------------------------------------------------

    private void BuildMultiplayerCommandRows(MultiplayerMenu menu)
    {
        menu.Stage = MultiplayerMenuStage.Command;
        menu.Rows.Clear();
        menu.First = 0;
        BattleUnit unit = FF9StateSystem.Battle.FF9Battle.GetUnit(menu.BtlIndex);
        CharacterPresetId presetId = FF9StateSystem.Common.FF9.party.member[unit.Position].PresetId;
        Boolean trance = unit.IsUnderAnyStatus(BattleStatus.Trance);
        // Same order as the main menu's panel
        BattleCommandMenu[] order = { BattleCommandMenu.Attack, BattleCommandMenu.Ability1, BattleCommandMenu.Ability2, BattleCommandMenu.Item, BattleCommandMenu.Defend, BattleCommandMenu.Change };
        foreach (BattleCommandMenu commandMenu in order)
        {
            if (!CharacterCommandSet.SupportedMenus.Contains(commandMenu))
                continue;
            BattleCommandId cmdId = CharacterCommands.CommandSets[presetId].Get(trance, commandMenu);
            cmdId = BattleCommandHelper.Patch(cmdId, commandMenu, unit.Player, unit);
            Int32 state = BattleCommandHelper.GetCommandEnabledState(cmdId, commandMenu, unit.Player, unit);
            if (state == 0 || cmdId == BattleCommandId.None)
                continue; // hidden, as in the main menu
            MultiplayerMenuRow row = new MultiplayerMenuRow { Text = FF9TextTool.CommandName(cmdId), Enabled = state == 2, Value = (Int32)commandMenu, CommandId = cmdId };
            if (DoubleCastSet.Contains(cmdId) || MixCommandSet.ContainsKey(cmdId) || cmdId == BattleCommandId.AccessMenu)
            {
                row.Enabled = false;
                row.Right = "main menu";
            }
            menu.Rows.Add(row);
        }
        menu.Cursor = Mathf.Clamp(menu.CommandCursor, 0, Math.Max(0, menu.Rows.Count - 1));
    }

    private void BuildMultiplayerAbilityRows(MultiplayerMenu menu)
    {
        menu.Stage = MultiplayerMenuStage.Sub;
        menu.Rows.Clear();
        menu.First = 0;
        BattleUnit unit = FF9StateSystem.Battle.FF9Battle.GetUnit(menu.BtlIndex);
        CharacterCommand ff9Command = CharacterCommands.Commands[menu.CommandId];
        if (CommandIsMonsterTransformCommand(menu.BtlIndex, menu.CommandId, out BTL_DATA.MONSTER_TRANSFORM transform))
        {
            for (Int32 i = 0; i < ff9Command.ListEntry.Length; i++)
            {
                Int32 id = ff9Command.ListEntry[i];
                AbilityStatus state = GetMonsterTransformAbilityState(id, menu.BtlIndex);
                if (state == AbilityStatus.None)
                    continue;
                AA_DATA aaData = transform.spell[id];
                Int32 mp = GetMultiplayerMpCost(aaData, unit, BattleAbilityId.Void, menu);
                menu.Rows.Add(new MultiplayerMenuRow { Text = aaData.Name, Right = mp > 0 ? mp.ToString() : String.Empty, Enabled = state == AbilityStatus.Enable, Value = i });
            }
        }
        else
        {
            if (_abilityDetailDict.TryGetValue(menu.BtlIndex, out AbilityPlayerDetail detail))
                SetAbilityAp(detail);
            Int32 index = 0;
            foreach (BattleAbilityId activeId in ff9Command.EnumerateAbilities())
            {
                Int32 i = index++;
                if (activeId == BattleAbilityId.Void)
                    continue;
                Int32 abilId = ff9abil.GetAbilityIdFromActiveAbility(activeId);
                AbilityStatus state = GetAbilityState(abilId, menu.BtlIndex);
                if (state == AbilityStatus.None)
                    continue;
                BattleAbilityId patchedId = BattleAbilityHelper.Patch(ff9abil.GetActiveAbilityFromAbilityId(abilId), unit.Player);
                Int32 mp = GetMultiplayerMpCost(FF9StateSystem.Battle.FF9Battle.aa_data[patchedId], unit, patchedId, menu);
                menu.Rows.Add(new MultiplayerMenuRow { Text = FF9TextTool.ActionAbilityName(patchedId), Right = mp > 0 ? mp.ToString() : String.Empty, Enabled = state == AbilityStatus.Enable, Value = i });
            }
        }
        if (menu.Rows.Count == 0)
            menu.Rows.Add(new MultiplayerMenuRow { Text = "(nothing)", Enabled = false, Value = -1 });
        menu.Cursor = 0;
    }

    private void BuildMultiplayerItemRows(MultiplayerMenu menu)
    {
        // Same filtering as DisplayItem
        menu.Stage = MultiplayerMenuStage.Sub;
        menu.Rows.Clear();
        menu.First = 0;
        menu.Items.Clear();
        BattleUnit unit = FF9StateSystem.Battle.FF9Battle.GetUnit(menu.BtlIndex);
        CharacterCommand ff9Command = CharacterCommands.Commands[menu.CommandId];
        Boolean isThrow = ff9Command.Type == CharacterCommandType.Throw;
        foreach (FF9ITEM ff9Item in FF9StateSystem.Common.FF9.item)
        {
            if (ff9Item.count <= 0)
                continue;
            FF9ITEM_DATA itemData = ff9item._FF9Item_Data[ff9Item.id];
            Boolean canBeUsed = isThrow ? ff9item.CanThrowItem(itemData) : ff9item.HasItemEffect(ff9Item.id) && (itemData.type & ItemType.Item) != 0;
            if (canBeUsed)
            {
                if (!String.IsNullOrEmpty(itemData.use_condition))
                {
                    if (ff9Command.OnlySpecificItem && !itemData.use_condition.Contains("CommandId"))
                    {
                        canBeUsed = false;
                    }
                    else
                    {
                        Expression c = new Expression(itemData.use_condition);
                        NCalcUtility.InitializeExpressionUnit(ref c, unit);
                        c.Parameters["CommandId"] = (Int32)menu.CommandId;
                        c.Parameters["CommandMenu"] = (Int32)menu.CommandMenu;
                        c.EvaluateFunction += NCalcUtility.commonNCalcFunctions;
                        c.EvaluateParameter += NCalcUtility.commonNCalcParameters;
                        canBeUsed = NCalcUtility.EvaluateNCalcCondition(c.Evaluate());
                    }
                }
                else if (ff9Command.OnlySpecificItem)
                {
                    canBeUsed = false;
                }
            }
            if (!canBeUsed)
                continue;
            menu.Rows.Add(new MultiplayerMenuRow { Text = FF9TextTool.ItemName(ff9Item.id), Right = ff9Item.count.ToString(), Enabled = true, Value = menu.Items.Count });
            menu.Items.Add(ff9Item.id);
        }
        if (menu.Rows.Count == 0)
            menu.Rows.Add(new MultiplayerMenuRow { Text = "(no items)", Enabled = false, Value = -1 });
        menu.Cursor = 0;
    }

    /// <summary>Target choices, with the main menu's rules (SetTargetVisibility / SetTargetAvailability).</summary>
    private void BuildMultiplayerTargetRows(MultiplayerMenu menu)
    {
        menu.Stage = MultiplayerMenuStage.Target;
        menu.Rows.Clear();
        menu.First = 0;
        menu.AllMode = false;
        BattleUnit caster = FF9StateSystem.Battle.FF9Battle.GetUnit(menu.BtlIndex);
        CharacterCommand ff9Command = CharacterCommands.Commands[menu.CommandId];
        menu.DefaultAlly = false;
        menu.TargetDead = false;
        if (ff9Command.Type == CharacterCommandType.Item)
        {
            ITEM_DATA itemData = ff9item.GetItemEffect(menu.Items[menu.SubIndex]);
            menu.TargetType = itemData.info.Target;
            menu.DefaultAlly = itemData.info.DefaultAlly;
            menu.TargetDead = itemData.info.ForDead;
        }
        else
        {
            AA_DATA aaData = GetMultiplayerActiveAbility(menu, menu.SubIndex);
            menu.TargetType = aaData.Info.Target;
            menu.DefaultAlly = aaData.Info.DefaultAlly;
            menu.TargetDead = aaData.Info.ForDead;
            if (ff9Command.Type == CharacterCommandType.Throw && menu.SubIndex >= 0 && ff9item.HasItemEffect(menu.Items[menu.SubIndex]))
            {
                ITEM_DATA effect = ff9item.GetItemEffect(menu.Items[menu.SubIndex]);
                if (menu.TargetType == TargetType.SingleAny || menu.TargetType == TargetType.SingleAlly || menu.TargetType == TargetType.SingleEnemy)
                    menu.TargetType = effect.info.Target;
                menu.DefaultAlly = effect.info.DefaultAlly;
                menu.TargetDead = effect.info.ForDead;
            }
            if (menu.CommandId == BattleCommandId.Attack && (caster.IsHealingRod || caster.HasSupportAbility(SupportAbility1.Healer)))
                menu.DefaultAlly = true;
        }
        FillMultiplayerTargetRows(menu);
    }

    private void FillMultiplayerTargetRows(MultiplayerMenu menu)
    {
        menu.Rows.Clear();
        menu.First = 0;
        TargetType type = menu.TargetType;
        Boolean many = type == TargetType.ManyAny || type == TargetType.ManyAlly || type == TargetType.ManyEnemy;
        Boolean enemies = type == TargetType.SingleAny || type == TargetType.SingleEnemy || type == TargetType.ManyAny || type == TargetType.ManyEnemy || type == TargetType.Automatic || type == TargetType.Special;
        Boolean allies = type == TargetType.SingleAny || type == TargetType.SingleAlly || type == TargetType.ManyAny || type == TargetType.ManyAlly || type == TargetType.Automatic || type == TargetType.Special;

        if (type == TargetType.Self)
        {
            menu.Rows.Add(new MultiplayerMenuRow { Text = FF9StateSystem.Battle.FF9Battle.GetUnit(menu.BtlIndex).Name, Enabled = true, Value = menu.BtlIndex });
        }
        else if (type == TargetType.AllEnemy || type == TargetType.RandomEnemy)
        {
            menu.Rows.Add(new MultiplayerMenuRow { Text = "All enemies", Enabled = true, Value = MultiplayerGroupAllEnemies });
        }
        else if (type == TargetType.AllAlly || type == TargetType.RandomAlly)
        {
            menu.Rows.Add(new MultiplayerMenuRow { Text = "All allies", Enabled = true, Value = MultiplayerGroupAllAllies });
        }
        else if (type == TargetType.All || type == TargetType.Random)
        {
            MultiplayerMenuRow foes = new MultiplayerMenuRow { Text = "All enemies", Enabled = true, Value = MultiplayerGroupAllEnemies };
            MultiplayerMenuRow friends = new MultiplayerMenuRow { Text = "All allies", Enabled = true, Value = MultiplayerGroupAllAllies };
            menu.Rows.Add(menu.DefaultAlly ? friends : foes);
            menu.Rows.Add(menu.DefaultAlly ? foes : friends);
        }
        else if (type == TargetType.Everyone)
        {
            menu.Rows.Add(new MultiplayerMenuRow { Text = "Everyone", Enabled = true, Value = MultiplayerGroupEveryone });
        }
        else if (many && menu.AllMode)
        {
            if (enemies)
                menu.Rows.Add(new MultiplayerMenuRow { Text = "All enemies", Enabled = true, Value = MultiplayerGroupAllEnemies });
            if (allies)
                menu.Rows.Add(new MultiplayerMenuRow { Text = "All allies", Enabled = true, Value = MultiplayerGroupAllAllies });
            if (menu.DefaultAlly && menu.Rows.Count == 2)
                menu.Rows.Reverse();
        }
        else
        {
            List<MultiplayerMenuRow> foeRows = new List<MultiplayerMenuRow>();
            List<MultiplayerMenuRow> friendRows = new List<MultiplayerMenuRow>();
            foreach (BattleUnit unit in FF9StateSystem.Battle.FF9Battle.EnumerateBattleUnits())
            {
                if (!unit.IsTargetable || (unit.IsPlayer ? !allies : !enemies))
                    continue;
                Boolean dead = unit.CurrentHp == 0 || unit.IsUnderAnyStatus(BattleStatus.Death);
                String text = unit.Name;
                String right = unit.IsPlayer ? $"{unit.CurrentHp}/{unit.MaximumHp}" : String.Empty;
                MultiplayerMenuRow row = new MultiplayerMenuRow { Text = text, Right = right, Enabled = !dead || menu.TargetDead, Value = unit.GetIndex() };
                (unit.IsPlayer ? friendRows : foeRows).Add(row);
            }
            menu.Rows.AddRange(menu.DefaultAlly ? friendRows : foeRows);
            menu.Rows.AddRange(menu.DefaultAlly ? foeRows : friendRows);
        }
        menu.Cursor = 0;
        for (Int32 i = 0; i < menu.Rows.Count; i++)
            if (menu.Rows[i].Enabled)
            {
                menu.Cursor = i;
                break;
            }
        if (menu.Rows.Count == 0)
            menu.Rows.Add(new MultiplayerMenuRow { Text = "(no target)", Enabled = false, Value = -1 });
        String list = String.Join(", ", menu.Rows.ConvertAll(r => $"'{r.Text}'{(r.Enabled ? "" : " (off)")}").ToArray());
        if (!Configuration.Multiplayer.DebugOverlay) // diagnostics for the blank target list, only with the debug overlay
            return;
        Log.Message($"[Multiplayer] P{menu.Player} target list for {menu.CommandId} sub {menu.SubIndex}: type {type}, default {(menu.DefaultAlly ? "ally" : "enemy")}, dead {menu.TargetDead}, all {menu.AllMode}, cursor {menu.Cursor}: {list}");
        menu.PanelLogPending = true;
    }

    // ---- Input ---------------------------------------------------------------------------------------------------

    private void HandleMultiplayerMenuInput(MultiplayerMenu menu)
    {
        Int32 slot = MultiplayerManager.GetPadSlot(menu.Player);
        PadState pad = XInputPads.Get(slot);
        if (!pad.Connected)
            return;

        // 1 up, 2 down, 3 left, 4 right
        Int32 direction = 0;
        if (pad.IsDown(XInputPads.DPadUp) || pad.LeftY > 16000)
            direction = 1;
        else if (pad.IsDown(XInputPads.DPadDown) || pad.LeftY < -16000)
            direction = 2;
        else if (pad.IsDown(XInputPads.DPadLeft) || pad.LeftX < -16000)
            direction = 3;
        else if (pad.IsDown(XInputPads.DPadRight) || pad.LeftX > 16000)
            direction = 4;
        Single now = Time.realtimeSinceStartup;
        if (direction == 0)
        {
            menu.NavDirection = 0;
        }
        else if (direction != menu.NavDirection || now >= menu.NavNextTime)
        {
            Boolean first = direction != menu.NavDirection;
            menu.NavDirection = direction;
            menu.NavNextTime = now + (first ? 0.35f : 0.1f);
            NavigateMultiplayerMenu(menu, direction == 3 ? -1 : direction == 4 ? 1 : 0, direction == 1 ? 1 : direction == 2 ? -1 : 0);
        }

        UInt16 confirmButton = EventInput.isJapaneseLayout ? XInputPads.B : XInputPads.A;
        UInt16 cancelButton = EventInput.isJapaneseLayout ? XInputPads.A : XInputPads.B;
        if (XInputPads.IsPressed(slot, confirmButton))
        {
            try
            {
                ConfirmMultiplayerMenu(menu);
            }
            catch (Exception err)
            {
                // Never leave a half-built (blank) page: report it and go back to the commands
                Log.Error(err, $"[Multiplayer] P{menu.Player}'s compact menu failed on {menu.Stage} ({menu.CommandId}, sub {menu.SubIndex}); back to the commands");
                BuildMultiplayerCommandRows(menu);
                ShowMultiplayerMessage(menu, "That didn't work; try again or use the main menu");
            }
        }
        else if (XInputPads.IsPressed(slot, cancelButton))
            CancelMultiplayerMenu(menu);
        else if ((XInputPads.IsPressed(slot, XInputPads.LeftShoulder) || XInputPads.IsPressed(slot, XInputPads.RightShoulder))
            && menu.Stage == MultiplayerMenuStage.Target
            && (menu.TargetType == TargetType.ManyAny || menu.TargetType == TargetType.ManyAlly || menu.TargetType == TargetType.ManyEnemy))
        {
            menu.AllMode = !menu.AllMode;
            FillMultiplayerTargetRows(menu);
            FF9Sfx.FF9SFX_Play(103);
        }
    }

    private void ConfirmMultiplayerMenu(MultiplayerMenu menu)
    {
        if (menu.Cursor < 0 || menu.Cursor >= menu.Rows.Count)
            return;
        MultiplayerMenuRow row = menu.Rows[menu.Cursor];
        if (!row.Enabled)
        {
            FF9Sfx.FF9SFX_Play(102);
            if (row.Right == "main menu")
                ShowMultiplayerMessage(menu, "Use the main menu for this command");
            return;
        }
        FF9Sfx.FF9SFX_Play(103);
        switch (menu.Stage)
        {
            case MultiplayerMenuStage.Command:
                ConfirmMultiplayerCommand(menu, row);
                break;
            case MultiplayerMenuStage.Sub:
                menu.SubIndex = row.Value;
                menu.SubCursor = menu.Cursor;
                menu.CameFromSub = true;
                BuildMultiplayerTargetRows(menu);
                break;
            case MultiplayerMenuStage.Target:
                if (row.Value >= 0)
                    SendMultiplayerCommand(menu, row.Value, CursorGroup.Individual);
                else if (row.Value == MultiplayerGroupAllEnemies)
                    SendMultiplayerCommand(menu, -1, CursorGroup.AllEnemy);
                else if (row.Value == MultiplayerGroupAllAllies)
                    SendMultiplayerCommand(menu, -1, CursorGroup.AllPlayer);
                else if (row.Value == MultiplayerGroupEveryone)
                    SendMultiplayerCommand(menu, -1, CursorGroup.All);
                break;
        }
    }

    private void ConfirmMultiplayerCommand(MultiplayerMenu menu, MultiplayerMenuRow row)
    {
        menu.CommandCursor = menu.Cursor;
        menu.CommandMenu = (BattleCommandMenu)row.Value;
        menu.CommandId = row.CommandId;
        menu.SubIndex = -1;
        menu.CameFromSub = false;
        if (menu.CommandId == BattleCommandId.Defend || menu.CommandId == BattleCommandId.Change)
        {
            SendMultiplayerCommand(menu, menu.BtlIndex, CursorGroup.Individual);
            return;
        }
        CharacterCommand ff9Command = CharacterCommands.Commands[menu.CommandId];
        switch (ff9Command.Type)
        {
            case CharacterCommandType.Normal:
                BuildMultiplayerTargetRows(menu);
                break;
            case CharacterCommandType.Ability:
                BuildMultiplayerAbilityRows(menu);
                break;
            case CharacterCommandType.Item:
            case CharacterCommandType.Throw:
                BuildMultiplayerItemRows(menu);
                break;
            case CharacterCommandType.Instant:
            {
                AA_DATA aaData = GetMultiplayerActiveAbility(menu, -1);
                TargetType targetType = aaData.Info.Target;
                menu.TargetType = targetType;
                if (targetType == TargetType.Self)
                    SendMultiplayerCommand(menu, menu.BtlIndex, CursorGroup.Individual);
                else if (targetType == TargetType.Everyone)
                    SendMultiplayerCommand(menu, -1, CursorGroup.All);
                else if (targetType == TargetType.AllEnemy)
                    SendMultiplayerCommand(menu, -1, CursorGroup.AllEnemy);
                else if (targetType == TargetType.AllAlly)
                    SendMultiplayerCommand(menu, -1, CursorGroup.AllPlayer);
                else
                    ShowMultiplayerMessage(menu, "Use the main menu for this command");
                break;
            }
        }
    }

    private void CancelMultiplayerMenu(MultiplayerMenu menu)
    {
        switch (menu.Stage)
        {
            case MultiplayerMenuStage.Command:
                FF9Sfx.FF9SFX_Play(102);
                return;
            case MultiplayerMenuStage.Sub:
                FF9Sfx.FF9SFX_Play(101);
                BuildMultiplayerCommandRows(menu);
                return;
            case MultiplayerMenuStage.Target:
                FF9Sfx.FF9SFX_Play(101);
                if (menu.CameFromSub)
                {
                    CharacterCommandType type = CharacterCommands.Commands[menu.CommandId].Type;
                    if (type == CharacterCommandType.Ability)
                        BuildMultiplayerAbilityRows(menu);
                    else
                        BuildMultiplayerItemRows(menu);
                    menu.Cursor = Mathf.Clamp(menu.SubCursor, 0, Math.Max(0, menu.Rows.Count - 1));
                }
                else
                {
                    BuildMultiplayerCommandRows(menu);
                }
                return;
        }
    }

    private static void ShowMultiplayerMessage(MultiplayerMenu menu, String message)
    {
        menu.Message = message;
        menu.MessageUntil = Time.realtimeSinceStartup + 2f;
    }

    // ---- Sending -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Build the command with the main menu's own ProcessCommand (its target ids, select modes, ability patches and
    /// special cases stay identical), run with this character's context swapped in, then send it like SendCommand.
    /// </summary>
    private void SendMultiplayerCommand(MultiplayerMenu menu, Int32 targetIndex, CursorGroup cursor)
    {
        Int32 savedPlayer = CurrentPlayerIndex;
        BattleCommandMenu savedMenu = _currentCommandIndex;
        BattleCommandId savedCommand = _currentCommandId;
        Int32 savedSub = _currentSubMenuIndex;
        TargetType savedCursor = _targetCursor;
        List<RegularItem> savedItems = new List<RegularItem>(_itemIdList);
        CommandDetail detail;
        try
        {
            CurrentPlayerIndex = menu.BtlIndex;
            _currentCommandIndex = menu.CommandMenu;
            _currentCommandId = menu.CommandId;
            _currentSubMenuIndex = menu.SubIndex;
            _targetCursor = menu.TargetType;
            _itemIdList.Clear();
            _itemIdList.AddRange(menu.Items);
            detail = ProcessCommand(targetIndex, cursor);
        }
        finally
        {
            CurrentPlayerIndex = savedPlayer;
            _currentCommandIndex = savedMenu;
            _currentCommandId = savedCommand;
            _currentSubMenuIndex = savedSub;
            _targetCursor = savedCursor;
            _itemIdList.Clear();
            _itemIdList.AddRange(savedItems);
        }

        BTL_DATA btl = FF9StateSystem.Battle.FF9Battle.btl_data[menu.BtlIndex];
        CMD_DATA cmd = btl.cmd[0];
        cmd.regist.sel_mode = 1;
        btl_cmd.SetCommand(cmd, detail.CommandId, detail.SubId, detail.TargetId, detail.TargetType, cmdMenu: detail.Menu);
        InputFinishList.Add(menu.BtlIndex);
        _partyDetail.SetBlink(menu.BtlIndex, false);
        Log.Message($"[Multiplayer] P{menu.Player} sent {detail.CommandId} (sub {detail.SubId}, targets 0x{detail.TargetId:X2}) from their compact menu");
        CloseMultiplayerMenu(menu, null);
    }

    /// <summary>
    /// GetSelectedActiveAbility for this menu's character. Its PatchAbility reads the main menu's CurrentPlayerIndex, which
    /// can be -1 (main menu just closed) or another character: that threw while building the target list, after the list
    /// was already cleared, leaving it blank.
    /// </summary>
    private AA_DATA GetMultiplayerActiveAbility(MultiplayerMenu menu, Int32 abilityIndex)
    {
        Int32 savedPlayer = CurrentPlayerIndex;
        try
        {
            CurrentPlayerIndex = menu.BtlIndex;
            return GetSelectedActiveAbility(menu.BtlIndex, menu.CommandId, abilityIndex, out _, out _);
        }
        finally
        {
            CurrentPlayerIndex = savedPlayer;
        }
    }

    /// <summary>GetActionMpCost with this menu's command instead of the main menu's.</summary>
    private Int32 GetMultiplayerMpCost(AA_DATA aaData, BattleUnit unit, BattleAbilityId abilId, MultiplayerMenu menu)
    {
        Int32 mpCost = aaData.MP;
        BattleAbilityHelper.GetPatchedMPCost(ref mpCost, abilId, unit, menu.CommandId, menu.CommandMenu, aaData);
        if ((aaData.Type & 4) != 0 && battle.GARNET_SUMMON_FLAG != 0)
            mpCost *= 4;
        return mpCost * unit.Player.mpCostFactor / 100;
    }

    // ---- Native panels: copies of the game's own command window ---------------------------------------------------
    // Each open compact menu is shown in a copy of the main command window (same frame, font, caption, the game's own
    // pointer hand), so it looks like the real menu. The copy is made inert: deactivated right after Instantiate (its
    // rows leave ButtonGroupState's groups in OnDisable), then stripped of navigation/click components, so the main
    // menu's cursor can never land on it. Up to 3 copies (one per player who isn't using the main menu) sit side by
    // side at the top of the screen. If a copy can't be made, the IMGUI fallback below draws the menu instead.

    private const Int32 MultiplayerVisibleRows = 6;
    private const Int32 MultiplayerPanelCount = MultiplayerManager.MaxPlayers - 1;

    private sealed class MultiplayerPanel
    {
        public GameObject Root;
        public UIWidget Widget;
        public GameObject[] Rows;       // the button rows (inert copies: no UIButton, so no GONavigationButton wrapper)
        public UILabel[] RowLabels;     // child 0 of each row
        public UILabel Caption;
        public GameObject PointerRow;
    }

    private readonly MultiplayerPanel[] _multiplayerPanels = new MultiplayerPanel[MultiplayerPanelCount];
    private Boolean _multiplayerPanelsFailed;

    /// <summary>Show each open compact menu in its panel (called at the end of UpdateMultiplayerMenus).</summary>
    private void SyncMultiplayerPanels(Boolean usable)
    {
        Int32 slot = 0;
        for (Int32 player = 1; player <= MultiplayerManager.MaxPlayers; player++)
        {
            if (!usable || !_multiplayerMenus.TryGetValue(player, out MultiplayerMenu menu))
                continue;
            if (slot >= MultiplayerPanelCount)
                break;
            MultiplayerPanel panel = GetMultiplayerPanel(slot);
            if (panel != null)
                ShowMultiplayerPanel(panel, menu, slot);
            slot++;
        }
        for (; slot < MultiplayerPanelCount; slot++)
            HideMultiplayerPanel(_multiplayerPanels[slot]);
    }

    private Boolean UsesMultiplayerPanels => !_multiplayerPanelsFailed && _commandPanel != null;

    private MultiplayerPanel GetMultiplayerPanel(Int32 slot)
    {
        MultiplayerPanel existing = _multiplayerPanels[slot];
        if (existing != null && existing.Root != null)
            return existing;
        if (!UsesMultiplayerPanels)
            return null;
        GameObject clone = null;
        try
        {
            GameObject source = _commandPanel.GameObject;
            GameObject activeButtonBefore = ButtonGroupState.ActiveButton;
            clone = UnityEngine.Object.Instantiate(source);
            clone.SetActive(false); // its rows leave ButtonGroupState's groups (OnDisable)
            clone.name = $"MultiplayerCommandPanel{slot + 1}";
            clone.transform.parent = source.transform.parent;
            clone.transform.localRotation = source.transform.localRotation;
            clone.transform.localScale = source.transform.localScale * 0.82f;
            clone.transform.localPosition = source.transform.localPosition;
            if (ButtonGroupState.ActiveButton != activeButtonBefore)
                ButtonGroupState.ActiveButton = activeButtonBefore;

            // Inert: no navigation, no clicks, no animations of its own
            foreach (ButtonGroupState component in clone.GetComponentsInChildren<ButtonGroupState>(true))
                UnityEngine.Object.DestroyImmediate(component);
            foreach (UIKeyNavigation component in clone.GetComponentsInChildren<UIKeyNavigation>(true))
                UnityEngine.Object.DestroyImmediate(component);
            foreach (UIButtonColor component in clone.GetComponentsInChildren<UIButtonColor>(true))
                UnityEngine.Object.DestroyImmediate(component);
            foreach (UIEventListener component in clone.GetComponentsInChildren<UIEventListener>(true))
                UnityEngine.Object.DestroyImmediate(component);
            foreach (BoxCollider component in clone.GetComponentsInChildren<BoxCollider>(true))
                UnityEngine.Object.DestroyImmediate(component);
            foreach (UITweener component in clone.GetComponentsInChildren<UITweener>(true))
                component.enabled = false;
            foreach (UIRect rect in clone.GetComponents<UIRect>())
                rect.SetAnchor((Transform)null);
            // Rows 0-5 are the command buttons, 6 the caption; anything after (the AccessMenu copy) is not used
            for (Int32 i = MultiplayerVisibleRows + 1; i < clone.transform.childCount; i++)
                clone.transform.GetChild(i).gameObject.SetActive(false);

            // Same layout GONavigationButton / CaptionBackground read: row = [label, highlight, background], caption label = child 2
            MultiplayerPanel panel = new MultiplayerPanel
            {
                Root = clone,
                Widget = clone.GetComponent<UIWidget>(),
                Rows = new GameObject[MultiplayerVisibleRows],
                RowLabels = new UILabel[MultiplayerVisibleRows],
                Caption = clone.GetChild(MultiplayerVisibleRows).GetChild(2).GetComponent<UILabel>(),
            };
            for (Int32 i = 0; i < MultiplayerVisibleRows; i++)
            {
                GameObject row = clone.GetChild(i);
                panel.Rows[i] = row;
                panel.RowLabels[i] = row.GetChild(0).GetComponent<UILabel>();
                panel.RowLabels[i].overflowMethod = UILabel.Overflow.ResizeFreely;
                row.GetChild(1).SetActive(false); // highlight
            }
            if (_multiplayerSlotPos == null)
                InitMultiplayerSlotGeometry(panel);
            _multiplayerPanels[slot] = panel;
            Log.Message($"[Multiplayer] Created compact battle menu panel {slot + 1}");
            return panel;
        }
        catch (Exception err)
        {
            if (clone != null)
                UnityEngine.Object.Destroy(clone);
            _multiplayerPanelsFailed = true;
            Log.Error(err, "[Multiplayer] Cannot copy the command window; compact menus use the simple overlay instead");
            return null;
        }
    }

    private void ShowMultiplayerPanel(MultiplayerPanel panel, MultiplayerMenu menu, Int32 slot)
    {
        if (!panel.Root.activeSelf)
            panel.Root.SetActive(true);

        BattleUnit unit = FF9StateSystem.Battle.FF9Battle.GetUnit(menu.BtlIndex);
        String stage = menu.Stage == MultiplayerMenuStage.Command ? Localization.Get("CommandCaption")
            : menu.Stage == MultiplayerMenuStage.Sub ? FF9TextTool.CommandName(menu.CommandId)
            : "Target";
        Boolean hasMessage = menu.Message != null && Time.realtimeSinceStartup < menu.MessageUntil;
        String position = menu.Rows.Count > MultiplayerVisibleRows ? $" {menu.Cursor + 1}/{menu.Rows.Count}" : String.Empty;
        panel.Caption.rawText = hasMessage ? $"P{menu.Player} {menu.Message}" : $"P{menu.Player} {unit.Name} - {stage}{position}";
        panel.Caption.color = hasMessage ? FF9TextTool.Yellow : FF9TextTool.White;

        EnsureMultiplayerCursorVisible(menu);
        GameObject cursorRow = null;
        for (Int32 i = 0; i < MultiplayerVisibleRows; i++)
        {
            // The main menu hides rows for commands a character lacks; this copy uses every row
            if (!panel.Rows[i].activeSelf)
                panel.Rows[i].SetActive(true);
            if (!panel.RowLabels[i].gameObject.activeSelf)
                panel.RowLabels[i].gameObject.SetActive(true);
            UILabel label = panel.RowLabels[i];
            Int32 index = GetMultiplayerSlotRow(menu, i);
            if (index < 0)
            {
                label.rawText = String.Empty;
                continue;
            }
            MultiplayerMenuRow row = menu.Rows[index];
            String text = row.Text;
            if (!String.IsNullOrEmpty(row.Right))
                text += "   " + row.Right;
            label.rawText = text;
            label.color = row.Enabled ? FF9TextTool.White : FF9TextTool.Gray;
            if (index == menu.Cursor)
                cursorRow = panel.Rows[i];
        }
        SetMultiplayerPointer(panel, cursorRow);
        PlaceMultiplayerPanel(panel, slot);
        if (menu.PanelLogPending)
        {
            menu.PanelLogPending = false;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append($"[Multiplayer] P{menu.Player} panel {slot + 1} (root active {panel.Root.activeInHierarchy}, alpha {panel.Widget?.finalAlpha:0.00}, first {menu.First}):");
            for (Int32 i = 0; i < MultiplayerVisibleRows; i++)
            {
                UILabel label = panel.RowLabels[i];
                sb.Append($" [{i}: '{label.rawText}' shown {label.gameObject.activeInHierarchy}/{label.enabled}, alpha {label.finalAlpha:0.00}, size {label.width}x{label.height}, at {(Vector2)panel.Rows[i].transform.localPosition}]");
            }
            Log.Message(sb.ToString());
        }
    }

    private void HideMultiplayerPanel(MultiplayerPanel panel)
    {
        if (panel == null || panel.Root == null)
            return;
        SetMultiplayerPointer(panel, null);
        if (panel.Root.activeSelf)
            panel.Root.SetActive(false);
    }

    /// <summary>The game's own blinking pointer hand, on the row under this menu's cursor.</summary>
    private static void SetMultiplayerPointer(MultiplayerPanel panel, GameObject row)
    {
        PointerManager pointers = Singleton<PointerManager>.Instance;
        // Same row: nothing to do, unless the main menu cleared every pointer meanwhile (DisableAllGroup...)
        if (ReferenceEquals(panel.PointerRow, row) && (row == null || pointers.GetPointerFromButton(row) != null))
            return;
        if (panel.PointerRow != null && !ReferenceEquals(panel.PointerRow, row))
            pointers.RemovePointerFromGameObject(panel.PointerRow);
        panel.PointerRow = row;
        if (row == null)
            return;
        // Attach with neutral settings (the shared offset/limit belong to the main menu's current group)
        Vector2 offset = pointers.PointerOffset;
        Vector4 limit = pointers.PointerLimitRect;
        pointers.PointerOffset = Vector2.zero;
        pointers.PointerLimitRect = UIManager.UIScreenCoOrdinate;
        pointers.AttachPointerToGameObject(row);
        pointers.PointerOffset = offset;
        pointers.PointerLimitRect = limit;
    }

    /// <summary>Top of the screen, left to right in player order, whatever the resolution and UI scaling.</summary>
    private static void PlaceMultiplayerPanel(MultiplayerPanel panel, Int32 slot)
    {
        if (panel.Widget == null)
            return;
        Camera camera = NGUITools.FindCameraForLayer(panel.Root.layer);
        if (camera == null)
            return;
        Vector3[] corners = panel.Widget.worldCorners; // bottom-left, top-left, top-right, bottom-right
        Vector3 topLeft = camera.WorldToViewportPoint(corners[1]);
        Vector3 topRight = camera.WorldToViewportPoint(corners[2]);
        Single width = topRight.x - topLeft.x;
        Single gap = 0.012f;
        Vector3 target = camera.ViewportToWorldPoint(new Vector3(gap + slot * (width + gap), 0.975f, topLeft.z));
        Vector3 delta = target - corners[1];
        delta.z = 0f;
        if (delta.sqrMagnitude > 1e-8f)
            panel.Root.transform.position += delta;
    }

    // ---- Fallback drawing (IMGUI) ---------------------------------------------------------------------------------

    private GUIStyle _multiplayerBoxStyle;
    private GUIStyle _multiplayerTextStyle;
    private Texture2D _multiplayerBackground;

    /// <summary>Called by MultiplayerBattleMenuView.OnGUI.</summary>
    public void DrawMultiplayerMenus()
    {
        if (UsesMultiplayerPanels || _multiplayerMenus.Count == 0 || !_commandEnable || PersistenSingleton<UIManager>.Instance.State != UIManager.UIState.BattleHUD)
            return;
        if (_multiplayerBoxStyle == null)
        {
            _multiplayerBackground = new Texture2D(1, 1);
            _multiplayerBackground.SetPixel(0, 0, new Color(0.42f, 0.43f, 0.46f, 0.92f)); // close to the game's grey windows
            _multiplayerBackground.Apply();
            _multiplayerBoxStyle = new GUIStyle(GUI.skin.box);
            _multiplayerBoxStyle.normal.background = _multiplayerBackground;
            _multiplayerTextStyle = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = false, alignment = TextAnchor.MiddleLeft };
        }
        Int32 fontSize = Math.Max(14, Screen.height / 42);
        _multiplayerTextStyle.fontSize = fontSize;
        Single rowHeight = fontSize * 1.35f;
        Single width = Math.Max(260f, Screen.width * 0.21f);
        Single margin = Screen.height * 0.02f;
        const Int32 maxRows = 8;

        Int32 slotIndex = 0;
        for (Int32 player = 1; player <= MultiplayerManager.MaxPlayers; player++)
        {
            if (!_multiplayerMenus.TryGetValue(player, out MultiplayerMenu menu))
                continue;
            Int32 visible = Math.Min(maxRows, menu.Rows.Count);
            Boolean hasMessage = menu.Message != null && Time.realtimeSinceStartup < menu.MessageUntil;
            Boolean hasHint = menu.Stage == MultiplayerMenuStage.Target && (menu.TargetType == TargetType.ManyAny || menu.TargetType == TargetType.ManyAlly || menu.TargetType == TargetType.ManyEnemy);
            Single height = rowHeight * (visible + 1 + (hasMessage || hasHint ? 1 : 0)) + margin;
            Single x = margin + slotIndex * (width + margin);
            Single y = Screen.height * 0.08f;
            slotIndex++;

            GUI.Box(new Rect(x, y, width, height), GUIContent.none, _multiplayerBoxStyle);
            BattleUnit unit = FF9StateSystem.Battle.FF9Battle.GetUnit(menu.BtlIndex);
            String stage = menu.Stage == MultiplayerMenuStage.Command ? "Command" : menu.Stage == MultiplayerMenuStage.Sub ? FF9TextTool.CommandName(menu.CommandId) : "Target";
            GUI.Label(new Rect(x + margin / 2f, y + margin / 4f, width - margin, rowHeight), $"<color=yellow>P{menu.Player}</color>  <b>{unit.Name}</b>  <color=#9fb8ff>{stage}</color>", _multiplayerTextStyle);

            Int32 first = Mathf.Clamp(menu.Cursor - visible / 2, 0, Math.Max(0, menu.Rows.Count - visible));
            for (Int32 i = 0; i < visible; i++)
            {
                Int32 index = first + i;
                MultiplayerMenuRow row = menu.Rows[index];
                Single rowY = y + margin / 4f + rowHeight * (i + 1);
                String color = row.Enabled ? (index == menu.Cursor ? "white" : "#d0d0d0") : "#707070";
                String cursor = index == menu.Cursor ? "<color=yellow>></color> " : "   ";
                GUI.Label(new Rect(x + margin / 2f, rowY, width * 0.72f, rowHeight), $"{cursor}<color={color}>{row.Text}</color>", _multiplayerTextStyle);
                if (!String.IsNullOrEmpty(row.Right))
                    GUI.Label(new Rect(x + width * 0.68f, rowY, width * 0.3f, rowHeight), $"<color={color}>{row.Right}</color>", _multiplayerTextStyle);
            }
            if (hasMessage || hasHint)
            {
                String footer = hasMessage ? $"<color=#ffb060>{menu.Message}</color>" : "<color=#9fb8ff>LB/RB: one / all</color>";
                GUI.Label(new Rect(x + margin / 2f, y + margin / 4f + rowHeight * (visible + 1), width - margin, rowHeight), footer, _multiplayerTextStyle);
            }
        }
    }
}
