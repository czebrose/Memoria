using Memoria.Data;
using Memoria.Prime;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Memoria.Multiplayer
{
    /// <summary>
    /// Entry point for the local multiplayer feature (fork addition).
    /// Everything multiplayer-related goes through here and is gated by <see cref="IsActive"/>,
    /// so that with [Multiplayer] Enabled = 0 the game behaves exactly like upstream Memoria.
    ///
    /// Players are numbered 1-4. Player N reads the XInput slot given by [Multiplayer] PlayerPads
    /// (default: slot N-1); the keyboard belongs to player 1. Only the player "in control"
    /// (<see cref="ActivePlayer"/>) reaches the game: XInputManager reads that player's slot and the
    /// keyboard is ignored unless player 1 is in control.
    /// </summary>
    public static class MultiplayerManager
    {
        public const Int32 MaxPlayers = 4;
        private const KeyCode DebugSwitchKey = KeyCode.F10;

        private static Int32 s_activePlayer = 1;
        private static Boolean s_wasActive;
        private static readonly Boolean[] s_slotConnected = new Boolean[XInputPads.SlotCount];
        private static MultiplayerOverlay s_overlay;

        private static String s_padsSource;
        private static Int32[] s_pads = { 0, 1, 2, 3 };
        private static String s_ownersSource;
        private static Dictionary<CharacterId, Int32> s_owners = new Dictionary<CharacterId, Int32>();

        public static Boolean IsActive => Configuration.Multiplayer.Enabled;

        /// <summary>The player (1-4) whose controller currently drives the game.</summary>
        public static Int32 ActivePlayer => s_activePlayer;

        /// <summary>False while multiplayer is on and someone other than player 1 has control.</summary>
        public static Boolean KeyboardAllowed => !IsActive || s_activePlayer == 1;

        /// <summary>Called once at startup, from AssetManager's static constructor.</summary>
        public static void Initialize()
        {
            Log.Message($"[Multiplayer] Enabled: {IsActive}");
            if (!IsActive)
                return;
            RefreshConfig();
            Log.Message($"[Multiplayer] Controller slots: {DescribePads()}");
            Log.Message($"[Multiplayer] Character owners: {DescribeOwners()}");
            Log.Message($"[Multiplayer] Control: P{s_activePlayer}");
        }

        /// <summary>Called every frame from XInputManager.Update, before it reads the active controller.</summary>
        public static void Update()
        {
            XInputDotNetPure.GamePad.MergeJoyShockDevices = !IsActive;
            if (!IsActive)
            {
                if (s_wasActive)
                {
                    s_wasActive = false;
                    SetActivePlayer(1, "multiplayer disabled");
                }
                return;
            }
            s_wasActive = true;
            RefreshConfig();
            XInputPads.Poll();
            LogSlotChanges();
            MultiplayerBattle.Update();

            if (Configuration.Multiplayer.DebugOverlay)
            {
                if (s_overlay == null)
                {
                    GameObject go = new GameObject("MultiplayerOverlay");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    s_overlay = go.AddComponent<MultiplayerOverlay>();
                }
                if (UnityEngine.Input.GetKeyDown(DebugSwitchKey))
                    SetActivePlayer(s_activePlayer % MaxPlayers + 1, $"{DebugSwitchKey} debug key");
                LogButtonPresses();
            }
        }

        public static void SetActivePlayer(Int32 player, String reason)
        {
            if (player < 1 || player > MaxPlayers || player == s_activePlayer)
                return;
            Log.Message($"[Multiplayer] Control: P{s_activePlayer} -> P{player} ({reason})");
            s_activePlayer = player;
        }

        /// <summary>XInput slot (0-3) used by a player, or -1 if none.</summary>
        public static Int32 GetPadSlot(Int32 player)
        {
            if (player < 1 || player > MaxPlayers)
                return -1;
            return s_pads[player - 1];
        }

        /// <summary>The XInput slot XInputManager should read: the active player's pad.</summary>
        public static Int32 ActivePadSlot => GetPadSlot(s_activePlayer);

        /// <summary>Player (1-4) that owns a character; characters not listed in the config belong to player 1.</summary>
        public static Int32 GetOwner(CharacterId character)
        {
            return s_owners.TryGetValue(character, out Int32 player) ? player : 1;
        }

        private static void RefreshConfig()
        {
            String pads = Configuration.Multiplayer.PlayerPads ?? "";
            if (pads != s_padsSource)
            {
                Boolean reload = s_padsSource != null;
                s_padsSource = pads;
                s_pads = ParsePads(pads);
                if (reload)
                    Log.Message($"[Multiplayer] Controller slots changed: {DescribePads()}");
            }
            String owners = Configuration.Multiplayer.GetPlayerCharacters(2) + "|" + Configuration.Multiplayer.GetPlayerCharacters(3) + "|" + Configuration.Multiplayer.GetPlayerCharacters(4);
            if (owners != s_ownersSource)
            {
                Boolean reload = s_ownersSource != null;
                s_ownersSource = owners;
                s_owners = ParseOwners();
                if (reload)
                    Log.Message($"[Multiplayer] Character owners changed: {DescribeOwners()}");
            }
        }

        private static Int32[] ParsePads(String text)
        {
            Int32[] result = { 0, 1, 2, 3 };
            String[] parts = SplitList(text);
            for (Int32 i = 0; i < parts.Length && i < MaxPlayers; i++)
            {
                Int32 slot;
                try
                {
                    slot = Int32.Parse(parts[i]);
                }
                catch (FormatException)
                {
                    Log.Warning($"[Multiplayer] PlayerPads: '{parts[i]}' is not a number; P{i + 1} keeps slot {result[i]}");
                    continue;
                }
                if (slot < 0 || slot >= XInputPads.SlotCount)
                {
                    Log.Warning($"[Multiplayer] PlayerPads: slot {slot} for P{i + 1} is out of range (0-3); keeping slot {result[i]}");
                    continue;
                }
                result[i] = slot;
            }
            return result;
        }

        private static Dictionary<CharacterId, Int32> ParseOwners()
        {
            Dictionary<CharacterId, Int32> result = new Dictionary<CharacterId, Int32>();
            for (Int32 player = 2; player <= MaxPlayers; player++)
            {
                foreach (String name in SplitList(Configuration.Multiplayer.GetPlayerCharacters(player)))
                {
                    CharacterId id;
                    if (!TryParseCharacter(name, out id))
                    {
                        Log.Warning($"[Multiplayer] Player{player}Characters: unknown character '{name}'");
                        continue;
                    }
                    if (result.ContainsKey(id))
                        Log.Warning($"[Multiplayer] {id} is listed for both P{result[id]} and P{player}; keeping P{result[id]}");
                    else
                        result[id] = player;
                }
            }
            return result;
        }

        private static Boolean TryParseCharacter(String text, out CharacterId id)
        {
            foreach (String name in Enum.GetNames(typeof(CharacterId)))
            {
                if (String.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                {
                    id = (CharacterId)Enum.Parse(typeof(CharacterId), name);
                    return id != CharacterId.NONE;
                }
            }
            try
            {
                Int32 number = Int32.Parse(text);
                id = (CharacterId)number;
                return number >= 0 && number < (Int32)CharacterId.NONE;
            }
            catch (FormatException)
            {
                id = CharacterId.NONE;
                return false;
            }
        }

        private static String[] SplitList(String text)
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

        private static void LogSlotChanges()
        {
            for (Int32 slot = 0; slot < XInputPads.SlotCount; slot++)
            {
                Boolean connected = XInputPads.Get(slot).Connected;
                if (connected == s_slotConnected[slot])
                    continue;
                s_slotConnected[slot] = connected;
                String players = "";
                for (Int32 player = 1; player <= MaxPlayers; player++)
                    if (GetPadSlot(player) == slot)
                        players += (players.Length > 0 ? ", " : "") + "P" + player;
                Log.Message($"[Multiplayer] Controller slot {slot} {(connected ? "connected" : "disconnected")} ({(players.Length > 0 ? players : "no player")})");
            }
        }

        private static readonly UInt16[] LoggedButtons = { XInputPads.A, XInputPads.B, XInputPads.X, XInputPads.Y, XInputPads.Start, XInputPads.Back, XInputPads.LeftShoulder, XInputPads.RightShoulder };

        private static void LogButtonPresses()
        {
            for (Int32 player = 1; player <= MaxPlayers; player++)
            {
                Int32 slot = GetPadSlot(player);
                foreach (UInt16 button in LoggedButtons)
                    if (XInputPads.IsPressed(slot, button))
                        Log.Message($"[Multiplayer] P{player} (slot {slot}) pressed {ButtonName(button)}{(player == s_activePlayer ? "" : " [ignored: not in control]")}");
            }
        }

        public static String ButtonName(UInt16 button)
        {
            switch (button)
            {
                case XInputPads.A: return "A";
                case XInputPads.B: return "B";
                case XInputPads.X: return "X";
                case XInputPads.Y: return "Y";
                case XInputPads.Start: return "Start";
                case XInputPads.Back: return "Back";
                case XInputPads.LeftShoulder: return "LB";
                case XInputPads.RightShoulder: return "RB";
                case XInputPads.LeftThumb: return "L3";
                case XInputPads.RightThumb: return "R3";
                case XInputPads.DPadUp: return "Up";
                case XInputPads.DPadDown: return "Down";
                case XInputPads.DPadLeft: return "Left";
                case XInputPads.DPadRight: return "Right";
                default: return "0x" + button.ToString("X4");
            }
        }

        private static String DescribePads()
        {
            String text = "";
            for (Int32 player = 1; player <= MaxPlayers; player++)
                text += (player > 1 ? ", " : "") + $"P{player}=slot {GetPadSlot(player)}" + (player == 1 ? "+keyboard" : "");
            return text;
        }

        private static String DescribeOwners()
        {
            if (s_owners.Count == 0)
                return "all characters belong to P1";
            String text = "";
            foreach (KeyValuePair<CharacterId, Int32> pair in s_owners)
                text += (text.Length > 0 ? ", " : "") + $"{pair.Key}=P{pair.Value}";
            return text + " (others: P1)";
        }
    }
}
