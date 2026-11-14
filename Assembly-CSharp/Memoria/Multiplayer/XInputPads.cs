using Memoria.Prime;
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Memoria.Multiplayer
{
    /// <summary>One XInput slot's state for the current frame.</summary>
    public struct PadState
    {
        public Boolean Connected;
        public UInt16 Buttons;
        public Byte LeftTrigger;
        public Byte RightTrigger;
        public Int16 LeftX;
        public Int16 LeftY;

        public Boolean IsDown(UInt16 button) => (Buttons & button) != 0;
    }

    /// <summary>
    /// Reads XInput slots 0-3 individually, straight from the OS (xinput1_4 / xinput1_3 / xinput9_1_0).
    /// Unlike XInputDotNetPure.GamePad.GetState, it does not merge JoyShockLibrary devices (DS4, DualSense,
    /// Switch Pro) into every slot, so each slot is exactly one controller. With Steam Input enabled, Steam
    /// exposes every physical pad (and every Remote Play guest) as its own XInput slot.
    /// </summary>
    public static class XInputPads
    {
        public const Int32 SlotCount = 4;

        public const UInt16 DPadUp = 0x0001;
        public const UInt16 DPadDown = 0x0002;
        public const UInt16 DPadLeft = 0x0004;
        public const UInt16 DPadRight = 0x0008;
        public const UInt16 Start = 0x0010;
        public const UInt16 Back = 0x0020;
        public const UInt16 LeftThumb = 0x0040;
        public const UInt16 RightThumb = 0x0080;
        public const UInt16 LeftShoulder = 0x0100;
        public const UInt16 RightShoulder = 0x0200;
        public const UInt16 A = 0x1000;
        public const UInt16 B = 0x2000;
        public const UInt16 X = 0x4000;
        public const UInt16 Y = 0x8000;

        private const UInt32 ErrorSuccess = 0;
        private const Single DisconnectedPollInterval = 1f; // polling an empty XInput slot is slow, so do it rarely

        private static readonly PadState[] s_current = new PadState[SlotCount];
        private static readonly PadState[] s_previous = new PadState[SlotCount];
        private static readonly Single[] s_nextPollTime = new Single[SlotCount];
        private static Int32 s_dllVersion = -1; // -1 unknown, 0 xinput1_4, 1 xinput1_3, 2 xinput9_1_0, 3 none
        private static Int32 s_lastFrame = -1;

        public static PadState Get(Int32 slot) => slot >= 0 && slot < SlotCount ? s_current[slot] : default(PadState);
        public static PadState GetPrevious(Int32 slot) => slot >= 0 && slot < SlotCount ? s_previous[slot] : default(PadState);

        public static Boolean IsPressed(Int32 slot, UInt16 button) => Get(slot).IsDown(button) && !GetPrevious(slot).IsDown(button);

        /// <summary>Refresh all slots; safe to call several times per frame (only the first call reads).</summary>
        public static void Poll()
        {
            if (s_lastFrame == Time.frameCount)
                return;
            s_lastFrame = Time.frameCount;
            Single now = Time.realtimeSinceStartup;
            for (Int32 slot = 0; slot < SlotCount; slot++)
            {
                s_previous[slot] = s_current[slot];
                if (!s_current[slot].Connected && now < s_nextPollTime[slot])
                    continue;
                s_current[slot] = Read(slot);
                if (!s_current[slot].Connected)
                    s_nextPollTime[slot] = now + DisconnectedPollInterval;
            }
        }

        private static PadState Read(Int32 slot)
        {
            XInputState raw;
            if (!TryGetState((UInt32)slot, out raw))
                return default(PadState);
            return new PadState
            {
                Connected = true,
                Buttons = raw.Gamepad.wButtons,
                LeftTrigger = raw.Gamepad.bLeftTrigger,
                RightTrigger = raw.Gamepad.bRightTrigger,
                LeftX = raw.Gamepad.sThumbLX,
                LeftY = raw.Gamepad.sThumbLY,
            };
        }

        private static Boolean TryGetState(UInt32 slot, out XInputState state)
        {
            state = default(XInputState);
            while (s_dllVersion < 3)
            {
                try
                {
                    Int32 version = s_dllVersion < 0 ? 0 : s_dllVersion;
                    UInt32 result;
                    if (version == 0)
                        result = XInputGetState14(slot, out state);
                    else if (version == 1)
                        result = XInputGetState13(slot, out state);
                    else
                        result = XInputGetState910(slot, out state);
                    if (s_dllVersion < 0)
                    {
                        s_dllVersion = version;
                        Log.Message($"[Multiplayer] Reading controllers through {DllName(version)}");
                    }
                    return result == ErrorSuccess;
                }
                catch (DllNotFoundException)
                {
                    TryNextDll();
                }
                catch (EntryPointNotFoundException)
                {
                    TryNextDll();
                }
            }
            return false;
        }

        private static void TryNextDll()
        {
            s_dllVersion = s_dllVersion < 0 ? 1 : s_dllVersion + 1;
            if (s_dllVersion >= 3)
                Log.Error("[Multiplayer] No XInput library found; separate controllers are unavailable.");
        }

        private static String DllName(Int32 version) => version == 0 ? "xinput1_4.dll" : version == 1 ? "xinput1_3.dll" : "xinput9_1_0.dll";

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputGamepad
        {
            public UInt16 wButtons;
            public Byte bLeftTrigger;
            public Byte bRightTrigger;
            public Int16 sThumbLX;
            public Int16 sThumbLY;
            public Int16 sThumbRX;
            public Int16 sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState
        {
            public UInt32 dwPacketNumber;
            public XInputGamepad Gamepad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern UInt32 XInputGetState14(UInt32 dwUserIndex, out XInputState pState);

        [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")]
        private static extern UInt32 XInputGetState13(UInt32 dwUserIndex, out XInputState pState);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern UInt32 XInputGetState910(UInt32 dwUserIndex, out XInputState pState);
    }
}
