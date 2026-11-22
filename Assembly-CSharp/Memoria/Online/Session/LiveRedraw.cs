using Memoria.Prime;
using System;
using System.Collections.Generic;

namespace Memoria.Online.Session
{
    /// <summary>
    /// Live flags: redraw only the objects a changed event byte affects, instead of reloading the whole field (a blink).
    ///
    /// While a field starts, every entry's main function (its Init: script level 0) that reads an event byte is noted
    /// (<see cref="NoteRead"/>). When the host's live flags change bytes on this guest's field, <see cref="TryRedraw"/>:
    ///  - nobody's Init read them: nothing to redraw (timers, the mod's bookkeeping, a chest's Speak reads it live);
    ///  - only ordinary entries read them (a chest, a door, an ice wall object): their Init runs again, with the opcodes
    ///    that create things (SetModel, InitObject/InitRegion/InitCode, DefinePlayerCharacter) and leave the field
    ///    (Field, Battle) skipped (<see cref="SkipOpcode"/>);
    ///  - the field's main entry read them, or an entry is busy: no targeted redraw (the caller reloads the field).
    /// The player's and the party's entries are left alone (re-running them would move the player).
    /// </summary>
    internal static class LiveRedraw
    {
        public const Int32 RedrawLevel = 1;

        // event byte -> uids of the entries whose Init read it, on the current field
        private static readonly Dictionary<Int32, HashSet<Int32>> s_readers = new Dictionary<Int32, HashSet<Int32>>();
        // uid -> stack depth before its Init was requested again (inside the redraw while deeper)
        private static readonly Dictionary<Int32, Int32> s_running = new Dictionary<Int32, Int32>();

        public static Boolean Enabled => GuestGuard.IsGuestSession;

        /// <summary>EventEngine.StartEvents: a new field (not a return from battle, which keeps the field's objects).</summary>
        public static void OnStartEvents(Int32 gMode, Boolean restored)
        {
            if (gMode == 2 || restored)
                return;
            s_readers.Clear();
            s_running.Clear();
        }

        /// <summary>EBin: a script read a global (event) variable.</summary>
        public static void NoteRead(Obj obj, Int32 ofs, EBin.VariableType type)
        {
            if (obj == null || obj.level != 0 || !Enabled)
                return;
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            if (engine == null || engine.gMode != 1)
                return;
            Int32 first, count;
            switch (type)
            {
                case EBin.VariableType.Bit:
                case EBin.VariableType.SBit:
                    first = ofs >> 3; count = 1; break;
                case EBin.VariableType.Byte:
                case EBin.VariableType.SByte:
                    first = ofs; count = 1; break;
                case EBin.VariableType.Int16:
                case EBin.VariableType.UInt16:
                    first = ofs; count = 2; break;
                case EBin.VariableType.Int24:
                case EBin.VariableType.UInt24:
                    first = ofs; count = 3; break;
                default:
                    return;
            }
            for (Int32 b = first; b < first + count; b++)
            {
                if (!s_readers.TryGetValue(b, out HashSet<Int32> uids))
                    s_readers[b] = uids = new HashSet<Int32>();
                uids.Add(obj.uid);
            }
        }

        /// <summary>EventEngine.DoEventCode: true when this opcode must be skipped (its arguments are then consumed by the caller).</summary>
        public static Boolean SkipOpcode(Obj exec)
        {
            if (exec == null || s_running.Count == 0 || !s_running.TryGetValue(exec.uid, out Int32 sx))
                return false;
            if (exec.sx > sx && exec.level == RedrawLevel)
                return true;
            if (exec.sx <= sx)
                s_running.Remove(exec.uid); // the redraw has returned
            return false;
        }

        /// <summary>Every frame (guest): forget redraws that have returned, so the entry's later calls run normally.</summary>
        public static void Tick()
        {
            if (s_running.Count == 0)
                return;
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            List<Int32> finished = new List<Int32>();
            foreach (KeyValuePair<Int32, Int32> kv in s_running)
            {
                Obj obj = engine?.FindObjByUID(kv.Key);
                if (obj == null || obj.sx <= kv.Value)
                    finished.Add(kv.Key);
            }
            foreach (Int32 uid in finished)
                s_running.Remove(uid);
        }

        public enum Result { NothingToDo, Redrawn, NeedsReload }

        /// <summary>Guest: the host's live bytes changed on this field; redraw what read them, if that can be done in place.</summary>
        public static Result TryRedraw(ICollection<Int32> bytes, out String detail)
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            detail = null;
            if (engine == null || engine.gMode != 1)
            {
                detail = "not on a field";
                return Result.NeedsReload;
            }
            EventContext context = engine.sEventContext0;
            HashSet<Int32> party = new HashSet<Int32>();
            if (context?.partyUID != null)
                foreach (Int32 sid in context.partyUID)
                    if (sid >= 0)
                        party.Add(sid);
            SortedDictionary<Int32, Obj> targets = new SortedDictionary<Int32, Obj>();
            List<String> skipped = new List<String>();
            foreach (Int32 b in bytes)
            {
                if (!s_readers.TryGetValue(b, out HashSet<Int32> uids))
                    continue;
                foreach (Int32 uid in uids)
                {
                    Obj obj = engine.FindObjByUID(uid);
                    if (obj == null || obj is Seq)
                        continue;
                    if (obj.sid == 0)
                    {
                        detail = $"byte [{b}] is read by the field's main entry";
                        return Result.NeedsReload;
                    }
                    if (party.Contains(obj.sid) || (context != null && obj.uid == context.controlUID) || uid >= 200)
                    {
                        skipped.Add($"uid {uid}");
                        continue;
                    }
                    targets[uid] = obj;
                }
            }
            if (targets.Count == 0)
            {
                detail = skipped.Count > 0 ? $"only the player's/party's entries read them ({String.Join(", ", skipped.ToArray())})" : "no entry's Init read them";
                return Result.NothingToDo;
            }
            foreach (Obj obj in targets.Values)
                if (obj.level <= RedrawLevel)
                {
                    detail = $"entry uid {obj.uid} (sid {obj.sid}) is busy in a script (level {obj.level})";
                    return Result.NeedsReload;
                }
            List<String> done = new List<String>();
            foreach (Obj obj in targets.Values)
            {
                Int32 sx = obj.sx;
                if (!engine.Request(obj, RedrawLevel, 0, false))
                {
                    detail = $"entry uid {obj.uid} (sid {obj.sid}) has no Init to run again";
                    return done.Count == 0 ? Result.NeedsReload : Result.Redrawn;
                }
                s_running[obj.uid] = sx;
                done.Add($"uid {obj.uid} (sid {obj.sid})");
            }
            detail = $"Init run again for {String.Join(", ", done.ToArray())}";
            return Result.Redrawn;
        }

        public static void Reset()
        {
            s_readers.Clear();
            s_running.Clear();
        }

        public static void LogReaders(ICollection<Int32> bytes)
        {
            foreach (Int32 b in bytes)
                if (s_readers.TryGetValue(b, out HashSet<Int32> uids))
                    Log.Message($"[Online] Live redraw: byte [{b}] read at Init by uid {String.Join(", ", new List<Int32>(uids).ConvertAll(u => u.ToString()).ToArray())}");
        }
    }
}
