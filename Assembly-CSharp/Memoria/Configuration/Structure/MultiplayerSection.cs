using Memoria.Prime.Ini;
using System;

namespace Memoria
{
    public sealed partial class Configuration
    {
        // [Multiplayer] Local multiplayer (fork addition). Disabled by default: with Enabled = 0 the game behaves like upstream Memoria.
        private sealed class MultiplayerSection : IniSection
        {
            public readonly IniValue<Int32> PlayerCount;
            public readonly IniValue<Boolean> DebugOverlay;
            public readonly IniValue<String> PlayerPads;
            public readonly IniValue<Boolean> FieldFollowers;
            public readonly IniValue<Boolean> BlackWaltzModel;
            public readonly IniValue<Boolean> BlackMageModel;
            public readonly IniValue<String> FieldModels;
            public readonly IniValue<Boolean> BattleMenus;

            public MultiplayerSection() : base(nameof(MultiplayerSection), false)
            {
                PlayerCount = BindInt32(nameof(PlayerCount), 2);
                BattleMenus = BindBoolean(nameof(BattleMenus), true);
                FieldFollowers = BindBoolean(nameof(FieldFollowers), true);
                BlackWaltzModel = BindBoolean(nameof(BlackWaltzModel), false);
                BlackMageModel = BindBoolean(nameof(BlackMageModel), false);
                FieldModels = BindString(nameof(FieldModels), "");
                DebugOverlay = BindBoolean(nameof(DebugOverlay), false);
                PlayerPads = BindString(nameof(PlayerPads), "0, 1, 2, 3");
            }
        }
    }
}
