using System;

namespace Memoria.Multiplayer
{
    /// <summary>
    /// Event-script key masks (EventInput bits) for players 2-4, so a script running on an extra player's behalf
    /// (their chest, their talk) reads their controller in IsButton(...) checks.
    ///
    /// Like ETb.ProcessKeyEvents for player 1, the masks are updated once per field tick (Tick); a press that started
    /// and ended between two ticks still counts as a key-on (presses are collected every frame in Collect).
    /// </summary>
    public static class MultiplayerKeys
    {
        private const Int32 PlayerCount = MultiplayerManager.MaxPlayers + 1;

        private static readonly UInt32[] s_key = new UInt32[PlayerCount];
        private static readonly UInt32[] s_keyOn = new UInt32[PlayerCount];
        private static readonly UInt32[] s_keyOff = new UInt32[PlayerCount];
        private static readonly UInt32[] s_pressedSinceTick = new UInt32[PlayerCount];

        /// <summary>Every frame: remember presses so a short tap between two ticks is not lost.</summary>
        internal static void Collect()
        {
            for (Int32 player = 2; player <= MultiplayerManager.MaxPlayers; player++)
            {
                Int32 slot = MultiplayerManager.GetPadSlot(player);
                s_pressedSinceTick[player] |= ToMask(XInputPads.Get(slot)) & ~ToMask(XInputPads.GetPrevious(slot));
            }
        }

        /// <summary>Once per field tick, before scripts may read the masks in the next tick.</summary>
        internal static void Tick()
        {
            for (Int32 player = 2; player <= MultiplayerManager.MaxPlayers; player++)
            {
                UInt32 held = ToMask(XInputPads.Get(MultiplayerManager.GetPadSlot(player)));
                s_keyOn[player] = (held & ~s_key[player]) | s_pressedSinceTick[player];
                s_keyOff[player] = ~held & s_key[player];
                s_key[player] = held;
                s_pressedSinceTick[player] = 0;
            }
        }

        public static UInt32 KeyOn(Int32 player, Boolean japaneseLayout) => Layout(player < PlayerCount ? s_keyOn[player] : 0u, japaneseLayout);
        public static UInt32 KeyOff(Int32 player, Boolean japaneseLayout) => Layout(player < PlayerCount ? s_keyOff[player] : 0u, japaneseLayout);
        public static UInt32 Key(Int32 player, Boolean japaneseLayout) => Layout(player < PlayerCount ? s_key[player] : 0u, japaneseLayout);

        /// <summary>Same masking and Japanese-script swap as ETb.KeyOn/KeyOff/GetInputs.</summary>
        private static UInt32 Layout(UInt32 mask, Boolean japaneseLayout)
        {
            mask &= 0x3FFFFFFu;
            return japaneseLayout ? ETb.ProcessJapaneseLayout(mask) : mask;
        }

        /// <summary>
        /// An Xbox-layout pad as event-script bits: the logical button (Confirm, Cancel...) plus the PlayStation button at
        /// the same position, like EventInput builds them for player 1 with the default button layout.
        /// </summary>
        private static UInt32 ToMask(PadState pad)
        {
            if (!pad.Connected)
                return 0u;
            Boolean swapConfirm = EventInput.isJapaneseLayout; // Confirm on Circle / B
            UInt32 mask = 0u;
            if (pad.IsDown(XInputPads.A)) mask |= EventInput.Cross | (swapConfirm ? EventInput.Cancel : EventInput.Confirm);
            if (pad.IsDown(XInputPads.B)) mask |= EventInput.Circle | (swapConfirm ? EventInput.Confirm : EventInput.Cancel);
            if (pad.IsDown(XInputPads.X)) mask |= EventInput.Square | EventInput.Special;
            if (pad.IsDown(XInputPads.Y)) mask |= EventInput.Triangle | EventInput.Menu;
            if (pad.IsDown(XInputPads.LeftShoulder)) mask |= EventInput.L1 | EventInput.LeftBumper;
            if (pad.IsDown(XInputPads.RightShoulder)) mask |= EventInput.R1 | EventInput.RightBumper;
            if (pad.LeftTrigger > 191) mask |= EventInput.L2 | EventInput.LeftTrigger;
            if (pad.RightTrigger > 191) mask |= EventInput.R2 | EventInput.RightTrigger;
            if (pad.IsDown(XInputPads.Start)) mask |= EventInput.Start;
            if (pad.IsDown(XInputPads.Back)) mask |= EventInput.Select;
            if (pad.IsDown(XInputPads.DPadUp) || pad.LeftY > 16000) mask |= EventInput.Up;
            if (pad.IsDown(XInputPads.DPadDown) || pad.LeftY < -16000) mask |= EventInput.Down;
            if (pad.IsDown(XInputPads.DPadLeft) || pad.LeftX < -16000) mask |= EventInput.Left;
            if (pad.IsDown(XInputPads.DPadRight) || pad.LeftX > 16000) mask |= EventInput.Right;
            return mask;
        }
    }
}
