using Memoria.Online.Menus;
using System;
using System.Collections.Generic;

namespace Memoria.Online.Session
{
    /// <summary>
    /// What the in-game Online menu (Memoria/Online/Menu/OnlineMenu.cs) reads and does. The actions are the control
    /// requests of SessionLobby.Control.cs, by character instead of party position; nothing here changes their rules.
    /// </summary>
    internal sealed partial class SessionLobby : IPartyControl
    {
        /// <summary>The session object (there is one per game; null before Steam is ready).</summary>
        public static SessionLobby Current { get; private set; }

        Boolean IPartyControl.IsPlaying => Phase == SessionPhase.Playing;
        Boolean IPartyControl.IsHost => IsHost;
        Boolean IPartyControl.WithHost => WithHost;
        UInt64 IPartyControl.Me => _lobby.LocalId;
        UInt64 IPartyControl.Host => _lobby.OwnerId;

        IList<UInt64> IPartyControl.Players
        {
            get
            {
                List<UInt64> players = new List<UInt64> { _lobby.OwnerId };
                foreach (UInt64 member in _lobby.Members)
                    if (member != _lobby.OwnerId)
                        players.Add(member);
                return players;
            }
        }

        String IPartyControl.PlayerName(UInt64 player) => SteamNative.GetName(player);
        UInt64 IPartyControl.HolderOf(Int32 character) => HolderOf(character);
        UInt64 IPartyControl.LeaderOf(Int32 character) => LeaderOf(character);
        Int32 IPartyControl.CharacterOf(UInt64 player) => CharacterOf(player);
        Boolean IPartyControl.OfferSent(UInt64 to) => _offers.Exists(o => o.From == _lobby.LocalId && o.To == to);

        UInt64 IPartyControl.OfferFrom
        {
            get
            {
                Offer offer = OfferTo(_lobby.LocalId);
                return offer != null ? offer.From : 0;
            }
        }

        void IPartyControl.Take(Int32 character) => Request("take", character);
        void IPartyControl.OfferSwap(Int32 character) => Request("swap", character);
        void IPartyControl.Release() => Release();
        void IPartyControl.AnswerOffer(Boolean accept) => AnswerOffer(accept);
        void IPartyControl.JoinHost() => JoinHost();

        /// <summary>MenuTour (debug): starts the session as soon as the host could (F6 at the title).</summary>
        internal Boolean TourTryStart()
        {
            if (Phase == SessionPhase.Playing)
                return true;
            if (!IsHost || Phase != SessionPhase.Gathering || StartBlocker() != null)
                return false;
            Start();
            return Phase == SessionPhase.Playing;
        }

        void IPartyControl.Follow(Int32 character, UInt64 leader)
        {
            Request("follow", ((Int64)character << 32) | (Int64)(leader & 0xFFFFFFFF));
        }
    }
}
