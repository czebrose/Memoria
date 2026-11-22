using Memoria.Online.Session;
using Memoria.Prime;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Memoria.Online.Menus
{
    /// <summary>
    /// Debug test driver for the Online menu (OnlineDebug = 1 and "-memoria-online-menu-tour" on the command line).
    /// Presses keys through the game's own input path (UnityXInput.Input.InjectKeyPress), so it works while the game
    /// window is in the background, and saves screenshots to %TEMP%\FF9OnlineMenuTour. Nothing runs without the flag.
    /// </summary>
    internal sealed class MenuTour : MonoBehaviour
    {
        public const String Flag = "-memoria-online-menu-tour";

        private String _folder;
        private Int32 _shot;

        /// <summary>With this too: host a lobby alone at the title, start the latest save, and tour the real session.</summary>
        public const String HostFlag = "-memoria-online-menu-tour-host";

        /// <summary>With this too: tour the title screen's Online entry and lobby instead.</summary>
        public const String TitleFlag = "-memoria-online-menu-tour-title";

        /// <summary>With this too (and -memoria-battle N): the battle's Online window, with the pretend session.</summary>
        public const String BattleFlag = "-memoria-online-menu-tour-battle";

        public static Boolean Requested => HasArg(Flag);

        private IEnumerator BattleSteps()
        {
            while (PersistenSingleton<UIManager>.Instance?.State != UIManager.UIState.BattleHUD || FF9StateSystem.Battle.FF9Battle.btl_phase != FF9StateBattleSystem.PHASE_NORMAL)
                yield return Wait(1f);
            List<Int32> party = new List<Int32>();
            foreach (PLAYER p in FF9StateSystem.Common.FF9.party.member)
                if (p != null)
                    party.Add((Int32)p.info.slot_no);
            OnlineMenu.Preview = new PreviewPartyControl(party, true);
            yield return Wait(2f);
            yield return Shot("battle");
            yield return Press(Control.Select, 1f);
            yield return Shot("battle-party");
            yield return Press(Control.Down);
            yield return Press(Control.Down);
            yield return Press(Control.Confirm, 1f);
            yield return Shot("battle-free-character");
            yield return Press(Control.Down);
            yield return Press(Control.Confirm, 1f);
            yield return Shot("battle-follow-list");
            yield return Press(Control.Cancel);
            yield return Press(Control.Cancel, 1f);
            yield return Press(Control.Down);
            yield return Press(Control.Confirm, 1f);
            yield return Shot("battle-offer");
            yield return Press(Control.Cancel);
            yield return Press(Control.Select, 1f);
            yield return Shot("battle-closed");
            yield return Wait(6f);
            yield return Press(Control.Select, 1f);
            yield return Shot("battle-reopened-during-turns");
            yield return Wait(8f);
            yield return Shot("battle-later");
        }

        private static Boolean AtTitle => PersistenSingleton<UIManager>.Instance?.State == UIManager.UIState.Title;

        /// <summary>The title screen: its Online entry and the lobby windows (hosting alone).</summary>
        private IEnumerator TitleSteps()
        {
            while (!AtTitle || !PersistenSingleton<UIManager>.Instance.TitleScene.MenuPanelObject.activeInHierarchy)
                yield return Wait(1f);
            yield return Wait(3f);
            yield return Shot("title");
            yield return Press(Control.Down);
            yield return Press(Control.Down);
            yield return Press(Control.Down);
            yield return Press(Control.Left);
            yield return Shot("title-left-of-cloud");
            yield return Press(Control.Up);
            yield return Shot("title-up");
            yield return ClickObject(TitleOnline.Current.Entry, 1f);
            yield return Shot("title-online-window");
            yield return Click("Online Title Window", "Host a game", 6f);
            yield return Shot("lobby-host-alone");
            yield return Click("Online Title Window", "Game: ", 1.5f, true);
            yield return Shot("lobby-game-list");
            yield return Press(Control.Cancel, 1f);
            yield return Shot("lobby-after-cancel-list");
            yield return Click("Online Title Window", "Leave lobby", 3f);
            yield return Shot("left-lobby");
            yield return Click("Online Title Window", "Join a friend", 2f);
            yield return Shot("join-friend-list");
            yield return Press(Control.Cancel, 1f);
            yield return Press(Control.Cancel, 1f);
            yield return Shot("title-back");
        }

        private Coroutine ClickObject(GameObject target, Single after) => StartCoroutine(ClickObjectCo(target, after));

        private IEnumerator ClickObjectCo(GameObject target, Single after)
        {
            Log.Message($"[Online] Menu tour: click {target?.name ?? "(nothing)"}");
            if (target != null)
            {
                Int32 touch = UICamera.currentTouchID;
                UICamera.currentTouchID = -1;
                target.SendMessage("OnClick", SendMessageOptions.DontRequireReceiver);
                UICamera.currentTouchID = touch;
            }
            yield return Wait(after);
        }

        private static Boolean HasArg(String flag)
        {
            foreach (String arg in Environment.GetCommandLineArgs())
                if (arg == flag)
                    return true;
            return false;
        }

        private void Start()
        {
            _folder = Path.Combine(Path.GetTempPath(), "FF9OnlineMenuTour");
            Directory.CreateDirectory(_folder);
            foreach (String old in Directory.GetFiles(_folder, "*.png"))
                File.Delete(old);
            Log.Message($"[Online] Menu tour: screenshots to {_folder}");
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            if (HasArg(BattleFlag))
            {
                yield return StartCoroutine(BattleSteps());
                OnlineMenu.Preview = null;
                Log.Message("[Online] Menu tour: done");
                yield break;
            }
            if (HasArg(TitleFlag))
            {
                yield return StartCoroutine(TitleSteps());
                Log.Message("[Online] Menu tour: done");
                yield break;
            }
            Boolean host = HasArg(HostFlag);
            if (host)
            {
                yield return StartCoroutine(HostAlone());
                if (SessionLobby.Current == null || SessionLobby.Current.Phase != SessionPhase.Playing)
                {
                    Log.Message("[Online] Menu tour: FAILED (no session to tour)");
                    yield break;
                }
            }
            while (!InFieldWithControl())
                yield return Wait(1f);
            yield return Wait(4f);
            yield return StartCoroutine(host ? SessionSteps() : PreviewSteps());
            OnlineMenu.Preview = null;
            Log.Message("[Online] Menu tour: done");
        }

        /// <summary>A pretend 4-player session (PreviewPartyControl): every window and state, no network.</summary>
        private IEnumerator PreviewSteps()
        {
            List<Int32> party = new List<Int32>();
            foreach (PLAYER p in FF9StateSystem.Common.FF9.party.member)
                if (p != null)
                    party.Add((Int32)p.info.slot_no);
            OnlineMenu.Preview = new PreviewPartyControl(party, true);
            yield return Wait(1f);
            yield return Shot("field-offer-notice");
            yield return Press(Control.Menu, 2f);
            yield return Shot("main-menu-online-entry");
            yield return Press(Control.Up);
            yield return Press(Control.Up);
            yield return Shot("cursor-on-online");
            yield return Press(Control.Confirm, 1f);
            yield return Shot("online-window-guest-with-offer");
            yield return Click("Online Command Window", "Party", 1f); // as a left click on the row
            yield return Shot("party-owners");
            yield return Press(Control.Confirm, 1f);
            yield return Shot("my-character-release");
            yield return Press(Control.Cancel);
            yield return Press(Control.Down);
            yield return Press(Control.Confirm, 1f);
            yield return Shot("free-character-take-follow");
            yield return Press(Control.Select, 1f);
            yield return Shot("free-character-help");
            yield return Press(Control.Select);
            yield return Press(Control.Down);
            yield return Press(Control.Confirm, 1f);
            yield return Shot("follow-player-list");
            yield return Press(Control.Down);
            yield return Press(Control.Confirm, 1f);
            yield return Shot("after-follow");
            yield return Press(Control.Down);
            yield return Press(Control.Confirm, 1f);
            yield return Shot("other-player-offer-accept-decline");
            yield return Press(Control.Cancel);
            yield return Press(Control.Up);
            yield return Press(Control.Up);
            yield return Press(Control.Up);
            yield return Press(Control.Confirm, 1f);
            yield return Shot("host-character-swap");
            yield return Press(Control.Confirm, 1f);
            yield return Shot("after-swap-offer");
            yield return Press(Control.Cancel, 1f);
            yield return Shot("back-to-online-window");
            yield return Press(Control.Cancel, 1f);
            yield return Shot("back-to-submenu");
            yield return Press(Control.Cancel, 2f);
            OnlineMenu.Preview = null;
            yield return Shot("closed");
        }

        /// <summary>The real session (this PC hosting alone): the host's views, a Take and a Follow list.</summary>
        private IEnumerator SessionSteps()
        {
            yield return Press(Control.Menu, 2f);
            yield return Shot("host-main-menu");
            yield return Press(Control.Up);
            yield return Press(Control.Up);
            yield return Press(Control.Confirm, 1f);
            yield return Shot("host-party");
            yield return Press(Control.Confirm, 1f);
            yield return Shot("host-own-character");
            yield return Press(Control.Cancel);
            yield return Press(Control.Down);
            yield return Press(Control.Confirm, 1f);
            yield return Shot("host-free-character");
            yield return Press(Control.Down);
            yield return Press(Control.Confirm, 1f);
            yield return Shot("host-follow-list");
            yield return Press(Control.Cancel);
            yield return Press(Control.Up);
            yield return Press(Control.Confirm, 2f);
            yield return Shot("host-after-take");
            yield return Press(Control.Cancel);
            yield return Press(Control.Cancel, 2f);
            yield return Shot("host-closed");
        }

        private IEnumerator HostAlone()
        {
            OnlineSession online = GetComponent<OnlineSession>();
            Single until = Time.realtimeSinceStartup + 180f;
            while (Time.realtimeSinceStartup < until && !(PersistenSingleton<UIManager>.Instance?.State == UIManager.UIState.Title && online.TourCreateLobby()))
                yield return Wait(1f);
            Log.Message("[Online] Menu tour: hosting a lobby alone");
            while (Time.realtimeSinceStartup < until && (SessionLobby.Current == null || !SessionLobby.Current.TourTryStart()))
                yield return Wait(2f);
        }

        // ---- Helpers ------------------------------------------------------------------------------------------

        private static Boolean InFieldWithControl()
        {
            return PersistenSingleton<UIManager>.Instance != null && PersistenSingleton<UIManager>.Instance.State == UIManager.UIState.FieldHUD
                && PersistenSingleton<EventEngine>.Instance != null && PersistenSingleton<EventEngine>.Instance.gMode == 1;
        }

        private Coroutine Wait(Single seconds) => StartCoroutine(WaitCo(seconds));
        private Coroutine Press(Control control, Single after = 0.6f) => StartCoroutine(PressCo(control, after));
        private Coroutine Press(KeyCode key, Single after = 0.6f) => StartCoroutine(PressCo(key, after));
        private Coroutine Shot(String name) => StartCoroutine(ShotCo(name));
        private Coroutine Click(String window, String row, Single after, Boolean prefix = false) => StartCoroutine(ClickCo(window, row, after, prefix));

        /// <summary>What NGUI does for a left click on a row: OnClick on it, with the mouse as the current touch.</summary>
        private IEnumerator ClickCo(String window, String row, Single after, Boolean prefix)
        {
            GameObject target = null;
            foreach (ButtonGroupState button in FindObjectsOfType<ButtonGroupState>())
                if (button.transform.parent != null && button.transform.parent.name == window && (prefix ? button.name.Contains(" " + row) : button.name.EndsWith(" " + row)))
                    target = button.gameObject;
            Log.Message($"[Online] Menu tour: click {window} / {row}{(target == null ? " (not found)" : "")}");
            if (target != null)
            {
                Int32 touch = UICamera.currentTouchID;
                UICamera.currentTouchID = -1;
                target.SendMessage("OnClick", SendMessageOptions.DontRequireReceiver);
                UICamera.currentTouchID = touch;
            }
            yield return Wait(after);
        }

        private static IEnumerator WaitCo(Single seconds)
        {
            Single until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
                yield return null;
        }

        public static KeyCode KeyOf(Control control)
        {
            switch (control)
            {
                case Control.Up: return KeyCode.UpArrow;
                case Control.Down: return KeyCode.DownArrow;
                case Control.Left: return KeyCode.LeftArrow;
                case Control.Right: return KeyCode.RightArrow;
                default: return PersistenSingleton<HonoInputManager>.Instance.InputKeysPrimary[(Int32)control];
            }
        }

        private IEnumerator PressCo(Control control, Single after)
        {
            KeyCode key = KeyOf(control);
            Log.Message($"[Online] Menu tour: press {control} ({key})");
            UnityXInput.Input.InjectKeyPress(key);
            yield return Wait(after);
        }

        private IEnumerator PressCo(KeyCode key, Single after)
        {
            Log.Message($"[Online] Menu tour: press {key}");
            UnityXInput.Input.InjectKeyPress(key);
            yield return Wait(after);
        }

        private IEnumerator ShotCo(String name)
        {
            String path = Path.Combine(_folder, $"{++_shot:00}-{name}.png");
            Application.CaptureScreenshot(path);
            Log.Message($"[Online] Menu tour: screenshot {path} (group {ButtonGroupState.ActiveGroup}, button {ButtonGroupState.ActiveButton?.name})");
            yield return Wait(0.5f);
        }

        public static void Dump(GameObject root)
        {
            StringBuilder sb = new StringBuilder();
            DumpNode(root.transform, 0, sb);
            Log.Message($"[Online] Menu tour: hierarchy of {root.name}\n{sb}");
        }

        private static void DumpNode(Transform t, Int32 depth, StringBuilder sb)
        {
            sb.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)");
            UIWidget w = t.GetComponent<UIWidget>();
            if (w != null)
                sb.Append($" [{w.GetType().Name} {w.width}x{w.height} @{t.localPosition.x:0},{t.localPosition.y:0} d{w.depth}{(w is UISprite s ? " " + s.spriteName : "")}{(w is UILabel l ? " \"" + l.rawText.Replace("\n", "|") + "\" fs" + l.fontSize : "")}]");
            else
                sb.Append($" @{t.localPosition.x:0},{t.localPosition.y:0}");
            if (t.GetComponent<UIPanel>() != null)
                sb.Append(" <UIPanel>");
            ButtonGroupState b = t.GetComponent<ButtonGroupState>();
            if (b != null)
                sb.Append($" <BGS {b.GroupName}>");
            if (t.GetComponent<UITable>() != null)
                sb.Append(" <UITable>");
            if (t.GetComponent<UIGrid>() != null)
                sb.Append(" <UIGrid>");
            sb.Append('\n');
            foreach (Transform child in t)
                DumpNode(child, depth + 1, sb);
        }
    }
}
