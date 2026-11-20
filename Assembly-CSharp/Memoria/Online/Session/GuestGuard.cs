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

        /// <summary>
        /// M4: this guest plays no character (released it, or none was free): its player is hidden, can't move and
        /// can't interact (talk, check, triggers); it watches. Set every frame by OnlineSession.
        /// </summary>
        public static Boolean Spectating;

        /// <summary>M5: the host is in a scene (or a battle) on this guest's field: the guest can't move or interact.</summary>
        public static Boolean Frozen;
        /// <summary>M5: why, shown on screen while frozen.</summary>
        public static String FrozenMessage;

        /// <summary>
        /// M5: this PC has had player control on the current field (or world map) since it loaded. Before that, the
        /// field's opening scripts run (on every PC that loads it): what they give or set isn't a guest's own doing.
        /// </summary>
        public static Boolean ActedOnThisField;

        /// <summary>
        /// This PC runs the field's scripts in step with the host's (a party scene follower): live flags from the host
        /// wait until it's over, since its own scripts set the same values at their own moment.
        /// </summary>
        public static Boolean HoldLiveFlags;

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
