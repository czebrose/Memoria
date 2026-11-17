using System;
using System.Collections.Generic;

namespace Memoria.Online
{
    /// <summary>Channel numbers shared by every transport.</summary>
    public static class TransportChannel
    {
        /// <summary>Control messages (hello, ping/pong). Reliable.</summary>
        public const Int32 Control = 0;
        /// <summary>Movement / ghost positions. Unreliable.</summary>
        public const Int32 Movement = 1;
        /// <summary>Shared state. Reliable.</summary>
        public const Int32 State = 2;

        public const Int32 Count = 3;
    }

    public struct TransportMessage
    {
        public UInt64 Peer;
        public Int32 Channel;
        public Byte[] Data;

        public TransportMessage(UInt64 peer, Int32 channel, Byte[] data)
        {
            Peer = peer;
            Channel = channel;
            Data = data;
        }
    }

    /// <summary>
    /// The only network surface the rest of the mod talks to.
    /// A peer is identified by a 64-bit id (the SteamID for <see cref="SteamTransport"/>;
    /// a LiteNetLib fallback would hand out its own ids).
    /// </summary>
    public interface ITransport
    {
        Boolean IsActive { get; }
        UInt64 LocalPeer { get; }
        /// <summary>The peer that owns the session (the listen host).</summary>
        UInt64 HostPeer { get; }
        /// <summary>Remote peers currently in the session (not including the local one).</summary>
        IList<UInt64> Peers { get; }

        event Action<UInt64> PeerJoined;
        event Action<UInt64> PeerLeft;

        Boolean Send(UInt64 peer, Int32 channel, Byte[] data, Int32 length, Boolean reliable);
        /// <summary>Call once per frame: drains received packets into <paramref name="received"/> and raises peer events.</summary>
        void Poll(List<TransportMessage> received);
    }
}
