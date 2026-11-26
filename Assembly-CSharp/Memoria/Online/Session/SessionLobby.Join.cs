using Assets.Scripts.Common;
using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Text;

namespace Memoria.Online.Session
{
    /// <summary>
    /// Joining a game in progress (docs/online/online-menu.md, "Joining a game in progress"). A player who enters the
    /// lobby after the host pressed Start sees the party as it is now and who plays whom ("liveparty", "assign",
    /// "hostplays"), picks a character nobody plays (or none, to watch) and chooses Join the game:
    ///   member data (joiner): "join" a new value per request
    ///   lobby data (host):    "liveparty" "position:characterId:name|..." the party now (the "party" key stays the
    ///                                     lobby's, which <see cref="HostCharacter"/> reads)
    ///                         "joinwait"  why a join waits (empty: it doesn't)
    /// The host answers once its game can be captured whole: on the field with control, or on the world map, with no
    /// screen change, battle, shared cutscene or menu. It gives the joiner its pick if that character is in the party
    /// and nobody plays it, captures its game (<see cref="GameSnapshot.CaptureCurrent"/>, as a resync does) and sends
    /// it with a Start, as for Continue. The joiner loads it and plays from the host's spot. Everything after that is
    /// the same as for a player who was there at Start.
    /// </summary>
    internal sealed partial class SessionLobby
    {
        private const Double JoinWaitLogMs = 10000;

        // Joiner
        private Boolean _joinAsked;
        // Host
        private readonly Dictionary<UInt64, String> _joinServed = new Dictionary<UInt64, String>();
        private readonly Dictionary<UInt64, Double> _joinWaitLogged = new Dictionary<UInt64, Double>();
        private String _publishedLiveParty, _publishedJoinWait;

        /// <summary>Joiner: Join the game was chosen; the host's game hasn't come yet.</summary>
        public Boolean JoinAsked => _joinAsked;

        /// <summary>Joiner: why the host's game is late (empty: it's on its way).</summary>
        public String JoinWait => _lobby.GetData("joinwait");

        /// <summary>Joiner (a game in progress): asks the host for its game, with this PC's pick.</summary>
        public void AskToJoin()
        {
            if (!IsGuest || Phase != SessionPhase.Gathering || !_startedBeforeJoin || _joinAsked)
                return;
            _joinAsked = true;
            _lobby.SetMemberData("join", DateTime.UtcNow.Ticks.ToString());
            Status = "Waiting for the host's game...";
            Log.Message($"[Online] Session: asked to join the game in progress ({(_myPick >= 0 ? $"wants {CharacterName(_myPick)} (character {_myPick})" : "watching")})");
        }

        /// <summary>Joiner: Cancel the join request (before the host's game came).</summary>
        public void CancelJoin()
        {
            if (!_joinAsked || Phase != SessionPhase.Gathering)
                return;
            _joinAsked = false;
            _lobby.SetMemberData("join", "");
            Status = String.Empty;
            Log.Message("[Online] Session: join request withdrawn");
        }

        // ---- Host ---------------------------------------------------------------------------------------------

        /// <summary>Host, playing, every poll (HostControl): publishes the party now and answers join requests.</summary>
        private void HostServeJoins(Double nowMs)
        {
            PublishLiveParty();
            String waitReason = String.Empty;
            foreach (UInt64 member in _lobby.Members)
            {
                if (member == _lobby.LocalId)
                    continue;
                String join = _lobby.GetMemberData(member, "join");
                if (String.IsNullOrEmpty(join) || _joinServed.TryGetValue(member, out String served) && served == join)
                    continue;
                if (_lobby.GetMemberData(member, "where") != "title" || !_isConnected(member))
                    continue; // still connecting, or not at the title screen any more
                String busy = HostBusyForJoin();
                if (busy != null)
                {
                    waitReason = $"the host is in {busy}: you'll join once they're back on the field or the world map";
                    if (!_joinWaitLogged.TryGetValue(member, out Double logged) || nowMs - logged > JoinWaitLogMs)
                    {
                        _joinWaitLogged[member] = nowMs;
                        Log.Message($"[Online] Session: {SteamNative.Tag(member)} asks to join; waiting ({busy})");
                    }
                    continue;
                }
                _joinServed[member] = join;
                _joinWaitLogged.Remove(member);
                ServeJoin(member);
            }
            if (waitReason != _publishedJoinWait)
            {
                _publishedJoinWait = waitReason;
                _lobby.SetData("joinwait", waitReason);
            }
        }

        /// <summary>Null when the host's game can be sent whole now, else what it's in ("a battle"...).</summary>
        private static String HostBusyForJoin()
        {
            UIManager ui = PersistenSingleton<UIManager>.Instance;
            if (ui == null || PersistenSingleton<SceneDirector>.Instance.IsFading)
                return "a screen change";
            if (SceneSync.Active)
                return "a shared cutscene";
            if (Battle.BattleEntry.PartyAltered)
                return "a battle";
            switch (ui.State)
            {
                case UIManager.UIState.FieldHUD:
                    return IsPlayableOnField() ? null : "a cutscene";
                case UIManager.UIState.WorldHUD:
                    return null;
                case UIManager.UIState.BattleHUD:
                case UIManager.UIState.BattleResult:
                    return "a battle";
                default:
                    return "a menu";
            }
        }

        private void ServeJoin(UInt64 member)
        {
            String who = SteamNative.Tag(member);
            Int32 pick = ParsePick(_lobby.GetMemberData(member, "pick"));
            List<Int32> party = CurrentParty();
            SetCharacter(member, -1); // nothing held from an earlier visit
            String plays;
            if (pick >= 0 && party.Contains(pick) && HolderOf(pick) == 0)
            {
                SetCharacter(member, pick);
                plays = $"plays {NameOf(pick)} (character {pick})";
            }
            else
            {
                plays = pick >= 0 ? $"watches ({NameOf(pick)} isn't free)" : "watches";
            }
            _lastCtl.Remove(member); // its requests count from 1 again
            PublishControl();
            Byte[] blob = GameSnapshot.CaptureCurrent(out _);
            UInt32 crc = GameSnapshot.Crc32(blob, 0, blob.Length);
            UInt32 transferId = _blobs.Send(member, blob);
            SendControl(member, MsgStart, ModeContinue, transferId, crc);
            Log.Message($"[Online] Session: {who} joins the game in progress and {plays}; the host's game sent from {(PersistenSingleton<UIManager>.Instance.State == UIManager.UIState.WorldHUD ? "the world map" : $"field {FF9StateSystem.Common.FF9.fldMapNo}")} ({blob.Length} bytes)");
        }

        private void PublishLiveParty()
        {
            StringBuilder party = new StringBuilder();
            List<Int32> ids = CurrentParty();
            for (Int32 i = 0; i < ids.Count; i++)
                party.Append($"{i}:{ids[i]}:{NameOf(ids[i]).Replace("|", " ").Replace(":", " ")}|");
            String value = party.ToString();
            if (value == _publishedLiveParty)
                return;
            _publishedLiveParty = value;
            _lobby.SetData("liveparty", value);
        }

        // ---- Joiner -------------------------------------------------------------------------------------------

        /// <summary>Joiner: the party now (instead of the lobby's), or false if the host hasn't published it.</summary>
        private Boolean ReadLiveParty()
        {
            String value = _lobby.GetData("liveparty");
            if (String.IsNullOrEmpty(value))
                return false;
            Party.Clear();
            foreach (String part in value.Split('|'))
            {
                String[] fields = part.Split(':');
                if (fields.Length == 3 && Int32.TryParse(fields[0], out Int32 index) && Int32.TryParse(fields[1], out Int32 character))
                    Party.Add(new PartyEntry { Index = index, CharacterId = character, Name = fields[2] });
            }
            return true;
        }

        private void ResetJoin()
        {
            _joinAsked = false;
            _joinServed.Clear();
            _joinWaitLogged.Clear();
            _publishedLiveParty = null;
            _publishedJoinWait = null;
        }
    }
}
