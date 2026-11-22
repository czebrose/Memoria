using Memoria.Online.Session;
using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Memoria.Online.Menus
{
    /// <summary>
    /// A small game-style window at the top of the field screen for party-control news: a swap offer waiting for an
    /// answer (until it's answered or expires) and short messages such as "You now play ...". Built from the main
    /// menu's window frame and label, shown only on the field HUD. Called every frame by OnlineSession.
    /// </summary>
    internal static class OnlineNotice
    {
        private const Int32 Width = 1100, Height = 104;

        private static GameObject _root;
        private static UIWidget _frame;
        private static UILabel _label;
        private static String _flash;
        private static Single _flashUntil;
        private static String _shown;

        /// <summary>Shows a message for a few seconds (on the field; if elsewhere, when back on the field in time).</summary>
        public static void Flash(String text, Single seconds = 4f)
        {
            _flash = text;
            _flashUntil = Time.realtimeSinceStartup + seconds;
        }

        public static void Update()
        {
            UIManager ui = PersistenSingleton<UIManager>.Instance;
            String text = Compose();
            Boolean show = text != null && ui != null && ui.State == UIManager.UIState.FieldHUD && ui.FieldHUDScene != null && ui.FieldHUDScene.gameObject.activeInHierarchy;
            if (!show)
            {
                if (_root != null && _root.activeSelf)
                    _root.SetActive(false);
                return;
            }
            if (_root == null && !Create(ui))
                return;
            if (text != _shown)
            {
                _shown = text;
                _label.rawText = text;
            }
            if (!_root.activeSelf)
            {
                MenuWindows.ApplyWindowColor(_root); // the window colour may have changed in Config
                _root.SetActive(true);
            }
        }

        private static String Compose()
        {
            SessionLobby session = SessionLobby.Current;
            IPartyControl control = OnlineMenu.Preview ?? (session != null && session.Phase == SessionPhase.Playing ? session : null);
            if (control != null)
            {
                UInt64 from = control.OfferFrom;
                if (from != 0)
                    return $"{control.PlayerName(from)} offers a swap: answer in the menu, Online.";
            }
            if (_flash != null && Time.realtimeSinceStartup < _flashUntil)
                return _flash;
            _flash = null;
            return null;
        }

        private static Boolean Create(UIManager ui)
        {
            MainMenuUI menu = ui.MainMenuScene;
            Transform frameTemplate = menu?.GenericInfoPanel?.transform.Find("Frame Background");
            if (frameTemplate == null || menu.LocationInfoPanel == null)
                return false;
            GameObject parent = ui.FieldHUDScene.gameObject;
            Int32 depth = 0;
            foreach (UIPanel panel in parent.GetComponentsInChildren<UIPanel>(true))
                depth = Math.Max(depth, panel.depth);

            _root = new GameObject("Online Notice");
            _root.transform.parent = parent.transform;
            _root.transform.localScale = Vector3.one;
            _root.transform.localPosition = new Vector3(0f, 540f - 40f - Height / 2f, 0f);
            _root.AddComponent<UIPanel>().depth = depth + 1;

            GameObject frameGo = Object.Instantiate(frameTemplate.gameObject);
            MenuWindows.DetachOutsideAnchors(frameGo); // anchored to the Time & Gil panel it was copied from
            frameGo.transform.parent = _root.transform;
            frameGo.transform.localScale = Vector3.one;
            frameGo.transform.localPosition = Vector3.zero;
            UILabel caption = frameGo.transform.Find("Caption")?.GetComponent<UILabel>();
            if (caption != null)
                Object.DestroyImmediate(caption.GetComponent<UILocalize>());
            _frame = frameGo.GetComponent<UIWidget>();
            MenuWindows.SizeFrame(_frame, Width, Height, "ONLINE");
            MenuWindows.ApplyWindowColor(frameGo); // the copy keeps the prefab's blue until a menu has been shown

            GameObject labelGo = Object.Instantiate(menu.LocationInfoPanel.GetChild(0));
            labelGo.transform.parent = _root.transform;
            labelGo.transform.localScale = Vector3.one;
            labelGo.transform.localPosition = Vector3.zero;
            _label = labelGo.GetComponent<UILabel>();
            _label.leftAnchor.target = null;
            _label.rightAnchor.target = null;
            _label.topAnchor.target = null;
            _label.bottomAnchor.target = null;
            _label.pivot = UIWidget.Pivot.Center;
            _label.alignment = NGUIText.Alignment.Center;
            _label.overflowMethod = UILabel.Overflow.ShrinkContent;
            _label.SetRawRect(0f, -4f, Width - 60f, Height - 36f);
            NGUITools.SetLayer(_root, parent.layer);
            _shown = null;
            return true;
        }
    }
}
