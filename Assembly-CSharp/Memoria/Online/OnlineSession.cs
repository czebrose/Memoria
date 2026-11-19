using Memoria.Online.Battle;
using Memoria.Online.Bodies;
using Memoria.Online.Ghosts;
using Memoria.Online.Party;
using Memoria.Online.Session;
using Memoria.Prime;
using SimpleJSON;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace Memoria.Online
{
    /// <summary>
    /// Online co-op, "Ghosts" phase: owns the Steam lobby, the P2P transport and the ghosts, ticked once per frame.
    /// Attached in SceneDirector.Awake; dormant until a lobby is hosted or joined.
    ///
    /// Traffic per peer:
    ///   ch 0 (control, reliable): ping every second (echoed; RTT), "ghost states sent to you" every 5 s (loss)
    ///   ch 1 (movement, unreliable): the local player's 23-byte <see cref="GhostState"/> at 15 Hz, always (with
    ///        <see cref="GhostState.NotOnField"/> while in a battle / menu / world map), so ghosts hide at once
    ///
    /// Hotkeys (Ctrl+Shift): H host, I invite (Steam overlay), J join (lobby id in the clipboard, else a friend's FF9
    /// lobby), L leave, O status box. With [Debug] Enabled = 1 and OnlineDebug = 1 in Memoria.ini, also:
    /// G shadow ghost (replays you ~1 s behind), P pause its feed 6 s, K network simulation (10% loss, 0-80 ms jitter),
    /// M ghost model override, N interpolation on/off, D adaptive / fixed 100 ms delay, C pretend to be a guest playing
    /// another character (offline test of the body swap; reloads the field), F7 switch Player / Cast scene mode, F8
    /// pretend another party member is acting (a shared scene they started).
    /// </summary>
    public sealed class OnlineSession : MonoBehaviour
    {
        private const Byte MsgPing = 1;
        private const Byte MsgPong = 2;
        private const Byte MsgSentCount = 3;
        private const Double StallMs = 5000;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static Double NowMs => Clock.Elapsed.TotalMilliseconds;

        private enum InitState { Waiting, Ready, NoSteam }

        private sealed class PeerStats
        {
            public UInt64 Id;
            public String Tag;
            public Double JoinedAt, LastReceived;
            public Boolean Stalled;
            public Int32 Stalls;
            public UInt32 PingSeq;
            public Int32 PingsSent, PongsReceived;
            public Double LastRtt = -1, MinRtt = Double.MaxValue, MaxRtt, SumRtt;
            public Int32 SecondPongs;
            public Double SecondSumRtt;
            public UInt32 GhostSent, GhostReceived;
            public UInt32 TheirSentToUs, ReceivedWhenReported;
            public Boolean Relay, SessionActive;

            public Double AvgRtt => PongsReceived > 0 ? SumRtt / PongsReceived : -1;
            public Double LossPercent => TheirSentToUs > 0 ? Math.Max(0, 100.0 * (1.0 - ReceivedWhenReported / (Double)TheirSentToUs)) : 0;
        }

        private InitState _init = InitState.Waiting;
        private Boolean _gamePumps;
        private Boolean _debug;
        private SteamLobby _lobby;
        private SteamTransport _transport;
        private readonly Dictionary<UInt64, PeerStats> _peers = new Dictionary<UInt64, PeerStats>();
        private readonly List<TransportMessage> _received = new List<TransportMessage>();
        private readonly Byte[] _ghostBuffer = new Byte[GhostState.Size];
        private readonly Byte[] _controlBuffer = new Byte[13];
        private GhostState _local;
        private Double _nextGhostSend, _nextPing, _nextSentCount, _nextSummary;
        private UInt32 _ghostStatesSampled;
        private readonly ShadowGhost _shadow = new ShadowGhost();
        private Int32 _modelOverrideIndex = -1;
        private Boolean _showStatus = true;
        private Boolean _finalLogged;
        private Int32 _clamped;
        private GUIStyle _style;

        // Step 2 / M1: game snapshots (debug hotkeys S, T, R, B)
        private readonly BlobTransfer _blobs = new BlobTransfer();
        private SessionLobby _session; // Step 2: title-screen lobby, character pick, start
        private PartySync _party; // Step 2 / M4: host-authoritative party state
        private BattleEntry _battle; // Spike D: shared battle entry and exit
        private Byte[] _snapshot;
        private String _snapshotSource;
        private JSONClass _verifyTree;
        private Boolean _verifyAsGuest;
        private Double _verifyNotBefore;

        private void OnEnable()
        {
            Camera.onPreCull += OnPreCull;
        }

        private void OnDisable()
        {
            Camera.onPreCull -= OnPreCull;
        }

        private static void OnPreCull(Camera camera)
        {
            try
            {
                GhostManager.ApplyRender();
                BodySwap.ApplyRender();
            }
            catch (Exception err)
            {
                Log.Error(err, "[Online] Ghost rendering failed");
            }
        }

        private void Update()
        {
            try
            {
                if (_init == InitState.Waiting)
                    TryInitialize();
                Double now = NowMs;
                HandleHotkeys(now);

                if (_init == InitState.Ready)
                {
                    // RULE 2 (docs/online/CONTRIBUTING.md): Steam callbacks only run once the game pumps them
                    // (title screen on). Pumping SteamAPI_RunCallbacks ourselves before that crashes the game in
                    // steamwrapper.dll's stats handler; until then, everything here is polled.
                    if (!_gamePumps && SiliconStudio.Social.IsCreated)
                    {
                        _gamePumps = true;
                        Log.Message($"[Online] The game now pumps SteamAPI_RunCallbacks; callbacks are live (overlay enabled: {SteamNative.IsOverlayEnabled(SteamNative.Utils)})");
                    }
                    _lobby.Update((Single)(now / 1000.0));
                    if (_transport.IsActive)
                        _blobs.Update(_transport);
                    _received.Clear();
                    _transport.Poll(_received);
                    foreach (TransportMessage msg in _received)
                        HandleMessage(msg, now);
                    _session.Update(now);
                    _battle.Update(now);
                }
                UpdateBodyRoles();
                BodySwap.Update();
                _party?.Update(_session != null && _session.Phase == SessionPhase.Playing, now);

                Boolean sending = (_transport != null && _transport.IsActive && _transport.Peers.Count > 0) || _shadow.Enabled;
                if (sending && now >= _nextGhostSend)
                {
                    _nextGhostSend = Math.Max(_nextGhostSend + GhostManager.SendIntervalMs, now - GhostManager.SendIntervalMs);
                    SendGhostState(now);
                }
                _shadow.Deliver(now);
                GhostManager.Update(now, Time.deltaTime);

                if (_init == InitState.Ready && _peers.Count > 0)
                    PerPeerTimers(now);
                if (_verifyTree != null && now >= _verifyNotBefore)
                    VerifyLoadedSnapshot();
            }
            catch (Exception err)
            {
                Log.Error(err, "[Online] Update failed");
            }
        }

        /// <summary>Step 2 / M3: tell the body swap who plays whom (the host's character leads).</summary>
        private void UpdateBodyRoles()
        {
            Boolean playing = _session != null && _session.Phase == SessionPhase.Playing && _session.HostCharacter >= 0;
            Boolean spectating = playing && !_lobby.IsHost && _session.MyCharacter < 0;
            GuestGuard.Spectating = spectating;
            BodySwap.HideLocalPlayer = spectating;
            if (!playing)
            {
                _lastMyCharacter = Int32.MinValue;
                BodySwap.SetSession(-1, -1, null, null);
                return;
            }
            if (_session.MyCharacter != _lastMyCharacter)
            {
                if (_lastMyCharacter != Int32.MinValue)
                    PartySync.Toast(_session.MyCharacter >= 0 ? $"You now play {CharacterBodies.ForCharacter(_session.MyCharacter)?.Name ?? $"character {_session.MyCharacter}"}" : "You're watching (no character)");
                Log.Message($"[Online] Control: this PC plays {(_session.MyCharacter >= 0 ? $"character {_session.MyCharacter}" : "nobody (watching)")}");
                _lastMyCharacter = _session.MyCharacter;
            }
            // The lead is the party's leader: the character the field scripts make the player (M4: the host may play another)
            List<Int32> party = SessionLobby.CurrentParty();
            Int32 lead = party.Count > 0 ? party[0] : _session.HostCharacter;
            // Who controls which character: the host its own, guests theirs (party members nobody picked are driven
            // by the host's game, not played: controller none)
            Dictionary<Int32, Int32> controllers = new Dictionary<Int32, Int32>();
            Dictionary<UInt64, Int32> peerCharacters = new Dictionary<UInt64, Int32>();
            controllers[_session.HostPlays] = _lobby.IsHost ? BodySwap.ControllerThisPC : BodySwap.ControllerOther;
            peerCharacters[_lobby.OwnerId] = _session.HostPlays;
            foreach (KeyValuePair<Int32, UInt64> kv in _session.Assignments)
            {
                controllers[kv.Key] = kv.Value == _lobby.LocalId ? BodySwap.ControllerThisPC : BodySwap.ControllerOther;
                peerCharacters[kv.Value] = kv.Key;
            }
            peerCharacters.Remove(_lobby.LocalId);
            BodySwap.SetSession(lead, _session.MyCharacter, controllers, peerCharacters);
        }

        private Int32 _lastMyCharacter = Int32.MinValue;

        /// <summary>Debug: cycle "pretend to be a guest playing X" (offline), then reload the field so it's created again.</summary>
        private void CycleDebugBody(Double now)
        {
            // The characters in the current party (in party order) that have a body, other than the leader
            List<Int32> ids = new List<Int32>();
            PLAYER[] members = FF9StateSystem.Common.FF9.party?.member;
            if (members != null)
                for (Int32 i = 1; i < members.Length; i++)
                    if (members[i] != null && CharacterBodies.ForCharacter((Int32)members[i].info.slot_no) != null)
                        ids.Add((Int32)members[i].info.slot_no);
            Int32 index = ids.IndexOf(BodySwap.DebugLocalCharacter);
            BodySwap.DebugLocalCharacter = index + 1 < ids.Count ? ids[index + 1] : -1;
            Log.Message($"[Online] Debug body: {(BodySwap.DebugLocalCharacter < 0 ? "off" : CharacterBodies.ForCharacter(BodySwap.DebugLocalCharacter).ToString())}; reloading the field");
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            if (engine != null && engine.gMode == 1)
            {
                CaptureCurrentSnapshot();
                LoadSnapshot(now);
            }
        }

        private void TryInitialize()
        {
            _debug = Configuration.Debug.OnlineDebug;
            if (!SteamSdkWrapper.IsInitialized)
                return;
            if (!SteamNative.TryLoad())
            {
                _init = InitState.NoSteam;
                Log.Warning("[Online] Steam unavailable: online play is off" + (_debug ? " (the debug shadow ghost still works)" : ""));
                return;
            }
            _lobby = new SteamLobby();
            _transport = new SteamTransport(_lobby);
            _transport.PeerJoined += OnPeerJoined;
            _transport.PeerLeft += OnPeerLeft;
            _lobby.HostChanged += owner => Log.Message($"[Online] Host is now {SteamNative.Tag(owner)}{(owner == _lobby.LocalId ? " (you)" : "")}; no host-leave policy in this phase, nothing else changes");
            _session = new SessionLobby(_lobby, _transport, _blobs, IsConnected);
            _blobs.Completed += OnSnapshotReceived;
            _blobs.Completed += _session.OnBlob;
            _party = new PartySync(_blobs, () => _lobby.IsHost, () => _lobby.OwnerId, () => _transport.Peers, CharacterOfPeer, () => _session.MyCharacter);
            _blobs.Completed += _party.OnBlob;
            _battle = new BattleEntry(_transport, _lobby, _blobs, () => _session.Phase == SessionPhase.Playing, () => _session.MyCharacter, CharacterOfPeer, IsPlayed);
            _blobs.Completed += _battle.OnBlob;
            _init = InitState.Ready;
            Log.Message($"[Online] Ready. Ctrl+Shift+H host, I invite, J join, L leave, O status{(_debug ? "; debug: G shadow ghost, P pause feed, K net sim, M model, N interpolation, D delay" : "")}");

            // Steam starts the game with "+connect_lobby <id>" when an invite is accepted while it isn't running
            // (out of scope: FF9_Launcher.exe may not pass it on; logged to find out)
            String[] args = Environment.GetCommandLineArgs();
            Boolean connectLobby = false;
            for (Int32 i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "+connect_lobby" && UInt64.TryParse(args[i + 1], out UInt64 lobbyId))
                {
                    connectLobby = true;
                    _lobby.Join(lobbyId);
                }
            }
            Log.Message($"[Online] +connect_lobby on the command line: {(connectLobby ? "yes, joining" : "no")}");
        }

        private void HandleHotkeys(Double now)
        {
            Boolean ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            Boolean shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!ctrl || !shift)
                return;
            if (Input.GetKeyDown(KeyCode.O))
            {
                _showStatus = !_showStatus;
                return;
            }
            if (_session != null)
            {
                // M4, playing: F1-F4 Take / Swap / Release party slot 1-4, F5 accept a swap offer, F6 decline
                if (_session.Phase == SessionPhase.Playing)
                {
                    if (Input.GetKeyDown(KeyCode.J) && !_lobby.IsHost)
                    {
                        _session.JoinHost(); // M5 (in game, J isn't "join a lobby")
                        return;
                    }
                    for (Int32 i = 0; i < 4; i++)
                        if (Input.GetKeyDown(KeyCode.F1 + i))
                            _session.ControlSlot(i);
                    if (Input.GetKeyDown(KeyCode.F5))
                        _session.AnswerOffer(true);
                    else if (Input.GetKeyDown(KeyCode.F6))
                        _session.AnswerOffer(false);
                }
                // Step 2 lobby (title screen): F1 release, F2-F4 pick party slot 2-4, F5 next game (host), F6 start (host)
                else if (Input.GetKeyDown(KeyCode.F1))
                    _session.Pick(-1);
                else if (Input.GetKeyDown(KeyCode.F2))
                    _session.Pick(1);
                else if (Input.GetKeyDown(KeyCode.F3))
                    _session.Pick(2);
                else if (Input.GetKeyDown(KeyCode.F4))
                    _session.Pick(3);
                else if (Input.GetKeyDown(KeyCode.F5))
                    _session.NextChoice();
                else if (Input.GetKeyDown(KeyCode.F6))
                    _session.Start();
            }
            if (_init == InitState.Ready)
            {
                if (Input.GetKeyDown(KeyCode.H))
                {
                    _lobby.Create();
                }
                else if (Input.GetKeyDown(KeyCode.I))
                {
                    _lobby.OpenInviteOverlay();
                }
                else if (Input.GetKeyDown(KeyCode.J))
                {
                    String clipboard = String.Empty;
                    try { clipboard = GUIUtility.systemCopyBuffer?.Trim() ?? String.Empty; } catch { }
                    if (UInt64.TryParse(clipboard, out UInt64 lobbyId) && (lobbyId >> 52) == 0x018) // universe 1, account type 8 (lobby)
                        _lobby.Join(lobbyId);
                    else
                        _lobby.JoinFriendLobby();
                }
                else if (Input.GetKeyDown(KeyCode.L))
                {
                    _lobby.Leave(); // members leave → PeerLeft → ghosts removed, sending stops
                }
            }
            if (!_debug)
                return;
            if (Input.GetKeyDown(KeyCode.G))
            {
                _shadow.SetEnabled(!_shadow.Enabled);
            }
            else if (Input.GetKeyDown(KeyCode.P))
            {
                _shadow.Pause(now);
            }
            else if (Input.GetKeyDown(KeyCode.K))
            {
                _shadow.NetworkSimulation = !_shadow.NetworkSimulation;
                Log.Message($"[Online] Shadow network simulation (10% loss, 0-80 ms jitter) {(_shadow.NetworkSimulation ? "on" : "off")}");
            }
            else if (Input.GetKeyDown(KeyCode.M))
            {
                _modelOverrideIndex++;
                if (_modelOverrideIndex >= GhostModels.All.Length)
                    _modelOverrideIndex = -1;
                GhostManager.ModelOverride = _modelOverrideIndex >= 0 ? GhostModels.All[_modelOverrideIndex] : null;
                GhostManager.RemoveAll("model override changed"); // they come back with the new model on their next state
                Log.Message($"[Online] Ghost model: {(GhostManager.ModelOverride != null ? GhostManager.ModelOverride.ToString() : "the sender's field leader")}");
            }
            else if (Input.GetKeyDown(KeyCode.N))
            {
                GhostManager.Interpolate = !GhostManager.Interpolate;
                Log.Message($"[Online] Ghost interpolation {(GhostManager.Interpolate ? "on" : "off")}");
            }
            else if (Input.GetKeyDown(KeyCode.D))
            {
                GhostManager.FixedDelayMs = GhostManager.FixedDelayMs < 0 ? 100 : -1;
                Log.Message($"[Online] Ghost interpolation delay: {(GhostManager.FixedDelayMs < 0 ? "adaptive (interval + jitter, 80-250 ms)" : $"fixed {GhostManager.FixedDelayMs} ms")}");
            }
            else if (Input.GetKeyDown(KeyCode.S))
            {
                CaptureCurrentSnapshot();
            }
            else if (Input.GetKeyDown(KeyCode.T))
            {
                CaptureSlotSnapshot();
            }
            else if (Input.GetKeyDown(KeyCode.R))
            {
                LoadSnapshot(now);
            }
            else if (Input.GetKeyDown(KeyCode.B))
            {
                SendSnapshotToPeers();
            }
            else if (Input.GetKeyDown(KeyCode.C))
            {
                CycleDebugBody(now);
            }
            else if (Input.GetKeyDown(KeyCode.F7))
            {
                BodySwap.DebugToggleMode();
            }
            else if (Input.GetKeyDown(KeyCode.F8))
            {
                BodySwap.DebugCycleActor();
            }
            else if (Input.GetKeyDown(KeyCode.Y))
            {
                _session?.SendResync("asked by the host"); // M4: the full-state fallback
            }
            else if (Input.GetKeyDown(KeyCode.U))
            {
                BattleEntry.FreeCharactersJoinHost = !BattleEntry.FreeCharactersJoinHost; // Spike D
                Log.Message($"[Online] Battle entry: party members nobody plays {(BattleEntry.FreeCharactersJoinHost ? "join" : "sit out")} the battles the host takes part in");
            }
            else if (Input.GetKeyDown(KeyCode.V))
            {
                Int32 scene = PersistenSingleton<EventEngine>.Instance != null ? PersistenSingleton<EventEngine>.Instance.OnlineDebugEncounter() : 0; // Spike D
                Log.Message(scene > 0 ? $"[Online] Battle entry: debug encounter, battle {scene}" : "[Online] Battle entry: debug encounter: no random battle here (needs a field with an encounter table and player control)");
            }
        }

        // ---- Step 2 / M1: snapshots (debug) ---------------------------------------------------------------------

        private void CaptureCurrentSnapshot()
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            if (engine == null || (engine.gMode != 1 && engine.gMode != 3))
            {
                Log.Message("[Online] Snapshot: only on a field or the world map (not in a battle or at the title)");
                return;
            }
            Stopwatch watch = Stopwatch.StartNew();
            Byte[] blob = GameSnapshot.CaptureCurrent(out JSONClass tree);
            SetSnapshot(blob, $"captured on field {FF9StateSystem.Common.FF9.fldMapNo}", watch.Elapsed.TotalMilliseconds);
        }

        private void CaptureSlotSnapshot()
        {
            if (PersistenSingleton<UIManager>.Instance.State != UIManager.UIState.Title)
            {
                Log.Message("[Online] Slot snapshot: only at the title screen");
                return;
            }
            Stopwatch watch = Stopwatch.StartNew();
            GameSnapshot.GetLatestSlot((slot, save) =>
            {
                if (slot < 0 || save < 0)
                {
                    Log.Warning($"[Online] Slot snapshot: no latest save found (slot {slot}, save {save})");
                    return;
                }
                GameSnapshot.CaptureSlot(slot, save, (blob, tree, error) =>
                {
                    if (blob == null)
                        Log.Warning($"[Online] Slot snapshot failed: {error}");
                    if (blob == null)
                        return;
                    SetSnapshot(blob, $"slot {slot}/{save} (latest save)", watch.Elapsed.TotalMilliseconds);
                    GameSnapshot.CompareWithGameLoad(slot, save, blob);
                });
            });
        }

        private void SetSnapshot(Byte[] blob, String source, Double captureMs)
        {
            _snapshot = blob;
            _snapshotSource = source;
            UInt32 crc = GameSnapshot.Crc32(blob, 0, blob.Length);
            // Loopback through the chunk codec: encode every chunk (in reverse order), decode them, compare
            BlobTransfer loopback = new BlobTransfer();
            Boolean codecOk = false;
            loopback.Completed += r => codecOk = r.Blob.Length == blob.Length && GameSnapshot.Crc32(r.Blob, 0, r.Blob.Length) == crc;
            Byte[] buffer = new Byte[BlobTransfer.HeaderSize + BlobTransfer.ChunkSize];
            Int32 count = loopback.ChunkCount(blob.Length);
            for (Int32 i = count - 1; i >= 0; i--)
            {
                Int32 length = BlobTransfer.Encode(1, blob, crc, i, count, buffer);
                Byte[] message = new Byte[length];
                Buffer.BlockCopy(buffer, 0, message, 0, length);
                loopback.OnMessage(0, message, length);
            }
            Log.Message($"[Online] Snapshot {source}: {blob.Length} bytes ({blob.Length / 1024.0:0.0} KB), {count} chunks of {BlobTransfer.ChunkSize / 1024} KB, crc {crc:X8}, made in {captureMs:0} ms; chunk codec loopback {(codecOk ? "OK" : "FAILED")}");
        }

        private void LoadSnapshot(Double now)
        {
            if (_snapshot == null)
            {
                Log.Message("[Online] Load snapshot: no snapshot yet (Ctrl+Shift+S on a field, T at the title, or receive one with B)");
                return;
            }
            UIManager ui = PersistenSingleton<UIManager>.Instance;
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            Boolean atTitle = ui.State == UIManager.UIState.Title;
            Boolean onFieldWithControl = engine != null && engine.gMode == 1 && engine.fieldmap?.playerController != null && engine.GetUserControl();
            if (!atTitle && !onFieldWithControl && !(engine != null && engine.gMode == 3))
            {
                Log.Message("[Online] Load snapshot: only at the title screen, on a field you can move in, or on the world map");
                return;
            }
            // Loaded as a guest would: keeps this PC's options and achievements, writes no file
            _verifyTree = GameSnapshot.Load(_snapshot, true, out String error);
            if (_verifyTree == null)
            {
                Log.Warning($"[Online] Load snapshot failed: {error}");
                return;
            }
            _verifyAsGuest = true;
            _verifyNotBefore = now + 3000;
            Log.Message($"[Online] Loading snapshot ({_snapshotSource}) from memory {(atTitle ? "from the title screen" : "in game")}; the state is compared once you can move again");
        }

        /// <summary>Once the loaded game is playable, compare its state with the snapshot that was applied.</summary>
        private void VerifyLoadedSnapshot()
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            Boolean playable = engine != null && ((engine.gMode == 1 && engine.fieldmap?.playerController != null && engine.GetUserControl()) || engine.gMode == 3);
            if (!playable)
                return;
            JSONClass expected = _verifyTree;
            _verifyTree = null;
            GameSnapshot.CaptureCurrent(out JSONClass actual);
            if (_verifyAsGuest)
                GameSnapshot.PrepareForGuest(actual);
            List<String> differences = GameSnapshot.Diff(expected, actual, 25);
            if (differences.Count == 0)
            {
                Log.Message("[Online] Snapshot check: the loaded game matches the snapshot exactly (play time ignored)");
                return;
            }
            Log.Message($"[Online] Snapshot check: {(differences.Count >= 25 ? "25+" : differences.Count.ToString())} difference(s) between the snapshot and the loaded game (play time ignored):");
            foreach (String difference in differences)
                Log.Message($"[Online]   {difference}");
        }

        private void SendSnapshotToPeers()
        {
            if (_snapshot == null || _transport == null || !_transport.IsActive || _transport.Peers.Count == 0)
            {
                Log.Message("[Online] Send snapshot: needs a snapshot (S or T) and at least one peer in the lobby");
                return;
            }
            foreach (UInt64 peer in _transport.Peers)
                _blobs.Send(peer, _snapshot);
        }

        /// <summary>Spike D: a player plays this character (the host's own or a guest's).</summary>
        private Boolean IsPlayed(Int32 character) => character == _session.HostPlays || _session.Assignments.ContainsKey(character);

        private Int32 CharacterOfPeer(UInt64 peer)
        {
            foreach (KeyValuePair<Int32, UInt64> kv in _session.Assignments)
                if (kv.Value == peer)
                    return kv.Key;
            return peer == _lobby.OwnerId ? _session.HostPlays : -1;
        }

        private void OnSnapshotReceived(BlobTransfer.Received received)
        {
            if (PartySync.IsPartyBlob(received.Blob) || BattleEntry.IsResultBlob(received.Blob))
                return;
            _snapshot = received.Blob;
            _snapshotSource = $"received from {SteamNative.Tag(received.Peer)} in {received.Milliseconds:0} ms";
            Log.Message($"[Online] Snapshot {_snapshotSource} stored; Ctrl+Shift+R loads it");
        }

        // ---- Ghost state: sample the local player at 15 Hz, send to every peer --------------------------------------

        private void SendGhostState(Double now)
        {
            SampleLocal(now);
            Int32 length = _local.Serialize(_ghostBuffer, 0);
            _ghostStatesSampled++;
            if (_transport != null && _transport.IsActive)
            {
                foreach (UInt64 peer in _transport.Peers)
                {
                    if (!_transport.Send(peer, TransportChannel.Movement, _ghostBuffer, length, false))
                        continue;
                    if (_peers.TryGetValue(peer, out PeerStats stats))
                        stats.GhostSent++;
                }
            }
            _shadow.Feed(_ghostBuffer, length, now);
        }

        private void SampleLocal(Double now)
        {
            _local.Tick = (UInt32)now;
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            FieldMapActorController leader = engine?.fieldmap?.playerController;
            Actor actor = leader?.originalActor;
            if (engine == null || engine.gMode != 1 || actor == null || leader.IsGhost || GuestGuard.Spectating) // watching: no ghost of us
            {
                _local.FieldId = GhostState.NotOnField;
                _local.Model = 0;
                _local.X = _local.Y = _local.Z = 0;
                _local.Tri = -1;
                _local.Facing = 0;
                _local.Anim = 0;
                _local.AnimFrame = 0;
                _local.Motion = GhostMotion.Idle;
                return;
            }
            _local.FieldId = (UInt16)FF9StateSystem.Common.FF9.fldMapNo;
            _local.Model = actor.model;
            _local.X = ToInt16(leader.curPos.x);
            _local.Y = ToInt16(leader.curPos.y);
            _local.Z = ToInt16(leader.curPos.z);
            _local.Tri = (Int16)Mathf.Clamp(leader.activeTri, -1, Int16.MaxValue);
            _local.FacingDegrees = actor.rotAngle[1];
            _local.Anim = actor.anim;
            _local.AnimFrame = actor.animFrame;
            _local.Motion = actor.anim == actor.idle ? GhostMotion.Idle
                : actor.anim == actor.walk ? GhostMotion.Walk
                : actor.anim == actor.run ? GhostMotion.Run
                : GhostMotion.Other;
        }

        private Int16 ToInt16(Single value)
        {
            Int32 rounded = Mathf.RoundToInt(value);
            if (rounded >= Int16.MinValue && rounded <= Int16.MaxValue)
                return (Int16)rounded;
            if (_clamped++ == 0)
                Log.Warning($"[Online] Field coordinate {value} doesn't fit in 16 bits (field {FF9StateSystem.Common.FF9.fldMapNo})");
            return (Int16)Mathf.Clamp(rounded, Int16.MinValue, Int16.MaxValue);
        }

        // ---- Received messages ------------------------------------------------------------------------------------

        private void HandleMessage(TransportMessage msg, Double now)
        {
            if (!_peers.TryGetValue(msg.Peer, out PeerStats s))
                return;
            s.LastReceived = now;
            if (s.Stalled)
            {
                s.Stalled = false;
                Log.Message($"[Online] {s.Tag}: traffic resumed");
            }
            Byte[] d = msg.Data;
            switch (msg.Channel)
            {
                case TransportChannel.Control:
                    if (_session != null && _session.OnControlMessage(msg.Peer, d))
                        break;
                    if (_battle != null && _battle.OnControlMessage(msg.Peer, d))
                        break;
                    if (d.Length == 13 && d[0] == MsgPing)
                    {
                        d[0] = MsgPong;
                        _transport.Send(msg.Peer, TransportChannel.Control, d, d.Length, true);
                    }
                    else if (d.Length == 13 && d[0] == MsgPong)
                    {
                        Double rtt = (Clock.ElapsedTicks - ReadInt64(d, 5)) * 1000.0 / Stopwatch.Frequency;
                        s.PongsReceived++;
                        s.LastRtt = rtt;
                        s.SumRtt += rtt;
                        s.MinRtt = Math.Min(s.MinRtt, rtt);
                        s.MaxRtt = Math.Max(s.MaxRtt, rtt);
                        s.SecondPongs++;
                        s.SecondSumRtt += rtt;
                    }
                    else if (d.Length == 5 && d[0] == MsgSentCount)
                    {
                        s.TheirSentToUs = ReadUInt32(d, 1);
                        s.ReceivedWhenReported = s.GhostReceived;
                    }
                    break;
                case TransportChannel.Movement:
                    s.GhostReceived++;
                    GhostManager.OnPacket(msg.Peer, d, 0, d.Length, now); // unknown message types are counted and ignored
                    break;
                case TransportChannel.State:
                    _blobs.OnMessage(msg.Peer, d, d.Length);
                    break;
            }
        }

        private void OnPeerJoined(UInt64 peer)
        {
            _peers[peer] = new PeerStats { Id = peer, Tag = SteamNative.Tag(peer), JoinedAt = NowMs, LastReceived = NowMs };
            Log.Message($"[Online] Connected to {_transport.Peers.Count} peer(s); +{SteamNative.Tag(peer)} (its ghost appears with its first state)");
        }

        private void OnPeerLeft(UInt64 peer)
        {
            if (_peers.TryGetValue(peer, out PeerStats s))
            {
                LogSummary(s, "LEFT");
                _peers.Remove(peer);
            }
            _battle?.OnPeerLeft(peer);
            GhostManager.Remove(peer, "left the lobby");
            GhostManager.ForgetPeer(peer);
            _blobs.Forget(peer);
            Log.Message($"[Online] Connected to {_transport.Peers.Count} peer(s)");
        }

        // ---- Pings, loss reports, stall detection, logs -------------------------------------------------------------

        private void PerPeerTimers(Double now)
        {
            if (now >= _nextPing)
            {
                _nextPing = now + 1000;
                LogSecond(now);
                foreach (PeerStats s in _peers.Values)
                {
                    s.PingSeq++;
                    s.PingsSent++;
                    _controlBuffer[0] = MsgPing;
                    WriteUInt32(_controlBuffer, 1, s.PingSeq);
                    WriteInt64(_controlBuffer, 5, Clock.ElapsedTicks);
                    _transport.Send(s.Id, TransportChannel.Control, _controlBuffer, 13, true);
                }
            }
            if (now >= _nextSentCount)
            {
                _nextSentCount = now + 5000;
                foreach (PeerStats s in _peers.Values)
                {
                    _controlBuffer[0] = MsgSentCount;
                    WriteUInt32(_controlBuffer, 1, s.GhostSent);
                    _transport.Send(s.Id, TransportChannel.Control, _controlBuffer, 5, true);
                }
            }
            if (now >= _nextSummary)
            {
                if (_nextSummary > 0)
                    foreach (PeerStats s in _peers.Values)
                        LogSummary(s, "SUMMARY");
                _nextSummary = now + 30000;
            }
        }

        private void LogSecond(Double now)
        {
            StringBuilder line = new StringBuilder($"[Online] Connected to {_transport.Peers.Count} peer(s), host {SteamNative.Tag(_transport.HostPeer)}{(_lobby.IsHost ? " (you)" : "")}");
            foreach (PeerStats s in _peers.Values)
            {
                SteamNative.P2PSessionState session = _transport.GetSessionState(s.Id);
                s.Relay = session.UsingRelay != 0;
                s.SessionActive = session.ConnectionActive != 0;
                if (!s.Stalled && now - s.LastReceived > StallMs)
                {
                    s.Stalled = true;
                    s.Stalls++;
                    Log.Warning($"[Online] {s.Tag}: STALLED, no packet for {(now - s.LastReceived) / 1000:0.0}s (session active={s.SessionActive}, error={session.P2PSessionError}, queued={session.PacketsQueuedForSend})");
                }
                String rtt = s.SecondPongs > 0 ? $"{s.SecondSumRtt / s.SecondPongs:0.0}ms" : "no pong";
                line.Append($" | {s.Tag}: rtt {rtt}, {(s.Relay ? "relay" : "direct")}, ghost loss {s.LossPercent:0.0}%, up {(now - s.JoinedAt) / 1000:0}s{(s.Stalled ? ", STALLED" : "")}");
                if (TryGetGhost(s.Id, out GhostActor ghost))
                    line.Append($", ghost {(ghost.Visible ? "shown" : "hidden")} delay {ghost.DelayMs:0}ms jitter {ghost.JitterMs:0}ms");
                s.SecondPongs = 0;
                s.SecondSumRtt = 0;
            }
            Log.Message(line.ToString());
        }

        private void LogSummary(PeerStats s, String tag)
        {
            String ghostInfo = "no ghost";
            if (TryGetGhost(s.Id, out GhostActor ghost))
                ghostInfo = $"ghost {ghost.Model.Name}: received {ghost.Received}, dropped as old {ghost.DroppedOld}, snaps {ghost.Snaps}, hidden {ghost.Hides}x, delay {ghost.DelayMs:0}ms, jitter {ghost.JitterMs:0}ms (max {ghost.MaxJitterMs:0}ms)";
            Log.Message($"[Online] {tag} {s.Tag}: up {(NowMs - s.JoinedAt) / 1000:0}s, pings {s.PingsSent}/pongs {s.PongsReceived}, " +
                $"rtt min/avg/max {(s.PongsReceived > 0 ? s.MinRtt : 0):0.0}/{s.AvgRtt:0.0}/{s.MaxRtt:0.0}ms, {(s.Relay ? "relay" : "direct")}, stalls {s.Stalls}, " +
                $"ghost states sent {s.GhostSent} / received {s.GhostReceived} (they reported {s.TheirSentToUs} sent: loss {s.LossPercent:0.00}%), " +
                $"{ghostInfo}, ignored other-field {GhostManager.IgnoredOtherField}, bad packets {GhostManager.IgnoredBadPacket}, spawn failures {GhostManager.SpawnFailures}");
        }

        private static Boolean TryGetGhost(UInt64 peer, out GhostActor ghost)
        {
            foreach (GhostActor g in GhostManager.All)
            {
                if (g.Peer == peer)
                {
                    ghost = g;
                    return true;
                }
            }
            ghost = null;
            return false;
        }

        private void OnApplicationQuit()
        {
            // Memoria's quit confirmation can make Unity call this twice; log once, and dispose nothing in case the
            // quit is cancelled (Steam leaves the lobby itself when the game exits)
            if (_finalLogged || _init != InitState.Ready)
                return;
            _finalLogged = true;
            foreach (PeerStats s in _peers.Values)
                LogSummary(s, "FINAL");
        }

        // ---- Status box -----------------------------------------------------------------------------------------

        private void OnGUI()
        {
            if (_init == InitState.Waiting)
                return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, wordWrap = false };
                _style.normal.textColor = Color.white;
            }
            // Always shown (status box or not): the M5 freeze notice, then the toasts
            String notice = GuestGuard.Frozen ? GuestGuard.FrozenMessage : null;
            String toast = PartySync.CurrentToast;
            Single noticeY = Screen.height * 0.75f;
            foreach (String line in new[] { notice, BattleEntry.Notice, toast })
            {
                if (line == null)
                    continue;
                GUIContent message = new GUIContent(line);
                Vector2 messageSize = _style.CalcSize(message);
                GUI.Box(new Rect((Screen.width - messageSize.x) / 2f, noticeY, messageSize.x + 16, messageSize.y + 8), message, _style);
                noticeY += messageSize.y + 14;
            }
            if (!_showStatus)
                return;
            StringBuilder text = new StringBuilder();
            if (_init == InitState.NoSteam)
            {
                text.Append("Online: Steam unavailable");
            }
            else
            {
                text.Append($"Online  lobby: {_lobby.State}");
                if (_lobby.LobbyId != 0)
                    text.Append($"  host: {SteamNative.GetName(_lobby.OwnerId)}{(_lobby.IsHost ? " (you)" : "")}");
                text.Append($"\nPeers: {_transport.Peers.Count}");
                if (!_gamePumps)
                    text.Append("  (Steam callbacks start at the title screen)");
                foreach (PeerStats s in _peers.Values)
                    text.Append($"\n  {SteamNative.GetName(s.Id)}: rtt {(s.LastRtt >= 0 ? $"{s.LastRtt:0}ms" : "-")} (avg {s.AvgRtt:0}), {(s.Relay ? "relay" : "direct")}, loss {s.LossPercent:0.0}%{(s.Stalled ? "  STALLED" : "")}");
                if (!String.IsNullOrEmpty(_lobby.LastEvent))
                    text.Append($"\n{_lobby.LastEvent}");
            }
            Int32 shown = 0;
            foreach (GhostActor g in GhostManager.All)
                if (g.Visible)
                    shown++;
            text.Append($"\nGhosts: {shown} shown / {GhostManager.Count}");
            foreach (GhostActor g in GhostManager.All)
                text.Append($"\n  {(g.Peer == ShadowGhost.Peer ? "shadow" : SteamNative.GetName(g.Peer))} ({g.Model.Name}): {(g.Visible ? "shown" : "hidden")}, delay {g.DelayMs:0}ms, jitter {g.JitterMs:0}ms");
            if (_init == InitState.Ready && _lobby.State == SteamLobby.LobbyState.None)
                text.Append("\nCtrl+Shift: H host, I invite, J join, L leave, O hide");
            if (_debug)
            {
                text.Append($"\nDebug: G shadow {(_shadow.Enabled ? "ON" : "off")}, P pause{(NowMs < _shadow.PausedUntilMs ? " (PAUSED)" : "")}, K net sim {(_shadow.NetworkSimulation ? "ON" : "off")}, ");
                text.Append($"M model {(GhostManager.ModelOverride?.Name ?? "sender's")}, N interp {(GhostManager.Interpolate ? "on" : "OFF")}, D delay {(GhostManager.FixedDelayMs < 0 ? "adaptive" : "fixed")}");
                text.Append($"\n{_party?.Describe()}  Y resync guests (host)");
                if (_battle != null)
                    text.Append($"\n{_battle.Describe()}");
                text.Append($"\n{BodySwap.Describe()}\n  C pretend to play another party member (reloads the field), F7 Player/Cast, F8 acting character");
                text.Append($"\nSnapshot: {(_snapshot != null ? $"{_snapshot.Length / 1024.0:0.0} KB, {_snapshotSource}" : "none")}{(_blobs.IsSending ? " (sending...)" : "")}  S capture, T latest slot (title), R load, B send");
            }
            GUIContent content = new GUIContent(text.ToString());
            Vector2 size = _style.CalcSize(content);
            GUI.Box(new Rect(8, 8, size.x + 8, size.y + 4), content, _style);
            _session?.OnGUI();
            _session?.OnControlGUI();
        }

        private Boolean IsConnected(UInt64 peer)
        {
            return _peers.TryGetValue(peer, out PeerStats s) && s.PongsReceived > 0;
        }

        // ---- Byte helpers ----------------------------------------------------------------------------------------

        private static void WriteUInt32(Byte[] b, Int32 o, UInt32 v)
        {
            b[o] = (Byte)v; b[o + 1] = (Byte)(v >> 8); b[o + 2] = (Byte)(v >> 16); b[o + 3] = (Byte)(v >> 24);
        }

        private static UInt32 ReadUInt32(Byte[] b, Int32 o)
        {
            return b[o] | ((UInt32)b[o + 1] << 8) | ((UInt32)b[o + 2] << 16) | ((UInt32)b[o + 3] << 24);
        }

        private static void WriteInt64(Byte[] b, Int32 o, Int64 v)
        {
            WriteUInt32(b, o, (UInt32)v);
            WriteUInt32(b, o + 4, (UInt32)(v >> 32));
        }

        private static Int64 ReadInt64(Byte[] b, Int32 o)
        {
            return (Int64)(ReadUInt32(b, o) | ((UInt64)ReadUInt32(b, o + 4) << 32));
        }
    }
}
