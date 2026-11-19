using System;
using UnityEngine;

namespace Memoria.Online.Battle
{
    /// <summary>
    /// Spike C: debug hotkeys and status box. Only active with [Debug] Enabled = 1 and OnlineDebug = 1.
    /// Ctrl+Shift+E: cycle Off / Record / Replay (the next battle is recorded, or replayed passively from the last
    /// recording). Ctrl+Shift+A: C2, the first ready character attacks the first enemy without the menu.
    /// Ctrl+Shift+Q: write the battle state to Memoria.log.
    /// </summary>
    public sealed class BattleSpikeHud : MonoBehaviour
    {
        private Boolean _enabled;
        private GUIStyle _style;
        private String _lastAction = String.Empty;

        private void Start()
        {
            _enabled = Configuration.Debug.OnlineDebug;
        }

        private void Update()
        {
            if (!_enabled)
                return;
            Boolean ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            Boolean shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!ctrl || !shift)
                return;
            if (Input.GetKeyDown(KeyCode.E))
            {
                BattleSpike.CycleMode();
            }
            else if (Input.GetKeyDown(KeyCode.A))
            {
                if (!InBattle)
                    return;
                _lastAction = "C2 inject: " + CommandInjector.InjectAttackForReadyPlayer();
                BattleSpike.Note(_lastAction);
            }
            else if (Input.GetKeyDown(KeyCode.Q))
            {
                if (InBattle)
                    BattleSpike.Note("State:\n" + BattleSnapshot.Capture());
            }
        }

        private static Boolean InBattle => PersistenSingleton<FF9StateSystem>.Instance.mode == 2 && FF9StateSystem.Battle.FF9Battle.btl_list.next != null;

        private void OnGUI()
        {
            if (!_enabled)
                return;
            if (BattleSpike.Mode == SpikeMode.Off && BattleSpike.Recorder == null && BattleSpike.Replayer == null)
                return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, wordWrap = true };
                _style.normal.textColor = Color.white;
            }
            String text = $"Spike C [{BattleSpike.Mode}]  (Ctrl+Shift+E mode, A inject attack, Q dump)";
            if (BattleSpike.Recorder != null)
                text += $"\nrecording: {BattleSpike.Recorder.EventCount} events";
            if (BattleSpike.Replayer != null)
                text += "\n" + BattleSpike.Replayer.Status;
            else if (BattleSpike.Mode == SpikeMode.Replay)
                text += BattleSpike.LastLog != null && BattleSpike.LastLog.Complete ? $"\nnext battle replays battle {BattleSpike.LastLog.MapNo} ({BattleSpike.LastLog.Events.Count} events)" : "\nno recording yet";
            if (!String.IsNullOrEmpty(BattleSpike.LastSummary))
                text += "\nlast: " + BattleSpike.LastSummary;
            if (!String.IsNullOrEmpty(_lastAction))
                text += "\n" + _lastAction;
            Single width = Mathf.Min(720f, Screen.width - 20f);
            Single height = _style.CalcHeight(new GUIContent(text), width) + 4f;
            GUI.Box(new Rect(10f, Screen.height - height - 10f, width, height), text, _style);
        }
    }
}
