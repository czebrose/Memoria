using Memoria.Online.Session;
using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;
using Row = Memoria.Online.Menus.MenuWindows.Row;
using Window = Memoria.Online.Menus.MenuWindows.Window;

namespace Memoria.Online.Menus
{
    /// <summary>
    /// The title screen's Online button (above the language button) and its windows (docs/online/online-menu.md):
    ///   ONLINE   not in a lobby: Host a game, Join a friend (Steam friends playing FF9 in a lobby)
    ///   LOBBY    in a lobby: who's there, the game, the party and who plays whom; the host chooses the game and
    ///            starts, guests pick a character; Invite friends (Steam overlay), Leave lobby
    /// The lobby window opens by itself when this PC enters a lobby another way (a Steam invite, Ctrl+Shift+H / J).
    /// Cancel closes the window and keeps the lobby (the Online button opens it again). Actions are the session's own
    /// (SessionLobby.Title.cs); the window redraws whenever the lobby changes. The title's idle slideshow waits while
    /// a window is open.
    /// </summary>
    internal sealed class TitleOnline
    {
        private const String MainGroup = "Online.Title";
        private const String ListGroup = "Online.TitleList";
        private const Int32 StartWidth = 640, LobbyWidth = 1040, ListWidth = 1000;
        /// <summary>Rows a list shows at once (more get a "More" row): 15 saves wouldn't fit on the screen.</summary>
        private const Int32 PageRows = 8;

        public static TitleOnline Current { get; private set; }

        /// <summary>A window is open (the debug status box steps aside).</summary>
        public static Boolean Active => Current != null && Current._window != null;

        public readonly GameObject Entry;

        /// <summary>Games: New Game and the save slots; Saves: one slot's saves (<see cref="_listSlot"/>).</summary>
        private enum ListKind { None, Games, Saves, Friends }

        private readonly TitleUI _title;
        private readonly MenuWindows _windows;
        private readonly Int32 _depth;
        private Window _window, _list;
        private ListKind _listKind;
        private Int32 _cursor, _listCursor, _listPage, _listSlot;
        private String _key;
        private SteamLobby.LobbyState _lastState = SteamLobby.LobbyState.None;
        private List<KeyValuePair<UInt64, UInt64>> _friends = new List<KeyValuePair<UInt64, UInt64>>();

        public TitleOnline(TitleUI title, GameObject languageButton)
        {
            Current = this;
            _title = title;

            // The button: a copy of the language button ("US"), above it
            Entry = Object.Instantiate(languageButton);
            Entry.name = "Online Button";
            Entry.transform.parent = languageButton.transform.parent;
            Entry.transform.localScale = Vector3.one;
            Entry.transform.localPosition = languageButton.transform.localPosition + new Vector3(0f, 126f, 0f);
            UILabel label = Entry.GetComponentsInChildren<UILabel>(true).FirstOrDefault();
            MenuWindows.SetText(label, "Online");
            if (label != null)
            {
                label.fontSize = 26;
                label.overflowMethod = UILabel.Overflow.ShrinkContent;
            }
            UIEventListener.Get(Entry).Click += title.onClick; // delegates aren't copied
            UIKeyNavigation nav = Entry.GetComponent<UIKeyNavigation>();
            UIKeyNavigation languageNav = languageButton.GetComponent<UIKeyNavigation>();
            nav.startsSelected = false;
            nav.onUp = null;
            nav.onDown = languageButton;
            nav.onLeft = languageNav.onLeft;
            nav.onRight = languageNav.onRight;
            languageNav.onUp = Entry;

            _windows = new MenuWindows(title, title.transform, MenuWindows.MainMenuRowTemplate);
            Int32 depth = 0;
            foreach (UIPanel panel in title.GetComponentsInChildren<UIPanel>(true))
                depth = Math.Max(depth, panel.depth);
            UIPanel parentPanel = NGUITools.FindInParents<UIPanel>(title.gameObject);
            if (parentPanel != null)
                depth = Math.Max(depth, parentPanel.depth);
            _depth = depth + 1;
        }

        private static SessionLobby Session => SessionLobby.Current;
        private static Boolean AtTitle => PersistenSingleton<UIManager>.Instance?.State == UIManager.UIState.Title;

        // ---- Title hooks ------------------------------------------------------------------------------------------

        public Boolean OnConfirm(GameObject go)
        {
            if (go == null)
                return false;
            if (go == Entry && ButtonGroupState.ActiveGroup == _title.OnlineMenuGroup)
            {
                FF9Sfx.FF9SFX_Play(103);
                Open();
                return true;
            }
            return MenuWindows.Confirm(_list, go) || MenuWindows.Confirm(_window, go);
        }

        public Boolean OnCancel(GameObject go)
        {
            if (_list != null && ButtonGroupState.ActiveGroup == ListGroup)
            {
                FF9Sfx.FF9SFX_Play(101);
                if (_listKind == ListKind.Saves)
                    OpenList(ListKind.Games, _listSlot); // back to the slots, on this one
                else
                    CloseList();
                return true;
            }
            if (_window != null && ButtonGroupState.ActiveGroup == MainGroup)
            {
                FF9Sfx.FF9SFX_Play(101);
                Close(true);
                return true;
            }
            return false;
        }

        /// <summary>Every frame (OnlineSession.Update).</summary>
        public void Update()
        {
            SessionLobby session = Session;
            SteamLobby.LobbyState state = session?.LobbyState ?? SteamLobby.LobbyState.None;
            if (!AtTitle)
            {
                if (_window != null)
                    Close(false); // the game is loading (the session started)
                _lastState = state;
                return;
            }
            if (_window == null)
            {
                // Entering a lobby some other way (a Steam invite, a hotkey): show it
                if (state != _lastState && state != SteamLobby.LobbyState.None && ButtonGroupState.ActiveGroup == _title.OnlineMenuGroup)
                {
                    _lastState = state;
                    Open();
                }
                _lastState = state;
                return;
            }
            _lastState = state;
            String key = Key(session);
            if (key == _key)
                return;
            _key = key;
            Build(true);
            if (_list != null && (_listKind == ListKind.Games || _listKind == ListKind.Saves ? !(session != null && session.IsHost && session.Phase == SessionPhase.Gathering) : state != SteamLobby.LobbyState.None))
                CloseList();
        }

        // ---- Flow -------------------------------------------------------------------------------------------------

        private void Open()
        {
            if (_window != null)
                return;
            _title.OnlineIdleTimer(false);
            _cursor = 0;
            _key = Key(Session);
            Build(false);
            MenuWindows.Focus(_window, 0);
            Log.Message($"[Online] Title: Online window opened ({Session?.LobbyState.ToString() ?? "no Steam"})");
        }

        /// <param name="toMenu">Give the cursor back to the title menu, on the Online button.</param>
        public void Close(Boolean toMenu)
        {
            MenuWindows.Destroy(ref _list);
            MenuWindows.Destroy(ref _window);
            _listKind = ListKind.None;
            if (!toMenu)
                return;
            ButtonGroupState.SetCursorMemorize(Entry, _title.OnlineMenuGroup);
            ButtonGroupState.ActiveGroup = _title.OnlineMenuGroup;
            _title.OnlineIdleTimer(true);
        }

        /// <param name="slot">Games: the slot row to put the cursor on (else the chosen game's); Saves: the slot to list.</param>
        private void OpenList(ListKind kind, Int32 slot = -1)
        {
            MenuWindows.Destroy(ref _list);
            _listKind = kind;
            _listCursor = 0;
            _listPage = 0;
            SessionLobby session = Session;
            if (kind == ListKind.Friends)
                _friends = session?.FriendLobbies() ?? new List<KeyValuePair<UInt64, UInt64>>();
            if (kind == ListKind.Saves)
                _listSlot = slot;
            if (session != null && (kind == ListKind.Games || kind == ListKind.Saves))
            {
                // The cursor starts on the chosen game (or its slot), on the page that shows it
                List<Int32> slots = session.ChoiceSlots;
                Int32 chosen = session.ChoiceIndex;
                Int32 at;
                if (kind == ListKind.Games)
                {
                    Int32 target = slot >= 0 ? slot : chosen >= 0 && chosen < slots.Count ? slots[chosen] : 0;
                    at = GameRows(session).IndexOf(target);
                }
                else
                {
                    at = SaveRows(session, _listSlot).IndexOf(chosen);
                }
                if (at >= 0)
                {
                    _listPage = at / PageRows;
                    _listCursor = at % PageRows;
                }
            }
            BuildList(false);
            MenuWindows.Focus(_list, _listCursor);
        }

        /// <summary>The Games list's rows: 0 (New Game), then each slot number that has saves.</summary>
        private static List<Int32> GameRows(SessionLobby session)
        {
            List<Int32> rows = new List<Int32>();
            foreach (Int32 slot in session.ChoiceSlots)
                if (slot > 0 && !rows.Contains(slot))
                    rows.Add(slot);
            rows.Sort();
            rows.Insert(0, 0);
            return rows;
        }

        /// <summary>A slot's saves, as choice indexes (newest first).</summary>
        private static List<Int32> SaveRows(SessionLobby session, Int32 slot)
        {
            List<Int32> rows = new List<Int32>();
            List<Int32> slots = session.ChoiceSlots;
            for (Int32 i = 0; i < slots.Count; i++)
                if (slot > 0 && slots[i] == slot)
                    rows.Add(i);
            return rows;
        }

        private void CloseList()
        {
            MenuWindows.Destroy(ref _list);
            _listKind = ListKind.None;
            if (_window != null)
                MenuWindows.Focus(_window, _cursor);
        }

        // ---- Window contents --------------------------------------------------------------------------------------

        private void Build(Boolean refresh)
        {
            SessionLobby session = Session;
            String caption, info;
            List<Row> rows = new List<Row>();
            Int32 width = StartWidth;
            if (session == null)
            {
                caption = "ONLINE";
                info = "Online play needs Steam: start the game from Steam, with your Steam account signed in.";
                rows.Add(MenuWindows.NewRow("Back", true, null, () => Close(true)));
            }
            else
            {
                switch (session.LobbyState)
                {
                    case SteamLobby.LobbyState.None:
                        caption = "ONLINE";
                        info = "Play the story together over Steam (up to 4 players).\nHost a game, then invite friends; or join a friend's game.";
                        rows.Add(MenuWindows.NewRow("Host a game", true, null, () => session.HostLobby()));
                        rows.Add(MenuWindows.NewRow("Join a friend", true, null, () => OpenList(ListKind.Friends)));
                        break;
                    case SteamLobby.LobbyState.Creating:
                    case SteamLobby.LobbyState.Joining:
                        caption = "ONLINE";
                        info = session.LobbyState == SteamLobby.LobbyState.Creating ? "Creating the lobby..." : "Joining the lobby...";
                        rows.Add(MenuWindows.NewRow("Cancel", true, null, () => session.LeaveLobby()));
                        break;
                    default:
                        caption = "LOBBY";
                        width = LobbyWidth;
                        info = LobbyInfo(session);
                        LobbyRows(session, rows);
                        break;
                }
            }
            if (_window == null)
                _window = _windows.Create("Title", MainGroup, width, _depth, _depth + 2);
            else if (_window.Width != width)
            {
                _window.Width = width;
                _cursor = 0;
            }
            _windows.Fill(_window, caption, info, rows, refresh, ref _cursor);
            MenuWindows.Place(_window, 0f, Math.Min(330f, _window.Height / 2f + 40f));
            if (_list != null)
                BuildList(true);
        }

        private static String LobbyInfo(SessionLobby session)
        {
            StringBuilder info = new StringBuilder();
            List<String> players = new List<String>();
            foreach (UInt64 member in session.Members)
            {
                String name = session.PlayerName(member) + (member == session.LocalId ? " (you)" : "");
                if (member == session.OwnerId)
                    name += " (host)";
                else if (!session.IsConnectedTo(member))
                    name += " (connecting...)";
                else if (session.WhereOf(member) == "game")
                    name += " (in game)";
                players.Add(name);
            }
            info.Append("Players: ").Append(String.Join(", ", players.ToArray()));
            if (session.StartedBeforeJoin)
            {
                // A game in progress: the party as it is now, then Join
                info.Append("\nThis game is in progress. Pick a character nobody plays (or none, to watch), then Join the game.");
                String join;
                if (session.Phase == SessionPhase.Starting)
                    join = "Receiving the host's game...";
                else if (session.JoinAsked)
                    join = String.IsNullOrEmpty(session.JoinWait) ? "Waiting for the host's game..." : Capitalize(session.JoinWait) + ".";
                else
                    join = String.IsNullOrEmpty(session.Status) ? (session.Party.Count == 0 ? "Waiting for the host's party..." : "Ready to join.") : Capitalize(session.Status);
                info.Append('\n').Append(join);
                return info.ToString();
            }
            info.Append("\nGame: ").Append(String.IsNullOrEmpty(session.GameLabel) ? (session.IsHost ? "..." : "the host is choosing") : session.GameLabel);
            if (session.IsHost)
            {
                List<String> party = new List<String>();
                foreach (PartyEntry entry in session.Party)
                    party.Add($"{entry.Name} ({Owner(session, entry.CharacterId)})");
                info.Append("\nParty: ").Append(party.Count > 0 ? String.Join(", ", party.ToArray()) : "...");
            }
            String status;
            if (session.Phase == SessionPhase.Starting)
                status = "The host started: receiving the host's game...";
            else if (session.IsHost)
                status = session.StartBlocker() ?? (String.IsNullOrEmpty(session.Status) ? "Ready: everyone is here." : session.Status);
            else
                status = String.IsNullOrEmpty(session.Status) ? "Pick a character, then wait for the host to start." : session.Status;
            info.Append('\n').Append(Capitalize(status));
            return info.ToString();
        }

        private void LobbyRows(SessionLobby session, List<Row> rows)
        {
            if (session.StartedBeforeJoin && session.Phase == SessionPhase.Gathering)
            {
                foreach (PartyEntry entry in session.Party)
                {
                    PartyEntry e = entry;
                    UInt64 by = session.PickedBy(e.CharacterId);
                    Boolean mine = session.MyPick == e.CharacterId;
                    Boolean free = by == 0;
                    String owner = mine ? "you" : by == 0 ? "free" : session.PlayerName(by);
                    rows.Add(MenuWindows.NewRow($"{e.Name}: {owner}", !session.JoinAsked && (mine || free), null, () => session.Pick(mine ? -1 : e.Index)));
                }
                if (session.JoinAsked)
                    rows.Add(MenuWindows.NewRow("Cancel joining", true, null, () => session.CancelJoin()));
                else
                    rows.Add(MenuWindows.NewRow("Join the game", session.Party.Count > 0 && session.IsConnectedTo(session.OwnerId), null, () => session.AskToJoin()));
            }
            if (!session.StartedBeforeJoin && session.Phase == SessionPhase.Gathering)
            {
                if (session.IsHost)
                {
                    String game = String.IsNullOrEmpty(session.GameLabel) ? "..." : session.GameLabel;
                    rows.Add(MenuWindows.NewRow($"Game: {Shorten(game, 36)}", !session.ChoiceLoading && session.ChoiceLabels.Count > 1, "Choose New Game or any save.", () => OpenList(ListKind.Games)));
                }
                else
                {
                    foreach (PartyEntry entry in session.Party)
                    {
                        PartyEntry e = entry;
                        UInt64 by = session.PickedBy(e.CharacterId);
                        Boolean mine = by == session.LocalId;
                        Boolean free = by == 0;
                        rows.Add(MenuWindows.NewRow($"{e.Name}: {Owner(session, e.CharacterId)}", mine || free && e.Index > 0, null, () => session.Pick(mine ? -1 : e.Index)));
                    }
                }
                rows.Add(MenuWindows.NewRow("Invite friends", true, null, () => session.InviteFriends()));
                if (session.IsHost)
                    rows.Add(MenuWindows.NewRow("Start", session.StartBlocker() == null, null, () => session.Start()));
            }
            rows.Add(MenuWindows.NewRow("Leave lobby", true, null, () => session.LeaveLobby()));
        }

        private void BuildList(Boolean refresh)
        {
            SessionLobby session = Session;
            if (session == null || _listKind == ListKind.None)
                return;
            List<Row> rows = new List<Row>();
            String caption, info = null;
            if (_listKind == ListKind.Games)
            {
                caption = "GAME";
                info = "Choose New Game or a save slot (* is the chosen game's slot).";
                List<String> labels = session.ChoiceLabels;
                List<Int32> slots = session.ChoiceSlots;
                Int32 chosen = session.ChoiceIndex;
                Int32 chosenSlot = chosen >= 0 && chosen < slots.Count ? slots[chosen] : -1;
                foreach (Int32 slot in GameRows(session))
                {
                    if (slot == 0)
                    {
                        Int32 newGame = slots.IndexOf(0);
                        rows.Add(MenuWindows.NewRow("New Game", newGame >= 0 && newGame != chosen, null, () => { session.Choose(newGame); CloseList(); }));
                        continue;
                    }
                    List<Int32> saves = SaveRows(session, slot);
                    Int32 listed = slot;
                    String newest = labels[saves[0]];
                    Int32 colon = newest.IndexOf(": ");
                    String text = $"Slot {slot}{(slot == chosenSlot ? "*" : "")} ({saves.Count} save{(saves.Count > 1 ? "s" : "")}): {(colon >= 0 ? newest.Substring(colon + 2) : newest)}";
                    rows.Add(MenuWindows.NewRow(Shorten(text, 44), true, null, () => OpenList(ListKind.Saves, listed)));
                }
            }
            else if (_listKind == ListKind.Saves)
            {
                caption = $"SLOT {_listSlot}";
                List<String> labels = session.ChoiceLabels;
                foreach (Int32 i in SaveRows(session, _listSlot))
                {
                    Int32 index = i;
                    rows.Add(MenuWindows.NewRow(Shorten(labels[i], 44), i != session.ChoiceIndex, null, () => { session.Choose(index); CloseList(); }));
                }
                if (rows.Count == 0)
                    info = "This slot has no saves any more.";
            }
            else
            {
                caption = "JOIN A FRIEND";
                foreach (KeyValuePair<UInt64, UInt64> friend in _friends)
                {
                    UInt64 lobby = friend.Value;
                    rows.Add(MenuWindows.NewRow(session.PlayerName(friend.Key), true, null, () => { CloseList(); session.JoinLobby(lobby); }));
                }
                if (_friends.Count == 0)
                    info = "None of your Steam friends is in a game right now. Ask them to host, then refresh; or accept their invite in Steam.";
                rows.Add(MenuWindows.NewRow("Refresh", true, null, () =>
                {
                    _friends = session.FriendLobbies();
                    BuildList(true);
                }));
            }
            Int32 pages = (rows.Count + PageRows - 1) / PageRows;
            if (pages > 1)
            {
                _listPage = Math.Max(0, Math.Min(_listPage, pages - 1));
                List<Row> page = rows.GetRange(_listPage * PageRows, Math.Min(PageRows, rows.Count - _listPage * PageRows));
                page.Add(MenuWindows.NewRow($"More ({_listPage + 1}/{pages})", true, null, () =>
                {
                    _listPage = (_listPage + 1) % pages;
                    _listCursor = PageRows; // the "More" row, so it can be pressed again (Fill keeps it in range)
                    BuildList(false);
                    MenuWindows.Focus(_list, _listCursor);
                }));
                rows = page;
            }
            if (rows.Count == 0)
                rows.Add(MenuWindows.NewRow("Back", true, null, CloseList));
            if (_list == null)
                _list = _windows.Create("List", ListGroup, ListWidth, _depth + 2, _depth + 4);
            _windows.Fill(_list, caption, info, rows, refresh, ref _listCursor);
            MenuWindows.Place(_list, 0f, Math.Min(300f, _list.Height / 2f + 20f));
        }

        // ---- Helpers ----------------------------------------------------------------------------------------------

        private static String Owner(SessionLobby session, Int32 character)
        {
            UInt64 by = session.PickedBy(character);
            if (by == 0)
                return "free";
            String name = by == session.LocalId ? "you" : session.PlayerName(by);
            return by == session.OwnerId && by != session.LocalId ? name + ", host" : name;
        }

        private static String Shorten(String text, Int32 max) => text.Length <= max ? text : text.Substring(0, max - 3) + "...";

        private static String Capitalize(String text) => String.IsNullOrEmpty(text) ? text : Char.ToUpperInvariant(text[0]) + text.Substring(1);

        private static String Key(SessionLobby session)
        {
            if (session == null)
                return "-";
            StringBuilder key = new StringBuilder();
            key.Append(session.LobbyState).Append('/').Append(session.Phase).Append('/').Append(session.StartedBeforeJoin).Append('/').Append(session.OwnerId);
            foreach (UInt64 member in session.Members)
                key.Append('|').Append(member).Append(session.WhereOf(member)).Append(session.IsConnectedTo(member));
            key.Append('/').Append(session.GameLabel).Append('/').Append(session.ChoiceIndex).Append(session.ChoiceLoading).Append(session.ChoiceLabels.Count);
            foreach (PartyEntry entry in session.Party)
                key.Append('|').Append(entry.CharacterId).Append(':').Append(session.PickedBy(entry.CharacterId));
            key.Append('/').Append(session.Status).Append('/').Append(session.IsHost ? session.StartBlocker() : "");
            key.Append('/').Append(session.MyPick).Append(session.JoinAsked).Append(session.StartedBeforeJoin ? session.JoinWait : "");
            return key.ToString();
        }
    }
}
