using System;

namespace Memoria
{
    public sealed partial class Configuration
    {
        // [Multiplayer] Local multiplayer (fork addition).
        public static class Multiplayer
        {
            public static Boolean Enabled => Instance._multiplayer.Enabled;
            /// <summary>Number of players (1-4). Player N controls the character in party slot N; other slots belong to player 1.</summary>
            public static Int32 PlayerCount => Math.Max(1, Math.Min(4, Instance._multiplayer.PlayerCount.Value));
            public static Boolean DebugOverlay => Instance._multiplayer.DebugOverlay;
            /// <summary>XInput slot (0-3) of each player's controller, in player order. Default "0, 1, 2, 3".</summary>
            public static String PlayerPads => Instance._multiplayer.PlayerPads;
            /// <summary>Show players 2-4's characters next to the leader on fields, walking with their own controller.</summary>
            public static Boolean FieldFollowers => Instance._multiplayer.FieldFollowers;
            /// <summary>On fields, an extra player's Beatrix is shown as Black Waltz No. 3 (its beaten model, the one that walks).</summary>
            public static Boolean BlackWaltzModel => Instance._multiplayer.BlackWaltzModel;
            /// <summary>On fields, an extra player's Vivi is shown as a Black Mage.</summary>
            public static Boolean BlackMageModel => Instance._multiplayer.BlackMageModel;
            /// <summary>Advanced per-character field model overrides: "Name=model,height,idle,walk,run,turnL,turnR,size[,anim%]; ..."</summary>
            public static String FieldModels => Instance._multiplayer.FieldModels;
            /// <summary>In battle, players whose character is ready while the main menu is busy get a compact menu of their own.</summary>
            public static Boolean BattleMenus => Instance._multiplayer.BattleMenus;
        }
    }
}
