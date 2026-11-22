using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Memoria.Online.Battle
{
    /// <summary>
    /// Step 3, owner side: turns the recorder's log into <see cref="BattleWire"/> messages for the participants.
    ///
    /// Pacing ("frame lockstep with a margin"): every event goes out as soon as it is recorded, stamped with the owner's
    /// battle frame (counted from its Start event), and every message says how far the owner has run. The passive PC
    /// plays each event at the same frame and never runs a frame the owner hasn't run plus <see cref="BattleStreamIn.Margin"/>
    /// frames, so a command starts on both PCs at the same point of the battle, and its effects (recorded when the
    /// owner's animation reached them) are in before the passive's animation gets there. A command's tape grows while
    /// it runs: what it draws after its Command went out follows in CmdMore records.
    ///
    /// (The first version sent whole actions only: the passive ran an action or more behind the owner, a long summon
    /// 25 s behind, and was cut off when the owner's battle ended.)
    ///
    /// ATB: per unit, "value + rate" samples, sent when the passive's extrapolation would be off by more than 1/64 of
    /// the gauge, at most every 250 ms (a gauge reset goes at once); the passive applies them at their frame and
    /// interpolates.
    /// </summary>
    public sealed class BattleStreamOut
    {
        private const Single AtbMinInterval = 0.25f;
        private const Int32 HeartbeatFrames = 2; // a message at least every 2 battle frames, events or not

        private sealed class AtbTrack
        {
            public Boolean Sent;
            public Int16 SentValue, Prev;
            public Single SentRate, SentTime;
            // the gauge over the last few frames: the rate sent is its recent speed (a gauge that paused during an
            // animation and runs again would otherwise be sent with its average over the pause: far too slow)
            public readonly Queue<KeyValuePair<Single, Int16>> Recent = new Queue<KeyValuePair<Single, Int16>>();
        }

        private const Single RateWindow = 0.2f;

        private sealed class SentCommand
        {
            public BattleEvent Event;
            public Int32 TapeSent;
            public SByte SummonSent;
        }

        private readonly BattleRecorder _recorder;
        private Int32 _next;            // first log event not sent yet
        private BattleSnapshot _sent;   // owner's state as last sent
        private readonly Dictionary<UInt16, AtbTrack> _atb = new Dictionary<UInt16, AtbTrack>();
        private readonly List<SentCommand> _commands = new List<SentCommand>(); // sent, still running
        private readonly MemoryStream _pending = new MemoryStream();
        private readonly BinaryWriter _w;
        private Int32 _lastSentFrame = -1;
        private UInt16 _runMask;

        public Int32 EventsSent, BytesSent, Messages, AtbSamples, AtbBytes, TapeLate;
        public Boolean EndSent;
        private Single _firstAtbTime = -1f;

        public BattleStreamOut(BattleRecorder recorder)
        {
            _recorder = recorder;
            _w = new BinaryWriter(_pending);
        }

        public Single AtbRate => _firstAtbTime < 0f || Time.realtimeSinceStartup - _firstAtbTime < 1f ? 0f : AtbSamples / (Time.realtimeSinceStartup - _firstAtbTime);

        private Int64 BeginRecord(Byte type, Int32 frame)
        {
            Int64 start = _pending.Length;
            _w.Write(type);
            _w.Write((UInt16)0); // length, patched by EndRecord
            _w.Write((UInt32)Math.Max(0, frame));
            return start;
        }

        private void EndRecord(Int64 start)
        {
            Int64 end = _pending.Length;
            _pending.Position = start + 1;
            _w.Write((UInt16)(end - start - 3));
            _pending.Position = end;
        }

        /// <summary>After each battle frame (owner): ATB samples, and the "run" buttons.</summary>
        public void SampleAtb()
        {
            if (!_recorder.Started || EndSent)
                return;
            // M2: each player unit runs when its controlling PC holds the run buttons (BattleControl.EscapeKeyFor)
            UInt16 runMask = BattleControl.OwnerRunMask();
            if (runMask != _runMask)
            {
                _runMask = runMask;
                Int64 at = BeginRecord(BattleWire.RunMask, _recorder.RelativeFrame);
                _w.Write(runMask);
                EndRecord(at);
            }
            Single now = Time.realtimeSinceStartup;
            List<KeyValuePair<UInt16, AtbTrack>> due = null;
            for (BTL_DATA btl = FF9StateSystem.Battle.FF9Battle.btl_list.next; btl != null; btl = btl.next)
            {
                if (!_atb.TryGetValue(btl.btl_id, out AtbTrack t))
                    _atb[btl.btl_id] = t = new AtbTrack { Prev = btl.cur.at };
                Int16 at = btl.cur.at, max = btl.max.at;
                Boolean reset = at < t.Prev;
                if (reset)
                    t.Recent.Clear();
                t.Recent.Enqueue(new KeyValuePair<Single, Int16>(now, at));
                while (t.Recent.Count > 2 && now - t.Recent.Peek().Key > RateWindow)
                    t.Recent.Dequeue();
                Single predicted = Mathf.Clamp(t.SentValue + t.SentRate * (now - t.SentTime), 0f, max);
                Boolean off = Mathf.Abs(at - predicted) > max / 64f + 1f;
                if (!t.Sent || reset || (off && now - t.SentTime >= AtbMinInterval))
                {
                    Single rate;
                    if (at >= max)
                        rate = 0f;
                    else if (reset || !t.Sent || now - t.Recent.Peek().Key < 0.05f)
                        rate = t.SentRate; // a refill usually runs at the speed of the last one
                    else
                        rate = (at - t.Recent.Peek().Value) / (now - t.Recent.Peek().Key);
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
            Int64 start = BeginRecord(BattleWire.AtbSample, _recorder.RelativeFrame);
            _w.Write((Byte)due.Count);
            foreach (KeyValuePair<UInt16, AtbTrack> kv in due)
            {
                _w.Write(kv.Key);
                _w.Write(kv.Value.SentValue);
                _w.Write((Int16)Mathf.Clamp(Mathf.RoundToInt(kv.Value.SentRate), Int16.MinValue, Int16.MaxValue));
            }
            EndRecord(start);
            AtbSamples += due.Count;
            AtbBytes += (Int32)(_pending.Length - start);
        }

        /// <summary>M2: a remote-controlled unit's turn came (its ATB is full: the controlling PC opens its menu) or went.</summary>
        public void WriteTurn(UInt16 unit, Boolean ready)
        {
            if (!_recorder.Started || EndSent)
                return;
            Int64 at = BeginRecord(BattleWire.TurnReady, _recorder.RelativeFrame);
            _w.Write(unit);
            _w.Write((Byte)(ready ? 1 : 0));
            EndRecord(at);
        }

        /// <summary>M2: these player units left the battle (their PC fled).</summary>
        public void WriteUnitsLeave(UInt16 mask)
        {
            if (!_recorder.Started || EndSent)
                return;
            Int64 at = BeginRecord(BattleWire.UnitsLeave, _recorder.RelativeFrame);
            _w.Write(mask);
            EndRecord(at);
        }

        /// <summary>Late join: this character was added to the battle as unit <paramref name="btlIndex"/>, party slot
        /// <paramref name="memberIndex"/> (BattleUnitJoin); every passive adds it at this frame.</summary>
        public void WriteUnitJoin(UInt16 character, Int32 btlIndex, Int32 memberIndex)
        {
            if (!_recorder.Started || EndSent)
                return;
            Int64 at = BeginRecord(BattleWire.UnitJoin, _recorder.RelativeFrame);
            _w.Write(character);
            _w.Write((Byte)((btlIndex & 0x0F) | (memberIndex << 4)));
            EndRecord(at);
        }

        /// <summary>What running commands drew (or chose) since they went out.</summary>
        private void WriteCommandUpdates()
        {
            foreach (SentCommand sc in _commands)
            {
                BattleEvent ev = sc.Event;
                if (ev.Tape.Count < sc.TapeSent)
                {
                    BattleSync.Note($"Stream: command #{ev.Seq}'s tape shrank after it was sent ({sc.TapeSent} → {ev.Tape.Count})");
                    sc.TapeSent = ev.Tape.Count;
                }
                if (ev.Tape.Count > sc.TapeSent || ev.ShortSummon != sc.SummonSent)
                {
                    Int64 start = BeginRecord(BattleWire.CmdMore, _recorder.RelativeFrame);
                    _w.Write((UInt16)ev.Seq);
                    _w.Write(ev.ShortSummon);
                    Int32 count = Math.Min(255, ev.Tape.Count - sc.TapeSent);
                    _w.Write((Byte)count);
                    for (Int32 k = 0; k < count; k++)
                    {
                        TapeEntry t = ev.Tape[sc.TapeSent + k];
                        _w.Write((Byte)t.Kind);
                        _w.Write(t.Value);
                    }
                    EndRecord(start);
                    sc.TapeSent += count;
                    sc.SummonSent = ev.ShortSummon;
                    TapeLate += count;
                }
            }
        }

        /// <summary>The next message for the participants (null: nothing to send yet).</summary>
        public Byte[] TakeMessage(UInt16 seq)
        {
            if (!_recorder.Started || EndSent)
                return null;
            List<BattleEvent> events = _recorder.Log.Events;
            // Tape first: a command's last draws and its CommandEnd can come in the same frame
            WriteCommandUpdates();
            for (; _next < events.Count; _next++)
            {
                BattleEvent ev = events[_next];
                if (ev.Type == BattleEventType.Atb)
                    continue; // the debug log's own ATB format; the stream sends samples
                Int64 start = BeginRecord((Byte)ev.Type, ev.Frame - _recorder.StartFrame);
                BattleWire.WriteEvent(_w, ev, ref _sent);
                EndRecord(start);
                EventsSent++;
                if (ev.Type == BattleEventType.Command)
                    _commands.Add(new SentCommand { Event = ev, TapeSent = ev.Tape.Count, SummonSent = ev.ShortSummon });
                else if (ev.Type == BattleEventType.CommandEnd)
                    _commands.RemoveAll(sc => sc.Event.Seq == ev.Seq);
                else if (ev.Type == BattleEventType.End)
                {
                    EndSent = true;
                    _commands.Clear();
                }
            }
            Int32 frame = _recorder.RelativeFrame;
            if (_pending.Length == 0 && frame - _lastSentFrame < HeartbeatFrames)
                return null;
            _lastSentFrame = frame;
            Byte[] body = _pending.ToArray();
            _pending.SetLength(0);
            Byte[] message = new Byte[7 + body.Length];
            message[0] = BattleWire.MsgStream;
            message[1] = (Byte)seq;
            message[2] = (Byte)(seq >> 8);
            message[3] = (Byte)frame;
            message[4] = (Byte)(frame >> 8);
            message[5] = (Byte)(frame >> 16);
            message[6] = (Byte)(frame >> 24);
            Buffer.BlockCopy(body, 0, message, 7, body.Length);
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

        public String Status => $"sent {EventsSent} events, {BytesSent / 1024f:0.0} KB in {Messages} messages (tape sent late {TapeLate}); ATB {AtbSamples} samples ({AtbRate:0.0}/s, {AtbBytes} B)";
    }

    /// <summary>
    /// Step 3, passive side: reads the owner's stream into a live <see cref="BattleLog"/> for the <see cref="BattleReplayer"/>,
    /// keeps the mirror of the owner's state, knows how far the owner has run, and plays the ATB samples back at their
    /// frame with interpolation.
    /// </summary>
    public sealed class BattleStreamIn
    {
        /// <summary>The passive PC stays at least this many battle frames behind the owner (0.3 s at 30 frames/s): the
        /// owner's effects arrive before the passive's animations reach them.</summary>
        public const Int32 Margin = 9;

        private sealed class AtbTarget
        {
            public Int16 Value;
            public Single Rate, At;
            public Int16 Shown = -1; // what this PC showed last (a new sample a little behind it doesn't step the gauge back)
        }

        private struct AtbQueued
        {
            public Int32 Frame;
            public UInt16 Unit;
            public Int16 Value, Rate;
        }

        public readonly BattleLog Log = new BattleLog();
        private readonly BattleSnapshot _mirror = new BattleSnapshot();
        private readonly Dictionary<UInt16, AtbTarget> _atb = new Dictionary<UInt16, AtbTarget>();
        private readonly Queue<AtbQueued> _atbQueue = new Queue<AtbQueued>();
        private struct Timed
        {
            public Int32 Frame;
            public Byte Type;
            public UInt16 Value;
            public Byte Flag;
        }

        private readonly Queue<Timed> _timed = new Queue<Timed>();

        /// <summary>The player units running at the frame played (each unit's controlling PC holds "run").</summary>
        public UInt16 RunMask;

        /// <summary>Any player unit running: btl_escape_key's value on a passive battle.</summary>
        public Byte EscapeKey => (Byte)(RunMask != 0 ? 1 : 0);
        private readonly Dictionary<Int32, BattleEvent> _commands = new Dictionary<Int32, BattleEvent>();

        public Int32 EventsReceived, BytesReceived, AtbSamples, TapeLate;
        public Boolean EndReceived;
        /// <summary>How far the owner has run (its last message's frame); -1: nothing yet.</summary>
        public Int32 OwnerFrame = -1;
        private Single _firstAtbTime = -1f;

        /// <summary>Events the replayer may consume (each one waits for its frame there).</summary>
        public Int32 Released => Log.Events.Count;

        public Single AtbRate => _firstAtbTime < 0f || Time.realtimeSinceStartup - _firstAtbTime < 1f ? 0f : AtbSamples / (Time.realtimeSinceStartup - _firstAtbTime);

        public void OnMessage(Byte[] d)
        {
            BytesReceived += d.Length;
            if (d.Length < 7)
                return;
            Int32 messageFrame = d[3] | (d[4] << 8) | (d[5] << 16) | (d[6] << 24);
            using (BinaryReader r = new BinaryReader(new MemoryStream(d, 7, d.Length - 7)))
            {
                while (r.BaseStream.Position < r.BaseStream.Length)
                {
                    Byte type = r.ReadByte();
                    Int32 length = r.ReadUInt16();
                    Int64 end = r.BaseStream.Position + length;
                    Int32 frame = (Int32)r.ReadUInt32();
                    if (type == BattleWire.AtbSample)
                        ReadAtb(r, frame);
                    else if (type == BattleWire.CmdMore)
                        ReadCommandMore(r);
                    else if (type == BattleWire.RunMask || type == BattleWire.UnitsLeave)
                        _timed.Enqueue(new Timed { Frame = frame, Type = type, Value = r.ReadUInt16() });
                    else if (type == BattleWire.TurnReady || type == BattleWire.UnitJoin)
                        _timed.Enqueue(new Timed { Frame = frame, Type = type, Value = r.ReadUInt16(), Flag = r.ReadByte() });
                    else
                    {
                        BattleEvent ev = BattleWire.ReadEvent((BattleEventType)type, r, _mirror);
                        ev.Frame = frame;
                        Add(ev);
                    }
                    if (r.BaseStream.Position != end)
                    {
                        BattleSync.Note($"Stream: record type {type} read {r.BaseStream.Position - (end - length)} of {length} bytes");
                        r.BaseStream.Position = end;
                    }
                }
            }
            if (messageFrame > OwnerFrame)
                OwnerFrame = messageFrame;
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
                _commands[ev.Seq] = ev;
            else if (ev.Type == BattleEventType.End)
            {
                EndReceived = true;
                Log.Complete = true;
            }
        }

        private void ReadCommandMore(BinaryReader r)
        {
            Int32 seq = r.ReadUInt16();
            SByte shortSummon = r.ReadSByte();
            Int32 count = r.ReadByte();
            _commands.TryGetValue(seq, out BattleEvent ev);
            if (ev == null)
                BattleSync.Note($"Stream: tape for command #{seq}, which never came");
            else
                ev.ShortSummon = shortSummon;
            for (Int32 i = 0; i < count; i++)
            {
                TapeEntry t = new TapeEntry { Kind = (TapeKind)r.ReadByte(), Value = r.ReadInt32() };
                ev?.Tape.Add(t);
            }
            TapeLate += count;
        }

        /// <summary>Kept for the end of a battle (every event is consumable on arrival now).</summary>
        public void ReleaseAll()
        {
        }

        private void ReadAtb(BinaryReader r, Int32 frame)
        {
            if (_firstAtbTime < 0f)
                _firstAtbTime = Time.realtimeSinceStartup;
            Int32 count = r.ReadByte();
            for (Int32 i = 0; i < count; i++)
            {
                _atbQueue.Enqueue(new AtbQueued { Frame = frame, Unit = r.ReadUInt16(), Value = r.ReadInt16(), Rate = r.ReadInt16() });
                AtbSamples++;
            }
        }

        /// <summary>Each frame: the samples up to <paramref name="frame"/> taken, then every gauge at the owner's last
        /// sample, moved on at its rate (capped at full).</summary>
        public void ApplyAtb(Int32 frame)
        {
            Single now = Time.realtimeSinceStartup;
            while (_timed.Count > 0 && _timed.Peek().Frame <= frame)
            {
                Timed t = _timed.Dequeue();
                if (t.Type == BattleWire.RunMask)
                    RunMask = t.Value;
                else if (t.Type == BattleWire.TurnReady)
                    BattleControl.OnTurn(t.Value, t.Flag != 0);
                else if (t.Type == BattleWire.UnitsLeave)
                    BattleControl.ApplyUnitsLeave(t.Value);
                else if (t.Type == BattleWire.UnitJoin)
                    BattleUnitJoin.ApplyFromStream(t.Value, t.Flag);
            }
            while (_atbQueue.Count > 0 && _atbQueue.Peek().Frame <= frame)
            {
                AtbQueued q = _atbQueue.Dequeue();
                Int16 shown = _atb.TryGetValue(q.Unit, out AtbTarget old) ? old.Shown : (Int16)(-1);
                _atb[q.Unit] = new AtbTarget { Value = q.Value, Rate = q.Rate, At = now, Shown = shown };
            }
            foreach (KeyValuePair<UInt16, AtbTarget> kv in _atb)
            {
                BTL_DATA btl = BattleSync.FindBtl(kv.Key);
                if (btl == null)
                    continue;
                AtbTarget t = kv.Value;
                Int16 at = (Int16)Mathf.Clamp(t.Value + t.Rate * (now - t.At), 0f, btl.max.at);
                // The interpolation runs a little ahead or behind the owner's gauge; a correction backwards by less
                // than 1/16 of the gauge waits for the gauge to catch up instead of making it stagger. A reset (after
                // an action, a big drop) shows at once.
                if (t.Shown > at && t.Shown - at < btl.max.at / 16 && t.Shown < btl.max.at)
                    at = t.Shown;
                t.Shown = at;
                btl.cur.at = at;
            }
        }

        public String Status => $"received {EventsReceived} events, {BytesReceived / 1024f:0.0} KB (tape late {TapeLate}); owner at frame {OwnerFrame}; ATB {AtbSamples} samples ({AtbRate:0.0}/s)";
    }
}
