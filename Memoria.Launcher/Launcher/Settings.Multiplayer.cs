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

        /// <summary>Characters a player can own, in CharacterId order (the game also accepts these numbers).</summary>
        public static readonly String[] MultiplayerCharacters = ["Zidane", "Vivi", "Garnet", "Steiner", "Freya", "Quina", "Eiko", "Amarant", "Cinna", "Marcus", "Blank", "Beatrix"];

        private Int16 _multiplayerEnabled;
        public Int16 MultiplayerEnabled { get => _multiplayerEnabled; set => SetProperty(ref _multiplayerEnabled, value); }
        private Int16 _multiplayerBattleMenus = 1;
        public Int16 MultiplayerBattleMenus { get => _multiplayerBattleMenus; set => SetProperty(ref _multiplayerBattleMenus, value); }
        private Int16 _multiplayerFieldFollowers = 1;
        public Int16 MultiplayerFieldFollowers { get => _multiplayerFieldFollowers; set => SetProperty(ref _multiplayerFieldFollowers, value); }
        private Int16 _multiplayerDebugOverlay;
        public Int16 MultiplayerDebugOverlay { get => _multiplayerDebugOverlay; set => SetProperty(ref _multiplayerDebugOverlay, value); }

        // Controller slot (0-3) of each player: [Multiplayer] PlayerPads
        private Int16 _multiplayerPad1 = 0, _multiplayerPad2 = 1, _multiplayerPad3 = 2, _multiplayerPad4 = 3;
        public Int16 MultiplayerPad1 { get => _multiplayerPad1; set => SetProperty(ref _multiplayerPad1, value); }
        public Int16 MultiplayerPad2 { get => _multiplayerPad2; set => SetProperty(ref _multiplayerPad2, value); }
        public Int16 MultiplayerPad3 { get => _multiplayerPad3; set => SetProperty(ref _multiplayerPad3, value); }
        public Int16 MultiplayerPad4 { get => _multiplayerPad4; set => SetProperty(ref _multiplayerPad4, value); }

        // Owner of each character (0 = player 1 ... 3 = player 4): [Multiplayer] Player2Characters, Player3Characters, Player4Characters
        private readonly Int16[] _multiplayerOwners = new Int16[12];
        public Int16 MultiplayerOwnerZidane { get => _multiplayerOwners[0]; set => SetMultiplayerOwner(0, value); }
        public Int16 MultiplayerOwnerVivi { get => _multiplayerOwners[1]; set => SetMultiplayerOwner(1, value); }
        public Int16 MultiplayerOwnerGarnet { get => _multiplayerOwners[2]; set => SetMultiplayerOwner(2, value); }
        public Int16 MultiplayerOwnerSteiner { get => _multiplayerOwners[3]; set => SetMultiplayerOwner(3, value); }
        public Int16 MultiplayerOwnerFreya { get => _multiplayerOwners[4]; set => SetMultiplayerOwner(4, value); }
        public Int16 MultiplayerOwnerQuina { get => _multiplayerOwners[5]; set => SetMultiplayerOwner(5, value); }
        public Int16 MultiplayerOwnerEiko { get => _multiplayerOwners[6]; set => SetMultiplayerOwner(6, value); }
        public Int16 MultiplayerOwnerAmarant { get => _multiplayerOwners[7]; set => SetMultiplayerOwner(7, value); }
        public Int16 MultiplayerOwnerCinna { get => _multiplayerOwners[8]; set => SetMultiplayerOwner(8, value); }
        public Int16 MultiplayerOwnerMarcus { get => _multiplayerOwners[9]; set => SetMultiplayerOwner(9, value); }
        public Int16 MultiplayerOwnerBlank { get => _multiplayerOwners[10]; set => SetMultiplayerOwner(10, value); }
        public Int16 MultiplayerOwnerBeatrix { get => _multiplayerOwners[11]; set => SetMultiplayerOwner(11, value); }

        // [Multiplayer] FieldModels, edited as text (one "Name=..." entry per line in the launcher, ';'-separated in the ini)
        private String _multiplayerFieldModels = "";
        public String MultiplayerFieldModels { get => _multiplayerFieldModels; set => SetProperty(ref _multiplayerFieldModels, value ?? ""); }

        private void SetMultiplayerOwner(Int32 character, Int16 owner)
        {
            if (_multiplayerOwners[character] == owner)
                return;
            _multiplayerOwners[character] = owner;
            OnPropertyChanged("MultiplayerOwner" + MultiplayerCharacters[character]);
        }

        /// <summary>Called from OnPropertyChanged: write a multiplayer setting that changed in the launcher.</summary>
        private void SaveMultiplayerSetting(IniFile iniFile, String propertyName)
        {
            if (propertyName == null || !propertyName.StartsWith("Multiplayer", StringComparison.Ordinal))
                return;
            switch (propertyName)
            {
                case nameof(MultiplayerEnabled):
                    iniFile.SetSetting(MultiplayerSection, "Enabled", MultiplayerEnabled != 0 ? "1" : "0");
                    return;
                case nameof(MultiplayerBattleMenus):
                    iniFile.SetSetting(MultiplayerSection, "BattleMenus", MultiplayerBattleMenus != 0 ? "1" : "0");
                    return;
                case nameof(MultiplayerFieldFollowers):
                    iniFile.SetSetting(MultiplayerSection, "FieldFollowers", MultiplayerFieldFollowers != 0 ? "1" : "0");
                    return;
                case nameof(MultiplayerDebugOverlay):
                    iniFile.SetSetting(MultiplayerSection, "DebugOverlay", MultiplayerDebugOverlay != 0 ? "1" : "0");
                    return;
                case nameof(MultiplayerPad1):
                case nameof(MultiplayerPad2):
                case nameof(MultiplayerPad3):
                case nameof(MultiplayerPad4):
                    iniFile.SetSetting(MultiplayerSection, "PlayerPads", $"{MultiplayerPad1}, {MultiplayerPad2}, {MultiplayerPad3}, {MultiplayerPad4}");
                    return;
                case nameof(MultiplayerFieldModels):
                    iniFile.SetSetting(MultiplayerSection, "FieldModels", JoinFieldModels(MultiplayerFieldModels));
                    return;
            }
            if (propertyName.StartsWith("MultiplayerOwner", StringComparison.Ordinal))
            {
                for (Int32 player = 2; player <= MultiplayerMaxPlayers; player++)
                {
                    List<String> names = new List<String>();
                    for (Int32 i = 0; i < MultiplayerCharacters.Length; i++)
                        if (_multiplayerOwners[i] == player - 1)
                            names.Add(MultiplayerCharacters[i]);
                    iniFile.SetSetting(MultiplayerSection, $"Player{player}Characters", String.Join(", ", names));
                }
            }
        }

        /// <summary>Called from LoadSettings: read [Multiplayer] into the launcher's properties (without writing back).</summary>
        private void LoadMultiplayerSettings(IniFile iniFile)
        {
            _multiplayerEnabled = ReadMultiplayerFlag(iniFile, "Enabled", false);
            _multiplayerBattleMenus = ReadMultiplayerFlag(iniFile, "BattleMenus", true);
            _multiplayerFieldFollowers = ReadMultiplayerFlag(iniFile, "FieldFollowers", true);
            _multiplayerDebugOverlay = ReadMultiplayerFlag(iniFile, "DebugOverlay", false);

            Int16[] pads = [0, 1, 2, 3];
            String[] padParts = SplitMultiplayerList(iniFile.GetSetting(MultiplayerSection, "PlayerPads", "0, 1, 2, 3"), ',');
            for (Int32 i = 0; i < padParts.Length && i < MultiplayerMaxPlayers; i++)
                if (Int16.TryParse(padParts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out Int16 slot) && slot >= 0 && slot < MultiplayerMaxPlayers)
                    pads[i] = slot;
            _multiplayerPad1 = pads[0];
            _multiplayerPad2 = pads[1];
            _multiplayerPad3 = pads[2];
            _multiplayerPad4 = pads[3];

            // Same rules as the game: unlisted characters belong to player 1, the first player listing a character keeps it
            for (Int32 i = 0; i < _multiplayerOwners.Length; i++)
                _multiplayerOwners[i] = 0;
            for (Int32 player = 2; player <= MultiplayerMaxPlayers; player++)
            {
                foreach (String name in SplitMultiplayerList(iniFile.GetSetting(MultiplayerSection, $"Player{player}Characters"), ','))
                {
                    Int32 character = FindMultiplayerCharacter(name);
                    if (character >= 0 && _multiplayerOwners[character] == 0)
                        _multiplayerOwners[character] = (Int16)(player - 1);
                }
            }

            _multiplayerFieldModels = String.Join(Environment.NewLine, SplitMultiplayerList(iniFile.GetSetting(MultiplayerSection, "FieldModels"), ';'));

            Refresh(nameof(MultiplayerEnabled));
            Refresh(nameof(MultiplayerBattleMenus));
            Refresh(nameof(MultiplayerFieldFollowers));
            Refresh(nameof(MultiplayerDebugOverlay));
            Refresh(nameof(MultiplayerPad1));
            Refresh(nameof(MultiplayerPad2));
            Refresh(nameof(MultiplayerPad3));
            Refresh(nameof(MultiplayerPad4));
            foreach (String character in MultiplayerCharacters)
                Refresh("MultiplayerOwner" + character);
            Refresh(nameof(MultiplayerFieldModels));
        }

        private static Int16 ReadMultiplayerFlag(IniFile iniFile, String key, Boolean defaultValue)
        {
            String value = iniFile.GetSetting(MultiplayerSection, key).Trim().Trim('"').Trim();
            if (Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out Int32 number))
                return (Int16)(number != 0 ? 1 : 0);
            return (Int16)(defaultValue ? 1 : 0);
        }

        private static Int32 FindMultiplayerCharacter(String text)
        {
            for (Int32 i = 0; i < MultiplayerCharacters.Length; i++)
                if (String.Equals(MultiplayerCharacters[i], text, StringComparison.OrdinalIgnoreCase))
                    return i;
            if (Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out Int32 number) && number >= 0 && number < MultiplayerCharacters.Length)
                return number;
            return -1;
        }

        private static String[] SplitMultiplayerList(String text, Char separator)
        {
            List<String> result = new List<String>();
            if (String.IsNullOrEmpty(text))
                return result.ToArray();
            foreach (String part in text.Split(separator, '\r', '\n'))
            {
                String trimmed = part.Trim().Trim('"').Trim();
                if (trimmed.Length > 0)
                    result.Add(trimmed);
            }
            return result.ToArray();
        }

        private static String JoinFieldModels(String text)
        {
            return String.Join("; ", SplitMultiplayerList(text, ';'));
        }
    }
}
