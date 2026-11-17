using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Memoria.Online
{
    /// <summary>
    /// <see cref="ITransport"/> over the legacy ISteamNetworking (SteamNetworking005) P2P API.
    /// Peers are the other members of the Steam lobby; the host is the lobby owner.
    ///
    /// Sessions: when a member appears, both sides call AcceptP2PSessionWithUser and send a hello on the
    /// control channel, so the session opens from both ends without depending on P2PSessionRequest_t.
    /// The P2PSessionRequest_t callback is still registered (accepts lobby members only) and
    /// P2PSessionConnectFail_t is logged.
    /// </summary>
    internal sealed class SteamTransport : ITransport, IDisposable
    {
        public const Byte MsgHello = 0xF0;
        /// <summary>Unreliable packets above this size are rejected by Steam.</summary>
        public const Int32 MaxUnreliableSize = 1200;

        private readonly SteamLobby _lobby;
        private readonly List<UInt64> _peers = new List<UInt64>();
        private readonly List<SteamCallback> _callbacks = new List<SteamCallback>();
        private readonly Byte[] _receiveBuffer = new Byte[64 * 1024];
        private readonly Queue<KeyValuePair<UInt64, Boolean>> _peerEvents = new Queue<KeyValuePair<UInt64, Boolean>>();
        private readonly Dictionary<UInt64, Int32> _droppedFromStrangers = new Dictionary<UInt64, Int32>();

        public Int32 PacketsSent { get; private set; }
        public Int32 PacketsReceived { get; private set; }
        public Int32 SendFailures { get; private set; }

        public SteamTransport(SteamLobby lobby)
        {
            _lobby = lobby;
            Boolean relay = SteamNative.AllowP2PPacketRelay(SteamNative.Networking, true);
            Log.Message($"[SteamSpike] AllowP2PPacketRelay(true) -> {relay}");
            _lobby.MemberJoined += OnMemberJoined;
            _lobby.MemberLeft += OnMemberLeft;
            _callbacks.Add(new SteamCallback(SteamNative.CB_P2PSessionRequest, 8, OnP2PSessionRequest));
            _callbacks.Add(new SteamCallback(SteamNative.CB_P2PSessionConnectFail, 16, OnP2PSessionConnectFail));
        }

        public Boolean IsActive => _lobby.State == SteamLobby.LobbyState.InLobby;
        public UInt64 LocalPeer => _lobby.LocalId;
        public UInt64 HostPeer => _lobby.OwnerId;
        public IList<UInt64> Peers => _peers;

        public event Action<UInt64> PeerJoined;
        public event Action<UInt64> PeerLeft;

        public Boolean Send(UInt64 peer, Int32 channel, Byte[] data, Int32 length, Boolean reliable)
        {
            if (!reliable && length > MaxUnreliableSize)
                throw new ArgumentException($"Unreliable packets are limited to {MaxUnreliableSize} bytes ({length})");
            Boolean ok = SteamNative.SendP2PPacket(SteamNative.Networking, peer, data, (UInt32)length,
                reliable ? SteamNative.P2PSendReliable : SteamNative.P2PSendUnreliable, channel);
            if (ok)
                PacketsSent++;
            else
                SendFailures++;
            return ok;
        }

        public void Poll(List<TransportMessage> received)
        {
            while (_peerEvents.Count > 0)
            {
                KeyValuePair<UInt64, Boolean> ev = _peerEvents.Dequeue();
                if (ev.Value)
                    PeerJoined?.Invoke(ev.Key);
                else
                    PeerLeft?.Invoke(ev.Key);
            }

            for (Int32 channel = 0; channel < TransportChannel.Count; channel++)
            {
                while (SteamNative.IsP2PPacketAvailable(SteamNative.Networking, out UInt32 size, channel))
                {
                    if (!SteamNative.ReadP2PPacket(SteamNative.Networking, _receiveBuffer, (UInt32)_receiveBuffer.Length, out size, out UInt64 sender, channel))
                        break;
                    if (!_peers.Contains(sender) && IsActive)
                        _lobby.RefreshMembers(); // a new member can send before the periodic membership poll sees them
                    if (!_peers.Contains(sender))
                    {
                        _droppedFromStrangers.TryGetValue(sender, out Int32 dropped);
                        _droppedFromStrangers[sender] = dropped + 1;
                        if (dropped == 0)
                            Log.Message($"[SteamSpike] Ignoring packets from {sender}, not a lobby member (yet)");
                        continue;
                    }
                    PacketsReceived++;
                    if (channel == TransportChannel.Control && size >= 1 && _receiveBuffer[0] == MsgHello)
                        continue; // session opener, nothing to deliver
                    Byte[] data = new Byte[size];
                    Buffer.BlockCopy(_receiveBuffer, 0, data, 0, (Int32)size);
                    received.Add(new TransportMessage(sender, channel, data));
                }
            }
        }

        public SteamNative.P2PSessionState GetSessionState(UInt64 peer)
        {
            SteamNative.GetP2PSessionState(SteamNative.Networking, peer, out SteamNative.P2PSessionState state);
            return state;
        }

        private void OnMemberJoined(UInt64 member)
        {
            if (_peers.Contains(member))
                return;
            _peers.Add(member);
            Boolean accepted = SteamNative.AcceptP2PSessionWithUser(SteamNative.Networking, member);
            Boolean hello = Send(member, TransportChannel.Control, new Byte[] { MsgHello }, 1, true);
            Log.Message($"[SteamSpike] Peer {SteamNative.GetName(member)} ({member}): accept={accepted}, hello sent={hello}");
            _peerEvents.Enqueue(new KeyValuePair<UInt64, Boolean>(member, true));
        }

        private void OnMemberLeft(UInt64 member)
        {
            if (!_peers.Remove(member))
                return;
            SteamNative.CloseP2PSessionWithUser(SteamNative.Networking, member);
            Log.Message($"[SteamSpike] Peer {SteamNative.GetName(member)} ({member}) left; P2P session closed");
            _peerEvents.Enqueue(new KeyValuePair<UInt64, Boolean>(member, false));
        }

        private void OnP2PSessionRequest(IntPtr param)
        {
            UInt64 remote = (UInt64)Marshal.ReadInt64(param, 0);
            Boolean member = _lobby.Members.Contains(remote);
            Boolean accepted = member && SteamNative.AcceptP2PSessionWithUser(SteamNative.Networking, remote);
            Log.Message($"[SteamSpike] Callback P2PSessionRequest_t from {SteamNative.GetName(remote)} ({remote}): lobby member={member}, accepted={accepted}");
        }

        private void OnP2PSessionConnectFail(IntPtr param)
        {
            UInt64 remote = (UInt64)Marshal.ReadInt64(param, 0);
            Byte error = Marshal.ReadByte(param, 8);
            // EP2PSessionError: 1 NotRunningApp, 2 NoRightsToApp, 3 DestinationNotLoggedIn, 4 Timeout
            Log.Warning($"[SteamSpike] Callback P2PSessionConnectFail_t: {SteamNative.GetName(remote)} ({remote}) error {error}");
        }

        public void Dispose()
        {
            foreach (UInt64 peer in _peers)
                SteamNative.CloseP2PSessionWithUser(SteamNative.Networking, peer);
            _peers.Clear();
            _lobby.MemberJoined -= OnMemberJoined;
            _lobby.MemberLeft -= OnMemberLeft;
            foreach (SteamCallback cb in _callbacks)
                cb.Dispose();
            _callbacks.Clear();
        }
    }
}
