using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Memoria.Online.Session
{
    /// <summary>
    /// Sends a large blob (a <see cref="GameSnapshot"/>) to peers on the reliable state channel, in chunks.
    ///
    /// Chunk message (channel 2, reliable), little-endian:
    ///   0  type        byte    <see cref="MessageType"/>
    ///   1  transferId  uint32
    ///   5  totalSize   uint32  whole blob
    ///   9  crc32       uint32  whole blob
    ///  13  index       uint16
    ///  15  count       uint16
    ///  17  length      uint16  this chunk's payload
    ///  19  payload
    ///
    /// Chunks are 16 KB and a few are queued per frame per peer: reliable P2P sends are buffered by Steam and can stall
    /// behind each other (spike A), so the queue is paced instead of handing Steam the whole blob at once.
    /// </summary>
    public sealed class BlobTransfer
    {
        public const Byte MessageType = 0x20;
        public const Int32 HeaderSize = 19;
        public const Int32 ChunkSize = 16 * 1024;
        private const Int32 ChunksPerFramePerPeer = 4;

        public sealed class Received
        {
            public UInt64 Peer;
            public UInt32 TransferId;
            public Byte[] Blob;
            public Double Milliseconds;
        }

        private sealed class Outgoing
        {
            public UInt64 Peer;
            public UInt32 TransferId;
            public Byte[] Blob;
            public UInt32 Crc;
            public Int32 Count;
            public Int32 Next;
            public Stopwatch Watch;
        }

        private sealed class Incoming
        {
            public UInt32 TransferId;
            public Byte[] Blob;
            public UInt32 Crc;
            public Boolean[] Have;
            public Int32 Missing;
            public Stopwatch Watch;
        }

        private readonly List<Outgoing> _outgoing = new List<Outgoing>();
        private readonly Dictionary<UInt64, Incoming> _incoming = new Dictionary<UInt64, Incoming>();
        private readonly Byte[] _chunkBuffer = new Byte[HeaderSize + ChunkSize];
        private UInt32 _nextTransferId = 1;

        /// <summary>Raised on a complete blob whose checksum matches.</summary>
        public event Action<Received> Completed;

        public Boolean IsSending => _outgoing.Count > 0;

        public Int32 ChunkCount(Int32 size) => Math.Max(1, (size + ChunkSize - 1) / ChunkSize);

        public UInt32 Send(UInt64 peer, Byte[] blob)
        {
            Outgoing transfer = new Outgoing
            {
                Peer = peer,
                TransferId = _nextTransferId++,
                Blob = blob,
                Crc = GameSnapshot.Crc32(blob, 0, blob.Length),
                Count = ChunkCount(blob.Length),
                Watch = Stopwatch.StartNew(),
            };
            _outgoing.Add(transfer);
            Log.Message($"[Online] Sending snapshot #{transfer.TransferId} to {SteamNative.Tag(peer)}: {blob.Length} bytes in {transfer.Count} chunks, crc {transfer.Crc:X8}");
            return transfer.TransferId;
        }

        /// <summary>Call once per frame: hands the next chunks to the transport.</summary>
        public void Update(ITransport transport)
        {
            for (Int32 i = _outgoing.Count - 1; i >= 0; i--)
            {
                Outgoing transfer = _outgoing[i];
                if (!transport.Peers.Contains(transfer.Peer))
                {
                    Log.Warning($"[Online] Snapshot #{transfer.TransferId} to {SteamNative.Tag(transfer.Peer)} abandoned: the peer left");
                    _outgoing.RemoveAt(i);
                    continue;
                }
                for (Int32 n = 0; n < ChunksPerFramePerPeer && transfer.Next < transfer.Count; n++)
                {
                    Int32 length = Encode(transfer.TransferId, transfer.Blob, transfer.Crc, transfer.Next, transfer.Count, _chunkBuffer);
                    if (!transport.Send(transfer.Peer, TransportChannel.State, _chunkBuffer, length, true))
                        break; // retry this chunk next frame
                    transfer.Next++;
                }
                if (transfer.Next >= transfer.Count)
                {
                    Log.Message($"[Online] Snapshot #{transfer.TransferId} handed to Steam for {SteamNative.Tag(transfer.Peer)} in {transfer.Watch.ElapsedMilliseconds} ms");
                    _outgoing.RemoveAt(i);
                }
            }
        }

        /// <summary>Writes chunk <paramref name="index"/> of a blob into <paramref name="buffer"/>; returns the message length.</summary>
        public static Int32 Encode(UInt32 transferId, Byte[] blob, UInt32 crc, Int32 index, Int32 count, Byte[] buffer)
        {
            Int32 offset = index * ChunkSize;
            Int32 length = Math.Min(ChunkSize, blob.Length - offset);
            buffer[0] = MessageType;
            WriteU32(buffer, 1, transferId);
            WriteU32(buffer, 5, (UInt32)blob.Length);
            WriteU32(buffer, 9, crc);
            WriteU16(buffer, 13, (UInt16)index);
            WriteU16(buffer, 15, (UInt16)count);
            WriteU16(buffer, 17, (UInt16)length);
            Buffer.BlockCopy(blob, offset, buffer, HeaderSize, length);
            return HeaderSize + length;
        }

        /// <summary>Takes one received channel-2 message. Returns false if it isn't a chunk (for other handlers).</summary>
        public Boolean OnMessage(UInt64 peer, Byte[] data, Int32 length)
        {
            if (length < HeaderSize || data[0] != MessageType)
                return false;
            UInt32 transferId = ReadU32(data, 1);
            UInt32 totalSize = ReadU32(data, 5);
            UInt32 crc = ReadU32(data, 9);
            Int32 index = ReadU16(data, 13);
            Int32 count = ReadU16(data, 15);
            Int32 chunkLength = ReadU16(data, 17);
            if (totalSize > 64 * 1024 * 1024 || count != ChunkCount((Int32)totalSize) || index >= count || HeaderSize + chunkLength > length
                || (Int64)index * ChunkSize + chunkLength > totalSize)
            {
                Log.Warning($"[Online] Malformed snapshot chunk from {SteamNative.Tag(peer)} (transfer {transferId}, chunk {index}/{count}, {chunkLength} bytes of {totalSize})");
                return true;
            }

            if (!_incoming.TryGetValue(peer, out Incoming transfer) || transfer.TransferId != transferId)
            {
                transfer = new Incoming
                {
                    TransferId = transferId,
                    Blob = new Byte[totalSize],
                    Crc = crc,
                    Have = new Boolean[count],
                    Missing = count,
                    Watch = Stopwatch.StartNew(),
                };
                _incoming[peer] = transfer;
                Log.Message($"[Online] Receiving snapshot #{transferId} from {SteamNative.Tag(peer)}: {totalSize} bytes in {count} chunks");
            }
            if (transfer.Have[index])
                return true;
            Buffer.BlockCopy(data, HeaderSize, transfer.Blob, index * ChunkSize, chunkLength);
            transfer.Have[index] = true;
            transfer.Missing--;
            if (transfer.Missing > 0)
                return true;

            _incoming.Remove(peer);
            UInt32 actual = GameSnapshot.Crc32(transfer.Blob, 0, transfer.Blob.Length);
            if (actual != transfer.Crc)
            {
                Log.Warning($"[Online] Snapshot #{transferId} from {SteamNative.Tag(peer)} has a bad checksum ({actual:X8}, expected {transfer.Crc:X8}); dropped");
                return true;
            }
            Log.Message($"[Online] Snapshot #{transferId} from {SteamNative.Tag(peer)} complete: {transfer.Blob.Length} bytes in {transfer.Watch.ElapsedMilliseconds} ms, crc {actual:X8} OK");
            Completed?.Invoke(new Received { Peer = peer, TransferId = transferId, Blob = transfer.Blob, Milliseconds = transfer.Watch.Elapsed.TotalMilliseconds });
            return true;
        }

        public void Forget(UInt64 peer)
        {
            _incoming.Remove(peer);
            _outgoing.RemoveAll(t => t.Peer == peer);
        }

        private static void WriteU16(Byte[] b, Int32 o, UInt16 v)
        {
            b[o] = (Byte)v;
            b[o + 1] = (Byte)(v >> 8);
        }

        private static void WriteU32(Byte[] b, Int32 o, UInt32 v)
        {
            b[o] = (Byte)v;
            b[o + 1] = (Byte)(v >> 8);
            b[o + 2] = (Byte)(v >> 16);
            b[o + 3] = (Byte)(v >> 24);
        }

        private static UInt16 ReadU16(Byte[] b, Int32 o) => (UInt16)(b[o] | (b[o + 1] << 8));

        private static UInt32 ReadU32(Byte[] b, Int32 o) => b[o] | ((UInt32)b[o + 1] << 8) | ((UInt32)b[o + 2] << 16) | ((UInt32)b[o + 3] << 24);
    }
}
