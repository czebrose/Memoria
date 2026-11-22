using Assets.Sources.Scripts.UI.Common;
using Memoria.Online.Session;
using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Memoria.Online.Menus
{
    /// <summary>
    /// The "Online" entry of the field main menu (docs/online/online-menu.md). While a session plays it sits above
    /// Config; choosing it shows who plays each party member (in place of the magic stones) and moves the cursor onto
    /// the party, like Status does. Choosing a character opens its commands:
    ///   Take          a character nobody plays
    ///   Follow        who a character nobody plays follows into battle (a list of the players)
    ///   Swap          offer the player of that character your own; Accept / Decline answer an offer made to you
    ///   Release       a guest gives their character back to the host and watches
    /// A guest also gets a small Online window first (Party, Join host, and Accept / Decline when an offer is open).
    /// That window is where later tabs go (an Inventory / Trade entry would be one more row in BuildCommandRows).
    ///
    /// Everything is built from the main menu's own widgets (window frames, submenu rows, labels), so it has the game's
    /// fonts, hand cursor, sounds, help text (Select) and keyboard / controller navigation. Actions only send the
    /// existing control requests (IPartyControl → SessionLobby.Control.cs); the menu redraws when the host's answer
    /// arrives.
    /// </summary>
    internal sealed class OnlineMenu
    {
        private const String CommandGroup = "Online.Command";
        private const String ActionGroup = "Online.Action";
        private const String PlayerGroup = "Online.Player";
        private const Int32 RowHeight = 78;
        private const Int32 CaptionSpace = 34;
        private const Int32 BottomSpace = 22;
        private const Int32 SubMenuSpan = 678; // the submenu table's height: 7 rows of 90 with 8 between

        /// <summary>Debug (MenuTour): a pretend session to show instead of the real one.</summary>
        public static IPartyControl Preview;

        /// <summary>The Online entry or one of its windows has the cursor (the debug status box steps aside).</summary>
        public static Boolean Active { get; private set; }

        public readonly GameObject Entry;

        private enum Mode { Off, Command, Characters, Action, Players }

        private sealed class Row
        {
            public GameObject Go;
            public UILabel Label;
            public Boolean Enabled;
            public Action Act;
            public String Text, Help;
        }

        private sealed class Window
        {
            public String Group;
            public GameObject Root;
            public UIWidget Frame;
            public UILabel Caption;
            public UILabel Info;
            public Int32 Width;
            public readonly List<Row> Rows = new List<Row>();
        }

        private readonly MainMenuUI _menu;
        private readonly GameObject _rowTemplate;
        private readonly GameObject _frameTemplate;
        private readonly GameObject _infoTemplate;
        private readonly UILabel[] _owners;
        private readonly GameObject[] _stones;
        private readonly Dictionary<GameObject, Vector3> _entryPositions = new Dictionary<GameObject, Vector3>();
        private readonly Dictionary<GameObject, Int32> _entryHeights = new Dictionary<GameObject, Int32>();
        private readonly Dictionary<GameObject, GameObject[]> _entryLinks = new Dictionary<GameObject, GameObject[]>();
        private readonly Int32 _panelDepth;
        private Window _command, _action, _players;
        private Mode _mode;
        private Boolean _fromCommand;
        private Int32 _member = -1; // party position the action window is for
        private Int32 _actionCursor, _commandCursor;
        private String _stateKey;

        public OnlineMenu(MainMenuUI menu)
        {
            _menu = menu;
            List<GameObject> entries = menu.OnlineSubMenuEntries;
            foreach (GameObject go in entries)
            {
                _entryPositions[go] = go.transform.localPosition;
                _entryHeights[go] = go.GetComponent<UIWidget>().height;
                UIKeyNavigation nav = go.GetComponent<UIKeyNavigation>();
                _entryLinks[go] = new[] { nav.onUp, nav.onDown };
            }

            // The Online entry: a copy of Card, like Memoria's Party entry (MainMenuUI.Awake)
            GameObject template = menu.OnlineEntryTemplate;
            Entry = Object.Instantiate(template);
            Entry.name = "Online SubMenu";
            Entry.transform.parent = template.transform.parent;
            Entry.transform.localScale = Vector3.one;
            SetText(Entry.GetComponentsInChildren<UILabel>(true).FirstOrDefault(), "Online");
            Entry.GetComponent<ButtonGroupState>().Help = new ButtonGroupState.HelpDetail { Enable = true, TextKey = String.Empty, Text = "Who plays each party member: take, swap, release or set who a character follows.", Tail = true };
            Entry.SetActive(false);

            // Row template: a copy of a submenu row made while inactive, so its copies can join another cursor group
            template.SetActive(false);
            _rowTemplate = Object.Instantiate(template);
            template.SetActive(true);
            _rowTemplate.name = "Online Row Template";
            _rowTemplate.transform.parent = menu.transform;
            Object.DestroyImmediate(_rowTemplate.GetComponentsInChildren<UILocalize>(true).FirstOrDefault());
            DetachOutsideAnchors(_rowTemplate);

            _frameTemplate = menu.GenericInfoPanel.transform.Find("Frame Background").gameObject;
            _infoTemplate = menu.LocationInfoPanel.GetChild(0);

            // Owner labels: in place of each character's magic stones while the Online entry is in use
            List<CharacterDetailHUD> huds = menu.OnlineCharacterHUDs;
            _owners = new UILabel[huds.Count];
            _stones = new GameObject[huds.Count];
            for (Int32 i = 0; i < huds.Count; i++)
            {
                _stones[i] = huds[i].Content.transform.Find("Magic Stone Panel")?.gameObject;
                GameObject labelGo = Object.Instantiate(huds[i].NameLabel.gameObject);
                labelGo.name = "Online Owner";
                labelGo.transform.parent = huds[i].Content.transform;
                labelGo.transform.localScale = Vector3.one;
                labelGo.transform.localPosition = Vector3.zero;
                UILabel label = labelGo.GetComponent<UILabel>();
                label.leftAnchor.target = null;
                label.rightAnchor.target = null;
                label.topAnchor.target = null;
                label.bottomAnchor.target = null;
                label.pivot = UIWidget.Pivot.Left;
                label.alignment = NGUIText.Alignment.Left;
                label.overflowMethod = UILabel.Overflow.ShrinkContent;
                label.fixedAlignment = false;
                label.SetRawRect(106f, -40f, 384f, 84f);
                label.rawText = String.Empty;
                labelGo.SetActive(false);
                _owners[i] = label;
            }

            Int32 depth = 0;
            List<UIPanel> panels = new List<UIPanel>(menu.SubMenuPanel.GetComponentsInChildren<UIPanel>(true));
            panels.AddRange(menu.GetComponentsInChildren<UIPanel>(true));
            UIPanel menuPanel = NGUITools.FindInParents<UIPanel>(menu.gameObject);
            if (menuPanel != null)
                panels.Add(menuPanel);
            foreach (UIPanel panel in panels)
                depth = Math.Max(depth, panel.depth);
            _panelDepth = depth + 1;
            ButtonGroupState.SetPointerDepthToGroup(_panelDepth + 2, CommandGroup);
            ButtonGroupState.SetPointerDepthToGroup(_panelDepth + 2, ActionGroup);
            ButtonGroupState.SetPointerDepthToGroup(_panelDepth + 4, PlayerGroup);
        }

        private static IPartyControl Control
        {
            get
            {
                if (Preview != null)
                    return Preview;
                SessionLobby session = SessionLobby.Current;
                return session != null && session.Phase == SessionPhase.Playing ? session : null;
            }
        }

        // ---- Main menu hooks --------------------------------------------------------------------------------------

        /// <summary>The main menu opens: show the entry while a session plays.</summary>
        public void OnShow()
        {
            Close();
            SetEntryVisible(Control != null);
            Entry.GetComponentsInChildren<UILabel>(true).FirstOrDefault().color = FF9TextTool.White;
        }

        /// <summary>The main menu closes (or the session ended): everything back as the game has it.</summary>
        public void Close()
        {
            DestroyWindow(ref _players);
            DestroyWindow(ref _action);
            DestroyWindow(ref _command);
            ShowOwners(false);
            _mode = Mode.Off;
            Active = false;
        }

        public void Update()
        {
            IPartyControl control = Control;
            if (control == null)
            {
                if (_mode != Mode.Off)
                {
                    Log.Message("[Online] Online menu: the session ended; closing it");
                    Close();
                    _menu.OnlineBackToSubMenu(_menu.OnlineConfigEntry);
                }
                if (Entry.activeSelf && ButtonGroupState.ActiveButton != Entry)
                    SetEntryVisible(false);
                return;
            }
            if (_mode == Mode.Off)
                return;
            String key = StateKey(control);
            if (key == _stateKey)
                return;
            _stateKey = key;
            RefreshOwners(control);
            switch (_mode)
            {
                case Mode.Command:
                    FillCommand(control, true);
                    break;
                case Mode.Action:
                    if (MemberCharacter(_member) < 0)
                        BackToCharacters();
                    else
                        FillAction(control, true);
                    break;
                case Mode.Players:
                    if (MemberCharacter(_member) < 0 || control.HolderOf(MemberCharacter(_member)) != 0)
                    {
                        DestroyWindow(ref _players);
                        _mode = Mode.Action;
                        FillAction(control, true);
                        ButtonGroupState.ActiveGroup = ActionGroup;
                    }
                    else
                    {
                        FillPlayers(control, true);
                    }
                    break;
            }
        }

        public Boolean OnConfirm(GameObject go)
        {
            String group = ButtonGroupState.ActiveGroup;
            if (go == null)
                return false;
            if (group == _menu.OnlineSubMenuGroup && go == Entry)
            {
                IPartyControl control = Control;
                if (control == null)
                {
                    FF9Sfx.FF9SFX_Play(102);
                    return true;
                }
                FF9Sfx.FF9SFX_Play(103);
                Open(control);
                return true;
            }
            if (_mode == Mode.Off)
                return false;
            if (group == _menu.OnlineCharacterGroup && ButtonGroupState.ContainButtonInGroup(go, group))
            {
                Int32 index = go.transform.GetSiblingIndex();
                if (MemberCharacter(index) < 0)
                {
                    FF9Sfx.FF9SFX_Play(102);
                    return true;
                }
                FF9Sfx.FF9SFX_Play(103);
                OpenAction(index);
                return true;
            }
            Window window = group == CommandGroup ? _command : group == ActionGroup ? _action : group == PlayerGroup ? _players : null;
            if (window == null)
            {
                // A click on the character list (secondary group) while a character's commands are open
                if (_mode == Mode.Action && ButtonGroupState.ContainButtonInGroup(go, _menu.OnlineCharacterGroup) && MemberCharacter(go.transform.GetSiblingIndex()) >= 0)
                {
                    FF9Sfx.FF9SFX_Play(103);
                    DestroyWindow(ref _action);
                    OpenAction(go.transform.GetSiblingIndex());
                    return true;
                }
                return false;
            }
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

        public Boolean OnCancel(GameObject go)
        {
            if (_mode == Mode.Off)
                return false;
            String group = ButtonGroupState.ActiveGroup;
            if (group == PlayerGroup)
            {
                FF9Sfx.FF9SFX_Play(101);
                DestroyWindow(ref _players);
                _mode = Mode.Action;
                ButtonGroupState.ActiveGroup = ActionGroup;
                return true;
            }
            if (group == ActionGroup)
            {
                FF9Sfx.FF9SFX_Play(101);
                BackToCharacters();
                return true;
            }
            if (group == _menu.OnlineCharacterGroup)
            {
                FF9Sfx.FF9SFX_Play(101);
                IPartyControl control = Control;
                if (_fromCommand && _command != null && control != null)
                {
                    _mode = Mode.Command;
                    ShowOwners(false); // the ONLINE window covers them
                    _stateKey = StateKey(control);
                    FillCommand(control, false); // what changed while the party had the cursor (an offer came or went)
                    ButtonGroupState.SetCursorMemorize(_command.Rows[0].Go, CommandGroup);
                    ButtonGroupState.ActiveGroup = CommandGroup;
                    ButtonGroupState.SetSecondaryOnGroup(_menu.OnlineSubMenuGroup);
                    ButtonGroupState.HoldActiveStateOnGroup(_menu.OnlineSubMenuGroup);
                    return true;
                }
                ExitToSubMenu();
                return true;
            }
            if (group == CommandGroup)
            {
                FF9Sfx.FF9SFX_Play(101);
                ExitToSubMenu();
                return true;
            }
            return false;
        }

        // ---- Flow -------------------------------------------------------------------------------------------------

        private void Open(IPartyControl control)
        {
            Active = true;
            _stateKey = StateKey(control);
            RefreshOwners(control);
            List<Row> rows = BuildCommandRows(control);
            ShowOwners(rows.Count <= 1); // with the ONLINE window first, the labels come with the party step
            if (rows.Count > 1)
            {
                _fromCommand = true;
                _mode = Mode.Command;
                _commandCursor = 0;
                FillCommand(control, false);
                ButtonGroupState.SetCursorMemorize(_command.Rows[0].Go, CommandGroup);
                ButtonGroupState.ActiveGroup = CommandGroup;
                ButtonGroupState.SetSecondaryOnGroup(_menu.OnlineSubMenuGroup);
                ButtonGroupState.HoldActiveStateOnGroup(_menu.OnlineSubMenuGroup);
            }
            else
            {
                _fromCommand = false;
                EnterCharacters(MyMember(control));
            }
            Log.Message($"[Online] Online menu: opened ({(_fromCommand ? "Online window" : "party")})");
        }

        private void EnterCharacters(Int32 member)
        {
            _mode = Mode.Characters;
            ShowOwners(true);
            if (_command != null)
                _command.Root.SetActive(false);
            _menu.OnlineSelectCharacter(member);
        }

        private void BackToCharacters()
        {
            DestroyWindow(ref _players);
            DestroyWindow(ref _action);
            _mode = Mode.Characters;
            _menu.OnlineSelectCharacter(_member);
        }

        private void ExitToSubMenu()
        {
            Close();
            _menu.OnlineBackToSubMenu(Entry);
        }

        private void OpenAction(Int32 member)
        {
            _member = member;
            _mode = Mode.Action;
            _actionCursor = 0;
            IPartyControl control = Control;
            FillAction(control, false);
            ButtonGroupState.SetCursorMemorize(_action.Rows[0].Go, ActionGroup);
            ButtonGroupState.ActiveGroup = ActionGroup;
            ButtonGroupState.SetSecondaryOnGroup(_menu.OnlineCharacterGroup);
            ButtonGroupState.HoldActiveStateOnGroup(_menu.OnlineCharacterGroup);
        }

        private void OpenPlayers()
        {
            _mode = Mode.Players;
            IPartyControl control = Control;
            FillPlayers(control, false);
            Int32 character = MemberCharacter(_member);
            UInt64 leader = control.LeaderOf(character);
            IList<UInt64> players = control.Players;
            Int32 start = Math.Max(0, players.IndexOf(leader));
            ButtonGroupState.SetCursorMemorize(_players.Rows[Math.Min(start, _players.Rows.Count - 1)].Go, PlayerGroup);
            ButtonGroupState.ActiveGroup = PlayerGroup;
        }

        /// <summary>After an action: back to the party, the cursor on the same character.</summary>
        private void Done()
        {
            BackToCharacters();
        }

        // ---- Window contents --------------------------------------------------------------------------------------

        private List<Row> BuildCommandRows(IPartyControl control)
        {
            List<Row> rows = new List<Row>();
            rows.Add(NewRow("Party", true, "Choose a party member: take, swap, release or set who they follow.", () => EnterCharacters(MyMember(control))));
            UInt64 from = control.OfferFrom;
            if (from != 0)
            {
                String offer = OfferText(control, from);
                rows.Add(NewRow("Accept swap", true, offer, () => { Control?.AnswerOffer(true); ExitToSubMenu(); }));
                rows.Add(NewRow("Decline swap", true, offer, () => { Control?.AnswerOffer(false); ExitToSubMenu(); }));
            }
            if (!control.IsHost)
            {
                Boolean with = control.WithHost;
                rows.Add(NewRow("Join host", !with, with ? "You're already on the host's field." : "Go to where the host is (their field and their world).", () => { Control?.JoinHost(); ExitToSubMenu(); }));
            }
            // Later: an "Inventory" / "Trade" row goes here (per-player inventories aren't built yet)
            return rows;
        }

        private void FillCommand(IPartyControl control, Boolean refresh)
        {
            List<Row> rows = BuildCommandRows(control);
            UInt64 from = control.OfferFrom;
            String info = from != 0 ? OfferText(control, from) : null;
            if (_command == null)
                _command = CreateWindow("Command", CommandGroup, 420);
            Vector3 entry = MenuPosition(Entry.transform);
            FillWindow(_command, "ONLINE", info, rows, refresh, ref _commandCursor);
            Place(_command, 331f - 12f - _command.Width / 2f, entry.y + 45f);
        }

        private void FillAction(IPartyControl control, Boolean refresh)
        {
            Int32 character = MemberCharacter(_member);
            String name = MemberName(_member);
            UInt64 me = control.Me;
            UInt64 holder = control.HolderOf(character);
            Int32 mine = control.CharacterOf(me);
            String mineName = CharacterName(mine);
            List<Row> rows = new List<Row>();
            String info;
            if (holder == me)
            {
                info = "You play this character.";
                if (control.IsHost)
                    rows.Add(NewRow("Release", false, "The host always plays a character: take or swap for another one instead.", null));
                else
                    rows.Add(NewRow("Release", true, $"Give {name} back to the host's game and watch.", () => { Control?.Release(); Done(); }));
            }
            else if (holder == 0)
            {
                UInt64 leader = control.LeaderOf(character);
                info = $"Nobody plays {name}. Follows {PlayerLabel(control, leader)}.";
                rows.Add(NewRow("Take", true, mine >= 0 ? $"Play {name}. {mineName} goes back to the host's game." : $"Play {name}.", () => { Control?.Take(character); Done(); }));
                rows.Add(NewRow("Follow", true, $"Who {name} follows: they join that player's battles, commanded from that player's PC.", OpenPlayers));
            }
            else
            {
                info = $"{PlayerLabel(control, holder)} plays {name}.";
                if (control.OfferFrom == holder)
                {
                    String offer = OfferText(control, holder);
                    rows.Add(NewRow("Accept swap", true, offer, () => { Control?.AnswerOffer(true); Done(); }));
                    rows.Add(NewRow("Decline swap", true, offer, () => { Control?.AnswerOffer(false); Done(); }));
                }
                else if (control.OfferSent(holder))
                    rows.Add(NewRow("Swap offered", false, $"Waiting for {control.PlayerName(holder)}'s answer (offers expire after 30 s).", null));
                else if (mine < 0)
                    rows.Add(NewRow("Swap", false, "You're watching: take a character nobody plays first.", null));
                else
                    rows.Add(NewRow("Swap", true, $"Offer {control.PlayerName(holder)} your {mineName} for {name}. They accept or decline.", () => { Control?.OfferSwap(character); Done(); }));
            }
            if (_action == null)
                _action = CreateWindow("Action", ActionGroup, 420);
            FillWindow(_action, name.ToUpperInvariant(), info, rows, refresh, ref _actionCursor);
            Vector3 hud = MenuPosition(_menu.OnlineCharacterHUDs[_member].Self.transform);
            Place(_action, 293f - 16f - _action.Width / 2f, hud.y + 97f);
        }

        private void FillPlayers(IPartyControl control, Boolean refresh)
        {
            Int32 character = MemberCharacter(_member);
            String name = MemberName(_member);
            UInt64 leader = control.LeaderOf(character);
            List<Row> rows = new List<Row>();
            foreach (UInt64 player in control.Players)
            {
                UInt64 p = player;
                Boolean current = p == leader;
                rows.Add(NewRow(PlayerLabel(control, p), !current, current ? $"{name} already follows {PlayerLabel(control, p)}." : $"{name} follows {PlayerLabel(control, p)} from the next battle.", () => { Control?.Follow(character, p); Done(); }));
            }
            Int32 cursor = rows.FindIndex(r => r.Enabled);
            if (_players == null)
                _players = CreateWindow("Players", PlayerGroup, 460);
            FillWindow(_players, "FOLLOW", null, rows, refresh, ref cursor);
            Vector3 action = MenuPosition(_action.Root.transform);
            Place(_players, action.x + _action.Width / 2f + 12f + _players.Width / 2f, action.y + _action.Frame.height / 2f - 40f);
        }

        // ---- Owner labels -----------------------------------------------------------------------------------------

        private void ShowOwners(Boolean show)
        {
            for (Int32 i = 0; i < _owners.Length; i++)
            {
                _owners[i].gameObject.SetActive(show);
                if (_stones[i] != null)
                    _stones[i].SetActive(!show);
            }
        }

        private void RefreshOwners(IPartyControl control)
        {
            List<CharacterDetailHUD> huds = _menu.OnlineCharacterHUDs;
            for (Int32 i = 0; i < _owners.Length; i++)
            {
                Int32 character = MemberCharacter(i);
                ButtonGroupState button = huds[i].Self.GetComponent<ButtonGroupState>();
                if (character < 0)
                {
                    _owners[i].rawText = String.Empty;
                    continue;
                }
                String name = MemberName(i);
                UInt64 holder = control.HolderOf(character);
                String help;
                if (holder == control.Me)
                {
                    _owners[i].rawText = "You";
                    _owners[i].color = FF9TextTool.Yellow;
                    help = $"You play {name}.";
                }
                else if (holder != 0)
                {
                    _owners[i].rawText = PlayerLabel(control, holder);
                    _owners[i].color = FF9TextTool.White;
                    help = $"{PlayerLabel(control, holder)} plays {name}.";
                }
                else
                {
                    UInt64 leader = control.LeaderOf(character);
                    _owners[i].rawText = $"Follows {(leader == control.Me ? "you" : control.PlayerName(leader))}";
                    _owners[i].color = FF9TextTool.Cyan;
                    help = $"Nobody plays {name}: they follow {PlayerLabel(control, leader)} into battle.";
                }
                if (control.OfferFrom != 0 && control.OfferFrom == holder)
                    help += $"\n{control.PlayerName(holder)} offers you a swap.";
                button.Help.TextKey = String.Empty;
                button.Help.Text = help;
            }
        }

        // ---- Entry layout -----------------------------------------------------------------------------------------

        private void SetEntryVisible(Boolean visible)
        {
            if (Entry.activeSelf == visible && (visible || !_layoutChanged))
                return;
            Entry.SetActive(visible);
            List<GameObject> entries = _menu.OnlineSubMenuEntries;
            if (visible)
                entries.Insert(entries.Count - 1, Entry); // above Config
            UITable table = Entry.transform.parent.GetComponent<UITable>();
            if (!visible)
            {
                // Back to the layout the menu had (the game's or Memoria's with Party)
                foreach (GameObject go in entries)
                {
                    go.transform.localPosition = _entryPositions[go];
                    go.GetComponent<UIWidget>().height = _entryHeights[go];
                    go.GetComponent<BoxCollider>().size = new Vector3(go.GetComponent<UIWidget>().width, _entryHeights[go]);
                    UIKeyNavigation nav = go.GetComponent<UIKeyNavigation>();
                    nav.onUp = _entryLinks[go][0];
                    nav.onDown = _entryLinks[go][1];
                }
                _layoutChanged = false;
                return;
            }
            if (table != null)
                table.enabled = false;
            Int32 count = entries.Count;
            Single pitch = (SubMenuSpan + 8f) / count;
            for (Int32 i = 0; i < count; i++)
            {
                GameObject go = entries[i];
                Int32 height = (Int32)Math.Round(pitch - 8f);
                Single top = -(Single)Math.Round(i * pitch);
                UIWidget widget = go.GetComponent<UIWidget>();
                widget.SetRawRect(_entryPositions[entries[0]].x, top - height / 2f, widget.width, height);
                go.GetComponent<BoxCollider>().size = new Vector3(widget.width, height);
                UIKeyNavigation nav = go.GetComponent<UIKeyNavigation>();
                nav.onUp = entries[(i + count - 1) % count];
                nav.onDown = entries[(i + 1) % count];
            }
            _layoutChanged = true;
        }

        private Boolean _layoutChanged;

        // ---- Windows ----------------------------------------------------------------------------------------------

        private Window CreateWindow(String name, String group, Int32 width)
        {
            Window window = new Window { Group = group, Width = width };
            GameObject root = new GameObject("Online " + name + " Window");
            window.Root = root;
            root.layer = _menu.gameObject.layer;
            root.transform.parent = _menu.transform;
            root.transform.localScale = Vector3.one;
            root.transform.localPosition = Vector3.zero;
            UIPanel panel = root.AddComponent<UIPanel>();
            panel.depth = group == PlayerGroup ? _panelDepth + 2 : _panelDepth;

            GameObject frameGo = Object.Instantiate(_frameTemplate);
            frameGo.name = "Frame Background";
            DetachOutsideAnchors(frameGo); // the frame is anchored to the Time & Gil panel it was copied from
            frameGo.transform.parent = root.transform;
            frameGo.transform.localScale = Vector3.one;
            frameGo.transform.localPosition = Vector3.zero;
            window.Frame = frameGo.GetComponent<UIWidget>();
            window.Caption = frameGo.transform.Find("Caption")?.GetComponent<UILabel>();
            if (window.Caption != null)
                Object.DestroyImmediate(window.Caption.GetComponent<UILocalize>());

            GameObject infoGo = Object.Instantiate(_infoTemplate);
            infoGo.name = "Info";
            infoGo.transform.parent = root.transform;
            infoGo.transform.localScale = Vector3.one;
            infoGo.transform.localPosition = Vector3.zero;
            window.Info = infoGo.GetComponent<UILabel>();
            window.Info.leftAnchor.target = null;
            window.Info.rightAnchor.target = null;
            window.Info.topAnchor.target = null;
            window.Info.bottomAnchor.target = null;
            window.Info.pivot = UIWidget.Pivot.TopLeft;
            window.Info.alignment = NGUIText.Alignment.Left;
            window.Info.overflowMethod = UILabel.Overflow.ShrinkContent;
            window.Info.fontSize = 30;
            NGUITools.SetLayer(root, _menu.gameObject.layer);
            ApplyWindowColor(root);
            return window;
        }

        private Row NewRow(String text, Boolean enabled, String help, Action act)
        {
            return new Row { Enabled = enabled, Act = act ?? (() => { }), Text = text, Help = help };
        }

        private void FillWindow(Window window, String caption, String info, List<Row> rows, Boolean refresh, ref Int32 cursor)
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
            // About 24 characters per line at this size; one, two or three lines
            Int32 infoLines = String.IsNullOrEmpty(info) ? 0 : Math.Min(3, (info.Length + 23) / 24);
            Int32 infoHeight = infoLines == 0 ? 0 : 14 + 42 * infoLines;
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
                UIEventListener.Get(go).Click += _menu.onClick; // mouse: the copy has the listener but not the menu's handler (delegates aren't copied)
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
            NGUITools.SetLayer(window.Root, _menu.gameObject.layer);
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

        internal static void SizeFrame(UIWidget frame, Int32 width, Int32 height, String caption)
        {
            frame.width = width;
            frame.height = height;
            foreach (UIWidget child in frame.GetComponentsInChildren<UIWidget>(true))
            {
                if (child == frame)
                    continue;
                if (child is UILabel label)
                {
                    SetText(label, caption);
                    if (!label.isAnchored)
                    {
                        label.width = width - 40;
                        label.transform.localPosition = new Vector3(-width / 2f + 40f, height / 2f - 6f, 0f);
                    }
                    continue;
                }
                if (child.isAnchored)
                    continue;
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

        /// <summary>Places a window by its centre x and its top y (menu coordinates), kept inside the screen.</summary>
        private static void Place(Window window, Single centerX, Single top)
        {
            Single half = window.Frame.height / 2f;
            Single y = top - half;
            y = Math.Min(y, 500f - half);
            y = Math.Max(y, -420f + half);
            centerX = Math.Max(-760f + window.Width / 2f, Math.Min(760f - window.Width / 2f, centerX));
            window.Root.transform.localPosition = new Vector3((Single)Math.Round(centerX), (Single)Math.Round(y), 0f);
        }

        private static void DestroyWindow(ref Window window)
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

        // ---- Helpers ----------------------------------------------------------------------------------------------

        private Vector3 MenuPosition(Transform t)
        {
            return _menu.transform.InverseTransformPoint(t.position);
        }

        private static void SetText(UILabel label, String text)
        {
            if (label == null)
                return;
            UILocalize localize = label.GetComponent<UILocalize>();
            if (localize != null)
                Object.DestroyImmediate(localize);
            label.rawText = text;
        }

        /// <summary>
        /// A copied widget keeps its anchors, and an anchor to something outside the copy still points at the
        /// original (Unity only remaps references inside the copy): the main menu's window frames are anchored to
        /// their panel, so a copied frame would snap back onto the Time &amp; Gil window every frame while its text
        /// stays where we put it. Drops those anchors; anchors within the copy (border to frame, ...) stay.
        /// </summary>
        /// <summary>The player's window colour (Config), as UIScene.DisplayWindowBackground gives a scene's windows.</summary>
        internal static void ApplyWindowColor(GameObject go)
        {
            foreach (UISprite sprite in go.GetComponentsInChildren<UISprite>(true))
                if (sprite.gameObject.tag == "Window Color" && sprite.atlas != FF9UIDataTool.GeneralAtlas && sprite.atlas != FF9UIDataTool.IconAtlas)
                    sprite.atlas = FF9UIDataTool.WindowAtlas;
        }

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

        private static Int32 MemberCharacter(Int32 member)
        {
            PLAYER[] members = FF9StateSystem.Common?.FF9?.party?.member;
            if (members == null || member < 0 || member >= members.Length || members[member] == null)
                return -1;
            return (Int32)members[member].info.slot_no;
        }

        private static String MemberName(Int32 member)
        {
            PLAYER[] members = FF9StateSystem.Common.FF9.party.member;
            return member >= 0 && member < members.Length && members[member] != null ? members[member].Name : "?";
        }

        private static String CharacterName(Int32 character)
        {
            if (character < 0)
                return "nobody";
            foreach (PLAYER p in FF9StateSystem.Common.FF9.PlayerList)
                if ((Int32)p.info.slot_no == character && !String.IsNullOrEmpty(p.Name))
                    return p.Name;
            return $"character {character}";
        }

        private static Int32 MyMember(IPartyControl control)
        {
            Int32 mine = control.CharacterOf(control.Me);
            PLAYER[] members = FF9StateSystem.Common.FF9.party.member;
            for (Int32 i = 0; i < members.Length; i++)
                if (members[i] != null && (Int32)members[i].info.slot_no == mine)
                    return i;
            return -1;
        }

        private static String PlayerLabel(IPartyControl control, UInt64 player)
        {
            if (player == control.Me)
                return "you";
            return control.PlayerName(player) + (player == control.Host ? " (host)" : "");
        }

        private static String OfferText(IPartyControl control, UInt64 from)
        {
            return $"{control.PlayerName(from)} offers {CharacterName(control.CharacterOf(from))} for your {CharacterName(control.CharacterOf(control.Me))}.";
        }

        private static String StateKey(IPartyControl control)
        {
            StringBuilder key = new StringBuilder();
            key.Append(control.Me).Append('/').Append(control.OfferFrom).Append('/').Append(control.WithHost).Append('/');
            foreach (UInt64 player in control.Players)
                key.Append(player).Append(control.OfferSent(player) ? "!" : "").Append(',');
            PLAYER[] members = FF9StateSystem.Common.FF9.party.member;
            for (Int32 i = 0; i < members.Length; i++)
            {
                Int32 c = MemberCharacter(i);
                key.Append('|').Append(c);
                if (c >= 0)
                    key.Append(':').Append(control.HolderOf(c)).Append(':').Append(control.LeaderOf(c));
            }
            return key.ToString();
        }
    }
}
