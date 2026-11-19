using Memoria.Data;
using Memoria.Prime;
using System;

namespace Memoria.Online
{
    /// <summary>
    /// Debug (OnlineDebug = 1): logs every change to the party roster (info.party) and the active party, with the field,
    /// its entrance and the script object running, to find which script changes the party after an in-memory load.
    /// </summary>
    public static class PartyTrace
    {
        public static void Roster(PLAYER player, Byte newValue)
        {
            if (!Configuration.Debug.OnlineDebug || player == null || player.info.party == newValue)
                return;
            Log.Message($"[PartyTrace] roster {player.Index} ({player.Name}) {player.info.party} -> {newValue} {Where()}");
        }

        public static void Slot(Int32 partySlot, CharacterId charId)
        {
            if (!Configuration.Debug.OnlineDebug)
                return;
            PLAYER current = FF9StateSystem.Common.FF9.party.member[partySlot];
            CharacterId currentId = current?.Index ?? CharacterId.NONE;
            if (currentId == charId)
                return;
            Log.Message($"[PartyTrace] party slot {partySlot}: {currentId} -> {charId} {Where()}");
        }

        private static String Where()
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            Obj cur = engine?.gCur;
            Byte[] globals = FF9StateSystem.EventState.gEventGlobal;
            return $"(field {FF9StateSystem.Common.FF9.fldMapNo}, entrance {FF9StateSystem.EventState.FieldEntrance}, script obj sid {cur?.sid} uid {cur?.uid} ip {cur?.ip}, gEventGlobal[21..22] {globals[21]} {globals[22]})";
        }
    }
}
