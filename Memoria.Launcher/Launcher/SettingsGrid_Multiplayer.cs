using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Memoria.Launcher.Utils;
using Application = System.Windows.Application;

namespace Memoria.Launcher
{
    // [Multiplayer] Fork addition: the launcher's Multiplayer tab. Each control edits one value of Memoria.ini's
    // [Multiplayer] section (see Settings.Multiplayer.cs); the ini file stays the source of truth.

    /// <summary>Column 1: switches and each player's controller.</summary>
    public sealed class SettingsGrid_Multiplayer : UiGrid
    {
        public SettingsGrid_Multiplayer()
        {
            DataContext = (MainWindow)Application.Current.MainWindow;

            CreateHeading("Settings.Multiplayer");
            CreateCheckbox("MultiplayerEnabled", "Settings.MultiplayerEnabled", "Settings.MultiplayerEnabled_Tooltip");
            CreateCheckbox("MultiplayerFieldFollowers", "Settings.MultiplayerFieldFollowers", "Settings.MultiplayerFieldFollowers_Tooltip");
            CreateCheckbox("MultiplayerBattleMenus", "Settings.MultiplayerBattleMenus", "Settings.MultiplayerBattleMenus_Tooltip");
            CreateCheckbox("MultiplayerDebugOverlay", "Settings.MultiplayerDebugOverlay", "Settings.MultiplayerDebugOverlay_Tooltip");

            CreateHeading("Settings.MultiplayerControllers");
            String[] slots = { "Settings.MultiplayerSlot1", "Settings.MultiplayerSlot2", "Settings.MultiplayerSlot3", "Settings.MultiplayerSlot4" };
            for (Int32 player = 1; player <= MainWindow.MultiplayerMaxPlayers; player++)
                CreateCombobox($"MultiplayerPad{player}", ComboBoxOptions.Localized(slots), 50, $"Settings.MultiplayerPlayer{player}", "Settings.MultiplayerControllers_Tooltip");
        }
    }

    /// <summary>Column 2: which player controls each character.</summary>
    public sealed class SettingsGrid_Multiplayer2 : UiGrid
    {
        public SettingsGrid_Multiplayer2()
        {
            DataContext = (MainWindow)Application.Current.MainWindow;

            CreateHeading("Settings.MultiplayerCharacters");
            String[] players = { "Settings.MultiplayerPlayer1", "Settings.MultiplayerPlayer2", "Settings.MultiplayerPlayer3", "Settings.MultiplayerPlayer4" };
            foreach (String character in MainWindow.MultiplayerCharacters)
                CreateCombobox("MultiplayerOwner" + character, ComboBoxOptions.Localized(players), 50, character, "Settings.MultiplayerCharacters_Tooltip");
        }
    }

    /// <summary>Column 3: field model overrides (free text, one character per line).</summary>
    public sealed class SettingsGrid_Multiplayer3 : UiGrid
    {
        public SettingsGrid_Multiplayer3()
        {
            DataContext = (MainWindow)Application.Current.MainWindow;

            CreateHeading("Settings.MultiplayerFieldModels");
            CreateTextbloc("Settings.MultiplayerFieldModels_Help", "Settings.MultiplayerFieldModels_Tooltip");

            Row++;
            RowDefinitions.Add(new RowDefinition());
            TextBox textBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 140,
                Margin = CommonMargin,
                FontSize = 12,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            };
            // Written to the ini when the box loses focus
            textBox.SetBinding(TextBox.TextProperty, new Binding("MultiplayerFieldModels") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            MakeTooltip(textBox, "Settings.MultiplayerFieldModels_Tooltip");
            textBox.SetValue(RowProperty, Row);
            textBox.SetValue(ColumnProperty, 0);
            textBox.SetValue(ColumnSpanProperty, MaxColumns);
            Children.Add(textBox);

            CreateTextbloc("Settings.MultiplayerFieldModels_Example", "Settings.MultiplayerFieldModels_Tooltip");
        }
    }
}
