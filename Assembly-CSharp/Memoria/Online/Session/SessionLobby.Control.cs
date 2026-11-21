using Assets.Scripts.Common;
using Memoria.Online.Party;
using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Memoria.Online.Session
{
    /// <summary>
    /// Step 2 / M4: in-game control changes (docs/online/step2-m4-party-state.md, section 8). Once the session plays,
    /// the lobby's picks are frozen and characters change hands only through these actions, arbitrated by the host:
    ///   Take     a character in the party nobody plays (the host may Take too: it changes which one it plays)
    ///   Swap     offer another player an exchange of characters; they Accept or Decline (offers expire after 30 s)
    ///   Release  a guest gives their character back to the host and watches (no body, no input)
    ///   Follow   (Step 3 / M2) a character nobody plays follows a player: it joins that player's battles and that
    ///            player's PC commands it there. Default: the host. A follower whose leader leaves follows the host again.
    /// The host always plays one character. Party members nobody plays are driven by the host's game.
    /// If the story removes a played character from the party: the first free character, else (a guest) watching.
    /// If a guest leaves the lobby: their character is free again (the host's game keeps driving it).
    /// Players use these through the field main menu's Online entry (Menu/OnlineMenu.cs, via SessionLobby.Menu.cs).
    ///
    /// Data (Steam lobby, polled like the lobby's):
    ///   lobby data (host):  "assign"    "characterId:accountId|..." (guests; as in the lobby)
    ///                       "hostplays" the host's character
    ///                       "offers"    "fromAccount:toAccount|..." open swap offers
    ///                       "follow"    "characterId:accountId|..." free characters following a guest (the rest follow the host)
    ///   member data:        "ctl"       "seq:verb:arg" a guest's latest request (take c / release / swap c /
    ///                                   accept fromAccount / decline fromAccount / follow (character << 32 | leader account));
    ///                                   the host acts on each new seq once
    /// </summary>
    internal sealed partial class SessionLobby
    {
        private const Double OfferLifetimeMs = 30000;

        private sealed class Offer
        {
            public UInt64 From, To;
            public Double Expires;
        }

        private Int32 _hostPlays = -1;
        private readonly List<Offer> _offers = new List<Offer>();
        private readonly Dictionary<UInt64, String> _lastCtl = new Dictionary<UInt64, String>();
        private readonly HashSet<Int32> _seenInParty = new HashSet<Int32>();
        private Int32 _ctlSeq;
        private String _publishedHostPlays, _publishedOffers, _publishedFollow;
        private readonly Dictionary<Int32, UInt64> _follow = new Dictionary<Int32, UInt64>(); // free character → its leader (guests only; absent = the host)
        private Double _nowMs;

        /// <summary>The character the host plays (the lobby's first one until a control change).</summary>
        public Int32 HostPlays => Phase == SessionPhase.Playing && _hostPlays >= 0 ? _hostPlays : HostCharacter;

        /// <summary>The characters in the party now, in party order (empty off a game).</summary>
        public static List<Int32> CurrentParty()
        {
            List<Int32> ids = new List<Int32>();
            PLAYER[] members = FF9StateSystem.Common?.FF9?.party?.member;
            if (members != null)
                foreach (PLAYER p in members)
                    if (p != null)
                        ids.Add((Int32)p.info.slot_no);
            return ids;
        }

        /// <summary>Who plays this character: a lobby member, or 0 (nobody: driven by the host's game).</summary>
        public UInt64 HolderOf(Int32 character)
        {
            if (character < 0)
                return 0;
            if (character == HostPlays)
                return _lobby.OwnerId;
            return Assignments.TryGetValue(character, out UInt64 member) ? member : 0;
        }

        /// <summary>M2: the player a free character follows (the host unless set otherwise).</summary>
        public UInt64 LeaderOf(Int32 character)
        {
            return _follow.TryGetValue(character, out UInt64 leader) && _lobby.Members.Contains(leader) ? leader : _lobby.OwnerId;
        }

        /// <summary>M2: who commands this character in a battle: its player, or for a free character its leader.</summary>
        public UInt64 ControllerOf(Int32 character)
        {
            UInt64 holder = HolderOf(character);
            return holder != 0 ? holder : LeaderOf(character);
        }

        /// <summary>M2: makes the free character at this party position follow the next player (host → guests → host).</summary>
        public void CycleFollow(Int32 position)
        {
            if (Phase != SessionPhase.Playing)
                return;
            List<Int32> party = CurrentParty();
            if (position < 0 || position >= party.Count || HolderOf(party[position]) != 0)
                return;
            Int32 character = party[position];
            List<UInt64> players = new List<UInt64> { _lobby.OwnerId };
            foreach (UInt64 member in _lobby.Members)
                if (member != _lobby.OwnerId)
                    players.Add(member);
            Int32 index = players.IndexOf(LeaderOf(character));
            UInt64 next = players[(index + 1) % players.Count];
            Request("follow", ((Int64)character << 32) | (Int64)(next & 0xFFFFFFFF));
        }

        private Int32 CharacterOf(UInt64 member)
        {
            if (member == _lobby.OwnerId)
                return HostPlays;
            foreach (KeyValuePair<Int32, UInt64> kv in Assignments)
                if (kv.Value == member)
                    return kv.Key;
            return -1;
        }

        private void SetCharacter(UInt64 member, Int32 character)
        {
            if (member == _lobby.OwnerId)
            {
                if (character >= 0)
                    _hostPlays = character;
                return;
            }
            List<Int32> held = new List<Int32>();
            foreach (KeyValuePair<Int32, UInt64> kv in Assignments)
                if (kv.Value == member)
                    held.Add(kv.Key);
            foreach (Int32 c in held)
                Assignments.Remove(c);
            if (character >= 0)
                Assignments[character] = member;
        }

        private String NameOf(Int32 character)
        {
            if (character < 0)
                return "nobody";
            foreach (PLAYER p in FF9StateSystem.Common.FF9.PlayerList)
                if ((Int32)p.info.slot_no == character && !String.IsNullOrEmpty(p.Name))
                    return p.Name;
            return CharacterName(character);
        }

        // ---- Requests (any PC) -----------------------------------------------------------------------------------

        /// <summary>Acts on the character at this party position: Take if free, Swap offer if another player's, Release if mine.</summary>
        public void ControlSlot(Int32 position)
        {
            if (Phase != SessionPhase.Playing)
                return;
            List<Int32> party = CurrentParty();
            if (position < 0 || position >= party.Count)
                return;
            Int32 character = party[position];
            UInt64 holder = HolderOf(character);
            if (holder == _lobby.LocalId)
                Request("release", -1);
            else if (holder == 0)
                Request("take", character);
            else
                Request("swap", character);
        }

        public void Release() => Request("release", -1);

        /// <summary>Answers the newest swap offer made to this PC.</summary>
        public void AnswerOffer(Boolean accept)
        {
            Offer offer = OfferTo(_lobby.LocalId);
            if (offer != null)
                Request(accept ? "accept" : "decline", (Int64)(offer.From & 0xFFFFFFFF));
        }

        private Offer OfferTo(UInt64 member)
        {
            for (Int32 i = _offers.Count - 1; i >= 0; i--)
                if (_offers[i].To == member)
                    return _offers[i];
            return null;
        }

        private void Request(String verb, Int64 arg)
        {
            if (Phase != SessionPhase.Playing)
                return;
            Log.Message($"[Online] Control: request {verb}{(arg >= 0 ? " " + arg : "")}");
            if (IsHost)
                Act(_lobby.LocalId, verb, arg);
            else
                _lobby.SetMemberData("ctl", $"{++_ctlSeq}:{verb}:{arg}");
        }

        // ---- Host ---------------------------------------------------------------------------------------------

        /// <summary>The host's control step while playing (instead of the lobby's pick arbitration).</summary>
        private void HostControl(Double nowMs)
        {
            _nowMs = nowMs;
            if (_hostPlays < 0)
                _hostPlays = HostCharacter;

            // New requests from guests
            HashSet<UInt64> members = new HashSet<UInt64>(_lobby.Members);
            foreach (UInt64 member in _lobby.Members)
            {
                if (member == _lobby.LocalId)
                    continue;
                String ctl = _lobby.GetMemberData(member, "ctl");
                _lastCtl.TryGetValue(member, out String last);
                if (String.IsNullOrEmpty(ctl) || ctl == last)
                    continue;
                _lastCtl[member] = ctl;
                String[] parts = ctl.Split(':');
                if (parts.Length == 3 && Int64.TryParse(parts[2], out Int64 arg))
                    Act(member, parts[1], arg);
            }

            // Guests who left: their character is free again (the host's game drives it)
            List<Int32> gone = new List<Int32>();
            foreach (KeyValuePair<Int32, UInt64> kv in Assignments)
                if (!members.Contains(kv.Value))
                    gone.Add(kv.Key);
            foreach (Int32 c in gone)
            {
                Log.Message($"[Online] Control: {NameOf(c)} is free again ({SteamNative.Tag(Assignments[c])} left)");
                Assignments.Remove(c);
            }
            _offers.RemoveAll(o => o.Expires < nowMs || !members.Contains(o.From) || !members.Contains(o.To));

            // M2: a follower whose leader left, or who is played now, follows the host again
            foreach (KeyValuePair<Int32, UInt64> kv in new List<KeyValuePair<Int32, UInt64>>(_follow))
            {
                if (members.Contains(kv.Value) && HolderOf(kv.Key) == 0)
                    continue;
                _follow.Remove(kv.Key);
                Log.Message($"[Online] Control: {NameOf(kv.Key)} follows the host again ({(members.Contains(kv.Value) ? "someone plays it now" : $"{SteamNative.Tag(kv.Value)} left")})");
            }

            // The story changed the party: a played character who left it is replaced by a free one
            List<Int32> party = CurrentParty();
            if (party.Count > 0 && PersistenSingleton<EventEngine>.Instance?.gMode == 1)
            {
                foreach (Int32 c in party)
                    _seenInParty.Add(c);
                if (!party.Contains(_hostPlays) && _seenInParty.Contains(_hostPlays))
                {
                    Int32 next = FirstFree(party);
                    Log.Message($"[Online] Control: {NameOf(_hostPlays)} left the party; the host now plays {NameOf(next >= 0 ? next : party[0])}");
                    _hostPlays = next >= 0 ? next : party[0];
                }
                foreach (KeyValuePair<Int32, UInt64> kv in new List<KeyValuePair<Int32, UInt64>>(Assignments))
                {
                    if (party.Contains(kv.Key) || !_seenInParty.Contains(kv.Key))
                        continue; // still there, or not in the party yet (a New Game's party is built by the first field)
                    Int32 next = FirstFree(party);
                    Log.Message($"[Online] Control: {NameOf(kv.Key)} left the party; {SteamNative.Tag(kv.Value)} now {(next >= 0 ? $"plays {NameOf(next)}" : "watches")}");
                    SetCharacter(kv.Value, next);
                }
            }
            PublishControl();
        }

        private Int32 FirstFree(List<Int32> party)
        {
            foreach (Int32 c in party)
                if (HolderOf(c) == 0)
                    return c;
            return -1;
        }

        private void Act(UInt64 member, String verb, Int64 arg)
        {
            String who = member == _lobby.LocalId ? "the host" : SteamNative.Tag(member);
            List<Int32> party = CurrentParty();
            Int32 mine = CharacterOf(member);
            switch (verb)
            {
                case "take":
                {
                    Int32 c = (Int32)arg;
                    if (!party.Contains(c) || HolderOf(c) != 0)
                    {
                        Log.Message($"[Online] Control: {who} can't take {NameOf(c)} (not in the party, or someone plays it)");
                        return;
                    }
                    SetCharacter(member, c);
                    Log.Message($"[Online] Control: {who} takes {NameOf(c)}{(mine >= 0 ? $" (gives up {NameOf(mine)})" : "")}");
                    break;
                }
                case "release":
                {
                    if (member == _lobby.OwnerId || mine < 0)
                        return; // the host always plays a character
                    SetCharacter(member, -1);
                    Log.Message($"[Online] Control: {who} releases {NameOf(mine)} and watches");
                    break;
                }
                case "swap":
                {
                    Int32 c = (Int32)arg;
                    UInt64 holder = HolderOf(c);
                    if (holder == 0)
                    {
                        Act(member, "take", c);
                        return;
                    }
                    if (holder == member || mine < 0)
                        return; // a watcher can only Take a free character
                    _offers.RemoveAll(o => o.From == member);
                    _offers.Add(new Offer { From = member, To = holder, Expires = _nowMs + OfferLifetimeMs });
                    Log.Message($"[Online] Control: {who} offers {(holder == _lobby.LocalId ? "the host" : SteamNative.Tag(holder))} {NameOf(mine)} for {NameOf(c)}");
                    break;
                }
                case "follow":
                {
                    Int32 c = (Int32)(arg >> 32);
                    UInt32 account = (UInt32)(arg & 0xFFFFFFFF);
                    UInt64 leader = MemberByAccount(account);
                    if (!party.Contains(c) || HolderOf(c) != 0 || leader == 0)
                    {
                        Log.Message($"[Online] Control: {who} can't make {NameOf(c)} follow account {account} (not a free party member, or no such player)");
                        return;
                    }
                    if (leader == _lobby.OwnerId)
                        _follow.Remove(c);
                    else
                        _follow[c] = leader;
                    Log.Message($"[Online] Control: {NameOf(c)} now follows {(leader == _lobby.OwnerId ? "the host" : SteamNative.Tag(leader))} (from the next battle; set by {who})");
                    break;
                }
                case "accept":
                case "decline":
                {
                    Offer offer = _offers.Find(o => o.To == member && (o.From & 0xFFFFFFFF) == (UInt64)arg);
                    if (offer == null)
                        return;
                    _offers.Remove(offer);
                    if (verb == "decline")
                    {
                        Log.Message($"[Online] Control: {who} declines the swap");
                        return;
                    }
                    Int32 a = CharacterOf(offer.From), b = CharacterOf(offer.To);
                    if (a < 0 || b < 0)
                        return;
                    SetCharacter(offer.From, -1);
                    SetCharacter(offer.To, -1);
                    SetCharacter(offer.From, b);
                    SetCharacter(offer.To, a);
                    Log.Message($"[Online] Control: swap done: {(offer.From == _lobby.LocalId ? "the host" : SteamNative.Tag(offer.From))} plays {NameOf(b)}, {(offer.To == _lobby.LocalId ? "the host" : SteamNative.Tag(offer.To))} plays {NameOf(a)}");
                    break;
                }
            }
            PublishControl();
        }

        private void PublishControl()
        {
            StringBuilder assign = new StringBuilder();
            foreach (KeyValuePair<Int32, UInt64> kv in Assignments)
                assign.Append($"{kv.Key}:{kv.Value & 0xFFFFFFFF}|");
            String value = assign.ToString();
            if (value != _publishedAssign)
            {
                _publishedAssign = value;
                _lobby.SetData("assign", value);
            }
            String hostPlays = _hostPlays.ToString();
            if (hostPlays != _publishedHostPlays)
            {
                _publishedHostPlays = hostPlays;
                _lobby.SetData("hostplays", hostPlays);
            }
            StringBuilder offers = new StringBuilder();
            foreach (Offer o in _offers)
                offers.Append($"{o.From & 0xFFFFFFFF}:{o.To & 0xFFFFFFFF}|");
            String offerValue = offers.ToString();
            if (offerValue != _publishedOffers)
            {
                _publishedOffers = offerValue;
                _lobby.SetData("offers", offerValue);
            }
            StringBuilder follow = new StringBuilder();
            foreach (KeyValuePair<Int32, UInt64> kv in _follow)
                follow.Append($"{kv.Key}:{kv.Value & 0xFFFFFFFF}|");
            String followValue = follow.ToString();
            if (followValue != _publishedFollow)
            {
                _publishedFollow = followValue;
                _lobby.SetData("follow", followValue);
            }
        }

        // ---- Guest ------------------------------------------------------------------------------------------------

        private void ReadControlData()
        {
            if (Int32.TryParse(_lobby.GetData("hostplays"), out Int32 hostPlays))
                _hostPlays = hostPlays;
            _follow.Clear();
            foreach (String part in _lobby.GetData("follow").Split('|'))
            {
                String[] fields = part.Split(':');
                if (fields.Length == 2 && Int32.TryParse(fields[0], out Int32 character) && UInt32.TryParse(fields[1], out UInt32 account))
                {
                    UInt64 leader = MemberByAccount(account);
                    if (leader != 0)
                        _follow[character] = leader;
                }
            }
            _offers.Clear();
            foreach (String part in _lobby.GetData("offers").Split('|'))
            {
                String[] fields = part.Split(':');
                if (fields.Length != 2 || !UInt32.TryParse(fields[0], out UInt32 from) || !UInt32.TryParse(fields[1], out UInt32 to))
                    continue;
                UInt64 fromMember = MemberByAccount(from), toMember = MemberByAccount(to);
                if (fromMember != 0 && toMember != 0)
                    _offers.Add(new Offer { From = fromMember, To = toMember });
            }
        }

        private UInt64 MemberByAccount(UInt32 account)
        {
            foreach (UInt64 member in _lobby.Members)
                if ((member & 0xFFFFFFFF) == account)
                    return member;
            return 0;
        }

        private void ResetControl()
        {
            _hostPlays = -1;
            _offers.Clear();
            _follow.Clear();
            _publishedFollow = null;
            _lastCtl.Clear();
            _seenInParty.Clear();
            _publishedHostPlays = _publishedOffers = null;
        }
    }
}
