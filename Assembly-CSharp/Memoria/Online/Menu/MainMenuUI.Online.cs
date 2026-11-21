using Assets.Sources.Scripts.UI.Common;
using Memoria.Online.Menus;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// [Online] The main menu's side of the Online menu (Memoria/Online/Menu/OnlineMenu.cs): what it needs from the
/// menu's private parts, and the calls MainMenuUI.cs makes into it (each marked [Online] there).
/// </summary>
public partial class MainMenuUI
{
    [NonSerialized]
    private OnlineMenu onlineMenu;

    internal String OnlineSubMenuGroup => MainMenuUI.SubMenuGroupButton;
    internal String OnlineCharacterGroup => MainMenuUI.CharacterGroupButton;
    internal GameObject OnlineEntryTemplate => this.CardSubMenu;
    internal GameObject OnlineConfigEntry => this.ConfigSubMenu;
    internal List<CharacterDetailHUD> OnlineCharacterHUDs => this.CharacterHUDList;

    /// <summary>The submenu entries in their order, the Online entry not included (Config last).</summary>
    internal List<GameObject> OnlineSubMenuEntries
    {
        get
        {
            List<GameObject> entries = new List<GameObject> { this.ItemSubMenu, this.AbilitySubMenu, this.EquipSubMenu, this.StatusSubMenu, this.OrderSubMenu, this.CardSubMenu };
            if (this.PartySubMenu != null)
                entries.Add(this.PartySubMenu);
            entries.Add(this.ConfigSubMenu);
            return entries;
        }
    }

    /// <summary>Puts the cursor on the party, as the Status entry does.</summary>
    internal void OnlineSelectCharacter(Int32 index)
    {
        this.SetAvailableCharacter(false);
        GameObject go = index >= 0 && index < this.CharacterHUDList.Count && this.CharacterHUDList[index].Self.GetComponent<ButtonGroupState>().enabled
            ? this.CharacterHUDList[index].Self
            : this.characterMemorize;
        if (go == null || !go.GetComponent<ButtonGroupState>().enabled)
            go = this.CharacterHUDList[this.GetFirstPlayer()].Self;
        ButtonGroupState.SetCursorMemorize(go, MainMenuUI.CharacterGroupButton);
        ButtonGroupState.ActiveGroup = MainMenuUI.CharacterGroupButton;
        ButtonGroupState.SetSecondaryOnGroup(MainMenuUI.SubMenuGroupButton);
        ButtonGroupState.HoldActiveStateOnGroup(MainMenuUI.SubMenuGroupButton);
    }

    /// <summary>Gives the cursor back to the submenu, on this entry.</summary>
    internal void OnlineBackToSubMenu(GameObject entry)
    {
        ButtonGroupState.SetCursorMemorize(entry, MainMenuUI.SubMenuGroupButton);
        ButtonGroupState.ActiveGroup = MainMenuUI.SubMenuGroupButton;
    }

    /// <summary>How far the submenu tween shifts its clip for this entry (its distance from the first entry).</summary>
    private Vector2 SubMenuShift(GameObject entry)
    {
        return new Vector2(0f, 9f + this.ItemSubMenu.transform.localPosition.y - entry.transform.localPosition.y);
    }

    private void OnlineAwake()
    {
        this.onlineMenu = new OnlineMenu(this);
        UIEventListener.Get(this.onlineMenu.Entry).Click += onClick;
    }

    private void OnlineShow() => this.onlineMenu?.OnShow();
    private void OnlineHide() => this.onlineMenu?.Close();
    private void OnlineUpdate() => this.onlineMenu?.Update();
    private Boolean OnlineConfirm(GameObject go) => this.onlineMenu != null && this.onlineMenu.OnConfirm(go);
    private Boolean OnlineCancel(GameObject go) => this.onlineMenu != null && this.onlineMenu.OnCancel(go);
}
