using Memoria.Prime;
using System;
using System.Collections.Generic;

namespace Memoria.Online.Ghosts
{
    /// <summary>
    /// Debug: a ghost of the local player that replays their own movement ~1 s behind (spike B's "shadow"), for
    /// testing ghosts without a second PC. The states take the exact path of a received packet (serialized bytes →
    /// <see cref="GhostManager.OnPacket"/>), optionally through a simulated network (loss, jitter) or a paused feed.
    /// </summary>
    public sealed class ShadowGhost
    {
        public const UInt64 Peer = 0x5348414430574F; // "SHAD0W"
        public const Double FeedDelayMs = 1000;
        public const Double PauseMs = 6000;

        public Boolean Enabled { get; private set; }
        public Boolean NetworkSimulation;
        public Double PausedUntilMs;
        public Int32 LostInPause, LostInSimulation;

        private struct InFlight
        {
            public Double DeliverAt;
            public Byte[] Data;
        }

        private readonly List<InFlight> _inFlight = new List<InFlight>();
        private readonly Random _random = new Random();

        public void SetEnabled(Boolean enabled)
        {
            if (enabled == Enabled)
                return;
            Enabled = enabled;
            _inFlight.Clear();
            if (!enabled)
                GhostManager.Remove(Peer, "shadow ghost turned off");
            Log.Message($"[Online] Shadow ghost {(enabled ? "on" : "off")}");
        }

        public void Pause(Double nowMs)
        {
            PausedUntilMs = nowMs + PauseMs;
            Log.Message($"[Online] Shadow feed paused for {PauseMs / 1000:0} s");
        }

        /// <summary>Takes one sent state, as it would go to ITransport.Send.</summary>
        public void Feed(Byte[] data, Int32 length, Double nowMs)
        {
            if (!Enabled)
                return;
            if (nowMs < PausedUntilMs)
            {
                LostInPause++;
                return;
            }
            Double delay = FeedDelayMs;
            if (NetworkSimulation)
            {
                if (_random.NextDouble() < 0.10)
                {
                    LostInSimulation++;
                    return;
                }
                delay += _random.NextDouble() * 80.0;
            }
            Byte[] copy = new Byte[length];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            _inFlight.Add(new InFlight { DeliverAt = nowMs + delay, Data = copy });
        }

        /// <summary>Delivers due states in arrival order (which differs from send order with jitter on).</summary>
        public void Deliver(Double nowMs)
        {
            while (true)
            {
                Int32 next = -1;
                for (Int32 i = 0; i < _inFlight.Count; i++)
                    if (_inFlight[i].DeliverAt <= nowMs && (next < 0 || _inFlight[i].DeliverAt < _inFlight[next].DeliverAt))
                        next = i;
                if (next < 0)
                    return;
                Byte[] data = _inFlight[next].Data;
                _inFlight.RemoveAt(next);
                GhostManager.OnPacket(Peer, data, 0, data.Length, nowMs);
            }
        }
    }
}
