using Memoria.Online.Menus;
using System;
using UnityEngine;

/// <summary>
/// [Online] The battle's side of its Online window (Memoria/Online/Menu/BattleOnline.cs): Select opens it in an online
/// session; confirm and cancel go to it first while it's open (the [Online] lines in BattleHUD.Scene.cs).
/// </summary>
public partial class BattleHUD
{
    [NonSerialized]
    private BattleOnline onlineBattle;

    public override Boolean OnKeySelect(GameObject go)
    {
        if (!this.Loading && !_hidingHud && !_usingMainMenu)
        {
            if (this.onlineBattle == null)
                this.onlineBattle = new BattleOnline(this);
            if (this.onlineBattle.OnSelect())
                return true;
        }
        return base.OnKeySelect(go);
    }

    private Boolean OnlineConfirm(GameObject go) => this.onlineBattle != null && this.onlineBattle.OnConfirm(go);
    private Boolean OnlineCancel(GameObject go) => this.onlineBattle != null && this.onlineBattle.OnCancel(go);
}
