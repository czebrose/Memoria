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
            /// <summary>Show players 2-4's characters next to the leader on fields, walking with their own controller.</summary>
            public static Boolean FieldFollowers => Instance._multiplayer.FieldFollowers;
            /// <summary>Per-character field model overrides: "Name=model,height,idle,walk,run,turnL,turnR,size; ..."</summary>
            public static String FieldModels => Instance._multiplayer.FieldModels;
            /// <summary>In battle, players whose character is ready while the main menu is busy get a compact menu of their own.</summary>
            public static Boolean BattleMenus => Instance._multiplayer.BattleMenus;
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
