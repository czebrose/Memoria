using Memoria.Prime.Ini;
using System;

namespace Memoria
{
    public sealed partial class Configuration
    {
        // [Multiplayer] Local multiplayer (fork addition). Disabled by default: with Enabled = 0 the game behaves like upstream Memoria.
        private sealed class MultiplayerSection : IniSection
        {
            public readonly IniValue<Boolean> DebugOverlay;
            public readonly IniValue<String> PlayerPads;
            public readonly IniValue<String> Player2Characters;
            public readonly IniValue<String> Player3Characters;
            public readonly IniValue<String> Player4Characters;
            public readonly IniValue<Boolean> FieldFollowers;
            public readonly IniValue<String> FieldModels;
            public readonly IniValue<Boolean> BattleMenus;

            public MultiplayerSection() : base(nameof(MultiplayerSection), false)
            {
                BattleMenus = BindBoolean(nameof(BattleMenus), true);
                FieldFollowers = BindBoolean(nameof(FieldFollowers), true);
                FieldModels = BindString(nameof(FieldModels), "");
                DebugOverlay = BindBoolean(nameof(DebugOverlay), false);
                PlayerPads = BindString(nameof(PlayerPads), "0, 1, 2, 3");
                Player2Characters = BindString(nameof(Player2Characters), "");
                Player3Characters = BindString(nameof(Player3Characters), "");
                Player4Characters = BindString(nameof(Player4Characters), "");
            }
        }
    }
}
