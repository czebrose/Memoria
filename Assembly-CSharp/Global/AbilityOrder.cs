using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// A character's own order of its action abilities (the Ability menu's Arrange): <see cref="PLAYER.abilityOrder"/> holds
/// ability ids (as <see cref="ff9abil"/> numbers them: the character's ability list) in the order the player chose.
/// Empty is the game's order. The Ability menu's Use list and the battle menu's ability lists (White Magic, Skill...)
/// show it; ids it doesn't name keep the game's order, after the ones it names. Support abilities aren't arranged. It's saved with the character ("abilityOrder"), so an
/// online session's party state carries it too.
/// </summary>
public static class AbilityOrder
{
    /// <summary>For a list of ability ids: the original index shown at each place (identity when there's no order).</summary>
    public static Int32[] Permutation(PLAYER player, IList<Int32> abilityIds)
    {
        Int32[] identity = Enumerable.Range(0, abilityIds.Count).ToArray();
        if (player?.abilityOrder == null || player.abilityOrder.Count == 0)
            return identity;
        Dictionary<Int32, Int32> rank = new Dictionary<Int32, Int32>();
        for (Int32 i = 0; i < player.abilityOrder.Count; i++)
            if (!rank.ContainsKey(player.abilityOrder[i]))
                rank[player.abilityOrder[i]] = i;
        return identity.OrderBy(i => abilityIds[i] != 0 && rank.TryGetValue(abilityIds[i], out Int32 r) ? r : Int32.MaxValue).ThenBy(i => i).ToArray();
    }

    /// <summary>Puts a list in the player's order (stable).</summary>
    public static List<Int32> Sorted(PLAYER player, List<Int32> abilityIds)
    {
        Int32[] p = Permutation(player, abilityIds);
        List<Int32> result = new List<Int32>(p.Length);
        foreach (Int32 i in p)
            result.Add(abilityIds[i]);
        return result;
    }

    /// <summary>Writes the order shown in the menu's action ability list.</summary>
    public static void Set(PLAYER player, IEnumerable<Int32> actionIds)
    {
        if (player == null)
            return;
        List<Int32> order = new List<Int32>();
        foreach (Int32 id in actionIds)
            if (id != 0 && !order.Contains(id))
                order.Add(id);
        player.abilityOrder = order;
    }
}
