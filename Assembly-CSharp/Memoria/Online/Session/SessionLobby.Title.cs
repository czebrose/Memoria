using System;
using System.Collections.Generic;

namespace Memoria.Online.Session
{
    /// <summary>
    /// What the title screen's Online button and lobby window (Memoria/Online/Menu/TitleOnline.cs) read and do. These
    /// are the same actions as the Ctrl+Shift hotkeys and the old debug lobby panel; nothing here changes their rules.
    /// </summary>
    internal sealed partial class SessionLobby
    {
        public SteamLobby.LobbyState LobbyState => _lobby.State;
        public String LobbyEvent => _lobby.LastEvent;
        public Boolean StartedBeforeJoin => _startedBeforeJoin;
        public UInt64 OwnerId => _lobby.OwnerId;
        public UInt64 LocalId => _lobby.LocalId;
        public IList<UInt64> Members => _lobby.Members;
        public Int32 MyPick => _myPick;

        /// <summary>The host's game choices (New Game, then the saves, newest first) and the chosen one.</summary>
        public List<String> ChoiceLabels => _choices.ConvertAll(c => c.Label);
        public Int32 ChoiceIndex => _choiceIndex;
        public Boolean ChoiceLoading => _choiceLoading;

        public void HostLobby() => _lobby.Create();
        public void JoinLobby(UInt64 lobbyId) => _lobby.Join(lobbyId);
        public void LeaveLobby() => _lobby.Leave();
        public void InviteFriends() => _lobby.OpenInviteOverlay();
        public List<KeyValuePair<UInt64, UInt64>> FriendLobbies() => _lobby.FriendLobbies();

        public String PlayerName(UInt64 member) => SteamNative.GetName(member);
        public Boolean IsConnectedTo(UInt64 member) => member == _lobby.LocalId || _isConnected(member);

        /// <summary>"title" / "game" as that member last published it (empty: not yet).</summary>
        public String WhereOf(UInt64 member) => member == _lobby.LocalId ? (AtTitle ? "title" : "game") : _lobby.GetMemberData(member, "where");

        /// <summary>Host, gathering: plays this game (as F5 cycles to it).</summary>
        public void Choose(Int32 index)
        {
            if (!IsHost || Phase != SessionPhase.Gathering || _choiceLoading || index < 0 || index >= _choices.Count || index == _choiceIndex)
                return;
            _choiceIndex = index;
            ApplyChoice();
        }

        /// <summary>The lobby member a party character goes to (0: the host's game, none picked it).</summary>
        public UInt64 PickedBy(Int32 character)
        {
            if (Party.Count > 0 && Party[0].CharacterId == character)
                return _lobby.OwnerId;
            return Assignments.TryGetValue(character, out UInt64 member) ? member : 0;
        }
    }
}
