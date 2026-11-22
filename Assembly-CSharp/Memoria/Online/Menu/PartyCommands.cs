using System;
using System.Collections.Generic;
using Row = Memoria.Online.Menus.MenuWindows.Row;

namespace Memoria.Online.Menus
{
    /// <summary>
    /// The party-control commands a character offers (Take, Follow, Swap, Accept / Decline, Release) and their texts,
    /// shared by the main menu's Online entry and the battle's Online window. The rules are the session's
    /// (SessionLobby.Control.cs); this only decides which rows show, greyed or not.
    /// </summary>
    internal static class PartyCommands
    {
        /// <param name="live">The control to act on when a row is chosen (null: the session ended meanwhile).</param>
        /// <param name="done">After an action (back to the party).</param>
        /// <param name="openFollow">Opens the list of players to follow.</param>
        /// <param name="inBattle">Texts say "from its next turn" instead of "from the next battle".</param>
        public static List<Row> ForCharacter(IPartyControl control, Int32 character, String name, Func<IPartyControl> live, Action done, Action openFollow, Boolean inBattle, out String info)
        {
            UInt64 me = control.Me;
            UInt64 holder = control.HolderOf(character);
            Int32 mine = control.CharacterOf(me);
            String mineName = CharacterName(mine);
            String when = inBattle ? " (from its next turn)" : "";
            List<Row> rows = new List<Row>();
            if (holder == me)
            {
                info = "You play this character.";
                if (control.IsHost)
                    rows.Add(MenuWindows.NewRow("Release", false, "The host always plays a character: take or swap for another one instead.", null));
                else
                    rows.Add(MenuWindows.NewRow("Release", true, $"Give {name} back to the host's game and watch{when}.", () => { live()?.Release(); done(); }));
            }
            else if (holder == 0)
            {
                UInt64 leader = control.LeaderOf(character);
                info = $"Nobody plays {name}. Follows {PlayerLabel(control, leader)}.";
                rows.Add(MenuWindows.NewRow("Take", true, (mine >= 0 ? $"Play {name}. {mineName} goes back to the host's game" : $"Play {name}") + $"{when}.", () => { live()?.Take(character); done(); }));
                rows.Add(MenuWindows.NewRow("Follow", true, inBattle
                    ? $"Who commands {name} in this battle (from its next turn) and joins with it the next ones."
                    : $"Who {name} follows: they join that player's battles, commanded from that player's PC.", openFollow));
            }
            else
            {
                info = $"{PlayerLabel(control, holder)} plays {name}.";
                if (control.OfferFrom == holder)
                {
                    String offer = OfferText(control, holder);
                    rows.Add(MenuWindows.NewRow("Accept swap", true, offer, () => { live()?.AnswerOffer(true); done(); }));
                    rows.Add(MenuWindows.NewRow("Decline swap", true, offer, () => { live()?.AnswerOffer(false); done(); }));
                }
                else if (control.OfferSent(holder))
                    rows.Add(MenuWindows.NewRow("Swap offered", false, $"Waiting for {control.PlayerName(holder)}'s answer (offers expire after 30 s).", null));
                else if (mine < 0)
                    rows.Add(MenuWindows.NewRow("Swap", false, "You're watching: take a character nobody plays first.", null));
                else
                    rows.Add(MenuWindows.NewRow("Swap", true, $"Offer {control.PlayerName(holder)} your {mineName} for {name}. They accept or decline.", () => { live()?.OfferSwap(character); done(); }));
            }
            return rows;
        }

        /// <summary>The players a free character can follow (the current leader greyed).</summary>
        public static List<Row> FollowRows(IPartyControl control, Int32 character, String name, Func<IPartyControl> live, Action done, Boolean inBattle)
        {
            UInt64 leader = control.LeaderOf(character);
            List<Row> rows = new List<Row>();
            foreach (UInt64 player in control.Players)
            {
                UInt64 p = player;
                Boolean current = p == leader;
                String label = PlayerLabel(control, p);
                rows.Add(MenuWindows.NewRow(label, !current, current ? $"{name} already follows {label}." : inBattle ? $"{label} commands {name} from its next turn." : $"{name} follows {label} from the next battle.", () => { live()?.Follow(character, p); done(); }));
            }
            return rows;
        }

        /// <summary>Who plays a character, short: "You", "Name (host)", "Follows Name".</summary>
        public static String OwnerLabel(IPartyControl control, Int32 character)
        {
            UInt64 holder = control.HolderOf(character);
            if (holder == control.Me)
                return "You";
            if (holder != 0)
                return PlayerLabel(control, holder);
            UInt64 leader = control.LeaderOf(character);
            return $"Follows {(leader == control.Me ? "you" : control.PlayerName(leader))}";
        }

        public static String PlayerLabel(IPartyControl control, UInt64 player)
        {
            if (player == control.Me)
                return "you";
            return control.PlayerName(player) + (player == control.Host ? " (host)" : "");
        }

        public static String OfferText(IPartyControl control, UInt64 from)
        {
            return $"{control.PlayerName(from)} offers {CharacterName(control.CharacterOf(from))} for your {CharacterName(control.CharacterOf(control.Me))}.";
        }

        public static String CharacterName(Int32 character)
        {
            if (character < 0)
                return "nobody";
            foreach (PLAYER p in FF9StateSystem.Common.FF9.PlayerList)
                if ((Int32)p.info.slot_no == character && !String.IsNullOrEmpty(p.Name))
                    return p.Name;
            return $"character {character}";
        }
    }
}
