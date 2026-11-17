using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Memoria.Online
{
    /// <summary>
    /// Receives Steam callbacks (not call results) from C# by emulating the C++ CCallbackBase object that
    /// SteamAPI_RegisterCallback expects, as old Steamworks.NET versions did:
    ///
    ///   class CCallbackBase {            // x64 layout
    ///       void* vtable;                // +0
    ///       uint8 m_nCallbackFlags;      // +8  (written by Steam)
    ///       int   m_iCallback;           // +12 (written by Steam)
    ///   };
    ///   virtual void Run(void* pvParam) = 0;
    ///   virtual void Run(void* pvParam, bool bIOFailure, SteamAPICall_t hSteamAPICall) = 0;
    ///   virtual int GetCallbackSizeBytes() = 0;
    ///
    /// MSVC places overloaded virtual functions in the vtable in *reverse* declaration order, so on Windows
    /// the slots are: [0] Run(pvParam, bIOFailure, hCall), [1] Run(pvParam), [2] GetCallbackSizeBytes.
    /// The delegates are Cdecl: on x64 there is one calling convention and "this" arrives as the first argument.
    /// Call results are not handled here: they are polled with ISteamUtils::IsAPICallCompleted / GetAPICallResult.
    ///
    /// Callbacks fire during SteamAPI_RunCallbacks, on Unity's main thread (the game pumps it every frame
    /// from SceneDirector.Update once the title screen initialised the social platform; see SteamSpike).
    /// </summary>
    internal sealed class SteamCallback : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RunDelegate(IntPtr thisPtr, IntPtr param);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RunCallResultDelegate(IntPtr thisPtr, IntPtr param, [MarshalAs(UnmanagedType.I1)] Boolean ioFailure, UInt64 hSteamAPICall);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate Int32 GetSizeDelegate(IntPtr thisPtr);

        // Strong references: if the GC collected one of these delegates, Steam would call into freed thunks
        private static readonly List<SteamCallback> Alive = new List<SteamCallback>();

        public readonly Int32 CallbackId;
        public Int32 FiredCount { get; private set; }

        private readonly Int32 _size;
        private readonly Action<IntPtr> _handler;
        private readonly RunDelegate _run;
        private readonly RunCallResultDelegate _runCallResult;
        private readonly GetSizeDelegate _getSize;
        private IntPtr _vtable;
        private IntPtr _object;

        public SteamCallback(Int32 callbackId, Int32 size, Action<IntPtr> handler)
        {
            CallbackId = callbackId;
            _size = size;
            _handler = handler;
            _run = OnRun;
            _runCallResult = OnRunCallResult;
            _getSize = OnGetSize;

            _vtable = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(_vtable, 0 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_runCallResult));
            Marshal.WriteIntPtr(_vtable, 1 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_run));
            Marshal.WriteIntPtr(_vtable, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_getSize));

            _object = Marshal.AllocHGlobal(16);
            Marshal.WriteIntPtr(_object, 0, _vtable);
            Marshal.WriteInt64(_object, 8, 0); // flags + padding + m_iCallback
            SteamNative.RegisterCallback(_object, callbackId);
            Alive.Add(this);
        }

        private void OnRun(IntPtr thisPtr, IntPtr param)
        {
            FiredCount++;
            try
            {
                _handler(param);
            }
            catch (Exception err)
            {
                Log.Error(err, $"[SteamSpike] Callback {CallbackId} handler failed");
            }
        }

        private void OnRunCallResult(IntPtr thisPtr, IntPtr param, Boolean ioFailure, UInt64 hSteamAPICall)
        {
            // Only used for call results; a registered callback should never land here.
            // If it does, the vtable order assumption is wrong: log loudly and still dispatch.
            Log.Warning($"[SteamSpike] Callback {CallbackId} arrived through the call-result slot (ioFailure={ioFailure}, call={hSteamAPICall}); vtable order differs from expectations");
            OnRun(thisPtr, param);
        }

        private Int32 OnGetSize(IntPtr thisPtr)
        {
            return _size;
        }

        public void Dispose()
        {
            if (_object == IntPtr.Zero)
                return;
            SteamNative.UnregisterCallback(_object);
            Marshal.FreeHGlobal(_object);
            Marshal.FreeHGlobal(_vtable);
            _object = IntPtr.Zero;
            _vtable = IntPtr.Zero;
            Alive.Remove(this);
        }
    }
}
