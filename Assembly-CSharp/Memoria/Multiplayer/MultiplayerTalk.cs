using Memoria.Prime;
using System;
using UnityEngine;

namespace Memoria.Multiplayer
{
    /// <summary>
    /// Phase 3c: players 2-4 talk to NPCs (and use shops, saves... whatever the NPC's talk function does).
    ///
    /// A press of A on an extra player's controller is latched every frame. In the next field tick, inside
    /// EventEngine.ProcessEvents (where the leader's own talk check runs), the press is turned into a talk if that
    /// player's character stands in talk range of an NPC and faces it (EventEngine.TryStartMultiplayerTalk): for that
    /// NPC's own code, object 250 means the talker until the talk ends (other scripts keep seeing the leader), and that
    /// player gets control, so dialogue choices and shop/save menus are theirs. The leader is held still (and cannot
    /// start a talk) meanwhile, even when the talk script does not take movement away.
    /// The talk ends when the NPC's talk function returns (its script level, set to 1 by the request, goes back up),
    /// or when the script leaves the field; control then goes back to player 1.
    /// </summary>
    public static class MultiplayerTalk
    {
        private const Single PressLifetime = 0.25f; // a latched press older than this is dropped

        private static readonly Single[] s_pressTime = { -1f, -1f, -1f, -1f, -1f };
        private static Int32 s_talkingPlayer;

        /// <summary>Called every frame from MultiplayerManager.Update: remember A presses of players 2-4.</summary>
        internal static void LatchInput()
        {
            for (Int32 player = 2; player <= MultiplayerManager.MaxPlayers; player++)
                if (XInputPads.IsPressed(MultiplayerManager.GetPadSlot(player), XInputPads.A))
                    s_pressTime[player] = Time.realtimeSinceStartup;
        }

        /// <summary>Called by EventEngine.ProcessEvents once per field tick.</summary>
        public static void Process(EventEngine engine)
        {
            if (!MultiplayerManager.IsActive)
            {
                if (engine.IsMultiplayerTalkActive)
                {
                    engine.EndMultiplayerTalk();
                    OnTalkEnded("multiplayer disabled");
                }
                ClearPresses();
                return;
            }

            if (engine.IsMultiplayerTalkActive)
            {
                // Presses during a talk advance the dialogue; they must not start a new talk afterwards
                ClearPresses();
                Obj npc = engine.MultiplayerTalkTarget;
                Actor talker = engine.MultiplayerTalker;
                if (npc == null || npc.level > 1 || talker.go == null)
                {
                    engine.EndMultiplayerTalk();
                    OnTalkEnded(talker.go == null ? "the character was removed" : "talk finished");
                }
                return;
            }

            // Same conditions as the leader: free to move, no menu open
            if (!engine.GetUserControl() || !EventInput.IsMovementControl)
            {
                ClearPresses();
                return;
            }

            Single now = Time.realtimeSinceStartup;
            for (Int32 player = 2; player <= MultiplayerManager.MaxPlayers; player++)
            {
                if (s_pressTime[player] < 0f)
                    continue;
                Boolean fresh = now - s_pressTime[player] <= PressLifetime;
                s_pressTime[player] = -1f;
                // A player who has been handed control of the game (F10) presses for the leader instead
                if (!fresh || player == MultiplayerManager.ActivePlayer)
                    continue;
                Actor talker = MultiplayerField.GetFollowerActor(player);
                if (talker == null || !engine.TryStartMultiplayerTalk(talker))
                    continue;
                s_talkingPlayer = player;
                Obj npc = engine.MultiplayerTalkTarget;
                Log.Message($"[Multiplayer] P{player} talks to object {npc.uid} (entry {npc.sid}) as the player character");
                MultiplayerManager.SetActivePlayer(player, $"talking to object {npc.uid}");
                return;
            }
        }

        /// <summary>A talk by an extra player is over: control goes back to player 1.</summary>
        public static void OnTalkEnded(String reason)
        {
            if (s_talkingPlayer == 0)
                return;
            Log.Message($"[Multiplayer] P{s_talkingPlayer}'s talk ended ({reason}); the leader is the player character again");
            if (MultiplayerManager.ActivePlayer == s_talkingPlayer)
                MultiplayerManager.SetActivePlayer(1, "talk ended");
            s_talkingPlayer = 0;
            ClearPresses();
        }

        private static void ClearPresses()
        {
            for (Int32 i = 0; i < s_pressTime.Length; i++)
                s_pressTime[i] = -1f;
        }
    }
}
