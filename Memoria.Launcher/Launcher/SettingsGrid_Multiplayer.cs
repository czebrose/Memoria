using System;
using Memoria.Launcher.Utils;
using Application = System.Windows.Application;

namespace Memoria.Launcher
{
    // [Multiplayer] Fork addition: the launcher's Multiplayer tab. Each control edits one value of Memoria.ini's
    // [Multiplayer] section (see Settings.Multiplayer.cs); the ini file stays the source of truth.

    /// <summary>Column 1: number of players, switches and each player's controller.</summary>
    public sealed class SettingsGrid_Multiplayer : UiGrid
    {
        public SettingsGrid_Multiplayer()
        {
            DataContext = (MainWindow)Application.Current.MainWindow;

            CreateHeading("Settings.Multiplayer");
            String[] players = { "Settings.MultiplayerPlayers1", "Settings.MultiplayerPlayers2", "Settings.MultiplayerPlayers3", "Settings.MultiplayerPlayers4" };
            CreateCombobox("MultiplayerPlayers", ComboBoxOptions.Localized(players), 50, "Settings.MultiplayerPlayers", "Settings.MultiplayerPlayers_Tooltip");
            CreateCheckbox("MultiplayerFieldFollowers", "Settings.MultiplayerFieldFollowers", "Settings.MultiplayerFieldFollowers_Tooltip");
            CreateCheckbox("MultiplayerBattleMenus", "Settings.MultiplayerBattleMenus", "Settings.MultiplayerBattleMenus_Tooltip");
            CreateCheckbox("MultiplayerDebugOverlay", "Settings.MultiplayerDebugOverlay", "Settings.MultiplayerDebugOverlay_Tooltip");

            CreateHeading("Settings.MultiplayerControllers");
            String[] slots = { "Settings.MultiplayerSlot1", "Settings.MultiplayerSlot2", "Settings.MultiplayerSlot3", "Settings.MultiplayerSlot4" };
            for (Int32 player = 1; player <= MainWindow.MultiplayerMaxPlayers; player++)
                CreateCombobox($"MultiplayerPad{player}", ComboBoxOptions.Localized(slots), 50, $"Settings.MultiplayerPlayer{player}", "Settings.MultiplayerControllers_Tooltip");
        }
    }

    /// <summary>Column 2: field model swaps for extra players' characters.</summary>
    public sealed class SettingsGrid_Multiplayer2 : UiGrid
    {
        public SettingsGrid_Multiplayer2()
        {
            DataContext = (MainWindow)Application.Current.MainWindow;

            CreateHeading("Settings.MultiplayerFieldModels");
            CreateCheckbox("MultiplayerBlackWaltzModel", "Settings.MultiplayerBlackWaltzModel", "Settings.MultiplayerBlackWaltzModel_Tooltip");
            CreateCheckbox("MultiplayerBlackMageModel", "Settings.MultiplayerBlackMageModel", "Settings.MultiplayerBlackMageModel_Tooltip");
        }
    }
}
