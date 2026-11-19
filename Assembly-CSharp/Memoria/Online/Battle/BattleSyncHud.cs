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
    public sealed class BattleSyncHud : MonoBehaviour
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
                BattleSync.CycleMode();
            }
            else if (Input.GetKeyDown(KeyCode.A))
            {
                if (!InBattle)
                    return;
                _lastAction = "C2 inject: " + CommandInjector.InjectAttackForReadyPlayer();
                BattleSync.Note(_lastAction);
            }
            else if (Input.GetKeyDown(KeyCode.Q))
            {
                if (InBattle)
                    BattleSync.Note("State:\n" + BattleSnapshot.Capture());
            }
        }

        private static Boolean InBattle => PersistenSingleton<FF9StateSystem>.Instance.mode == 2 && FF9StateSystem.Battle.FF9Battle.btl_list.next != null;

        private void OnGUI()
        {
            if (!_enabled)
                return;
            if (BattleSync.Mode == DebugReplayMode.Off && BattleSync.Recorder == null && BattleSync.Replayer == null && !(BattleSync.OwnerStream != null && InBattle))
                return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, wordWrap = true };
                _style.normal.textColor = Color.white;
            }
            String text = $"Battle sync [debug {BattleSync.Mode}]  (Ctrl+Shift+E record/replay, A inject attack, Q dump)";
            String shared = BattleSync.StatusLine;
            if (shared != null)
                text += "\nshared battle " + shared;
            if (BattleSync.Recorder != null)
                text += $"\nrecording: {BattleSync.Recorder.EventCount} events";
            if (BattleSync.Replayer != null)
                text += "\n" + BattleSync.Replayer.Status;
            else if (BattleSync.Mode == DebugReplayMode.Replay)
                text += BattleSync.LastLog != null && BattleSync.LastLog.Complete ? $"\nnext battle replays battle {BattleSync.LastLog.MapNo} ({BattleSync.LastLog.Events.Count} events)" : "\nno recording yet";
            if (!String.IsNullOrEmpty(BattleSync.LastSummary))
                text += "\nlast: " + BattleSync.LastSummary;
            if (!String.IsNullOrEmpty(_lastAction))
                text += "\n" + _lastAction;
            Single width = Mathf.Min(720f, Screen.width - 20f);
            Single height = _style.CalcHeight(new GUIContent(text), width) + 4f;
            GUI.Box(new Rect(10f, Screen.height - height - 10f, width, height), text, _style);
        }
    }
}
