using Memoria.Data;
using System;
using System.Text;
using UnityEngine;

namespace Memoria.Multiplayer
{
    /// <summary>
    /// Debug overlay ([Multiplayer] DebugOverlay = 1): each player's controller slot, live buttons,
    /// owned party members, and who is in control. Created by MultiplayerManager.Update.
    /// </summary>
    public class MultiplayerOverlay : MonoBehaviour
    {
        private static readonly UInt16[] ShownButtons =
        {
            XInputPads.A, XInputPads.B, XInputPads.X, XInputPads.Y,
            XInputPads.LeftShoulder, XInputPads.RightShoulder, XInputPads.Back, XInputPads.Start,
            XInputPads.DPadUp, XInputPads.DPadDown, XInputPads.DPadLeft, XInputPads.DPadRight,
        };

        private GUIStyle _style;
        private readonly StringBuilder _text = new StringBuilder();

        private void OnGUI()
        {
            if (!MultiplayerManager.IsActive || !Configuration.Multiplayer.DebugOverlay)
                return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box);
                _style.alignment = TextAnchor.UpperLeft;
                _style.fontSize = Math.Max(12, Screen.height / 60);
                _style.richText = true;
                _style.wordWrap = false;
            }

            _text.Length = 0;
            _text.Append("<b>Multiplayer</b>   F10: pass control to next player\n");
            for (Int32 player = 1; player <= MultiplayerManager.MaxPlayers; player++)
                AppendPlayer(player);

            GUIContent content = new GUIContent(_text.ToString());
            Vector2 size = _style.CalcSize(content);
            GUI.Box(new Rect(10f, 10f, size.x, size.y), content, _style);
        }

        private void AppendPlayer(Int32 player)
        {
            Boolean inControl = player == MultiplayerManager.ActivePlayer;
            Int32 slot = MultiplayerManager.GetPadSlot(player);
            PadState pad = XInputPads.Get(slot);

            _text.Append(inControl ? "<color=yellow>► " : "   ");
            _text.Append($"P{player}  slot {slot}{(player == 1 ? "+kbd" : "")}  ");
            if (!pad.Connected)
            {
                _text.Append("<color=grey>no controller</color>");
            }
            else
            {
                Boolean any = false;
                foreach (UInt16 button in ShownButtons)
                {
                    if (pad.IsDown(button))
                    {
                        _text.Append(MultiplayerManager.ButtonName(button)).Append(' ');
                        any = true;
                    }
                }
                if (pad.LeftTrigger > 64) { _text.Append("LT "); any = true; }
                if (pad.RightTrigger > 64) { _text.Append("RT "); any = true; }
                if (Math.Abs((Int32)pad.LeftX) > 8000 || Math.Abs((Int32)pad.LeftY) > 8000)
                {
                    _text.Append($"stick({pad.LeftX / 327},{pad.LeftY / 327}) ");
                    any = true;
                }
                if (!any)
                    _text.Append("<color=grey>connected</color>");
            }
            _text.Append("   ");
            AppendOwnedCharacters(player);
            if (inControl)
                _text.Append("  [CONTROL]</color>");
            _text.Append('\n');
        }

        private void AppendOwnedCharacters(Int32 player)
        {
            try
            {
                PLAYER[] members = FF9StateSystem.Common?.FF9?.party?.member;
                if (members == null)
                    return;
                Boolean first = true;
                foreach (PLAYER member in members)
                {
                    if (member == null || MultiplayerManager.GetOwner(member.Index) != player)
                        continue;
                    _text.Append(first ? "owns: " : ", ").Append(member.Name);
                    first = false;
                }
            }
            catch (Exception)
            {
                // party not available yet (title screen)
            }
        }
    }
}
