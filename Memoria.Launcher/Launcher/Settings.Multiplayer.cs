using System;
using System.Collections.Generic;
using System.Globalization;

namespace Memoria.Launcher
{
    // [Multiplayer] Fork addition: the launcher's view of Memoria.ini's [Multiplayer] section (SettingsGrid_Multiplayer).
    // Every value is read from and written straight back to the ini, so editing the file by hand keeps working.
    // These settings are not in SettingsList on purpose: its generic writer sets "Enabled = 1" in the section whenever
    // a toggle is switched on, which would turn multiplayer on when, say, BattleMenus is ticked.
    public partial class MainWindow
    {
        private const String MultiplayerSection = "Multiplayer";
        public const Int32 MultiplayerMaxPlayers = 4;

        // Number of players as a combobox index: 0 = one player (multiplayer off), 1-3 = 2-4 players.
        // Writes [Multiplayer] Enabled and PlayerCount (PlayerCount is kept when multiplayer is switched off).
        private Int16 _multiplayerPlayers;
        public Int16 MultiplayerPlayers { get => _multiplayerPlayers; set => SetProperty(ref _multiplayerPlayers, value); }

        private Int16 _multiplayerBattleMenus = 1;
        public Int16 MultiplayerBattleMenus { get => _multiplayerBattleMenus; set => SetProperty(ref _multiplayerBattleMenus, value); }
        private Int16 _multiplayerFieldFollowers = 1;
        public Int16 MultiplayerFieldFollowers { get => _multiplayerFieldFollowers; set => SetProperty(ref _multiplayerFieldFollowers, value); }
        private Int16 _multiplayerDebugOverlay;
        public Int16 MultiplayerDebugOverlay { get => _multiplayerDebugOverlay; set => SetProperty(ref _multiplayerDebugOverlay, value); }
        private Int16 _multiplayerBlackWaltzModel;
        public Int16 MultiplayerBlackWaltzModel { get => _multiplayerBlackWaltzModel; set => SetProperty(ref _multiplayerBlackWaltzModel, value); }
        private Int16 _multiplayerBlackMageModel;
        public Int16 MultiplayerBlackMageModel { get => _multiplayerBlackMageModel; set => SetProperty(ref _multiplayerBlackMageModel, value); }

        // Controller slot (0-3) of each player: [Multiplayer] PlayerPads
        private Int16 _multiplayerPad1 = 0, _multiplayerPad2 = 1, _multiplayerPad3 = 2, _multiplayerPad4 = 3;
        public Int16 MultiplayerPad1 { get => _multiplayerPad1; set => SetProperty(ref _multiplayerPad1, value); }
        public Int16 MultiplayerPad2 { get => _multiplayerPad2; set => SetProperty(ref _multiplayerPad2, value); }
        public Int16 MultiplayerPad3 { get => _multiplayerPad3; set => SetProperty(ref _multiplayerPad3, value); }
        public Int16 MultiplayerPad4 { get => _multiplayerPad4; set => SetProperty(ref _multiplayerPad4, value); }

        /// <summary>Called from OnPropertyChanged: write a multiplayer setting that changed in the launcher.</summary>
        private void SaveMultiplayerSetting(IniFile iniFile, String propertyName)
        {
            switch (propertyName)
            {
                case nameof(MultiplayerPlayers):
                    iniFile.SetSetting(MultiplayerSection, "Enabled", MultiplayerPlayers > 0 ? "1" : "0");
                    if (MultiplayerPlayers > 0)
                        iniFile.SetSetting(MultiplayerSection, "PlayerCount", $"{MultiplayerPlayers + 1}");
                    break;
                case nameof(MultiplayerBattleMenus):
                    iniFile.SetSetting(MultiplayerSection, "BattleMenus", MultiplayerBattleMenus != 0 ? "1" : "0");
                    break;
                case nameof(MultiplayerFieldFollowers):
                    iniFile.SetSetting(MultiplayerSection, "FieldFollowers", MultiplayerFieldFollowers != 0 ? "1" : "0");
                    break;
                case nameof(MultiplayerDebugOverlay):
                    iniFile.SetSetting(MultiplayerSection, "DebugOverlay", MultiplayerDebugOverlay != 0 ? "1" : "0");
                    break;
                case nameof(MultiplayerBlackWaltzModel):
                    iniFile.SetSetting(MultiplayerSection, "BlackWaltzModel", MultiplayerBlackWaltzModel != 0 ? "1" : "0");
                    break;
                case nameof(MultiplayerBlackMageModel):
                    iniFile.SetSetting(MultiplayerSection, "BlackMageModel", MultiplayerBlackMageModel != 0 ? "1" : "0");
                    break;
                case nameof(MultiplayerPad1):
                case nameof(MultiplayerPad2):
                case nameof(MultiplayerPad3):
                case nameof(MultiplayerPad4):
                    iniFile.SetSetting(MultiplayerSection, "PlayerPads", $"{MultiplayerPad1}, {MultiplayerPad2}, {MultiplayerPad3}, {MultiplayerPad4}");
                    break;
            }
        }

        /// <summary>Called from LoadSettings: read [Multiplayer] into the launcher's properties (without writing back).</summary>
        private void LoadMultiplayerSettings(IniFile iniFile)
        {
            Boolean enabled = ReadMultiplayerFlag(iniFile, "Enabled", false) != 0;
            Int32 playerCount = ReadMultiplayerNumber(iniFile, "PlayerCount", 2);
            _multiplayerPlayers = (Int16)(enabled ? Math.Max(2, Math.Min(MultiplayerMaxPlayers, playerCount)) - 1 : 0);
            _multiplayerBattleMenus = ReadMultiplayerFlag(iniFile, "BattleMenus", true);
            _multiplayerFieldFollowers = ReadMultiplayerFlag(iniFile, "FieldFollowers", true);
            _multiplayerDebugOverlay = ReadMultiplayerFlag(iniFile, "DebugOverlay", false);
            _multiplayerBlackWaltzModel = ReadMultiplayerFlag(iniFile, "BlackWaltzModel", false);
            _multiplayerBlackMageModel = ReadMultiplayerFlag(iniFile, "BlackMageModel", false);

            Int16[] pads = [0, 1, 2, 3];
            String[] padParts = SplitMultiplayerList(iniFile.GetSetting(MultiplayerSection, "PlayerPads", "0, 1, 2, 3"));
            for (Int32 i = 0; i < padParts.Length && i < MultiplayerMaxPlayers; i++)
                if (Int16.TryParse(padParts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out Int16 slot) && slot >= 0 && slot < MultiplayerMaxPlayers)
                    pads[i] = slot;
            _multiplayerPad1 = pads[0];
            _multiplayerPad2 = pads[1];
            _multiplayerPad3 = pads[2];
            _multiplayerPad4 = pads[3];

            Refresh(nameof(MultiplayerPlayers));
            Refresh(nameof(MultiplayerBattleMenus));
            Refresh(nameof(MultiplayerFieldFollowers));
            Refresh(nameof(MultiplayerDebugOverlay));
            Refresh(nameof(MultiplayerBlackWaltzModel));
            Refresh(nameof(MultiplayerBlackMageModel));
            Refresh(nameof(MultiplayerPad1));
            Refresh(nameof(MultiplayerPad2));
            Refresh(nameof(MultiplayerPad3));
            Refresh(nameof(MultiplayerPad4));
        }

        private static Int16 ReadMultiplayerFlag(IniFile iniFile, String key, Boolean defaultValue)
        {
            return (Int16)(ReadMultiplayerNumber(iniFile, key, defaultValue ? 1 : 0) != 0 ? 1 : 0);
        }

        private static Int32 ReadMultiplayerNumber(IniFile iniFile, String key, Int32 defaultValue)
        {
            String value = iniFile.GetSetting(MultiplayerSection, key).Trim().Trim('"').Trim();
            return Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out Int32 number) ? number : defaultValue;
        }

        private static String[] SplitMultiplayerList(String text)
        {
            List<String> result = new List<String>();
            if (String.IsNullOrEmpty(text))
                return result.ToArray();
            foreach (String part in text.Split(','))
            {
                String trimmed = part.Trim().Trim('"').Trim();
                if (trimmed.Length > 0)
                    result.Add(trimmed);
            }
            return result.ToArray();
        }
    }
}
