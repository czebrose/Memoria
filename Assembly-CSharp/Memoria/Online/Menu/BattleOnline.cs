using Memoria.Online.Session;
using Memoria.Prime;
using System;
using System.Collections.Generic;
using UnityEngine;
using Row = Memoria.Online.Menus.MenuWindows.Row;
using Window = Memoria.Online.Menus.MenuWindows.Window;

namespace Memoria.Online.Menus
{
    /// <summary>
    /// The battle's Online window (docs/online/online-menu.md): Select (in an online session) opens it over the battle
    /// without pausing it. PARTY lists the party with who plays each character; choosing one opens its commands (the
    /// same as the main menu's Online entry: Take, Follow, Swap, Accept / Decline, Release); Follow lists the players.
    /// In a shared battle a change moves the character at its next turn (BattleControl.OwnerSync). A turn of this PC's
    /// coming up takes the cursor and closes the window. Select or Cancel closes it.
    /// </summary>
    internal sealed class BattleOnline
    {
        private const String PartyGroup = "Online.Battle";
        private const String ActionGroup = "Online.BattleAction";
        private const String PlayerGroup = "Online.BattlePlayer";

        public static BattleOnline Current { get; private set; }

        private readonly BattleHUD _hud;
        private readonly MenuWindows _windows;
        private readonly Int32 _depth;
        private Window _party, _action, _players;
        private Int32 _partyCursor, _actionCursor, _playerCursor;
        private Int32 _member = -1;
        private String _key;
        private String _previousGroup;

        public BattleOnline(BattleHUD hud)
        {
            Current = this;
            _hud = hud;
            _windows = new MenuWindows(hud, hud.transform, MenuWindows.MainMenuRowTemplate);
            Int32 depth = 0;
            foreach (UIPanel panel in hud.GetComponentsInChildren<UIPanel>(true))
                depth = Math.Max(depth, panel.depth);
            UIPanel parentPanel = NGUITools.FindInParents<UIPanel>(hud.gameObject);
            if (parentPanel != null)
                depth = Math.Max(depth, parentPanel.depth);
            _depth = depth + 1;
        }

        private static IPartyControl Control
        {
            get
            {
                if (OnlineMenu.Preview != null)
                    return OnlineMenu.Preview;
                SessionLobby session = SessionLobby.Current;
                return session != null && session.Phase == SessionPhase.Playing ? session : null;
            }
        }

        public Boolean IsOpen => _party != null;

        // ---- BattleHUD hooks --------------------------------------------------------------------------------------

        /// <summary>Select: opens or closes the window. False outside an online session (Select does what it did).</summary>
        public Boolean OnSelect()
        {
            if (IsOpen)
            {
                FF9Sfx.FF9SFX_Play(101);
                Close(true);
                return true;
            }
            IPartyControl control = Control;
            if (control == null)
                return false;
            FF9Sfx.FF9SFX_Play(103);
            _previousGroup = ButtonGroupState.ActiveGroup;
            _partyCursor = 0;
            _key = OnlineMenu.StateKey(control);
            FillParty(control, false);
            MenuWindows.Focus(_party, Math.Max(0, MyMember(control)));
            Log.Message("[Online] Battle: Online window opened");
            return true;
        }

        public Boolean OnConfirm(GameObject go)
        {
            if (!IsOpen || go == null)
                return false;
            return MenuWindows.Confirm(_players, go) || MenuWindows.Confirm(_action, go) || MenuWindows.Confirm(_party, go);
        }

        public Boolean OnCancel(GameObject go)
        {
            if (!IsOpen)
                return false;
            String group = ButtonGroupState.ActiveGroup;
            if (group == PlayerGroup)
            {
                FF9Sfx.FF9SFX_Play(101);
                MenuWindows.Destroy(ref _players);
                MenuWindows.Focus(_action, _actionCursor);
                return true;
            }
            if (group == ActionGroup)
            {
                FF9Sfx.FF9SFX_Play(101);
                BackToParty();
                return true;
            }
            if (group == PartyGroup)
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
            if (!IsOpen)
                return;
            IPartyControl control = Control;
            String group = ButtonGroupState.ActiveGroup;
            Boolean ours = group == PartyGroup || group == ActionGroup || group == PlayerGroup;
            if (control == null || !ours || PersistenSingleton<UIManager>.Instance.State != UIManager.UIState.BattleHUD)
            {
                // The session ended, the battle ended, or a turn here took the cursor
                Close(false);
                return;
            }
            String key = OnlineMenu.StateKey(control);
            if (key == _key)
                return;
            _key = key;
            FillParty(control, true);
            if (_action != null)
            {
                if (MemberCharacter(_member) < 0)
                    BackToParty();
                else
                    FillAction(control, true);
            }
            if (_players != null && (_action == null || control.HolderOf(MemberCharacter(_member)) != 0))
            {
                MenuWindows.Destroy(ref _players);
                if (_action != null)
                    MenuWindows.Focus(_action, _actionCursor);
            }
            else if (_players != null)
                FillPlayers(control, true);
        }

        /// <param name="restore">Give the cursor back to what had it before (the battle menu, if one was open).</param>
        public void Close(Boolean restore)
        {
            MenuWindows.Destroy(ref _players);
            MenuWindows.Destroy(ref _action);
            MenuWindows.Destroy(ref _party);
            if (restore && !String.IsNullOrEmpty(_previousGroup))
                ButtonGroupState.ActiveGroup = _previousGroup;
            _previousGroup = null;
        }

        // ---- Windows ----------------------------------------------------------------------------------------------

        private void FillParty(IPartyControl control, Boolean refresh)
        {
            List<Row> rows = new List<Row>();
            PLAYER[] members = FF9StateSystem.Common.FF9.party.member;
            for (Int32 i = 0; i < members.Length; i++)
            {
                if (members[i] == null)
                    continue;
                Int32 member = i;
                Int32 character = (Int32)members[i].info.slot_no;
                rows.Add(MenuWindows.NewRow($"{members[i].Name}: {PartyCommands.OwnerLabel(control, character)}", true, null, () => OpenAction(member)));
            }
            String info = "Changes apply at the character's next turn.";
            if (control.OfferFrom != 0)
                info = PartyCommands.OfferText(control, control.OfferFrom) + "\n" + info;
            if (_party == null)
                _party = _windows.Create("Battle Party", PartyGroup, 780, _depth, _depth + 2);
            _windows.Fill(_party, "PARTY", info, rows, refresh, ref _partyCursor);
            MenuWindows.Place(_party, 0f, 470f);
        }

        private void OpenAction(Int32 member)
        {
            IPartyControl control = Control;
            if (control == null || MemberCharacter(member) < 0)
                return;
            _member = member;
            _partyCursor = 0;
            for (Int32 i = 0; i < member; i++)
                if (FF9StateSystem.Common.FF9.party.member[i] != null)
                    _partyCursor++;
            _actionCursor = 0;
            MenuWindows.Destroy(ref _action);
            FillAction(control, false);
            MenuWindows.Focus(_action, 0);
        }

        private void FillAction(IPartyControl control, Boolean refresh)
        {
            Int32 character = MemberCharacter(_member);
            String name = FF9StateSystem.Common.FF9.party.member[_member].Name;
            List<Row> rows = PartyCommands.ForCharacter(control, character, name, () => Control, BackToParty, OpenPlayers, true, out String info);
            if (_action == null)
                _action = _windows.Create("Battle Action", ActionGroup, 460, _depth + 2, _depth + 4);
            _windows.Fill(_action, name.ToUpperInvariant(), info, rows, refresh, ref _actionCursor);
            Vector3 party = _party.Root.transform.localPosition;
            MenuWindows.Place(_action, party.x + _party.Width / 2f - 40f, party.y + _party.Height / 2f - 60f);
        }

        private void OpenPlayers()
        {
            IPartyControl control = Control;
            if (control == null)
                return;
            _playerCursor = 0;
            FillPlayers(control, false);
            MenuWindows.Focus(_players, Math.Max(0, _players.Rows.FindIndex(r => r.Enabled)));
        }

        private void FillPlayers(IPartyControl control, Boolean refresh)
        {
            Int32 character = MemberCharacter(_member);
            String name = FF9StateSystem.Common.FF9.party.member[_member].Name;
            List<Row> rows = PartyCommands.FollowRows(control, character, name, () => Control, BackToParty, true);
            if (_players == null)
                _players = _windows.Create("Battle Players", PlayerGroup, 460, _depth + 4, _depth + 6);
            _windows.Fill(_players, "FOLLOW", null, rows, refresh, ref _playerCursor);
            Vector3 action = _action.Root.transform.localPosition;
            MenuWindows.Place(_players, action.x - 60f, action.y + _action.Height / 2f - 60f);
        }

        private void BackToParty()
        {
            MenuWindows.Destroy(ref _players);
            MenuWindows.Destroy(ref _action);
            if (_party != null)
                MenuWindows.Focus(_party, _partyCursor);
        }

        // ---- Helpers ----------------------------------------------------------------------------------------------

        private static Int32 MemberCharacter(Int32 member)
        {
            PLAYER[] members = FF9StateSystem.Common?.FF9?.party?.member;
            if (members == null || member < 0 || member >= members.Length || members[member] == null)
                return -1;
            return (Int32)members[member].info.slot_no;
        }

        /// <summary>The row of this PC's character in PARTY (the rows skip empty party slots).</summary>
        private static Int32 MyMember(IPartyControl control)
        {
            Int32 mine = control.CharacterOf(control.Me);
            PLAYER[] members = FF9StateSystem.Common.FF9.party.member;
            Int32 row = 0;
            for (Int32 i = 0; i < members.Length; i++)
            {
                if (members[i] == null)
                    continue;
                if ((Int32)members[i].info.slot_no == mine)
                    return row;
                row++;
            }
            return 0;
        }
    }
}
