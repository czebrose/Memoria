using Memoria.Prime;
using System;
using System.Collections.Generic;

namespace Memoria.Online
{
    /// <summary>
    /// Every message type byte (the first byte of a message) and which handler owns it. OnlineSession.HandleMessage
    /// offers a message to the session, then the battle code, then SceneSync, and the first one that recognises the
    /// type byte keeps it: two handlers with the same byte means the later one never sees its message (v0.1.1:
    /// BattleControl.MsgControlUpdate and SceneSync.MsgStart were both 40, so guests never followed a shared scene).
    /// <see cref="Check"/> runs once at start and logs an error for any byte listed twice. Add new types here.
    /// </summary>
    internal static class MessageIds
    {
        private static IEnumerable<KeyValuePair<Byte, String>> All()
        {
            yield return Id(OnlineSession.MsgPing, "OnlineSession.MsgPing");
            yield return Id(OnlineSession.MsgPong, "OnlineSession.MsgPong");
            yield return Id(OnlineSession.MsgSentCount, "OnlineSession.MsgSentCount");
            yield return Id(Session.SessionLobby.MsgStart, "SessionLobby.MsgStart");
            yield return Id(Session.SessionLobby.MsgResync, "SessionLobby.MsgResync");
            yield return Id(Session.SessionLobby.MsgMeet, "SessionLobby.MsgMeet");
            yield return Id(Session.SessionLobby.MsgFlags, "SessionLobby.MsgFlags");
            yield return Id(Session.SessionLobby.MsgHostState, "SessionLobby.MsgHostState");
            for (Byte b = Battle.BattleEntryMessages.Presence; b <= Battle.BattleEntryMessages.Alive; b++)
                yield return Id(b, $"BattleEntryMessages ({b})");
            yield return Id(Battle.BattleWire.MsgStream, "BattleWire.MsgStream");
            yield return Id(Battle.BattleEntryMessages.BattleOpen, "BattleEntryMessages.BattleOpen");
            yield return Id(Battle.BattleEntryMessages.JoinAsk, "BattleEntryMessages.JoinAsk");
            yield return Id(Battle.BattleEntryMessages.JoinRefuse, "BattleEntryMessages.JoinRefuse");
            for (Byte b = Battle.BattleControl.MsgCommandRequest; b <= Battle.BattleControl.MsgFleeResult; b++)
                yield return Id(b, $"BattleControl ({b})");
            yield return Id(Battle.BattleControl.MsgControlUpdate, "BattleControl.MsgControlUpdate");
            yield return Id(Battle.BattleQueue.MsgQueue, "BattleQueue.MsgQueue");
            for (Byte b = Session.SceneSync.MsgStart; b <= Session.SceneSync.MsgResume; b++)
                yield return Id(b, $"SceneSync ({b})");
            yield return Id(SteamTransport.MsgHello, "SteamTransport.MsgHello");
        }

        private static KeyValuePair<Byte, String> Id(Byte b, String owner) => new KeyValuePair<Byte, String>(b, owner);

        private static Boolean _checked;

        /// <summary>Logs an error for each type byte two handlers share. Returns true if there is none.</summary>
        public static Boolean Check()
        {
            if (_checked)
                return true;
            _checked = true;
            Dictionary<Byte, String> owners = new Dictionary<Byte, String>();
            Boolean clean = true;
            foreach (KeyValuePair<Byte, String> id in All())
            {
                if (owners.TryGetValue(id.Key, out String other))
                {
                    Log.Error($"[Online] Message type {id.Key} is used by both {other} and {id.Value}: the second never receives it");
                    clean = false;
                    continue;
                }
                owners[id.Key] = id.Value;
            }
            return clean;
        }
    }
}
