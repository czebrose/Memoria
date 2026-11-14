using Memoria.Prime;
using System;
using System.Collections.Generic;
using UnityEngine;
using Interaction = EventEngine.MultiplayerInteraction;
using Redirect = EventEngine.MultiplayerRedirect;

namespace Memoria.Multiplayer
{
    /// <summary>
    /// Phase 3c: players 2-4 interact with the field like the leader does: talk to NPCs and talk regions, open chests,
    /// pick up hidden items. Runs once per field tick from EventEngine.ProcessEvents, right after the leader's own
    /// CollisionRequest:
    ///  - a Confirm press (latched every frame) facing an NPC or standing in a talk region starts a talk (tag 3), and
    ///    that player gets control at once (dialogue choices, shops, saves);
    ///  - the object the character stands next to gets its range function (tag 2) requested each tick, as for the
    ///    leader (chests check the button in there); if it takes movement away, its player gets control until it returns;
    ///  - a Confirm press that starts no talk makes the next tick a "press tick": code loops that look for the player
    ///    near a hidden item see that player's character and buttons, and the region it stands in gets its range
    ///    function requested (regions only on a press: field exits are region range functions, leader-only);
    ///  - when such scripts run a function on the character (the item pickup, the "open chest" pose), it runs on the
    ///    leader's entry instead (EventEngine.Request): the leader kneels, the item is added, and the extra player gets
    ///    control for the "Received..." message until that function returns.
    /// While a target's code runs, object 250 means that player's character and its button checks read that player's
    /// controller (EventEngine.Multiplayer.cs). Everything ends when its function returns (script level back above the
    /// requested level) or when the script leaves the field; control then goes back to P1.
    /// </summary>
    public static class MultiplayerTalk
    {
        private const Single PressLifetime = 0.25f; // a latched press older than this is dropped
        private const Single MessageAttributionTime = 1f; // a message opening this soon after a script read a player's press is theirs

        private static readonly Single[] s_pressTime = { -1f, -1f, -1f, -1f, -1f };
        private static readonly Single[] s_scriptPressTime = { -1f, -1f, -1f, -1f, -1f };
        private static Int32 s_messageOwner;
        private static readonly List<Obj> s_targets = new List<Obj>();

        /// <summary>Called every frame from MultiplayerManager.Update.</summary>
        internal static void LatchInput()
        {
            MultiplayerKeys.Collect();
            for (Int32 player = 2; player <= MultiplayerManager.MaxPlayers; player++)
                if (XInputPads.IsPressed(MultiplayerManager.GetPadSlot(player), EventInput.isJapaneseLayout ? XInputPads.B : XInputPads.A))
                    s_pressTime[player] = Time.realtimeSinceStartup;
        }

        /// <summary>Called by EventEngine.ProcessEvents once per field tick.</summary>
        public static void Process(EventEngine engine)
        {
            try
            {
                ProcessInteractions(engine);
                UpdateMessageOwner(engine);
                UpdateBubbles(engine);
                if (MultiplayerManager.IsActive)
                    MultiplayerField.TurnLeaderTowardActingPlayer(engine);
            }
            finally
            {
                engine.AdvanceMultiplayerTick();
            }
        }

        /// <summary>
        /// A message window that opens while an extra player's talk, chest or pickup runs, or right after a script read
        /// their button press, is theirs: they get control until it closes.
        /// </summary>
        private static void UpdateMessageOwner(EventEngine engine)
        {
            if (!MultiplayerManager.IsActive)
                return;
            Boolean open = Singleton<DialogManager>.Instance.Visible;
            if (open)
            {
                if (s_messageOwner != 0 || MultiplayerManager.ActivePlayer != 1)
                    return;
                Int32 owner = 0;
                foreach (Interaction interaction in engine.MultiplayerInteractions)
                    if ((interaction.HasControl || interaction.IsTalk) && interaction.Target.level <= 1)
                    {
                        owner = interaction.Player;
                        break;
                    }
                if (owner == 0 && engine.MultiplayerRedirects.Count > 0)
                    owner = engine.MultiplayerRedirects[0].Player;
                Single now = Time.realtimeSinceStartup;
                for (Int32 player = 2; owner == 0 && player <= MultiplayerManager.MaxPlayers; player++)
                    if (s_scriptPressTime[player] >= 0f && now - s_scriptPressTime[player] < MessageAttributionTime)
                        owner = player;
                if (owner == 0)
                    return;
                s_messageOwner = owner;
                MultiplayerManager.SetActivePlayer(owner, "their message");
            }
            else if (s_messageOwner != 0)
            {
                Int32 owner = s_messageOwner;
                s_messageOwner = 0;
                ReturnControl(engine, owner, "message closed");
            }
        }

        /// <summary>Extra players' "!": what they face that can be talked to, plus Bubble calls of scripts running for them.</summary>
        private static void UpdateBubbles(EventEngine engine)
        {
            Boolean allowed = MultiplayerManager.IsActive && engine.gMode == 1 && engine.GetUserControl()
                && EventInput.IsMovementControl && !engine.IsMultiplayerTalkActive && !Singleton<DialogManager>.Instance.Visible;
            if (allowed)
            {
                for (Int32 player = 2; player <= MultiplayerManager.MaxPlayers; player++)
                {
                    Actor actor = player == MultiplayerManager.ActivePlayer ? null : MultiplayerField.GetFollowerActor(player);
                    if (actor != null && engine.GetMultiplayerTalkIcon(actor, out BubbleUI.IconType type))
                        MultiplayerBubbles.Poll(player, type);
                }
            }
            MultiplayerBubbles.EndTick(allowed);
        }

        /// <summary>A script running for an extra player read their Confirm/Cancel/Special press (EventEngine.ScriptKeyOn).</summary>
        public static void OnScriptReadPress(Int32 player)
        {
            if (player > 1 && player < s_scriptPressTime.Length)
                s_scriptPressTime[player] = Time.realtimeSinceStartup;
        }

        private static void ProcessInteractions(EventEngine engine)
        {
            engine.SetMultiplayerPressTick(0, null);
            if (!MultiplayerManager.IsActive)
            {
                EndAll(engine, "multiplayer disabled");
                ClearPresses();
                return;
            }
            MultiplayerKeys.Tick();

            // Finished interactions and redirected player functions
            foreach (Interaction interaction in new List<Interaction>(engine.MultiplayerInteractions))
                if (interaction.Target.level > 1 || interaction.Actor.go == null)
                    End(engine, interaction, interaction.Actor.go == null ? "the character was removed" : "finished");
            foreach (Redirect redirect in new List<Redirect>(engine.MultiplayerRedirects))
                if (redirect.Leader.level > redirect.Level)
                    EndRedirect(engine, redirect, "finished");

            // A range function that took movement away (a chest opening) belongs to its player
            if (!engine.GetUserControl())
                foreach (Interaction interaction in engine.MultiplayerInteractions)
                    if (!interaction.HasControl)
                        GiveControl(interaction, $"{Describe(interaction.Target)} reacts to them");

            // New interactions: same conditions as the leader's (free to move, no menu open, nobody talking)
            if (!engine.GetUserControl() || !EventInput.IsMovementControl || engine.IsMultiplayerTalkActive)
            {
                ClearPresses();
                return;
            }

            Single now = Time.realtimeSinceStartup;
            Int32 pressPlayer = 0;
            Actor pressActor = null;
            for (Int32 player = 2; player <= MultiplayerManager.MaxPlayers; player++)
            {
                // A player who has been handed control of the game (F10) acts as the leader instead
                Actor actor = player == MultiplayerManager.ActivePlayer ? null : MultiplayerField.GetFollowerActor(player);
                if (actor == null)
                {
                    s_pressTime[player] = -1f;
                    continue;
                }
                // One interaction at a time per player; a pending range check finishes within a tick, keep the press
                if (engine.HasMultiplayerInteraction(player))
                    continue;

                if (s_pressTime[player] >= 0f)
                {
                    Boolean fresh = now - s_pressTime[player] <= PressLifetime;
                    s_pressTime[player] = -1f;
                    if (fresh)
                    {
                        Int32 angle = 0;
                        Obj talkTarget = engine.FindMultiplayerTalkTarget(actor, out angle);
                        Interaction talk = talkTarget != null ? engine.StartMultiplayerInteraction(player, actor, talkTarget, EventEngine.tagTalk, angle) : null;
                        if (talk != null)
                        {
                            GiveControl(talk, $"talking to {Describe(talkTarget)}");
                            return; // the leader and everyone else wait for this talk
                        }
                        // No talk: the press may be for a hidden item (code loops, next tick) or a region's range function
                        if (pressPlayer == 0)
                        {
                            pressPlayer = player;
                            pressActor = actor;
                        }
                        Obj region = engine.FindMultiplayerRegionRangeTarget(actor);
                        if (region != null && engine.StartMultiplayerInteraction(player, actor, region, 2, 0) != null)
                            continue;
                    }
                }

                // Range functions of the object the character stands next to (chests check the button in there)
                s_targets.Clear();
                engine.FindMultiplayerRangeTargets(actor, s_targets);
                foreach (Obj target in s_targets)
                    if (engine.StartMultiplayerInteraction(player, actor, target, 2, 0) != null)
                        break;
            }
            engine.SetMultiplayerPressTick(pressPlayer, pressActor);
        }

        /// <summary>EventEngine.Request ran a function of the leader's entry on an extra player's behalf (item pickup...).</summary>
        public static void OnPlayerFunctionRedirected(Int32 player, Obj leader, Int32 tag)
        {
            Log.Message($"[Multiplayer] P{player} triggered the player character's function {tag}: it runs on the leader (object {leader.uid})");
            MultiplayerManager.SetActivePlayer(player, "picking something up");
        }

        /// <summary>End every interaction (scene change, multiplayer off).</summary>
        public static void EndAll(EventEngine engine, String reason)
        {
            engine.SetMultiplayerPressTick(0, null);
            foreach (Interaction interaction in new List<Interaction>(engine.MultiplayerInteractions))
                End(engine, interaction, reason);
            foreach (Redirect redirect in new List<Redirect>(engine.MultiplayerRedirects))
                EndRedirect(engine, redirect, reason);
        }

        /// <summary>End the interactions of a character that is being removed.</summary>
        public static void EndFor(EventEngine engine, Actor actor, String reason)
        {
            foreach (Interaction interaction in new List<Interaction>(engine.MultiplayerInteractions))
                if (ReferenceEquals(interaction.Actor, actor))
                    End(engine, interaction, reason);
        }

        private static void GiveControl(Interaction interaction, String reason)
        {
            interaction.HasControl = true;
            Log.Message($"[Multiplayer] P{interaction.Player} interacts with {Describe(interaction.Target)} ({(interaction.IsTalk ? "talk" : "range")}) as the player character");
            MultiplayerManager.SetActivePlayer(interaction.Player, reason);
        }

        private static void End(EventEngine engine, Interaction interaction, String reason)
        {
            engine.EndMultiplayerInteraction(interaction);
            if (!interaction.HasControl)
                return; // a range check that did nothing: no log, control never moved
            Log.Message($"[Multiplayer] P{interaction.Player}'s interaction with {Describe(interaction.Target)} ended ({reason})");
            ReturnControl(engine, interaction.Player, $"interaction {reason}");
        }

        private static void EndRedirect(EventEngine engine, Redirect redirect, String reason)
        {
            engine.EndMultiplayerRedirect(redirect);
            Log.Message($"[Multiplayer] P{redirect.Player}'s player-character function on the leader ended ({reason})");
            ReturnControl(engine, redirect.Player, $"pickup {reason}");
        }

        /// <summary>Control goes back to P1 once nothing of that player's holds it any more.</summary>
        private static void ReturnControl(EventEngine engine, Int32 player, String reason)
        {
            if (s_messageOwner == player && Singleton<DialogManager>.Instance.Visible)
                return; // their message is still open; UpdateMessageOwner returns control when it closes
            foreach (Interaction other in engine.MultiplayerInteractions)
                if (other.Player == player && other.HasControl)
                    return;
            foreach (Redirect other in engine.MultiplayerRedirects)
                if (other.Player == player)
                    return;
            if (MultiplayerManager.ActivePlayer == player)
                MultiplayerManager.SetActivePlayer(1, reason);
            ClearPresses();
        }

        private static String Describe(Obj target) => target.cid == 3 ? $"region {target.uid} (entry {target.sid})" : $"object {target.uid} (entry {target.sid})";

        private static void ClearPresses()
        {
            for (Int32 i = 0; i < s_pressTime.Length; i++)
                s_pressTime[i] = -1f;
        }

        /// <summary>Leaving the field (battle, world map...): no extra player's bubble stays on screen.</summary>
        internal static void OnLeftField()
        {
            MultiplayerBubbles.HideAll();
        }
    }
}
