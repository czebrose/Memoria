using Memoria.Prime;
using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Memoria.Multiplayer
{
    /// <summary>
    /// The "!" bubble for players 2-4. The game has one bubble (BubbleUI), always over the player character; each extra
    /// player gets a clone of it over their own character. Like EIcon does for the leader, a bubble is "polled" during a
    /// field tick (an NPC they face can be talked to, or a script running for them calls Bubble) and shown/hidden at the
    /// end of the tick (MultiplayerTalk.Process -> EndTick).
    /// </summary>
    public static class MultiplayerBubbles
    {
        private const Int32 PlayerCount = MultiplayerManager.MaxPlayers + 1;

        private static readonly BubbleUI[] s_bubbles = new BubbleUI[PlayerCount];
        private static readonly Int32[] s_polled = new Int32[PlayerCount];   // IconType + 1, 0 = none this tick
        private static readonly Int32[] s_shown = new Int32[PlayerCount];    // IconType + 1, 0 = hidden
        private static readonly Actor[] s_shownOver = new Actor[PlayerCount];

        /// <summary>A bubble for this player this tick (several polls: the last one wins, like EIcon).</summary>
        public static void Poll(Int32 player, BubbleUI.IconType type)
        {
            if (player > 1 && player < PlayerCount)
                s_polled[player] = (Int32)type + 1;
        }

        /// <summary>End of a field tick: show, change or hide each extra player's bubble.</summary>
        public static void EndTick(Boolean allowed)
        {
            for (Int32 player = 2; player < PlayerCount; player++)
            {
                Int32 wanted = allowed ? s_polled[player] : 0;
                s_polled[player] = 0;
                Actor actor = wanted != 0 ? MultiplayerField.GetFollowerActor(player) : null;
                if (actor == null || actor.go == null || !actor.go.activeSelf)
                    wanted = 0;
                if (wanted == s_shown[player] && ReferenceEquals(actor, s_shownOver[player]))
                    continue;
                if (wanted == 0)
                    Hide(player);
                else
                    Show(player, actor, (BubbleUI.IconType)(wanted - 1));
            }
        }

        public static void HideAll()
        {
            for (Int32 player = 2; player < PlayerCount; player++)
            {
                s_polled[player] = 0;
                Hide(player);
            }
        }

        private static void Show(Int32 player, Actor actor, BubbleUI.IconType type)
        {
            BubbleUI bubble = GetBubble(player);
            FieldMap fieldMap = PersistenSingleton<EventEngine>.Instance.fieldmap;
            if (bubble == null || fieldMap == null)
                return;
            BubbleMappingInfo.GetActorInfo(actor, out Transform target, out Vector3 offset);
            if (!bubble.gameObject.activeSelf)
                bubble.gameObject.SetActive(true);
            bubble.Show(target, actor, null, fieldMap, offset, EIcon.GetBubbleFlagData(type), null);
            s_shown[player] = (Int32)type + 1;
            s_shownOver[player] = actor;
        }

        private static void Hide(Int32 player)
        {
            if (s_shown[player] == 0)
                return;
            s_shown[player] = 0;
            s_shownOver[player] = null;
            BubbleUI bubble = s_bubbles[player];
            if (bubble != null)
                bubble.Hide();
        }

        /// <summary>A clone of the game's bubble, next to it in the UI (the Singleton keeps the original as its instance).</summary>
        private static BubbleUI GetBubble(Int32 player)
        {
            if (s_bubbles[player] != null)
                return s_bubbles[player];
            try
            {
                BubbleUI original = Singleton<BubbleUI>.Instance;
                if (original == null)
                    return null;
                GameObject clone = (GameObject)Object.Instantiate(original.gameObject);
                clone.name = $"MultiplayerBubbleP{player}";
                clone.transform.parent = original.transform.parent;
                clone.transform.localPosition = original.transform.localPosition;
                clone.transform.localRotation = original.transform.localRotation;
                clone.transform.localScale = original.transform.localScale;
                clone.layer = original.gameObject.layer;
                s_bubbles[player] = clone.GetComponent<BubbleUI>();
                s_bubbles[player].Hide();
                Log.Message($"[Multiplayer] Created P{player}'s \"!\" bubble");
            }
            catch (Exception err)
            {
                Log.Error(err, $"[Multiplayer] Cannot create P{player}'s \"!\" bubble");
            }
            return s_bubbles[player];
        }
    }
}
