using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Memoria.Online.Battle
{
    /// <summary>
    /// Step 3, owner side: turns the recorder's log into <see cref="BattleWire"/> messages for the participants.
    ///
    /// Pacing ("buffer by one action"): a command is sent only once it is finished (its tape is complete then), and
    /// nothing recorded after it goes out before it. So the passive PC always holds a whole action before it starts
    /// it: its animation never reaches an effect point whose outcome hasn't arrived, and a death or tick never shows
    /// before the animation that causes it. Events between actions (ticks, scripts) go out at once.
    ///
    /// ATB: not in the ordered stream. Per unit, "value + rate" samples, sent when the passive's extrapolation would be
    /// off by more than 1/64 of the gauge, at most every 250 ms (a gauge reset goes at once); the passive interpolates.
    /// </summary>
    public sealed class BattleStreamOut
    {
        private const Single AtbMinInterval = 0.25f;

        private sealed class AtbTrack
        {
            public Boolean Sent;
            public Int16 SentValue, Prev;
            public Single SentRate, SentTime;
        }

        private readonly BattleRecorder _recorder;
        private Int32 _next;            // first log event not sent yet
        private BattleSnapshot _sent;   // owner's state as last sent
        private readonly Dictionary<UInt16, AtbTrack> _atb = new Dictionary<UInt16, AtbTrack>();
        private readonly MemoryStream _pending = new MemoryStream();
        private readonly BinaryWriter _w;

        public Int32 EventsSent, BytesSent, Messages, AtbSamples, AtbBytes;
        public Boolean EndSent;
        private Single _firstAtbTime = -1f;

        public BattleStreamOut(BattleRecorder recorder)
        {
            _recorder = recorder;
            _w = new BinaryWriter(_pending);
        }

        public Single AtbRate => _firstAtbTime < 0f || Time.realtimeSinceStartup - _firstAtbTime < 1f ? 0f : AtbSamples / (Time.realtimeSinceStartup - _firstAtbTime);

        /// <summary>After each battle frame (owner): ATB samples.</summary>
        public void SampleAtb()
        {
            if (!_recorder.Started || EndSent)
                return;
            Single now = Time.realtimeSinceStartup;
            List<KeyValuePair<UInt16, AtbTrack>> due = null;
            for (BTL_DATA btl = FF9StateSystem.Battle.FF9Battle.btl_list.next; btl != null; btl = btl.next)
            {
                if (!_atb.TryGetValue(btl.btl_id, out AtbTrack t))
                    _atb[btl.btl_id] = t = new AtbTrack { Prev = btl.cur.at };
                Int16 at = btl.cur.at, max = btl.max.at;
                Boolean reset = at < t.Prev;
                Single predicted = Mathf.Clamp(t.SentValue + t.SentRate * (now - t.SentTime), 0f, max);
                Boolean off = Mathf.Abs(at - predicted) > max / 64f + 1f;
                if (!t.Sent || reset || (off && now - t.SentTime >= AtbMinInterval))
                {
                    Single rate;
                    if (at >= max)
                        rate = 0f;
                    else if (reset || !t.Sent || now - t.SentTime < 0.05f)
                        rate = t.SentRate; // a refill usually runs at the speed of the last one
                    else
                        rate = (at - t.SentValue) / (now - t.SentTime);
                    t.Sent = true;
                    t.SentValue = at;
                    t.SentRate = Mathf.Max(0f, rate);
                    t.SentTime = now;
                    if (due == null)
                        due = new List<KeyValuePair<UInt16, AtbTrack>>();
                    due.Add(new KeyValuePair<UInt16, AtbTrack>(btl.btl_id, t));
                }
                t.Prev = at;
            }
            if (due == null)
                return;
            if (_firstAtbTime < 0f)
                _firstAtbTime = now;
            Int64 start = _pending.Length;
            _w.Write(BattleWire.AtbSample);
            _w.Write((UInt16)(1 + due.Count * 6));
            _w.Write((Byte)due.Count);
            foreach (KeyValuePair<UInt16, AtbTrack> kv in due)
            {
                _w.Write(kv.Key);
                _w.Write(kv.Value.SentValue);
                _w.Write((Int16)Mathf.Clamp(Mathf.RoundToInt(kv.Value.SentRate), Int16.MinValue, Int16.MaxValue));
            }
            AtbSamples += due.Count;
            AtbBytes += (Int32)(_pending.Length - start);
        }

        /// <summary>The next message for the participants (null: nothing to send). All of it once the battle ended.</summary>
        public Byte[] TakeMessage(UInt16 seq)
        {
            List<BattleEvent> events = _recorder.Log.Events;
            Int32 boundary = _recorder.Log.Complete ? events.Count : _recorder.FirstOpenCommandIndex(events.Count);
            for (; _next < boundary; _next++)
            {
                BattleEvent ev = events[_next];
                if (ev.Type == BattleEventType.Atb)
                    continue; // the debug log's own ATB format; the stream sends samples
                Int64 start = _pending.Length;
                _w.Write((Byte)ev.Type);
                _w.Write((UInt16)0); // length, patched below
                BattleWire.WriteEvent(_w, ev, ref _sent);
                Int64 end = _pending.Length;
                _pending.Position = start + 1;
                _w.Write((UInt16)(end - start - 3));
                _pending.Position = end;
                EventsSent++;
                if (ev.Type == BattleEventType.End)
                    EndSent = true;
            }
            if (_pending.Length == 0)
                return null;
            Byte[] body = _pending.ToArray();
            _pending.SetLength(0);
            Byte[] message = new Byte[3 + body.Length];
            message[0] = BattleWire.MsgStream;
            message[1] = (Byte)seq;
            message[2] = (Byte)(seq >> 8);
            Buffer.BlockCopy(body, 0, message, 3, body.Length);
            BytesSent += message.Length;
            Messages++;
            return message;
        }

        /// <summary>Bytes the spike's ATB format (every unit every 3 frames when one changed) would have needed.</summary>
        public Int32 OldAtbBytes()
        {
            Int32 total = 0;
            foreach (BattleEvent ev in _recorder.Log.Events)
                if (ev.Type == BattleEventType.Atb)
                    total += 2 + 2 * ev.AtbValues.Length;
            return total;
        }

        public String Status => $"sent {EventsSent} events, {BytesSent / 1024f:0.0} KB in {Messages} messages; ATB {AtbSamples} samples ({AtbRate:0.0}/s, {AtbBytes} B)";
    }

    /// <summary>
    /// Step 3, passive side: reads the owner's stream into a live <see cref="BattleLog"/> for the <see cref="BattleReplayer"/>,
    /// keeps the mirror of the owner's state, and plays the ATB samples back with interpolation.
    /// </summary>
    public sealed class BattleStreamIn
    {
        private sealed class AtbTarget
        {
            public Int16 Value;
            public Single Rate, At;
        }

        public readonly BattleLog Log = new BattleLog();
        private readonly BattleSnapshot _mirror = new BattleSnapshot();
        private readonly Dictionary<UInt16, AtbTarget> _atb = new Dictionary<UInt16, AtbTarget>();
        private readonly HashSet<Int32> _open = new HashSet<Int32>();
        private Int32 _released;
        private Single _heldSince = -1f;

        public Int32 EventsReceived, BytesReceived, AtbSamples;
        public Boolean EndReceived;
        private Single _firstAtbTime = -1f;

        /// <summary>Events the replayer may consume: whole actions only (the owner sends them that way already;
        /// this keeps it true if it ever doesn't), everything once End is in, or anything held over 3 s.</summary>
        public Int32 Released
        {
            get
            {
                if (_released < Log.Events.Count && _heldSince >= 0f && Time.realtimeSinceStartup - _heldSince > 3f)
                {
                    BattleSync.Note($"Stream: released {Log.Events.Count - _released} event(s) of an unfinished action after 3 s");
                    _released = Log.Events.Count;
                    _heldSince = -1f;
                }
                return _released;
            }
        }

        public Single AtbRate => _firstAtbTime < 0f || Time.realtimeSinceStartup - _firstAtbTime < 1f ? 0f : AtbSamples / (Time.realtimeSinceStartup - _firstAtbTime);

        public void OnMessage(Byte[] d)
        {
            BytesReceived += d.Length;
            using (BinaryReader r = new BinaryReader(new MemoryStream(d, 3, d.Length - 3)))
            {
                while (r.BaseStream.Position < r.BaseStream.Length)
                {
                    Byte type = r.ReadByte();
                    Int32 length = r.ReadUInt16();
                    Int64 end = r.BaseStream.Position + length;
                    if (type == BattleWire.AtbSample)
                        ReadAtb(r);
                    else
                        Add(BattleWire.ReadEvent((BattleEventType)type, r, _mirror));
                    if (r.BaseStream.Position != end)
                    {
                        BattleSync.Note($"Stream: event type {type} read {r.BaseStream.Position - (end - length)} of {length} bytes");
                        r.BaseStream.Position = end;
                    }
                }
            }
        }

        private void Add(BattleEvent ev)
        {
            EventsReceived++;
            Log.Events.Add(ev);
            if (ev.Type == BattleEventType.Start)
            {
                Log.MapNo = ev.MapNo;
                Log.Pattern = ev.Pattern;
                Log.StartType = ev.StartType;
                Log.StartPhase = ev.Result;
            }
            else if (ev.Type == BattleEventType.Command)
                _open.Add(ev.Seq);
            else if (ev.Type == BattleEventType.CommandEnd)
                _open.Remove(ev.Seq);
            else if (ev.Type == BattleEventType.End)
            {
                EndReceived = true;
                Log.Complete = true;
                _open.Clear();
            }
            if (_open.Count == 0)
            {
                _released = Log.Events.Count;
                _heldSince = -1f;
            }
            else if (_heldSince < 0f)
                _heldSince = Time.realtimeSinceStartup;
        }

        public void ReleaseAll()
        {
            _released = Log.Events.Count;
            _heldSince = -1f;
        }

        private void ReadAtb(BinaryReader r)
        {
            Single now = Time.realtimeSinceStartup;
            if (_firstAtbTime < 0f)
                _firstAtbTime = now;
            Int32 count = r.ReadByte();
            for (Int32 i = 0; i < count; i++)
            {
                UInt16 id = r.ReadUInt16();
                _atb[id] = new AtbTarget { Value = r.ReadInt16(), Rate = r.ReadInt16(), At = now };
                AtbSamples++;
            }
        }

        /// <summary>Each frame: every gauge at the owner's last sample, moved on at its rate (capped at full).</summary>
        public void ApplyAtb()
        {
            Single now = Time.realtimeSinceStartup;
            foreach (KeyValuePair<UInt16, AtbTarget> kv in _atb)
            {
                BTL_DATA btl = BattleSync.FindBtl(kv.Key);
                if (btl == null)
                    continue;
                AtbTarget t = kv.Value;
                btl.cur.at = (Int16)Mathf.Clamp(t.Value + t.Rate * (now - t.At), 0f, btl.max.at);
            }
        }

        public String Status => $"received {EventsReceived} events, {BytesReceived / 1024f:0.0} KB; ATB {AtbSamples} samples ({AtbRate:0.0}/s)";
    }
}
