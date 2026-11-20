using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Text;

namespace Memoria.Online.Session
{
    /// <summary>
    /// Step 2 / M5: the event globals (FF9StateSystem.EventState.gEventGlobal: story progress, chests, field flags).
    ///  - Every PC logs each byte that changes while playing (every 250 ms), with the field and who changed it.
    ///  - A guest also hands its changes to <see cref="SessionLobby"/>, which sends them to the host (tier 2), except
    ///    the field-local bookkeeping bytes (<see cref="IsForwarded"/>) and what a field's opening scripts set before
    ///    the guest had control (<see cref="GuestGuard.ActedOnThisField"/>).
    ///  - The host applies them (<see cref="ApplyFromGuest"/>) and sends them on to the other guests on the field where
    ///    they happened; the host's own changes go to the guests on its field the same way. A guest applies them live
    ///    (<see cref="ApplyFromHost"/>). Guests elsewhere get them with their next meeting resync.
    /// A whole-game load replaces the array (a new one): that only takes a new baseline.
    /// </summary>
    internal static class EventWatch
    {
        private const Double IntervalMs = 250;
        private const Int32 MaxPerLine = 48;

        /// <summary>
        /// Bytes 0-1: the scenario counter (forwarded: players are trusted not to rush the story). Bytes 2-63: the
        /// general/field bookkeeping (field entrance, ambient sound states and ids, fades, "movement enabled" bits,
        /// the previous field...), rewritten by every field: never forwarded. R1 showed 2-3 and 9-14 change on every
        /// field; the rest of the range is the same kind of variable in the scripts (VAR_GlobUInt8_17/24/37,
        /// VAR_GlobBool_144-167, VAR_GlobInt16_21/28).
        /// </summary>
        public static Boolean IsForwarded(Int32 index) => index < 2 || index >= 64;

        /// <summary>CRC32 of the shared event bytes (the <see cref="IsForwarded"/> ones); 0 if there's no game.</summary>
        public static UInt32 SharedDigest()
        {
            Byte[] global = FF9StateSystem.EventState?.gEventGlobal;
            if (global == null || global.Length < 64)
                return 0;
            Byte[] shared = new Byte[global.Length - 62];
            shared[0] = global[0];
            shared[1] = global[1];
            Array.Copy(global, 64, shared, 2, global.Length - 64);
            return GameSnapshot.Crc32(shared, 0, shared.Length);
        }

        private static Byte[] _watched;
        private static Byte[] _copy;
        private static Double _next;

        /// <summary>Logs this interval's changes; returns them (index, new value) for a guest to forward, or null.</summary>
        public static List<KeyValuePair<Int32, Byte>> Update(Double nowMs, String who, Boolean force = false)
        {
            if (!force)
            {
                if (nowMs < _next)
                    return null;
                _next = nowMs + IntervalMs;
            }
            Byte[] global = FF9StateSystem.EventState?.gEventGlobal;
            if (global == null)
                return null;
            if (!ReferenceEquals(global, _watched) || _copy == null || _copy.Length != global.Length)
            {
                _watched = global;
                _copy = (Byte[])global.Clone();
                return null;
            }
            List<KeyValuePair<Int32, Byte>> changes = null;
            StringBuilder text = null;
            for (Int32 i = 0; i < global.Length; i++)
            {
                if (global[i] == _copy[i])
                    continue;
                if (changes == null)
                {
                    changes = new List<KeyValuePair<Int32, Byte>>();
                    text = new StringBuilder();
                }
                if (changes.Count < MaxPerLine)
                    text.Append($" [{i}] {_copy[i]}->{global[i]}");
                changes.Add(new KeyValuePair<Int32, Byte>(i, global[i]));
                _copy[i] = global[i];
            }
            if (changes == null)
                return null;
            Log.Message($"[Online] Event bytes: {changes.Count} changed on {Where()} ({who}{(InScene() ? ", during a scene" : "")}):{text}{(changes.Count > MaxPerLine ? " ..." : "")}");
            return changes;
        }

        /// <summary>Host: a guest's changes, put into the live game (logged; not counted as the host's own changes).</summary>
        public static void ApplyFromGuest(String from, List<KeyValuePair<Int32, Byte>> changes)
        {
            Byte[] global = FF9StateSystem.EventState?.gEventGlobal;
            if (global == null)
                return;
            StringBuilder text = new StringBuilder();
            Int32 applied = 0;
            foreach (KeyValuePair<Int32, Byte> change in changes)
            {
                if (change.Key < 0 || change.Key >= global.Length || !IsForwarded(change.Key))
                    continue;
                if (applied < MaxPerLine)
                    text.Append($" [{change.Key}] {global[change.Key]}->{change.Value}");
                global[change.Key] = change.Value;
                if (ReferenceEquals(global, _watched) && _copy != null && change.Key < _copy.Length)
                    _copy[change.Key] = change.Value;
                applied++;
            }
            if (applied > 0)
                Log.Message($"[Online] Event bytes: {applied} applied from {from} on {Where()}:{text}{(applied > MaxPerLine ? " ..." : "")}");
        }

        /// <summary>
        /// Guest: the host's changes (its own or another guest's, on this guest's field), put into the live game. Only
        /// bytes that differ are written; they become the baseline, so they aren't sent back as this guest's own.
        /// Returns how many changed.
        /// </summary>
        public static Int32 ApplyFromHost(String from, List<KeyValuePair<Int32, Byte>> changes)
        {
            Byte[] global = FF9StateSystem.EventState?.gEventGlobal;
            if (global == null)
                return 0;
            StringBuilder text = new StringBuilder();
            Int32 applied = 0;
            foreach (KeyValuePair<Int32, Byte> change in changes)
            {
                if (change.Key < 0 || change.Key >= global.Length || !IsForwarded(change.Key) || global[change.Key] == change.Value)
                    continue;
                if (applied < MaxPerLine)
                    text.Append($" [{change.Key}] {global[change.Key]}->{change.Value}");
                global[change.Key] = change.Value;
                if (ReferenceEquals(global, _watched) && _copy != null && change.Key < _copy.Length)
                    _copy[change.Key] = change.Value;
                applied++;
            }
            if (applied > 0)
                Log.Message($"[Online] Event bytes: {applied} applied live from {from} on {Where()}:{text}{(applied > MaxPerLine ? " ..." : "")}");
            return applied;
        }

        private static String Where()
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            return engine == null ? "?" : engine.gMode == 1 ? $"field {FF9StateSystem.Common.FF9.fldMapNo}" : engine.gMode == 3 ? "world map" : engine.gMode == 2 ? "battle" : $"mode {engine.gMode}";
        }

        private static Boolean InScene()
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            return engine != null && engine.gMode == 1 && !engine.GetUserControl();
        }

        public static void Reset()
        {
            _watched = null;
            _copy = null;
        }
    }
}
