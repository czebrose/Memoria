using Memoria.Online.Battle;
using Memoria.Online.Session;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Memoria.Online.Menus
{
    /// <summary>
    /// The battle's Actions window (docs/online/online-menu.md): a small game-style window at the top right of the
    /// battle screen, in an online session, listing the party's commands in the order they'll run
    /// (<see cref="BattleQueue"/>): the running one first ("Now"), then the waiting ones, then this PC's commands still
    /// on their way to the battle's owner ("sending"). It shows only while something is in it. Built like
    /// <see cref="OnlineNotice"/>, from the main menu's window frame and label. Called every frame by OnlineSession.
    /// </summary>
    internal static class BattleQueueHud
    {
        private const Int32 Width = 520, LineHeight = 46, Margin = 30, MaxLines = 6;

        private static GameObject _root;
        private static GameObject _parent;
        private static UIWidget _frame;
        private static UILabel _label;
        private static String _shown;
        private static Int32 _lines = -1;

        public static void Update()
        {
            try
            {
                UIManager ui = PersistenSingleton<UIManager>.Instance;
                BattleHUD hud = UIManager.Battle;
                Boolean battle = SessionSettings.Playing && ui != null && ui.State == UIManager.UIState.BattleHUD && hud != null && hud.gameObject.activeInHierarchy;
                List<String> lines = battle ? Compose() : null;
                if (lines == null || lines.Count == 0)
                {
                    if (_root != null && _root.activeSelf)
                        _root.SetActive(false);
                    return;
                }
                if ((_root == null || _parent != hud.gameObject) && !Create(ui, hud.gameObject))
                    return;
                String text = String.Join("\n", lines.ToArray());
                if (lines.Count != _lines)
                {
                    _lines = lines.Count;
                    Int32 height = 64 + _lines * LineHeight;
                    MenuWindows.SizeFrame(_frame, Width, height, "ACTIONS");
                    _label.SetRawRect(0f, -6f, Width - 60f, height - 40f);
                    _root.transform.localPosition = new Vector3(960f - Margin - Width / 2f, 540f - Margin - height / 2f, 0f);
                }
                if (text != _shown)
                {
                    _shown = text;
                    _label.rawText = text;
                }
                if (!_root.activeSelf)
                {
                    MenuWindows.ApplyWindowColor(_root);
                    _root.SetActive(true);
                }
            }
            catch (Exception err)
            {
                Memoria.Prime.Log.Error(err, "[Online] Battle: the Actions window failed");
                if (_root != null)
                    Object.Destroy(_root);
                _root = null;
            }
        }

        private static List<String> Compose()
        {
            List<String> lines = new List<String>();
            HashSet<UInt16> listed = new HashSet<UInt16>();
            Int32 n = 0;
            foreach (BattleQueue.Entry e in BattleQueue.Current())
            {
                listed.Add(e.Caster);
                String place = e.Running ? "Now" : $"{++n}.";
                lines.Add($"{place}  {BattleQueue.CasterName(e.Caster)}: {BattleQueue.ActionName(e.Cmd, e.Sub)}");
            }
            foreach (KeyValuePair<UInt16, String> pending in BattleControl.PendingCommands())
                if (!listed.Contains(pending.Key)) // else the owner has it in its queue already
                    lines.Add($"...  {BattleQueue.CasterName(pending.Key)}: {pending.Value} (sending)");
            if (lines.Count > MaxLines)
            {
                Int32 more = lines.Count - (MaxLines - 1);
                lines.RemoveRange(MaxLines - 1, lines.Count - (MaxLines - 1));
                lines.Add($"+{more} more");
            }
            return lines;
        }

        private static Boolean Create(UIManager ui, GameObject parent)
        {
            if (_root != null)
                Object.Destroy(_root);
            _root = null;
            MainMenuUI menu = ui.MainMenuScene;
            Transform frameTemplate = menu?.GenericInfoPanel?.transform.Find("Frame Background");
            if (frameTemplate == null || menu.LocationInfoPanel == null)
                return false;
            Int32 depth = 0;
            foreach (UIPanel panel in parent.GetComponentsInChildren<UIPanel>(true))
                depth = Math.Max(depth, panel.depth);

            _parent = parent;
            _root = new GameObject("Online Battle Actions");
            _root.transform.parent = parent.transform;
            _root.transform.localScale = Vector3.one;
            _root.AddComponent<UIPanel>().depth = depth + 1;

            GameObject frameGo = Object.Instantiate(frameTemplate.gameObject);
            MenuWindows.DetachOutsideAnchors(frameGo);
            frameGo.transform.parent = _root.transform;
            frameGo.transform.localScale = Vector3.one;
            frameGo.transform.localPosition = Vector3.zero;
            UILabel caption = frameGo.transform.Find("Caption")?.GetComponent<UILabel>();
            if (caption != null)
                Object.DestroyImmediate(caption.GetComponent<UILocalize>());
            _frame = frameGo.GetComponent<UIWidget>();
            MenuWindows.ApplyWindowColor(frameGo);

            GameObject labelGo = Object.Instantiate(menu.LocationInfoPanel.GetChild(0));
            labelGo.transform.parent = _root.transform;
            labelGo.transform.localScale = Vector3.one;
            labelGo.transform.localPosition = Vector3.zero;
            _label = labelGo.GetComponent<UILabel>();
            Object.DestroyImmediate(_label.GetComponent<UILocalize>());
            _label.leftAnchor.target = null;
            _label.rightAnchor.target = null;
            _label.topAnchor.target = null;
            _label.bottomAnchor.target = null;
            _label.pivot = UIWidget.Pivot.Center;
            _label.alignment = NGUIText.Alignment.Left;
            _label.overflowMethod = UILabel.Overflow.ShrinkContent;
            NGUITools.SetLayer(_root, parent.layer);
            _shown = null;
            _lines = -1;
            _root.SetActive(false);
            return true;
        }
    }
}
