using System;

namespace Memoria
{
    public sealed partial class Configuration
    {
        // [Multiplayer] Local multiplayer (fork addition).
        public static class Multiplayer
        {
            public static Boolean Enabled => Instance._multiplayer.Enabled;
            public static Boolean DebugOverlay => Instance._multiplayer.DebugOverlay;
            /// <summary>XInput slot (0-3) of each player's controller, in player order. Default "0, 1, 2, 3".</summary>
            public static String PlayerPads => Instance._multiplayer.PlayerPads;
            /// <summary>Characters owned by player 2..4 (CharacterId names or numbers, comma-separated). Unlisted characters belong to player 1.</summary>
            public static String GetPlayerCharacters(Int32 player)
            {
                switch (player)
                {
                    case 2: return Instance._multiplayer.Player2Characters;
                    case 3: return Instance._multiplayer.Player3Characters;
                    case 4: return Instance._multiplayer.Player4Characters;
                    default: return "";
                }
            }
        }
    }
}
