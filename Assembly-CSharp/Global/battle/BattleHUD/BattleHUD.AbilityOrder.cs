using FF9;
using Memoria.Data;
using Memoria.Database;
using System;
using System.Collections.Generic;

/// <summary>
/// The battle menu's ability lists (White Magic, Skill...) in the character's own order (the Ability menu's Arrange,
/// <see cref="AbilityOrder"/>). The list shows the command's entries permuted; every lookup from a list place to the
/// command's entry goes through <see cref="AbilityEntryIndex"/>.
/// </summary>
public partial class BattleHUD
{
    /// <summary>For an ability command: the command entry shown at each list place (null: the game's order).</summary>
    private Int32[] AbilityListOrder(Int32 playerIndex, BattleCommandId cmdId)
    {
        if (playerIndex < 0 || !CharacterCommands.Commands.TryGetValue(cmdId, out CharacterCommand command) || command.Type != CharacterCommandType.Ability)
            return null;
        if (CommandIsMonsterTransformCommand(playerIndex, cmdId, out _))
            return null;
        PLAYER player = FF9StateSystem.Battle.FF9Battle.GetUnit(playerIndex)?.Player;
        if (player?.abilityOrder == null || player.abilityOrder.Count == 0)
            return null;
        List<Int32> ids = new List<Int32>(command.ListEntry.Length);
        foreach (Int32 entry in command.ListEntry)
            ids.Add(ff9abil.GetAbilityIdFromActiveAbility((BattleAbilityId)entry));
        return AbilityOrder.Permutation(player, ids);
    }

    /// <summary>The command entry at this place of the list as shown.</summary>
    private Int32 AbilityEntryIndex(Int32 playerIndex, BattleCommandId cmdId, Int32 listIndex)
    {
        if (listIndex < 0)
            return listIndex;
        Int32[] order = AbilityListOrder(playerIndex, cmdId);
        return order != null && listIndex < order.Length ? order[listIndex] : listIndex;
    }

    /// <summary>Puts a freshly built ability list in the shown order.</summary>
    private List<ListDataTypeBase> InAbilityOrder(List<ListDataTypeBase> list, Int32 playerIndex, BattleCommandId cmdId)
    {
        Int32[] order = AbilityListOrder(playerIndex, cmdId);
        if (order == null || order.Length != list.Count)
            return list;
        List<ListDataTypeBase> result = new List<ListDataTypeBase>(list.Count);
        foreach (Int32 i in order)
            result.Add(list[i]);
        return result;
    }
}
