using Memoria.Prime;
using System;
using System.Text;

namespace Memoria.Online.Session
{
    /// <summary>
    /// Step 2 / M5, first step of "guests' flags go to the host": logs every byte of the event globals
    /// (FF9StateSystem.EventState.gEventGlobal: story progress, chests, field flags) that changes while playing, with
    /// the field and who changed it (the host, a guest with the host, a guest alone). Nothing is sent yet; the log
    /// tells which bytes are chests, flags and story before we forward them.
    /// A whole-game load replaces the array (a new one): that only takes a new baseline.
    /// </summary>
    internal static class EventWatch
    {
        private const Double IntervalMs = 250;
        private const Int32 MaxPerLine = 48;

        private static Byte[] _watched;
        private static Byte[] _copy;
        private static Double _next;

        public static void Update(Double nowMs, String who)
        {
            if (nowMs < _next)
                return;
            _next = nowMs + IntervalMs;
            Byte[] global = FF9StateSystem.EventState?.gEventGlobal;
            if (global == null)
                return;
            if (!ReferenceEquals(global, _watched) || _copy == null || _copy.Length != global.Length)
            {
                _watched = global;
                _copy = (Byte[])global.Clone();
                return;
            }
            StringBuilder changes = null;
            Int32 count = 0;
            for (Int32 i = 0; i < global.Length; i++)
            {
                if (global[i] == _copy[i])
                    continue;
                if (count < MaxPerLine)
                    (changes ?? (changes = new StringBuilder())).Append($" [{i}] {_copy[i]}->{global[i]}");
                count++;
                _copy[i] = global[i];
            }
            if (count == 0)
                return;
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            String where = engine == null ? "?" : engine.gMode == 1 ? $"field {FF9StateSystem.Common.FF9.fldMapNo}" : engine.gMode == 3 ? "world map" : engine.gMode == 2 ? "battle" : $"mode {engine.gMode}";
            Log.Message($"[Online] Event bytes: {count} changed on {where} ({who}{(engine != null && engine.gMode == 1 && !engine.GetUserControl() ? ", during a scene" : "")}):{changes}{(count > MaxPerLine ? " ..." : "")}");
        }

        public static void Reset()
        {
            _watched = null;
            _copy = null;
        }
    }
}
