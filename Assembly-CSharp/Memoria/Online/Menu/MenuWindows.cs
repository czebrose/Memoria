using Assets.Sources.Scripts.UI.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Memoria.Online.Menus
{
    /// <summary>
    /// Game-style windows for the online menus (the main menu's Online entry, the title screen's lobby), built from
    /// the main menu's own widgets: the Time &amp; Gil window frame, the Location label and a submenu row. They get the
    /// game's fonts, window colour, hand cursor, sounds and keyboard / controller / mouse handling. Each window is a
    /// cursor group; a row's confirm comes back through the scene's OnKeyConfirm, which calls <see cref="Confirm"/>.
    /// </summary>
    internal sealed class MenuWindows
    {
        public const Int32 RowHeight = 78;
        private const Int32 CaptionSpace = 34;
        private const Int32 BottomSpace = 22;

        public sealed class Row
        {
            public GameObject Go;
            public UILabel Label;
            public Boolean Enabled;
            public Action Act;
            public String Text, Help;
        }

        public sealed class Window
        {
            public String Group;
            public GameObject Root;
            public UIWidget Frame;
            public UILabel Caption;
            public UILabel Info;
            public Int32 Width;
            public readonly List<Row> Rows = new List<Row>();
            public Int32 Height => Frame.height;
        }

        private readonly UIScene _scene;
        private readonly Transform _parent;
        private readonly GameObject _rowTemplate;

        /// <param name="scene">Gets the rows' mouse clicks (its OnKeyConfirm).</param>
        /// <param name="parent">The windows' parent (its coordinates are the windows').</param>
        /// <param name="rowTemplate">A submenu row to copy (MainMenuUI's Card entry).</param>
        public MenuWindows(UIScene scene, Transform parent, GameObject rowTemplate)
        {
            _scene = scene;
            _parent = parent;
            // A copy made while inactive, so its copies can join another cursor group (ButtonGroupState.OnEnable)
            Boolean active = rowTemplate.activeSelf;
            rowTemplate.SetActive(false);
            _rowTemplate = Object.Instantiate(rowTemplate);
            rowTemplate.SetActive(active);
            _rowTemplate.name = "Online Row Template";
            _rowTemplate.transform.parent = parent;
            Object.DestroyImmediate(_rowTemplate.GetComponentsInChildren<UILocalize>(true).FirstOrDefault());
            DetachOutsideAnchors(_rowTemplate);
        }

        private static MainMenuUI MainMenu => PersistenSingleton<UIManager>.Instance.MainMenuScene;

        /// <summary>The main menu's Card row (a template for rows), found whether or not the main menu has opened yet.</summary>
        public static GameObject MainMenuRowTemplate => MainMenu.SubMenuPanel.GetComponentsInChildren<ButtonGroupState>(true).First(b => b.name == "Card SubMenu").gameObject;

        public Window Create(String name, String group, Int32 width, Int32 panelDepth, Int32 pointerDepth)
        {
            Window window = new Window { Group = group, Width = width };
            GameObject root = new GameObject("Online " + name + " Window");
            window.Root = root;
            Int32 layer = _parent.gameObject.layer;
            root.layer = layer;
            root.transform.parent = _parent;
            root.transform.localScale = Vector3.one;
            root.transform.localPosition = Vector3.zero;
            root.AddComponent<UIPanel>().depth = panelDepth;
            ButtonGroupState.SetPointerDepthToGroup(pointerDepth, group);

            GameObject frameGo = Object.Instantiate(MainMenu.GenericInfoPanel.transform.Find("Frame Background").gameObject);
            frameGo.name = "Frame Background";
            DetachOutsideAnchors(frameGo); // the frame is anchored to the Time & Gil panel it was copied from
            frameGo.transform.parent = root.transform;
            frameGo.transform.localScale = Vector3.one;
            frameGo.transform.localPosition = Vector3.zero;
            window.Frame = frameGo.GetComponent<UIWidget>();
            window.Caption = frameGo.transform.Find("Caption")?.GetComponent<UILabel>();
            if (window.Caption != null)
                Object.DestroyImmediate(window.Caption.GetComponent<UILocalize>());

            GameObject infoGo = Object.Instantiate(MainMenu.LocationInfoPanel.GetChild(0));
            infoGo.name = "Info";
            infoGo.transform.parent = root.transform;
            infoGo.transform.localScale = Vector3.one;
            infoGo.transform.localPosition = Vector3.zero;
            window.Info = infoGo.GetComponent<UILabel>();
            DetachOutsideAnchors(infoGo);
            window.Info.leftAnchor.target = null;
            window.Info.rightAnchor.target = null;
            window.Info.topAnchor.target = null;
            window.Info.bottomAnchor.target = null;
            window.Info.pivot = UIWidget.Pivot.TopLeft;
            window.Info.alignment = NGUIText.Alignment.Left;
            window.Info.overflowMethod = UILabel.Overflow.ShrinkContent;
            window.Info.fontSize = 30;
            NGUITools.SetLayer(root, layer);
            ApplyWindowColor(root);
            return window;
        }

        public static Row NewRow(String text, Boolean enabled, String help, Action act)
        {
            return new Row { Enabled = enabled, Act = act ?? (() => { }), Text = text, Help = help };
        }

        /// <summary>
        /// (Re)builds a window: caption, an optional info text (several lines: '\n'), and its rows. With refresh and
        /// the window's group active, the cursor stays on the same row index.
        /// </summary>
        public void Fill(Window window, String caption, String info, List<Row> rows, Boolean refresh, ref Int32 cursor)
        {
            Boolean hadCursor = refresh && ButtonGroupState.ActiveGroup == window.Group;
            if (hadCursor)
            {
                GameObject current = ButtonGroupState.ActiveButton;
                Int32 index = window.Rows.FindIndex(r => r.Go == current);
                if (index >= 0)
                    cursor = index;
            }
            foreach (Row old in window.Rows)
            {
                old.Go.SetActive(false); // leaves the cursor group (ButtonGroupState.OnDisable)
                Object.Destroy(old.Go);
            }
            window.Rows.Clear();

            Int32 width = window.Width;
            Int32 infoHeight = InfoHeight(info, width);
            Int32 height = CaptionSpace + infoHeight + rows.Count * RowHeight + BottomSpace;
            SizeFrame(window.Frame, width, height, caption);
            Single top = height / 2f - CaptionSpace;
            window.Info.gameObject.SetActive(infoHeight > 0);
            if (infoHeight > 0)
            {
                window.Info.rawText = info;
                window.Info.SetRawRect(-width / 2f + 30f, top - 6f, width - 60f, infoHeight - 10f);
                top -= infoHeight;
            }
            for (Int32 i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                GameObject go = Object.Instantiate(_rowTemplate);
                go.name = $"Row {i} {row.Text}";
                go.transform.parent = window.Root.transform;
                go.transform.localScale = Vector3.one;
                go.transform.localPosition = Vector3.zero; // re-parenting keeps the world position (an odd z)
                UIEventListener.Get(go).Click += _scene.onClick; // mouse: the copy has the listener but not the menu's handler (delegates aren't copied)
                ButtonGroupState button = go.GetComponent<ButtonGroupState>();
                button.GroupName = window.Group;
                button.Help = new ButtonGroupState.HelpDetail { Enable = !String.IsNullOrEmpty(row.Help), TextKey = String.Empty, Text = row.Help ?? String.Empty, Tail = true };
                UIWidget widget = go.GetComponent<UIWidget>();
                Int32 rowWidth = width - 36;
                widget.SetRawRect(0f, top - RowHeight / 2f, rowWidth, RowHeight);
                go.GetComponent<BoxCollider>().size = new Vector3(rowWidth, RowHeight);
                foreach (UIWidget child in go.GetComponentsInChildren<UIWidget>(true))
                {
                    if (child == widget || child.isAnchored)
                        continue;
                    if (child.name == "Spliter")
                    {
                        child.width = rowWidth;
                        child.transform.localPosition = new Vector3(0f, -RowHeight / 2f - 4f, 0f);
                        child.gameObject.SetActive(i < rows.Count - 1);
                    }
                    else
                    {
                        child.width = child is UILabel ? rowWidth - 20 : rowWidth;
                        child.height = RowHeight;
                        child.transform.localPosition = Vector3.zero;
                    }
                }
                row.Go = go;
                row.Label = go.GetComponentsInChildren<UILabel>(true).FirstOrDefault();
                SetText(row.Label, row.Text);
                row.Label.color = row.Enabled ? FF9TextTool.White : FF9TextTool.Gray;
                window.Rows.Add(row);
                top -= RowHeight;
            }
            for (Int32 i = 0; i < window.Rows.Count; i++)
            {
                UIKeyNavigation nav = window.Rows[i].Go.GetComponent<UIKeyNavigation>();
                nav.onUp = window.Rows[(i + window.Rows.Count - 1) % window.Rows.Count].Go;
                nav.onDown = window.Rows[(i + 1) % window.Rows.Count].Go;
                nav.onLeft = null;
                nav.onRight = null;
                nav.startsSelected = i == 0;
            }
            NGUITools.SetLayer(window.Root, _parent.gameObject.layer);
            window.Root.SetActive(true);
            foreach (Row row in window.Rows)
                row.Go.SetActive(true);
            cursor = Math.Max(0, Math.Min(cursor, window.Rows.Count - 1));
            if (hadCursor && window.Rows.Count > 0)
            {
                ButtonGroupState.MuteActiveSound = true;
                ButtonGroupState.ActiveButton = window.Rows[cursor].Go;
                ButtonGroupState.MuteActiveSound = false;
            }
        }

        /// <summary>Gives a window the cursor, on this row.</summary>
        public static void Focus(Window window, Int32 row = 0)
        {
            if (window.Rows.Count == 0)
                return;
            ButtonGroupState.SetCursorMemorize(window.Rows[Math.Max(0, Math.Min(row, window.Rows.Count - 1))].Go, window.Group);
            ButtonGroupState.ActiveGroup = window.Group;
        }

        /// <summary>A confirm on one of this window's rows: plays the sound and runs it. False if go isn't one of them.</summary>
        public static Boolean Confirm(Window window, GameObject go)
        {
            if (window == null || ButtonGroupState.ActiveGroup != window.Group)
                return false;
            Row row = window.Rows.Find(r => r.Go == go);
            if (row == null)
                return true;
            if (!row.Enabled)
            {
                FF9Sfx.FF9SFX_Play(102);
                return true;
            }
            FF9Sfx.FF9SFX_Play(103);
            row.Act();
            return true;
        }

        /// <summary>The info text's height: about one line per (width - 60) / 16 characters, '\n' starting a line.</summary>
        private static Int32 InfoHeight(String info, Int32 width)
        {
            if (String.IsNullOrEmpty(info))
                return 0;
            Int32 perLine = Math.Max(10, (width - 60) / 16);
            Int32 lines = 0;
            foreach (String line in info.Split('\n'))
                lines += Math.Max(1, (line.Length + perLine - 1) / perLine);
            return 14 + 42 * lines;
        }

        internal static void SizeFrame(UIWidget frame, Int32 width, Int32 height, String caption)
        {
            frame.width = width;
            frame.height = height;
            foreach (UIWidget child in frame.GetComponentsInChildren<UIWidget>(true))
            {
                if (child == frame)
                    continue;
                if (child.isAnchored)
                {
                    // Border, body and caption follow the frame, but their anchors may only update when shown: a
                    // window resized while open (the ONLINE window turning into the wider LOBBY) would keep the old size
                    child.ResetAndUpdateAnchors();
                    if (child is UILabel anchoredLabel)
                        SetText(anchoredLabel, caption);
                    continue;
                }
                if (child is UILabel label)
                {
                    SetText(label, caption);
                    label.width = width - 40;
                    label.transform.localPosition = new Vector3(-width / 2f + 40f, height / 2f - 6f, 0f);
                    continue;
                }
                switch (child.name)
                {
                    case "Body":
                        child.width = width - 36;
                        child.height = height - 36;
                        child.transform.localPosition = Vector3.zero;
                        break;
                    case "Shadow":
                        child.width = width;
                        child.height = height;
                        child.transform.localPosition = new Vector3(12f, -12f, 0f);
                        break;
                    default:
                        child.width = width;
                        child.height = height;
                        child.transform.localPosition = Vector3.zero;
                        break;
                }
            }
        }

        /// <summary>Places a window by its centre x and its top y (the parent's coordinates), kept inside the screen.</summary>
        public static void Place(Window window, Single centerX, Single top)
        {
            Single half = window.Frame.height / 2f;
            Single y = top - half;
            y = Math.Min(y, 500f - half);
            y = Math.Max(y, -420f + half);
            centerX = Math.Max(-760f + window.Width / 2f, Math.Min(760f - window.Width / 2f, centerX));
            window.Root.transform.localPosition = new Vector3((Single)Math.Round(centerX), (Single)Math.Round(y), 0f);
        }

        public static void Destroy(ref Window window)
        {
            if (window == null)
                return;
            foreach (Row row in window.Rows)
                if (row.Go != null)
                    row.Go.SetActive(false);
            if (window.Root != null)
            {
                window.Root.SetActive(false);
                Object.Destroy(window.Root);
            }
            ButtonGroupState.RemoveCursorMemorize(window.Group); // no cursor memory on a destroyed row
            window = null;
        }

        public static void SetText(UILabel label, String text)
        {
            if (label == null)
                return;
            UILocalize localize = label.GetComponent<UILocalize>();
            if (localize != null)
                Object.DestroyImmediate(localize);
            label.rawText = text;
        }

        /// <summary>The player's window colour (Config), as UIScene.DisplayWindowBackground gives a scene's windows.</summary>
        internal static void ApplyWindowColor(GameObject go)
        {
            foreach (UISprite sprite in go.GetComponentsInChildren<UISprite>(true))
                if (sprite.gameObject.tag == "Window Color" && sprite.atlas != FF9UIDataTool.GeneralAtlas && sprite.atlas != FF9UIDataTool.IconAtlas)
                    sprite.atlas = FF9UIDataTool.WindowAtlas;
        }

        /// <summary>
        /// A copied widget keeps its anchors, and an anchor to something outside the copy still points at the
        /// original (Unity only remaps references inside the copy): the main menu's window frames are anchored to
        /// their panel, so a copied frame would snap back onto the Time &amp; Gil window every frame while its text
        /// stays where we put it. Drops those anchors; anchors within the copy (border to frame, ...) stay.
        /// </summary>
        internal static void DetachOutsideAnchors(GameObject copy)
        {
            foreach (UIRect rect in copy.GetComponentsInChildren<UIRect>(true))
            {
                Boolean changed = false;
                foreach (UIRect.AnchorPoint anchor in new[] { rect.leftAnchor, rect.rightAnchor, rect.topAnchor, rect.bottomAnchor })
                {
                    if (anchor.target != null && !anchor.target.IsChildOf(copy.transform))
                    {
                        anchor.target = null;
                        changed = true;
                    }
                }
                if (changed)
                    rect.ResetAnchors();
            }
        }
    }
}
