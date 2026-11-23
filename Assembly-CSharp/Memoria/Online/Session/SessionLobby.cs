using Assets.Scripts.Common;
using Memoria.Data;
using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Memoria.Online.Session
{
    public enum SessionPhase
    {
        /// <summary>Not in a lobby.</summary>
        Idle,
        /// <summary>In a lobby at the title screen: the host picks the game, guests pick characters.</summary>
        Gathering,
        /// <summary>Guest: the host pressed Start; waiting for the game snapshot.</summary>
        Starting,
        /// <summary>Everyone is in the host's game.</summary>
        Playing,
    }

    public sealed class PartyEntry
    {
        public Int32 Index;        // position in the party list, 0 = the host's character
        public Int32 CharacterId;  // CharacterId: what players pick
        public String Name;
    }

    /// <summary>
    /// Step 2 / M2: the title-screen lobby. The host picks New Game or a save; that game's party is published; each guest
    /// picks a free character (the first one is always the host's; characters nobody picks are the host's); the host
    /// presses Start and everyone enters the host's game.
    ///
    /// Picks are by character, not by party slot: party order differs between a declared list and what a script builds,
    /// and slots shift when the story changes the party.
    ///
    /// The party shown for New Game: the mod's declared list (<see cref="NewGamePartyFile"/>) if it has one, since a
    /// mod's opening script may build a party the title screen can't know; otherwise the game's initial party.
    ///
    /// State lives in Steam lobby data so it shows before P2P is up:
    ///   lobby data (host only):  "game"   label of the chosen game
    ///                            "party"  "position:characterId:name|..."
    ///                            "assign" "characterId:accountId|..."   the host's arbitration of the picks
    ///                            "started" "1" once the session has started (no joining mid-game in this step)
    ///   member data (each user): "pick"   character id the user wants (-1 = none)
    ///                            "where"  "title" / "game"
    /// Start goes over P2P (control channel): MsgStart, then for Continue the snapshot via <see cref="BlobTransfer"/>.
    /// New Game: everyone runs the game's own New Game; the host resyncs everyone (MsgResync + snapshot) the first time
    /// it has control on a field.
    /// </summary>
    internal sealed partial class SessionLobby
    {
        /// <summary>
        /// Optional, shipped by a mod in its StreamingAssets/Data/Online/: the New Game party, one character per line,
        /// "characterId[, display name]", first line = the host's character; '#' starts a comment.
        /// </summary>
        public const String NewGamePartyFile = "Data/Online/NewGameParty.txt";

        public const Byte MsgStart = 4;
        public const Byte MsgResync = 5;
        private const Byte ModeNewGame = 0;
        private const Byte ModeContinue = 1;

        private sealed class GameChoice
        {
            public Boolean NewGame;
            public Int32 Slot, Save;
            public String Label;
            public Double Timestamp;
        }

        private sealed class PendingLoad
        {
            public UInt32 TransferId;
            public UInt32 Crc;
            public Boolean IsResync;
            /// <summary>M5 meeting resync: the field the guest was on when it asked; it keeps its spot (-1: no).</summary>
            public Int32 KeepSpotField = -1;
        }

        private readonly SteamLobby _lobby;
        private readonly SteamTransport _transport;
        private readonly BlobTransfer _blobs;
        private readonly Func<UInt64, Boolean> _isConnected;

        public SessionPhase Phase { get; private set; } = SessionPhase.Idle;
        public readonly List<PartyEntry> Party = new List<PartyEntry>();
        /// <summary>Character id → lobby member. The host's own characters are implicit (the first one and every unassigned one).</summary>
        public readonly Dictionary<Int32, UInt64> Assignments = new Dictionary<Int32, UInt64>();
        public String GameLabel { get; private set; } = String.Empty;
        public String Status { get; private set; } = String.Empty;

        // Host
        private readonly List<GameChoice> _choices = new List<GameChoice>();
        private Int32 _choiceIndex = -1;
        private Byte[] _continueBlob;
        private Boolean _choiceLoading;
        private Boolean _awaitingNewGameResync;
        private FieldMap _fieldMapAtStart; // the resync waits for a field map loaded after Start (the old one lingers)
        private String _publishedAssign;

        // Guest
        private PendingLoad _pending;
        private readonly Dictionary<UInt32, Byte[]> _receivedBlobs = new Dictionary<UInt32, Byte[]>();
        private Int32 _myPick = -1;
        private Boolean _startedBeforeJoin;

        private Double _nextPoll;
        private String _where;
        private Boolean _buildPublished;
        private readonly Dictionary<UInt64, Double> _buildSeenAt = new Dictionary<UInt64, Double>(); // peer → first seen (for "no build id")
        private readonly HashSet<UInt64> _buildChecked = new HashSet<UInt64>();
        private const Double NoBuildIdAfterMs = 5000;

        public SessionLobby(SteamLobby lobby, SteamTransport transport, BlobTransfer blobs, Func<UInt64, Boolean> isConnected)
        {
            _lobby = lobby;
            _transport = transport;
            _blobs = blobs;
            _isConnected = isConnected;
            Current = this; // the Online menu (SessionLobby.Menu.cs)
        }

        public Boolean IsHost => _lobby.IsHost;
        public Boolean IsGuest => _lobby.State == SteamLobby.LobbyState.InLobby && !_lobby.IsHost;

        /// <summary>The character this player controls (CharacterId), or -1 if none was assigned.</summary>
        public Int32 MyCharacter
        {
            get
            {
                if (IsHost)
                    return HostPlays;
                foreach (KeyValuePair<Int32, UInt64> kv in Assignments)
                    if (kv.Value == _lobby.LocalId)
                        return kv.Key;
                return -1;
            }
        }

        /// <summary>The host's character (CharacterId), or -1.</summary>
        public Int32 HostCharacter => Party.Count > 0 ? Party[0].CharacterId : -1;

        private static Boolean AtTitle => PersistenSingleton<UIManager>.Instance.State == UIManager.UIState.Title;

        // ---- Per frame ---------------------------------------------------------------------------------------------

        public void Update(Double nowMs)
        {
            if (_lobby.State != SteamLobby.LobbyState.InLobby)
            {
                if (Phase != SessionPhase.Idle)
                    Reset("left the lobby");
                SessionSettings.Update(Phase, IsHost, _lobby); // M2: own settings back
                return;
            }
            SessionSettings.Update(Phase, IsHost, _lobby); // Step 3 / M2: the host's battle settings for the session
            if (Phase == SessionPhase.Idle)
            {
                Phase = SessionPhase.Gathering;
                _startedBeforeJoin = !IsHost && _lobby.GetData("started") == "1";
                if (!IsHost)
                    _lobby.SetMemberData("ctl", ""); // no request from an earlier session
                Log.Message($"[Online] Session: {(IsHost ? "hosting" : "joined")} a lobby{(_startedBeforeJoin ? " whose session has already started (joining mid-game isn't supported yet)" : "")}");
                if (IsHost)
                    RefreshChoices();
            }

            if (nowMs >= _nextPoll)
            {
                _nextPoll = nowMs + 250;
                String where = AtTitle ? "title" : "game";
                if (where != _where)
                {
                    _where = where;
                    _lobby.SetMemberData("where", where);
                }
                CheckBuilds(nowMs);
                if (IsHost && Phase == SessionPhase.Playing)
                    HostControl(nowMs); // M4: in-game control changes (SessionLobby.Control.cs)
                else if (IsHost)
                    HostArbitrate();
                else
                    ReadHostData();
            }

            if (IsHost && _awaitingNewGameResync && Phase == SessionPhase.Playing && IsPlayableOnField()
                && !ReferenceEquals(PersistenSingleton<EventEngine>.Instance.fieldmap, _fieldMapAtStart) && !PersistenSingleton<SceneDirector>.Instance.IsFading)
                SendResync();
            if (!IsHost && _pending != null)
                TryLoadPending();
            UpdateMeet(nowMs); // M5: free roaming, meeting resyncs (SessionLobby.Meet.cs)
        }

        // ---- Host: game choice, party, picks ---------------------------------------------------------------------

        private void RefreshChoices()
        {
            _choices.Clear();
            _choices.Add(new GameChoice { NewGame = true, Label = "New Game" });
            _choiceIndex = 0;
            GameSnapshot.GetLatestSlot((slot, save) =>
            {
                if (slot < 0)
                {
                    ApplyChoice();
                    return;
                }
                FF9StateSystem.Serializer.Storage.LoadSlotPreview(slot, (slotId, previews) =>
                {
                    if (previews != null)
                    {
                        for (Int32 i = 0; i < previews.Count; i++)
                        {
                            SharedDataPreviewSlot preview = previews[i];
                            if (preview == null || !preview.HasData || preview.IsPreviewCorrupted)
                                continue;
                            String leader = preview.CharacterInfoList != null && preview.CharacterInfoList.Count > 0 && preview.CharacterInfoList[0] != null ? $"{preview.CharacterInfoList[0].Name} Lv{preview.CharacterInfoList[0].Level}, " : "";
                            TimeSpan played = TimeSpan.FromSeconds(preview.PlayDuration);
                            _choices.Add(new GameChoice
                            {
                                Slot = slotId,
                                Save = i,
                                Timestamp = preview.Timestamp,
                                Label = $"Continue {slotId + 1}-{i + 1}: {preview.Location} ({leader}{(Int32)played.TotalHours}:{played.Minutes:D2})",
                            });
                        }
                    }
                    _choices.Sort((a, b) => a.NewGame ? -1 : b.NewGame ? 1 : b.Timestamp.CompareTo(a.Timestamp));
                    _choiceIndex = _choices.Count > 1 ? 1 : 0; // the latest save if there is one
                    ApplyChoice();
                });
            });
        }

        public void NextChoice()
        {
            if (!IsHost || Phase != SessionPhase.Gathering || _choices.Count == 0 || _choiceLoading)
                return;
            _choiceIndex = (_choiceIndex + 1) % _choices.Count;
            ApplyChoice();
        }

        /// <summary>Puts the chosen game in the host's state (at the title screen) to read its party.</summary>
        private void ApplyChoice()
        {
            if (_choiceIndex < 0 || _choiceIndex >= _choices.Count || !AtTitle)
                return;
            GameChoice choice = _choices[_choiceIndex];
            _continueBlob = null;
            if (choice.NewGame)
            {
                // What New Game does first; Continue would replace all of it (settings included) by the save's own
                FF9StateSystem.ReInitStateSystem();
                PublishGame(choice, LoadDeclaredNewGameParty());
                return;
            }
            _choiceLoading = true;
            GameSnapshot.CaptureSlot(choice.Slot, choice.Save, (blob, tree, error) =>
            {
                _choiceLoading = false;
                if (blob == null)
                {
                    Status = $"Can't read that save: {error}";
                    Log.Warning($"[Online] Session: {Status}");
                    return;
                }
                _continueBlob = blob;
                (FF9StateSystem.Serializer.Parser as JsonParser)?.ParseToFF9StateSystem(GameSnapshot.FromBytes(blob));
                PublishGame(choice, null);
            });
        }

        /// <summary>The mod's declared New Game party (see <see cref="NewGamePartyFile"/>), or null if it has none.</summary>
        private static List<PartyEntry> LoadDeclaredNewGameParty()
        {
            String text = AssetManager.LoadString(NewGamePartyFile, true);
            if (String.IsNullOrEmpty(text))
                return null;
            List<PartyEntry> party = new List<PartyEntry>();
            foreach (String rawLine in text.Split('\n'))
            {
                String line = rawLine;
                Int32 comment = line.IndexOf('#');
                if (comment >= 0)
                    line = line.Substring(0, comment);
                line = line.Trim();
                if (line.Length == 0)
                    continue;
                String[] fields = line.Split(',');
                if (!Int32.TryParse(fields[0].Trim(), out Int32 id))
                {
                    Log.Warning($"[Online] {NewGamePartyFile}: '{rawLine.Trim()}' isn't 'characterId[, name]'");
                    continue;
                }
                String name = fields.Length > 1 ? fields[1].Trim() : null;
                if (String.IsNullOrEmpty(name))
                    name = FF9StateSystem.Common.FF9.GetPlayer((CharacterId)id)?.Name ?? $"character {id}";
                party.Add(new PartyEntry { Index = party.Count, CharacterId = id, Name = name });
            }
            Log.Message($"[Online] Session: New Game party declared by the mod ({NewGamePartyFile}): {party.Count} character(s)");
            return party.Count > 0 ? party : null;
        }

        /// <param name="declared">For New Game: the mod's declared party, or null to use the game's current party.</param>
        private void PublishGame(GameChoice choice, List<PartyEntry> declared)
        {
            Party.Clear();
            if (declared != null)
            {
                Party.AddRange(declared);
            }
            else
            {
                PARTY_DATA party = FF9StateSystem.Common.FF9.party;
                for (Int32 i = 0; party != null && party.member != null && i < party.member.Length; i++)
                {
                    PLAYER player = party.member[i];
                    if (player != null)
                        Party.Add(new PartyEntry { Index = Party.Count, CharacterId = (Int32)player.info.slot_no, Name = player.Name });
                }
            }
            GameLabel = choice.Label;
            StringBuilder partyData = new StringBuilder();
            foreach (PartyEntry entry in Party)
                partyData.Append($"{entry.Index}:{entry.CharacterId}:{entry.Name.Replace("|", "").Replace(":", "")}|");
            _lobby.SetData("game", GameLabel);
            _lobby.SetData("party", partyData.ToString());
            Status = String.Empty;
            Log.Message($"[Online] Session: game {GameLabel}, party {partyData}");
            HostArbitrate();
        }

        /// <summary>First come, first served, as the host sees the picks; a pick of a taken, unknown or the host's character is ignored.</summary>
        private void HostArbitrate()
        {
            HashSet<UInt64> members = new HashSet<UInt64>(_lobby.Members);
            List<Int32> stale = new List<Int32>();
            foreach (KeyValuePair<Int32, UInt64> kv in Assignments)
            {
                Int32 pick = members.Contains(kv.Value) ? ParsePick(_lobby.GetMemberData(kv.Value, "pick")) : -1;
                if (pick != kv.Key || !IsPickable(kv.Key))
                    stale.Add(kv.Key);
            }
            foreach (Int32 character in stale)
                Assignments.Remove(character);
            foreach (UInt64 member in _lobby.Members)
            {
                if (member == _lobby.LocalId || Assignments.ContainsValue(member))
                    continue;
                Int32 pick = ParsePick(_lobby.GetMemberData(member, "pick"));
                if (IsPickable(pick) && !Assignments.ContainsKey(pick))
                {
                    Assignments[pick] = member;
                    Log.Message($"[Online] Session: {CharacterName(pick)} (character {pick}) goes to {SteamNative.Tag(member)}");
                }
            }
            StringBuilder assign = new StringBuilder();
            foreach (KeyValuePair<Int32, UInt64> kv in Assignments)
                assign.Append($"{kv.Key}:{kv.Value & 0xFFFFFFFF}|");
            String value = assign.ToString();
            if (value != _publishedAssign)
            {
                _publishedAssign = value;
                _lobby.SetData("assign", value);
            }
        }

        public String StartBlocker()
        {
            if (!IsHost)
                return "only the host starts";
            if (Phase != SessionPhase.Gathering)
                return "already started";
            if (!AtTitle)
                return "the host must be at the title screen";
            if (_choiceLoading || Party.Count == 0)
                return "reading the save...";
            if (_choiceIndex > 0 && _continueBlob == null)
                return "the save couldn't be read";
            foreach (UInt64 member in _lobby.Members)
            {
                if (member == _lobby.LocalId)
                    continue;
                if (_lobby.GetMemberData(member, "where") != "title")
                    return $"{SteamNative.GetName(member)} isn't at the title screen";
                if (!_isConnected(member))
                    return $"connecting to {SteamNative.GetName(member)}...";
            }
            return null;
        }

        public void Start()
        {
            String blocker = StartBlocker();
            if (blocker != null)
            {
                Status = $"Can't start: {blocker}";
                return;
            }
            GameChoice choice = _choices[_choiceIndex];
            _lobby.SetData("started", "1");
            Phase = SessionPhase.Playing;
            if (choice.NewGame)
            {
                foreach (UInt64 peer in _transport.Peers)
                    SendControl(peer, MsgStart, ModeNewGame, 0, 0);
                Log.Message($"[Online] Session started: New Game on every PC; the host resyncs everyone once it has control");
                _awaitingNewGameResync = true;
                _fieldMapAtStart = PersistenSingleton<EventEngine>.Instance?.fieldmap;
                StartNewGame();
                return;
            }
            UInt32 crc = GameSnapshot.Crc32(_continueBlob, 0, _continueBlob.Length);
            foreach (UInt64 peer in _transport.Peers)
            {
                UInt32 transferId = _blobs.Send(peer, _continueBlob);
                SendControl(peer, MsgStart, ModeContinue, transferId, crc);
            }
            Log.Message($"[Online] Session started: {choice.Label} ({_continueBlob.Length} bytes to {_transport.Peers.Count} guest(s))");
            GameSnapshot.Load(_continueBlob, false, out String error);
            if (error != null)
                Log.Warning($"[Online] Session: the host's own load failed: {error}");
        }

        /// <summary>Sends the host's whole game to every guest (after New Game; M4: on demand, the full fallback).</summary>
        public void SendResync(String why = "New Game")
        {
            if (!IsHost || Phase != SessionPhase.Playing)
                return;
            _awaitingNewGameResync = false;
            Byte[] blob = GameSnapshot.CaptureCurrent(out _);
            UInt32 crc = GameSnapshot.Crc32(blob, 0, blob.Length);
            foreach (UInt64 peer in _transport.Peers)
            {
                UInt32 transferId = _blobs.Send(peer, blob);
                SendControl(peer, MsgResync, 0, transferId, crc);
            }
            Log.Message($"[Online] Session: resync ({why}) sent from field {FF9StateSystem.Common.FF9.fldMapNo} ({blob.Length} bytes)");
        }

        // ---- Guest ------------------------------------------------------------------------------------------------

        /// <summary>Picks the character at this position of the party list (-1 = release).</summary>
        public void Pick(Int32 position)
        {
            if (!IsGuest || Phase != SessionPhase.Gathering)
                return;
            Int32 character = position >= 0 && position < Party.Count ? Party[position].CharacterId : -1;
            if (position >= 0 && character < 0)
                return;
            _myPick = character;
            _lobby.SetMemberData("pick", character.ToString());
            Log.Message($"[Online] Session: picked {(character >= 0 ? $"{CharacterName(character)} (character {character})" : "nothing")}");
        }

        private void ReadHostData()
        {
            GameLabel = _lobby.GetData("game");
            Party.Clear();
            foreach (String part in _lobby.GetData("party").Split('|'))
            {
                String[] fields = part.Split(':');
                if (fields.Length == 3 && Int32.TryParse(fields[0], out Int32 index) && Int32.TryParse(fields[1], out Int32 character))
                    Party.Add(new PartyEntry { Index = index, CharacterId = character, Name = fields[2] });
            }
            Assignments.Clear();
            foreach (String part in _lobby.GetData("assign").Split('|'))
            {
                String[] fields = part.Split(':');
                if (fields.Length == 2 && Int32.TryParse(fields[0], out Int32 character) && UInt32.TryParse(fields[1], out UInt32 account))
                    foreach (UInt64 member in _lobby.Members)
                        if ((member & 0xFFFFFFFF) == account)
                            Assignments[character] = member;
            }
            ReadControlData();
        }

        /// <summary>Control-channel messages for the session; false if the message isn't one.</summary>
        public Boolean OnControlMessage(UInt64 peer, Byte[] data)
        {
            if (OnMeetMessage(peer, data) || OnFlagsMessage(peer, data) || OnHostStateMessage(peer, data))
                return true;
            if ((data.Length != 10 && !(data.Length == 12 && data[0] == MsgResync)) || (data[0] != MsgStart && data[0] != MsgResync))
                return false;
            if (peer != _lobby.OwnerId)
            {
                Log.Warning($"[Online] Session: ignored a start/resync from {SteamNative.Tag(peer)}, who isn't the host");
                return true;
            }
            Byte mode = data[1];
            UInt32 transferId = ReadU32(data, 2);
            UInt32 crc = ReadU32(data, 6);
            if (data[0] == MsgStart)
            {
                if (Phase != SessionPhase.Gathering || _startedBeforeJoin)
                    return true;
                GuestGuard.BeginGuestSession();
                Log.Message($"[Online] Session: the host started {(mode == ModeNewGame ? "a New Game" : GameLabel)}; you play {(MyCharacter >= 0 ? $"{CharacterName(MyCharacter)} (character {MyCharacter})" : "no character (watching)")}");
                if (mode == ModeNewGame)
                {
                    Phase = SessionPhase.Playing;
                    StartNewGame();
                    return true;
                }
                Phase = SessionPhase.Starting;
                _pending = new PendingLoad { TransferId = transferId, Crc = crc };
                Status = "Receiving the host's game...";
            }
            else
            {
                Int32 keepSpot = data.Length == 12 && mode == ModeKeepSpot ? data[10] | (data[11] << 8) : -1;
                _pending = new PendingLoad { TransferId = transferId, Crc = crc, IsResync = true, KeepSpotField = keepSpot };
                Log.Message(keepSpot >= 0 ? $"[Online] Session: the host's world for field {keepSpot} is coming (meeting; you keep your spot)" : "[Online] Session: the host is resyncing you (its whole game)");
            }
            TryLoadPending();
            return true;
        }

        /// <summary>A complete blob from the transfer layer.</summary>
        public void OnBlob(BlobTransfer.Received received)
        {
            if (received.Peer != _lobby.OwnerId || Memoria.Online.Party.PartySync.IsPartyBlob(received.Blob))
                return;
            _receivedBlobs[received.TransferId] = received.Blob;
            TryLoadPending();
        }

        /// <summary>A mid-game reload only from the field or the world map with no menu open (a menu left open by a reload breaks).</summary>
        private static Boolean IsReadyForResync()
        {
            UIManager.UIState state = PersistenSingleton<UIManager>.Instance.State;
            return (state == UIManager.UIState.FieldHUD || state == UIManager.UIState.WorldHUD) && !PersistenSingleton<SceneDirector>.Instance.IsFading;
        }

        private void TryLoadPending()
        {
            if (_pending == null || !_receivedBlobs.TryGetValue(_pending.TransferId, out Byte[] blob))
                return;
            if (_pending.IsResync && !IsReadyForResync())
                return; // a menu, a battle or a scene change is going on: reload once back on the field
            PendingLoad pending = _pending;
            _pending = null;
            _receivedBlobs.Clear();
            if (GameSnapshot.Crc32(blob, 0, blob.Length) != pending.Crc)
            {
                Status = "The host's game arrived damaged";
                Log.Warning($"[Online] Session: snapshot {pending.TransferId} doesn't match the start message's checksum");
                return;
            }
            SavedSpot spot = null;
            if (pending.KeepSpotField >= 0 && !TrySaveSpot(pending.KeepSpotField, out spot))
            {
                _metField = -1; // asks again if we meet again
                Log.Message($"[Online] Meet: you left field {pending.KeepSpotField} before the host's world arrived; not loaded");
                return;
            }
            GameSnapshot.Load(blob, true, out String error);
            if (error != null)
            {
                Status = $"Loading the host's game failed: {error}";
                Log.Warning($"[Online] Session: {Status}");
                return;
            }
            _restoreSpot = spot;
            _metField = PersistenSingleton<FF9StateSystem>.Instance.mode == 1 ? FF9StateSystem.Common.FF9.fldMapNo : -1; // with the host now: no meeting request for this field
            Phase = SessionPhase.Playing;
            Status = String.Empty;
            Log.Message($"[Online] Session: loaded the host's game{(pending.IsResync ? " (resync)" : "")}");
        }

        // ---- Shared ---------------------------------------------------------------------------------------------

        private static void StartNewGame()
        {
            // As TitleUI.OnNewGameButtonClick
            UIManager ui = PersistenSingleton<UIManager>.Instance;
            ui.TitleScene.Hide(() =>
            {
                FF9StateSystem.ReInitStateSystem();
                SceneDirector.Replace("FieldMap", SceneTransition.FadeOutToBlack_FadeIn, true);
                ui.TitleScene.SplashScreenEnabled = true;
                SoundLib.StopMovieMusic("FMV000", true);
            });
        }

        private static Boolean IsPlayableOnField()
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            return engine != null && engine.gMode == 1 && engine.fieldmap?.playerController != null && engine.GetUserControl();
        }

        private void Reset(String reason)
        {
            Log.Message($"[Online] Session ended ({reason})");
            Phase = SessionPhase.Idle;
            Party.Clear();
            Assignments.Clear();
            _choices.Clear();
            _choiceIndex = -1;
            _continueBlob = null;
            _pending = null;
            _receivedBlobs.Clear();
            _awaitingNewGameResync = false;
            _publishedAssign = null;
            _myPick = -1;
            _where = null;
            _buildPublished = false;
            _buildSeenAt.Clear();
            _buildChecked.Clear();
            ResetControl();
            ResetMeet();
            Status = String.Empty;
        }

        /// <summary>
        /// Every PC puts its build (<see cref="OnlineBuild.Id"/>) in its lobby member data; each other member's is
        /// compared once. A different build, or none after a few seconds (a build from before build ids, such as the first v0.1.4 zip), is logged and
        /// shown: most "bugs" between two PCs on different builds are only that.
        /// </summary>
        private void CheckBuilds(Double nowMs)
        {
            if (!_buildPublished)
            {
                _lobby.SetMemberData("build", OnlineBuild.Id);
                _buildPublished = true;
                Log.Message($"[Online] Build: this PC runs {OnlineBuild.Id}");
            }
            foreach (UInt64 peer in _transport.Peers)
            {
                if (peer == _lobby.LocalId || _buildChecked.Contains(peer))
                    continue;
                if (!_buildSeenAt.TryGetValue(peer, out Double seen))
                    _buildSeenAt[peer] = seen = nowMs;
                String theirs = _lobby.GetMemberData(peer, "build");
                if (String.IsNullOrEmpty(theirs) && nowMs - seen < NoBuildIdAfterMs)
                    continue;
                _buildChecked.Add(peer);
                if (theirs == OnlineBuild.Id)
                {
                    Log.Message($"[Online] Build: {SteamNative.Tag(peer)} runs the same build ({theirs})");
                    continue;
                }
                String what = String.IsNullOrEmpty(theirs) ? "an older build (no build id; for example the first v0.1.4 zip)" : $"build {theirs}";
                Log.Warning($"[Online] Build: {SteamNative.Tag(peer)} runs {what}, this PC {OnlineBuild.Id}: both PCs need the same build");
                Memoria.Online.Party.PartySync.Toast($"{SteamNative.GetName(peer)} runs {what}; you run {OnlineBuild.Id}. Both PCs need the same build.");
            }
        }

        private void SendControl(UInt64 peer, Byte type, Byte mode, UInt32 transferId, UInt32 crc)
        {
            Byte[] message = new Byte[10];
            message[0] = type;
            message[1] = mode;
            WriteU32(message, 2, transferId);
            WriteU32(message, 6, crc);
            _transport.Send(peer, TransportChannel.Control, message, message.Length, true);
        }

        /// <summary>A character a guest may pick: in the party and not the host's own.</summary>
        private Boolean IsPickable(Int32 character)
        {
            if (character < 0 || character == HostCharacter)
                return false;
            foreach (PartyEntry entry in Party)
                if (entry.CharacterId == character)
                    return true;
            return false;
        }

        private String CharacterName(Int32 character)
        {
            foreach (PartyEntry entry in Party)
                if (entry.CharacterId == character)
                    return entry.Name;
            return "?";
        }

        private static Int32 ParsePick(String value) => Int32.TryParse(value, out Int32 pick) ? pick : -1;

        private static void WriteU32(Byte[] b, Int32 o, UInt32 v)
        {
            b[o] = (Byte)v; b[o + 1] = (Byte)(v >> 8); b[o + 2] = (Byte)(v >> 16); b[o + 3] = (Byte)(v >> 24);
        }

        private static UInt32 ReadU32(Byte[] b, Int32 o) => b[o] | ((UInt32)b[o + 1] << 8) | ((UInt32)b[o + 2] << 16) | ((UInt32)b[o + 3] << 24);
    }
}
