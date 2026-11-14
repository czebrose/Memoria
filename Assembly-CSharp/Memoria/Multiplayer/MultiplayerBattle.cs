using Assets.Scripts.Common;
using Memoria.Data;
using Memoria.Prime;
using System;

namespace Memoria.Multiplayer
{
    /// <summary>
    /// Battle rules for local multiplayer (Phase 2): each character's command menu is driven by the player who
    /// owns that character. BattleHUD calls in here from a few marked places; with multiplayer off every method
    /// is a no-op (or returns the upstream answer).
    /// </summary>
    public static class MultiplayerBattle
    {
        private static Boolean s_controlFromBattle;

        /// <summary>Owner (1-4) of a battle unit, by its battle index (BattleHUD's "player id").</summary>
        public static Int32 OwnerOf(Int32 btlIndex)
        {
            BattleUnit unit = GetPlayerUnit(btlIndex);
            return unit == null ? 1 : MultiplayerManager.GetOwner(unit.PlayerIndex);
        }

        /// <summary>
        /// BattleHUD.InitialBattle: player 1 is in control until a command menu opens. This runs at battle start and
        /// also when the battle menu is re-enabled while no command menu is open (e.g. after a scripted sequence).
        /// </summary>
        public static void OnBattleStart()
        {
            if (!MultiplayerManager.IsActive)
                return;
            MultiplayerManager.SetActivePlayer(1, "battle menu initialised");
            s_controlFromBattle = true;
        }

        /// <summary>BattleHUD.SwitchPlayer: a character's command menu opens, so its owner takes control.</summary>
        public static void OnCommandMenuOpened(Int32 btlIndex)
        {
            if (!MultiplayerManager.IsActive)
                return;
            BattleUnit unit = GetPlayerUnit(btlIndex);
            if (unit == null)
                return;
            Int32 owner = MultiplayerManager.GetOwner(unit.PlayerIndex);
            if (owner != MultiplayerManager.ActivePlayer)
                MultiplayerManager.SetActivePlayer(owner, $"{unit.Name} ({unit.PlayerIndex}) command menu");
            s_controlFromBattle = true;
        }

        /// <summary>
        /// Whether the player in control may pick this ready character by hand (next-character key, party panel click).
        /// With multiplayer on, players only cycle through their own characters.
        /// </summary>
        public static Boolean CanSelect(Int32 btlIndex)
        {
            return !MultiplayerManager.IsActive || OwnerOf(btlIndex) == MultiplayerManager.ActivePlayer;
        }

        /// <summary>"P2 " prefix for names and menu captions; empty with multiplayer off.</summary>
        public static String Tag(Int32 btlIndex)
        {
            return MultiplayerManager.IsActive ? $"P{OwnerOf(btlIndex)} " : String.Empty;
        }

        /// <summary>"P2 " prefix for a player unit's name; empty with multiplayer off or for enemies.</summary>
        public static String Tag(BattleUnit unit)
        {
            return MultiplayerManager.IsActive && unit != null && unit.IsPlayer ? $"P{MultiplayerManager.GetOwner(unit.PlayerIndex)} " : String.Empty;
        }

        /// <summary>Called every frame by MultiplayerManager: hand control back to player 1 once the battle is over.</summary>
        internal static void Update()
        {
            if (!s_controlFromBattle || SceneDirector.IsBattleScene())
                return;
            s_controlFromBattle = false;
            MultiplayerManager.SetActivePlayer(1, "battle ended");
        }

        private static BattleUnit GetPlayerUnit(Int32 btlIndex)
        {
            try
            {
                BattleUnit unit = FF9StateSystem.Battle?.FF9Battle?.GetUnit(btlIndex);
                return unit != null && unit.IsPlayer ? unit : null;
            }
            catch (Exception err)
            {
                Log.Warning($"[Multiplayer] Cannot read battle unit {btlIndex}: {err.Message}");
                return null;
            }
        }
    }
}
