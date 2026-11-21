using Memoria.Prime;
using System;
using System.Collections.Generic;

namespace Memoria.Online.Menus
{
    /// <summary>
    /// The party-control side of an online session, as the Online menu sees it (SessionLobby implements it; see
    /// docs/online/step2-m4-party-state.md section 8 and step3-m2-results.md for the rules). Players are Steam ids;
    /// 0 means nobody. Every action is a request: the host decides, and the menu shows the result when it arrives.
    /// </summary>
    internal interface IPartyControl
    {
        Boolean IsPlaying { get; }
        Boolean IsHost { get; }
        /// <summary>Guest: on the host's field (M5).</summary>
        Boolean WithHost { get; }
        UInt64 Me { get; }
        UInt64 Host { get; }
        /// <summary>The host first, then the guests.</summary>
        IList<UInt64> Players { get; }
        String PlayerName(UInt64 player);
        /// <summary>Who plays this character (0: nobody, the host's game drives it).</summary>
        UInt64 HolderOf(Int32 character);
        /// <summary>The player a free character follows into battle (the host unless set otherwise).</summary>
        UInt64 LeaderOf(Int32 character);
        /// <summary>The character this player plays (-1: watching).</summary>
        Int32 CharacterOf(UInt64 player);
        /// <summary>This PC has a swap offer open to that player.</summary>
        Boolean OfferSent(UInt64 to);
        /// <summary>The player whose swap offer to this PC is open (0: none).</summary>
        UInt64 OfferFrom { get; }

        void Take(Int32 character);
        void OfferSwap(Int32 character);
        void Release();
        void Follow(Int32 character, UInt64 leader);
        void AnswerOffer(Boolean accept);
        void JoinHost();
    }

    /// <summary>
    /// Debug only (MenuTour): a pretend session with three other players, to see every state of the menu on one PC.
    /// Its actions only change this object and log; no Steam, no host.
    /// </summary>
    internal sealed class PreviewPartyControl : IPartyControl
    {
        private const UInt64 MeId = 1, HostId = 2, ThirdId = 3, FourthId = 4;
        private readonly Dictionary<Int32, UInt64> _holders = new Dictionary<Int32, UInt64>();
        private readonly Dictionary<Int32, UInt64> _leaders = new Dictionary<Int32, UInt64>();
        private UInt64 _offerFrom;
        private readonly HashSet<UInt64> _offersSent = new HashSet<UInt64>();

        /// <summary>Party characters in order: the host plays the first, this PC the second, the 3rd player the fourth; the third is free.</summary>
        public PreviewPartyControl(IList<Int32> party, Boolean offerToMe)
        {
            if (party.Count > 0) _holders[party[0]] = HostId;
            if (party.Count > 1) _holders[party[1]] = MeId;
            if (party.Count > 3) _holders[party[3]] = ThirdId;
            if (party.Count > 2) _leaders[party[2]] = ThirdId;
            if (offerToMe)
                _offerFrom = ThirdId;
        }

        public Boolean IsPlaying => true;
        public Boolean IsHost => false;
        public Boolean WithHost => false;
        public UInt64 Me => MeId;
        public UInt64 Host => HostId;
        public IList<UInt64> Players => new[] { HostId, MeId, ThirdId, FourthId };

        public String PlayerName(UInt64 player)
        {
            switch (player)
            {
                case MeId: return "Player 2";
                case HostId: return "Player 1";
                case ThirdId: return "Player 3";
                default: return "Player 4";
            }
        }

        public UInt64 HolderOf(Int32 character) => _holders.TryGetValue(character, out UInt64 p) ? p : 0;
        public UInt64 LeaderOf(Int32 character) => _leaders.TryGetValue(character, out UInt64 p) ? p : HostId;

        public Int32 CharacterOf(UInt64 player)
        {
            foreach (KeyValuePair<Int32, UInt64> kv in _holders)
                if (kv.Value == player)
                    return kv.Key;
            return -1;
        }

        public Boolean OfferSent(UInt64 to) => _offersSent.Contains(to);
        public UInt64 OfferFrom => _offerFrom;

        public void Take(Int32 character)
        {
            Log.Message($"[Online] Menu preview: take {character}");
            Int32 mine = CharacterOf(MeId);
            if (mine >= 0)
                _holders.Remove(mine);
            _holders[character] = MeId;
            _leaders.Remove(character);
        }

        public void OfferSwap(Int32 character)
        {
            Log.Message($"[Online] Menu preview: swap offer for {character}");
            _offersSent.Add(HolderOf(character));
        }

        public void Release()
        {
            Log.Message("[Online] Menu preview: release");
            Int32 mine = CharacterOf(MeId);
            if (mine >= 0)
                _holders.Remove(mine);
        }

        public void Follow(Int32 character, UInt64 leader)
        {
            Log.Message($"[Online] Menu preview: {character} follows {PlayerName(leader)}");
            _leaders[character] = leader;
        }

        public void AnswerOffer(Boolean accept)
        {
            Log.Message($"[Online] Menu preview: {(accept ? "accept" : "decline")} the offer");
            if (accept)
            {
                Int32 mine = CharacterOf(MeId), theirs = CharacterOf(_offerFrom);
                if (mine >= 0 && theirs >= 0)
                {
                    _holders[mine] = _offerFrom;
                    _holders[theirs] = MeId;
                }
            }
            _offerFrom = 0;
        }

        public void JoinHost() => Log.Message("[Online] Menu preview: join host");
    }
}
