using Memoria.Prime;
using System;

namespace Memoria.Online.Session
{
    /// <summary>
    /// What a guest's game must not do while it runs the host's game. Set as soon as a host snapshot is loaded as a
    /// guest; stays set until the game is restarted.
    ///
    /// M1: autosave. Memoria autosaves on every field load (EventEngine, marked [Online] edit); on a guest that would
    /// write the host's game into the guest's own save file right after the snapshot loads.
    /// (M4 adds the save menu and Moogle saves.)
    /// </summary>
    public static class GuestGuard
    {
        public static Boolean IsGuestSession { get; private set; }
        public static Int32 BlockedAutosaves { get; private set; }

        public static void BeginGuestSession()
        {
            if (!IsGuestSession)
                Log.Message("[Online] Guest session: this PC's save files won't be written (autosave blocked)");
            IsGuestSession = true;
        }

        /// <summary>Called where the game would autosave; true = don't.</summary>
        public static Boolean BlockAutosave()
        {
            if (!IsGuestSession)
                return false;
            BlockedAutosaves++;
            Log.Message($"[Online] Autosave blocked (guest session; {BlockedAutosaves} so far)");
            return true;
        }
    }
}
