using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace Memoria.Online
{
    /// <summary>
    /// Spike A test driver. Attached to the SceneDirector at startup; dormant until a hotkey is used
    /// (or the game is started with "+connect_lobby &lt;id&gt;", or a Steam invite is accepted).
    ///
    ///   Ctrl+Shift+H  host: create a friends-only lobby (4 players); its ID is copied to the clipboard
    ///   Ctrl+Shift+I  open the Steam overlay's invite dialog
    ///   Ctrl+Shift+J  join: the lobby ID in the clipboard if there is one, else a friend's FF9 lobby
    ///   Ctrl+Shift+L  leave the lobby
    ///   Ctrl+Shift+O  show/hide the on-screen status
    ///
    /// Once in a lobby with other members, it exercises the three channels:
    ///   ch 0 (reliable): ping every second, echoed; RTT is logged every second per peer
    ///   ch 1 (unreliable): 15 Hz 48-byte "ghost" packets with sequence numbers; loss is measured
    ///   ch 2 (reliable): a sequenced "state" packet every 2 seconds; ordering is checked
    /// </summary>
    public sealed class SteamSpike : MonoBehaviour
    {
        private const Byte MsgPing = 1;
        private const Byte MsgPong = 2;
        private const Single StallSeconds = 5f;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static Double Now => Clock.Elapsed.TotalSeconds;

        private enum InitState { Waiting, Ready, Failed }

        private sealed class PeerStats
        {
            public String Name;
            public Double JoinedAt;
            public Double LastReceived;
            public Boolean Stalled;
            public Int32 Stalls;
            public UInt32 PingSeq;
            public Int32 PingsSent, PongsReceived;
            public Double LastRtt = -1, MinRtt = Double.MaxValue, MaxRtt, SumRtt;
            public Int32 SecondPongs;
            public Double SecondSumRtt;
            public UInt32 GhostFirst, GhostHighest;
            public Int32 GhostReceived;
            public Boolean GhostAny;
            public UInt32 StateExpected;
            public Int32 StateReceived, StateOutOfOrder;
            public Boolean UsingRelay;
            public Boolean SessionActive;

            public Double AvgRtt => PongsReceived > 0 ? SumRtt / PongsReceived : -1;
            public Double GhostLoss => GhostAny ? 1.0 - GhostReceived / (Double)(GhostHighest - GhostFirst + 1) : 0;
        }

        private InitState _init = InitState.Waiting;
        private Boolean _pumpingOurselves;
        private SteamLobby _lobby;
        private SteamTransport _transport;
        private readonly Dictionary<UInt64, PeerStats> _stats = new Dictionary<UInt64, PeerStats>();
        private readonly List<TransportMessage> _received = new List<TransportMessage>();
        private readonly Byte[] _pingBuffer = new Byte[13];
        private readonly Byte[] _ghostBuffer = new Byte[48];
        private readonly Byte[] _stateBuffer = new Byte[16];
        private Double _nextPing, _nextGhost, _nextState, _nextSummary;
        private UInt32 _ghostSeq, _stateSeq;
        private Boolean _showOverlay = true;
        private GUIStyle _style;

        private void Update()
        {
            try
            {
                if (_init == InitState.Waiting)
                    TryInitialize();
                if (_init != InitState.Ready)
                    return;

                // The game only pumps SteamAPI_RunCallbacks (SceneDirector.Update -> Social.ProcessCallbacks ->
                // steamwrapper!ProcessCallbacks_Internal) once the title screen has created the social platform.
                // Before that, pump ourselves; never both.
                Boolean gamePumps = SiliconStudio.Social.IsCreated;
                if (gamePumps == _pumpingOurselves)
                {
                    _pumpingOurselves = !gamePumps;
                    Log.Message($"[SteamSpike] SteamAPI_RunCallbacks is now pumped by {(_pumpingOurselves ? "SteamSpike" : "the game")}");
                }
                if (_pumpingOurselves)
                    SteamNative.RunCallbacks();

                HandleHotkeys();
                _lobby.Update((Single)Now);
                _received.Clear();
                _transport.Poll(_received);
                foreach (TransportMessage msg in _received)
                    HandleMessage(msg);
                if (_transport.IsActive && _transport.Peers.Count > 0)
                    SendTraffic();
            }
            catch (Exception err)
            {
                Log.Error(err, "[SteamSpike] Disabled after an error");
                _init = InitState.Failed;
            }
        }

        private void TryInitialize()
        {
            if (!SteamSdkWrapper.IsInitialized)
                return;
            if (!SteamNative.TryLoad())
            {
                _init = InitState.Failed;
                return;
            }
            _lobby = new SteamLobby();
            _transport = new SteamTransport(_lobby);
            _transport.PeerJoined += OnPeerJoined;
            _transport.PeerLeft += OnPeerLeft;
            _init = InitState.Ready;
            Log.Message("[SteamSpike] Ready. Ctrl+Shift+H host, Ctrl+Shift+I invite, Ctrl+Shift+J join, Ctrl+Shift+L leave, Ctrl+Shift+O status");

            // Steam starts the game with "+connect_lobby <id>" when an invite is accepted while the game isn't running
            String[] args = Environment.GetCommandLineArgs();
            Log.Message($"[SteamSpike] Command line: {String.Join(" ", args)}");
            for (Int32 i = 0; i + 1 < args.Length; i++)
                if (args[i] == "+connect_lobby" && UInt64.TryParse(args[i + 1], out UInt64 lobbyId))
                    _lobby.Join(lobbyId);
        }

        private void HandleHotkeys()
        {
            Boolean ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            Boolean shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!ctrl || !shift)
                return;
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
                if (UInt64.TryParse(clipboard, out UInt64 lobbyId) && (lobbyId >> 52) == 0x018) // universe 1, account type 8 (chat/lobby)
                    _lobby.Join(lobbyId);
                else
                    _lobby.JoinFriendLobby();
            }
            else if (Input.GetKeyDown(KeyCode.L))
            {
                _lobby.Leave();
            }
            else if (Input.GetKeyDown(KeyCode.O))
            {
                _showOverlay = !_showOverlay;
            }
        }

        private void OnPeerJoined(UInt64 peer)
        {
            _stats[peer] = new PeerStats { Name = SteamNative.GetName(peer), JoinedAt = Now, LastReceived = Now };
            Log.Message($"[SteamSpike] Connected to {_transport.Peers.Count} peer(s); +{_stats[peer].Name}");
        }

        private void OnPeerLeft(UInt64 peer)
        {
            if (_stats.TryGetValue(peer, out PeerStats s))
            {
                LogSummary(peer, s, "LEFT");
                _stats.Remove(peer);
            }
            Log.Message($"[SteamSpike] Connected to {_transport.Peers.Count} peer(s)");
        }

        private void SendTraffic()
        {
            Double now = Now;
            if (now >= _nextPing)
            {
                _nextPing = now + 1.0;
                LogSecond(now);
                foreach (UInt64 peer in _transport.Peers)
                {
                    if (!_stats.TryGetValue(peer, out PeerStats s))
                        continue; // its PeerJoined event is dispatched on the next Poll
                    s.PingSeq++;
                    s.PingsSent++;
                    _pingBuffer[0] = MsgPing;
                    WriteUInt32(_pingBuffer, 1, s.PingSeq);
                    WriteInt64(_pingBuffer, 5, Clock.ElapsedTicks);
                    _transport.Send(peer, TransportChannel.Control, _pingBuffer, _pingBuffer.Length, true);
                }
            }
            if (now >= _nextGhost)
            {
                _nextGhost = now + 1.0 / 15.0;
                _ghostSeq++;
                WriteUInt32(_ghostBuffer, 0, _ghostSeq);
                foreach (UInt64 peer in _transport.Peers)
                    _transport.Send(peer, TransportChannel.Movement, _ghostBuffer, _ghostBuffer.Length, false);
            }
            if (now >= _nextState)
            {
                _nextState = now + 2.0;
                _stateSeq++;
                WriteUInt32(_stateBuffer, 0, _stateSeq);
                foreach (UInt64 peer in _transport.Peers)
                    _transport.Send(peer, TransportChannel.State, _stateBuffer, _stateBuffer.Length, true);
            }
            if (now >= _nextSummary)
            {
                if (_nextSummary > 0)
                    foreach (KeyValuePair<UInt64, PeerStats> kv in _stats)
                        LogSummary(kv.Key, kv.Value, "SUMMARY");
                _nextSummary = now + 30.0;
            }
        }

        private void HandleMessage(TransportMessage msg)
        {
            if (!_stats.TryGetValue(msg.Peer, out PeerStats s))
                return;
            Double now = Now;
            s.LastReceived = now;
            if (s.Stalled)
            {
                s.Stalled = false;
                Log.Message($"[SteamSpike] {s.Name}: traffic resumed");
            }
            Byte[] d = msg.Data;
            switch (msg.Channel)
            {
                case TransportChannel.Control:
                    if (d.Length == 13 && d[0] == MsgPing)
                    {
                        d[0] = MsgPong;
                        _transport.Send(msg.Peer, TransportChannel.Control, d, d.Length, true);
                    }
                    else if (d.Length == 13 && d[0] == MsgPong)
                    {
                        Int64 sentTicks = ReadInt64(d, 5);
                        Double rtt = (Clock.ElapsedTicks - sentTicks) * 1000.0 / Stopwatch.Frequency;
                        s.PongsReceived++;
                        s.LastRtt = rtt;
                        s.SumRtt += rtt;
                        s.MinRtt = Math.Min(s.MinRtt, rtt);
                        s.MaxRtt = Math.Max(s.MaxRtt, rtt);
                        s.SecondPongs++;
                        s.SecondSumRtt += rtt;
                    }
                    break;
                case TransportChannel.Movement:
                    if (d.Length >= 4)
                    {
                        UInt32 seq = ReadUInt32(d, 0);
                        if (!s.GhostAny)
                        {
                            s.GhostAny = true;
                            s.GhostFirst = seq;
                        }
                        if (seq >= s.GhostFirst)
                        {
                            s.GhostReceived++;
                            s.GhostHighest = Math.Max(s.GhostHighest, seq);
                        }
                    }
                    break;
                case TransportChannel.State:
                    if (d.Length >= 4)
                    {
                        UInt32 seq = ReadUInt32(d, 0);
                        if (s.StateReceived > 0 && seq != s.StateExpected)
                        {
                            s.StateOutOfOrder++;
                            Log.Warning($"[SteamSpike] {s.Name}: state packet {seq} received, expected {s.StateExpected}");
                        }
                        s.StateReceived++;
                        s.StateExpected = seq + 1;
                    }
                    break;
            }
        }

        private void LogSecond(Double now)
        {
            if (_stats.Count == 0)
                return;
            StringBuilder line = new StringBuilder();
            line.Append($"[SteamSpike] Connected to {_transport.Peers.Count} peer(s), host {SteamNative.GetName(_transport.HostPeer)}{(_lobby.IsHost ? " (you)" : "")}");
            foreach (KeyValuePair<UInt64, PeerStats> kv in _stats)
            {
                PeerStats s = kv.Value;
                SteamNative.P2PSessionState session = _transport.GetSessionState(kv.Key);
                s.UsingRelay = session.UsingRelay != 0;
                s.SessionActive = session.ConnectionActive != 0;
                if (!s.Stalled && now - s.LastReceived > StallSeconds)
                {
                    s.Stalled = true;
                    s.Stalls++;
                    Log.Warning($"[SteamSpike] {s.Name}: no packet for {now - s.LastReceived:0.0}s (session active={s.SessionActive}, error={session.P2PSessionError}, queued={session.PacketsQueuedForSend})");
                }
                String rtt = s.SecondPongs > 0 ? $"{s.SecondSumRtt / s.SecondPongs:0.0}ms" : "no pong";
                line.Append($" | {s.Name}: rtt {rtt}, {(s.UsingRelay ? "relay" : "direct")}, ghost loss {s.GhostLoss * 100:0.0}%, up {now - s.JoinedAt:0}s{(s.Stalled ? ", STALLED" : "")}");
                s.SecondPongs = 0;
                s.SecondSumRtt = 0;
            }
            Log.Message(line.ToString());
        }

        private void LogSummary(UInt64 peer, PeerStats s, String tag)
        {
            Log.Message($"[SteamSpike] {tag} {s.Name} ({peer}): up {Now - s.JoinedAt:0}s, pings {s.PingsSent}/pongs {s.PongsReceived}, " +
                $"rtt min/avg/max {(s.PongsReceived > 0 ? s.MinRtt : 0):0.0}/{s.AvgRtt:0.0}/{s.MaxRtt:0.0}ms, " +
                $"ghost {s.GhostReceived} received, loss {s.GhostLoss * 100:0.00}%, state {s.StateReceived} received / {s.StateOutOfOrder} out of order, " +
                $"stalls {s.Stalls}, {(s.UsingRelay ? "relay" : "direct")}, sent {_transport.PacketsSent} recv {_transport.PacketsReceived} sendFail {_transport.SendFailures}, callbacks fired {_lobby.CallbacksFired}");
        }

        private void OnGUI()
        {
            if (!_showOverlay || _init == InitState.Waiting)
                return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, wordWrap = false };
                _style.normal.textColor = Color.white;
            }
            StringBuilder text = new StringBuilder();
            if (_init == InitState.Failed)
            {
                text.Append("SteamSpike: disabled (see Memoria.log)");
            }
            else
            {
                text.Append($"SteamSpike  lobby: {_lobby.State}");
                if (_lobby.LobbyId != 0)
                    text.Append($" {_lobby.LobbyId}  host: {SteamNative.GetName(_lobby.OwnerId)}{(_lobby.IsHost ? " (you)" : "")}");
                text.Append($"\nConnected to {_transport.Peers.Count} peer(s)");
                foreach (KeyValuePair<UInt64, PeerStats> kv in _stats)
                {
                    PeerStats s = kv.Value;
                    text.Append($"\n  {s.Name}: rtt {(s.LastRtt >= 0 ? $"{s.LastRtt:0}ms" : "-")} (avg {s.AvgRtt:0}), {(s.UsingRelay ? "relay" : "direct")}, loss {s.GhostLoss * 100:0.0}%{(s.Stalled ? "  STALLED" : "")}");
                }
                if (!String.IsNullOrEmpty(_lobby.LastEvent))
                    text.Append($"\n{_lobby.LastEvent}");
                if (_lobby.State == SteamLobby.LobbyState.None)
                    text.Append("\nCtrl+Shift: H host, I invite, J join, L leave, O hide");
            }
            GUIContent content = new GUIContent(text.ToString());
            Vector2 size = _style.CalcSize(content);
            GUI.Box(new Rect(8, 8, size.x + 8, size.y + 4), content, _style);
        }

        private void OnApplicationQuit()
        {
            if (_init != InitState.Ready)
                return;
            foreach (KeyValuePair<UInt64, PeerStats> kv in _stats)
                LogSummary(kv.Key, kv.Value, "FINAL");
            _transport.Dispose();
            _lobby.Dispose();
        }

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
